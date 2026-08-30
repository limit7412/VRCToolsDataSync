using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCToolsDataSync.Core.Domain;
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

    /// <summary>書き込み権限の確認に使う中身。</summary>
    private static readonly byte[] ProbePayload = "VRCToolsDataSync access check"u8.ToArray();

    private readonly S3Client _client;
    private readonly S3StorageOptions _options;
    private readonly string _keyPrefix;
    private readonly string _endpointIdentity;
    private readonly ILogger _logger;
    private readonly ILogger<PollingManifestWatcher>? _watcherLogger;

    // 条件付き書き込みに対応していないプロバイダを検知したら降格する。
    // 複数スレッドから読み書きするため volatile にする。
    private volatile bool _conditionalWrites;

    public S3SyncStorage(S3StorageOptions options, ILoggerFactory? loggerFactory = null)
    {
        _options = options;
        _client = new S3Client(options);
        _keyPrefix = options.NormalizedKeyPrefix;
        _endpointIdentity = BuildEndpointIdentity(options.ServiceUrl);
        _conditionalWrites = options.UseConditionalWrites;
        _logger = loggerFactory?.CreateLogger<S3SyncStorage>() ?? (ILogger)NullLogger<S3SyncStorage>.Instance;
        _watcherLogger = loggerFactory?.CreateLogger<PollingManifestWatcher>();
    }

    public string DisplayName => _keyPrefix.Length == 0
        ? $"s3://{_options.BucketName} ({_client.Host})"
        : $"s3://{_options.BucketName}/{_keyPrefix} ({_client.Host})";

    // 同期先が変わると manifest の version の意味も変わるため、ToolState を
    // 同期先ごとに分ける。ポートとサブパスを持つエンドポイント (MinIO を
    // サブパスに置いた構成など) も区別する必要があるので、ホスト名だけでなく
    // エンドポイント全体を識別に含める。
    public string StateKeyPrefix => $"s3|{_endpointIdentity}/{_options.BucketName}/{_keyPrefix}|";

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

    private void Upload(string localPath, string key)
    {
        StorageKey.Validate(key);
        _client.PutFile(ToObjectKey(key), localPath);
    }

    public IStagedUpload BeginUpload()
    {
        var stagingDirectory = Path.Combine(Path.GetTempPath(), "VRCToolsDataSync", "staging");
        Directory.CreateDirectory(stagingDirectory);
        PruneStaleStagingFiles(stagingDirectory);
        var stagingPath = Path.Combine(stagingDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        return new StagedObject(this, stagingPath);
    }

    public IEnumerable<IncompleteUpload> ListIncompleteUploads()
    {
        // 実データと同じ接頭辞に絞る。manifest は 1 回の PUT で送るため、
        // 未完了のまま残るのは blobs/ の下だけである。
        var prefix = ToObjectKey(BlobKeys.Prefix);
        foreach (var (key, uploadId, initiated) in _client.ListMultipartUploads(prefix))
        {
            // キー接頭辞を設定している場合、呼び出し側にはそれを除いた形で返す。
            var scoped = _keyPrefix.Length > 0 ? _keyPrefix + "/" : string.Empty;
            if (scoped.Length > 0 && !key.StartsWith(scoped, StringComparison.Ordinal)) continue;
            yield return new IncompleteUpload(key[scoped.Length..], uploadId, initiated);
        }
    }

    public void AbortIncompleteUpload(IncompleteUpload upload)
        => _client.AbortMultipartUpload(ToObjectKey(upload.Key), upload.UploadId);

    public IEnumerable<StoredObject> List(string keyPrefix)
    {
        var prefix = ToObjectKey(keyPrefix);
        foreach (var (key, lastModified, size) in _client.ListObjects(prefix))
        {
            // キー接頭辞を設定している場合、呼び出し側にはそれを除いた形で返す。
            if (_keyPrefix.Length > 0)
            {
                var scoped = _keyPrefix + "/";
                if (!key.StartsWith(scoped, StringComparison.Ordinal)) continue;
                yield return new StoredObject(key[scoped.Length..], lastModified, size);
            }
            else
            {
                yield return new StoredObject(key, lastModified, size);
            }
        }
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

    public bool Exists(string key)
    {
        StorageKey.Validate(key);
        return _client.Exists(ToObjectKey(key));
    }

    public StoredObject? Stat(string key)
    {
        StorageKey.Validate(key);
        var head = _client.Head(ToObjectKey(key));
        return head is null ? null : new StoredObject(key, head.Value.LastModified, head.Value.Size);
    }

    /// <summary>
    /// 見たときのままなら削除する。削除の直前に HEAD で読み直し、更新日時が
    /// <paramref name="expected"/> と一致する場合だけ消す。
    /// <para>
    /// <b>ここは不可分にできない。</b> S3 の条件付き削除は <c>If-Match</c> に ETag を
    /// 取るが、ETag は内容の関数である。置き場所を内容から決めている以上、同じキーの
    /// 内容は常に同じで、別の PC が送り直しても ETag は変わらない。つまり
    /// 「送り直された直後か」を ETag では区別できず、防ぎたい競合にはまったく効かない。
    /// 汎用バケットの <c>DeleteObject</c> には更新日時を条件にする手段も無い。
    /// </para>
    /// <para>
    /// できるのは削除の直前に読み直すところまでで、そこから DELETE までの一瞬は残る。
    /// 幅は 1 往復ぶんで、猶予期間 (既定 7 日) に対しては無視できる。ここに入った場合も、
    /// 次の Push が実体の欠落を見つけて送り直すため自然に回復する。
    /// </para>
    /// <para>
    /// なお同期フォルダモードでは事情が違う。あちらで読めるのは同期クライアントが
    /// 手元へ持ってきた写しなので、残る幅は伝播遅延そのものになる
    /// (<see cref="LocalFolderSyncStorage.TryDelete"/>)。
    /// </para>
    /// </summary>
    public bool TryDelete(StoredObject expected)
    {
        StorageKey.Validate(expected.Key);

        var current = _client.Head(ToObjectKey(expected.Key));
        if (current is null) return true;
        if (current.Value.LastModified != expected.LastModified) return false;

        // 回収の削除は再送しない。届いたのに応答だけ失われた場合、再送までの間に
        // 別の PC が送り直していると 2 度目がそれを消す。
        _client.DeleteObject(ToObjectKey(expected.Key), retryTransientFailures: false);
        return true;
    }

    public IManifestWatcher CreateManifestWatcher()
        => new PollingManifestWatcher(this, ManifestPollInterval, _watcherLogger);

    /// <summary>
    /// 同期先を実際に使えるかを確かめる。設定画面や CLI の接続テストから呼ぶ。
    /// <para>
    /// 読み取りだけでは足りない。読み取り専用の認証情報でも manifest の取得は
    /// 通ってしまい、設定を保存した後の最初の Push で初めて失敗する。
    /// 検査用のオブジェクトを書いて消し、書き込みと削除の権限まで確認する。
    /// </para>
    /// </summary>
    public void VerifyAccess()
    {
        // 認証、バケットの存在、読み取り権限。
        LoadManifest();

        // 一覧の権限。S3 では s3:GetObject と s3:ListBucket を別々に許可できるため、
        // 取得だけ通る認証情報がここまで素通りする。回収 (storage gc) は
        // ListObjectsV2 を必ず呼ぶので、確認しないと設定は保存できても実行時に
        // 403 となり、孤児を片付ける唯一の手段が使えない。
        // 1 ページ目を取れれば十分なので、1 件だけ引いて打ち切る。
        _ = _client.ListObjects(ToObjectKey(BlobKeys.Prefix)).Take(1).ToList();

        // 書き込みと削除の権限。名前が衝突しないよう GUID を使い、後始末する。
        //
        // 削除の失敗も接続テストの失敗として扱う。Push は実データを消さないが、
        // 参照されなくなった実体の回収 (storage gc) が削除を行うため、
        // s3:DeleteObject を欠く認証情報では設定を保存できても回収が失敗し続ける。
        // 消し残した検査用オブジェクトはここでは回収できないが、設定が保存されない
        // 以上、利用者は権限を直して再度試すことになる。
        var probeKey = ToObjectKey($"_access-check/{Guid.NewGuid():N}");
        _client.PutBytes(probeKey, ProbePayload, "application/octet-stream", conditionHeaders: null);
        try
        {
            _client.DeleteObject(probeKey);
        }
        catch (SyncStorageException ex)
        {
            _logger.LogWarning(ex, "検査用オブジェクトの削除に失敗しました: {Key}", probeKey);
            throw new SyncStorageException(
                "同期先のオブジェクトを削除できません。参照されなくなった実体の回収 " +
                $"(storage gc) に削除の権限 (s3:DeleteObject) が必要です。({ex.Message})", ex);
        }
    }

    private bool Put(string objectKey, byte[] payload, IReadOnlyList<KeyValuePair<string, string>>? conditions)
        => _client.PutBytes(objectKey, payload, "application/json", conditions) == S3PutOutcome.Success;

    private string ToObjectKey(string key)
        => _keyPrefix.Length == 0 ? key : _keyPrefix + "/" + key;

    /// <summary>
    /// 同期先の識別に使うエンドポイントの正規形。スキーム、ホスト、既定でない
    /// ポート、サブパスまでを含める。
    /// </summary>
    private static string BuildEndpointIdentity(string serviceUrl)
    {
        var uri = new Uri(serviceUrl.Trim(), UriKind.Absolute);
        var port = uri.IsDefaultPort
            ? string.Empty
            : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        return $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{port}{uri.AbsolutePath.TrimEnd('/')}";
    }

    /// <summary>
    /// 一時ファイルへ書き出してから、Commit でアップロードする。
    /// SQLite の VACUUM INTO はローカルのファイルにしか書けないため、
    /// オブジェクトストレージへは一段挟む必要がある。
    /// </summary>
    private sealed class StagedObject : IStagedUpload
    {
        private readonly S3SyncStorage _storage;

        public StagedObject(S3SyncStorage storage, string stagingPath)
        {
            _storage = storage;
            LocalPath = stagingPath;
        }

        public string LocalPath { get; }

        public void Commit(string key) => _storage.Upload(LocalPath, key);

        public void Dispose()
        {
            if (File.Exists(LocalPath))
            {
                try { File.Delete(LocalPath); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
