using System.Text.Json;
using Microsoft.Extensions.Logging;
using VRCToolsDataSync.Core.Sync;
using VRCToolsDataSync.Core.Watch;

namespace VRCToolsDataSync.Core.Storage;

/// <summary>
/// ローカルの同期フォルダ (OneDrive など) を同期先として扱う。
/// 実 PC 間の転送はフォルダ同期クライアントに任せ、本実装はファイル操作だけを行う。
/// </summary>
public sealed class LocalFolderSyncStorage : ISyncStorage
{
    /// <summary>同期フォルダの同期履歴キーに付く接頭辞の先頭。</summary>
    public const string StateKeyScheme = "folder|";

    /// <summary>読み書きの権限を確認するために書いて読み戻す中身。</summary>
    private static readonly byte[] ProbePayload = "VRCToolsDataSync access check"u8.ToArray();

    private readonly string _rootDirectory;
    private readonly ILoggerFactory? _loggerFactory;

    public LocalFolderSyncStorage(string rootDirectory, ILoggerFactory? loggerFactory = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new SyncStorageConfigurationException("同期フォルダのパスが未設定です");
        }
        // 末尾の区切りを落として揃える。"D:\\sync" と "D:\\sync\\" が
        // 別の同期先として扱われると、同期履歴が分かれてしまう。
        _rootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory.Trim()));
        _loggerFactory = loggerFactory;
    }

    public string RootDirectory => _rootDirectory;

    public string DisplayName => _rootDirectory;

    // フォルダごとに同期履歴を分ける。同じ接頭辞を共有すると、別のフォルダへ
    // 切り替えたときに前のフォルダの LastPulledVersion がそのまま使われ、
    // 起動時 Pull が「ローカルが最新」として省略されたり、Push が競合を
    // 見逃して切り替え先を上書きしたりする。
    // 更新前の settings.json が持つツールキーだけの履歴は、同じフォルダを
    // 指している場合に限り SyncRunner が引き継ぐ。
    public string StateKeyPrefix => $"{StateKeyScheme}{_rootDirectory.ToLowerInvariant()}|";

    /// <summary>
    /// フォルダが存在し、同期に必要な読み書きと削除ができるかを確かめる。
    /// 読み取り専用の場所や、同期クライアントがまだ実体化していないプレースホルダを
    /// 指しているケースを、設定を保存する前に弾く。
    /// <para>
    /// NTFS では作成・読み取り・削除が別々の権限なので、3 つとも実際に試す。
    /// どれか 1 つでも欠けると、設定は保存できても後の同期が失敗する。
    /// </para>
    /// </summary>
    public void VerifyAccess()
    {
        if (!Directory.Exists(_rootDirectory))
        {
            throw new SyncStorageConfigurationException($"同期フォルダが存在しません: {_rootDirectory}");
        }
        var probe = Path.Combine(_rootDirectory, $".vrctoolsdatasync-access-check-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, ProbePayload);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SyncStorageConfigurationException(
                $"同期フォルダへ書き込めません: {_rootDirectory} ({ex.Message})");
        }

        // 読み取りの可否。NTFS では読み取りだけを拒否できるので、書き込みが通っても
        // manifest の読み込みや Pull が失敗する構成があり得る。書いた内容を読み戻して
        // 確かめる (S3 側は LoadManifest で読み取りを確認しており、それに合わせる)。
        string? readFailure = null;
        try
        {
            if (!File.ReadAllBytes(probe).AsSpan().SequenceEqual(ProbePayload))
            {
                readFailure = "書いた内容と異なる内容が読み出されました";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            readFailure = ex.Message;
        }
        if (readFailure is not null)
        {
            // 読めないだけで削除は通ることがあるので、後始末は試みる。
            try { File.Delete(probe); } catch { /* best-effort cleanup */ }
            throw new SyncStorageConfigurationException(
                $"同期フォルダから読み取れません: {_rootDirectory} ({readFailure})");
        }

        // 削除の可否も確かめる。NTFS では「ファイルの作成」と「削除」が別の権限
        // なので、作成だけ許して削除を拒むフォルダを作れる。Push はローカルから
        // 消えた任意ファイルの削除と古い note の回収で削除を行うため、そこを
        // 確認しないと設定を保存できても後の同期で失敗する。
        //
        // 失敗した場合、検査用ファイルは残る。設定が保存されない以上、利用者は
        // 権限を直して再度試すことになるので、S3 側と同じ扱いにしている。
        try
        {
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SyncStorageConfigurationException(
                $"同期フォルダのファイルを削除できません: {_rootDirectory} ({ex.Message})");
        }
    }

    public ManifestSnapshot LoadManifest()
    {
        var path = StorageKey.ToLocalPath(_rootDirectory, ManifestStore.ManifestKey);
        if (!File.Exists(path))
        {
            return new ManifestSnapshot(new SyncManifest(), null);
        }
        using var stream = File.OpenRead(path);
        var manifest = JsonSerializer.Deserialize<SyncManifest>(stream, ManifestJson.Options) ?? new SyncManifest();
        return new ManifestSnapshot(manifest, null);
    }

    /// <summary>
    /// manifest を書き出す。ローカルフォルダには保存時に内容を比較して弾く手段が
    /// 無いため <paramref name="expectedTag"/> は使わず、常に成功を返す。
    /// <para>
    /// つまりこのモードでは compare-and-swap が効かない。読み直しと書き込みの間に
    /// 別プロセスが更新した場合、その更新は失われる
    /// (<see cref="ManifestStore.UpdateToolEntry"/> の version 検査で、同じ tool に
    /// 対する競合は Push 側で検出できるが、書き込み自体は不可分ではない)。
    /// 別 PC との衝突はフォルダ同期クライアントの競合検出に委ねる。
    /// </para>
    /// </summary>
    public bool TrySaveManifest(SyncManifest manifest, string? expectedTag)
    {
        var path = StorageKey.ToLocalPath(_rootDirectory, ManifestStore.ManifestKey);
        Directory.CreateDirectory(_rootDirectory);

        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = File.Create(tmp))
            {
                JsonSerializer.Serialize(stream, manifest, ManifestJson.Options);
            }
            if (File.Exists(path))
            {
                File.Replace(tmp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
            }
        }
        return true;
    }

    public void Upload(string localPath, string key)
    {
        AtomicFile.Copy(localPath, StorageKey.ToLocalPath(_rootDirectory, key), overwrite: true);
    }

    public IStagedUpload BeginUpload()
    {
        // 置き場所は内容から決まるので、確定するのは Commit 時。ただし最後の移動を
        // 安価に済ませたいので、書き出し自体は確定先と同じフォルダで行う
        // (別ドライブを経由すると数百 MB のコピーになる)。
        var blobDirectory = StorageKey.ToLocalPath(_rootDirectory, BlobKeys.Prefix.TrimEnd('/'));
        Directory.CreateDirectory(blobDirectory);
        // 別プロセスの Push と一時ファイル名が衝突しないよう GUID を含める。
        var stagingPath = Path.Combine(blobDirectory, ".building-" + Guid.NewGuid().ToString("N"));
        return new StagedFile(stagingPath, this);
    }

    public IEnumerable<StoredObject> List(string keyPrefix)
    {
        var root = StorageKey.ToLocalPath(_rootDirectory, keyPrefix.TrimEnd('/'));
        if (!Directory.Exists(root)) yield break;

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            // 書き出し中の一時ファイルは回収の対象にしない。
            var name = Path.GetFileName(path);
            if (name.StartsWith(".building-", StringComparison.Ordinal)) continue;

            var info = new FileInfo(path);
            if (!info.Exists) continue;
            var relative = Path.GetRelativePath(_rootDirectory, path);
            yield return new StoredObject(
                StorageKey.FromRelativePath(relative),
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                info.Length);
        }
    }

    public bool TryDownload(string key, string localPath)
    {
        var source = StorageKey.ToLocalPath(_rootDirectory, key);
        if (!File.Exists(source))
        {
            return false;
        }
        AtomicFile.Copy(source, localPath, overwrite: true);
        return true;
    }

    public bool Exists(string key)
        => File.Exists(StorageKey.ToLocalPath(_rootDirectory, key));

    public void Delete(string key)
    {
        var path = StorageKey.ToLocalPath(_rootDirectory, key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public IManifestWatcher CreateManifestWatcher()
        => new CloudWatcher(this, logger: _loggerFactory?.CreateLogger<CloudWatcher>());

    private sealed class StagedFile : IStagedUpload
    {
        private readonly LocalFolderSyncStorage _storage;
        private bool _committed;

        public StagedFile(string stagingPath, LocalFolderSyncStorage storage)
        {
            LocalPath = stagingPath;
            _storage = storage;
        }

        public string LocalPath { get; }

        public void Commit(string key)
        {
            StorageKey.Validate(key);
            var destination = StorageKey.ToLocalPath(_storage._rootDirectory, key);
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            if (File.Exists(destination))
            {
                // 内容から決まるキーなので、既にあるなら中身は同じ。置き換えずに済ませる。
                File.Delete(LocalPath);
            }
            else
            {
                File.Move(LocalPath, destination);
            }
            _committed = true;
        }

        public void Dispose()
        {
            if (_committed) return;
            if (File.Exists(LocalPath))
            {
                try { File.Delete(LocalPath); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
