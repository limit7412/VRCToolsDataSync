using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCToolsDataSync.Core.Sync;

namespace VRCToolsDataSync.Core.Storage;

/// <summary>
/// S3 互換オブジェクトストレージを同期先として扱う。
/// Cloudflare R2 / Amazon S3 / Backblaze B2 / MinIO のように、エンドポイントと
/// 署名リージョンの違いだけで済むプロバイダをまとめて扱える。
/// </summary>
public sealed class S3SyncStorage : ISyncStorage
{
    /// <summary>manifest の変更を確認する間隔。</summary>
    public static readonly TimeSpan ManifestPollInterval = TimeSpan.FromSeconds(60);

    /// <summary>アップロード用の一時ファイルを掃除対象とみなすまでの時間。</summary>
    private static readonly TimeSpan StagingRetention = TimeSpan.FromDays(1);

    private readonly S3Client _client;
    private readonly S3StorageOptions _options;
    private readonly string _keyPrefix;
    private readonly ILogger _logger;

    // 条件付き書き込みに対応していないプロバイダを検知したら降格する。
    // 複数スレッドから読み書きするため volatile にする。
    private volatile bool _conditionalWrites;

    public S3SyncStorage(S3StorageOptions options, ILogger<S3SyncStorage>? logger = null)
    {
        _options = options;
        _client = new S3Client(options);
        _keyPrefix = options.NormalizedKeyPrefix;
        _conditionalWrites = options.UseConditionalWrites;
        _logger = logger ?? NullLogger<S3SyncStorage>.Instance;
    }

    public string DisplayName => _keyPrefix.Length == 0
        ? $"s3://{_options.BucketName} ({_client.Host})"
        : $"s3://{_options.BucketName}/{_keyPrefix} ({_client.Host})";

    // 同期先が変わると manifest の version の意味も変わるため、ToolState を
    // 同期先ごとに分ける。バケットとキー接頭辞まで含めて識別する。
    public string StateKeyPrefix => $"s3|{_client.Host}/{_options.BucketName}/{_keyPrefix}|";

    public ManifestSnapshot LoadManifest()
    {
        var body = _client.GetBytes(ToObjectKey(ManifestStore.ManifestKey));
        if (body is null)
        {
            return new ManifestSnapshot(new SyncManifest(), null);
        }
        var manifest = JsonSerializer.Deserialize<SyncManifest>(body.Content, ManifestJson.Options)
                       ?? new SyncManifest();
        return new ManifestSnapshot(manifest, body.ETag);
    }

    public bool TrySaveManifest(SyncManifest manifest, string? expectedTag)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJson.Options);
        var key = ToObjectKey(ManifestStore.ManifestKey);

        if (!_conditionalWrites)
        {
            return Put(key, payload, conditions: null);
        }

        // expectedTag が null なら、読み込み時点で manifest が無かったということ。
        // If-None-Match:* にして、読み込みから書き込みまでの間に他 PC が作っていた
        // ケースを取りこぼさないようにする。
        var conditions = expectedTag is null
            ? new List<KeyValuePair<string, string>> { new("If-None-Match", "*") }
            : new List<KeyValuePair<string, string>> { new("If-Match", expectedTag) };

        var outcome = _client.PutBytes(key, payload, "application/json", conditions);
        switch (outcome)
        {
            case S3PutOutcome.Success:
                return true;
            case S3PutOutcome.PreconditionFailed:
                // 他 PC が先に更新した。呼び出し側が読み直してやり直す。
                return false;
            case S3PutOutcome.ConditionalWriteUnsupported:
                _conditionalWrites = false;
                _logger.LogWarning(
                    "同期先が条件付き書き込みに対応していないため無効化しました。" +
                    "複数 PC が同時に Push した場合、manifest の更新が取りこぼされる可能性があります。");
                return Put(key, payload, conditions: null);
            default:
                return false;
        }
    }

    public void Upload(string localPath, string key)
    {
        StorageKey.Validate(key);
        _client.PutFile(ToObjectKey(key), localPath);
    }

    public IStagedUpload BeginUpload(string key)
    {
        StorageKey.Validate(key);
        var stagingDirectory = Path.Combine(Path.GetTempPath(), "VRCToolsDataSync", "staging");
        Directory.CreateDirectory(stagingDirectory);
        PruneStaleStagingFiles(stagingDirectory);
        var stagingPath = Path.Combine(stagingDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        return new StagedObject(this, key, stagingPath);
    }

    /// <summary>
    /// 置き去りになった一時ファイルを片付ける。ここには SQLite の完全な複製
    /// (数百 MB) が置かれるため、アップロード中にプロセスが落ちた分を放置すると
    /// ディスクを食い続ける。進行中のアップロードを消さないよう、十分に古いものだけを対象にする。
    /// </summary>
    private void PruneStaleStagingFiles(string stagingDirectory)
    {
        var threshold = DateTime.UtcNow - StagingRetention;
        try
        {
            foreach (var path in Directory.EnumerateFiles(stagingDirectory, "*.tmp"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < threshold) File.Delete(path);
                }
                catch (IOException)
                {
                    // 別プロセスが使用中。次の機会に任せる。
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "一時ファイルの掃除に失敗しました: {Directory}", stagingDirectory);
        }
    }

    public bool TryDownload(string key, string localPath)
    {
        StorageKey.Validate(key);
        return _client.GetToFile(ToObjectKey(key), localPath);
    }

    public void Delete(string key)
    {
        StorageKey.Validate(key);
        _client.DeleteObject(ToObjectKey(key));
    }

    public IReadOnlyList<string> List(string keyPrefix)
    {
        var objectKeys = _client.ListKeys(ToObjectKey(keyPrefix));
        if (_keyPrefix.Length == 0)
        {
            return objectKeys;
        }
        // 呼び出し側にはキー接頭辞を取り除いた論理キーを返す。manifest に記録する
        // 値も論理キーなので、接頭辞を変えても manifest はそのまま通用する。
        var trimLength = _keyPrefix.Length + 1;
        return objectKeys
            .Where(k => k.Length > trimLength)
            .Select(k => k[trimLength..])
            .ToList();
    }

    public IManifestWatcher CreateManifestWatcher()
        => new PollingManifestWatcher(this, ManifestPollInterval);

    /// <summary>
    /// 同期先へ到達できるかを確かめる。設定画面や CLI の接続テストから呼ぶ。
    /// </summary>
    public void VerifyAccess()
    {
        // manifest の読み取りだけで、認証・バケットの存在・権限の有無をまとめて確認できる。
        LoadManifest();
    }

    private bool Put(string objectKey, byte[] payload, IReadOnlyList<KeyValuePair<string, string>>? conditions)
        => _client.PutBytes(objectKey, payload, "application/json", conditions) == S3PutOutcome.Success;

    private string ToObjectKey(string key)
        => _keyPrefix.Length == 0 ? key : _keyPrefix + "/" + key;

    /// <summary>
    /// 一時ファイルへ書き出してから、Commit でアップロードする。
    /// SQLite の VACUUM INTO はローカルのファイルにしか書けないため、
    /// オブジェクトストレージへは一段挟む必要がある。
    /// </summary>
    private sealed class StagedObject : IStagedUpload
    {
        private readonly S3SyncStorage _storage;
        private readonly string _key;

        public StagedObject(S3SyncStorage storage, string key, string stagingPath)
        {
            _storage = storage;
            _key = key;
            LocalPath = stagingPath;
        }

        public string LocalPath { get; }

        public void Commit() => _storage.Upload(LocalPath, _key);

        public void Dispose()
        {
            if (File.Exists(LocalPath))
            {
                try { File.Delete(LocalPath); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
