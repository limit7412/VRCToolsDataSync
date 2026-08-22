using System.Text.Json;
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

    private readonly string _rootDirectory;

    public LocalFolderSyncStorage(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new SyncStorageConfigurationException("同期フォルダのパスが未設定です");
        }
        // 末尾の区切りを落として揃える。"D:\\sync" と "D:\\sync\\" が
        // 別の同期先として扱われると、同期履歴が分かれてしまう。
        _rootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory.Trim()));
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
    /// フォルダが存在し、書き込めるかを確かめる。読み取り専用の場所や、
    /// 同期クライアントがまだ実体化していないプレースホルダを指しているケースを、
    /// 設定を保存する前に弾く。
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
            File.WriteAllBytes(probe, Array.Empty<byte>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SyncStorageConfigurationException(
                $"同期フォルダへ書き込めません: {_rootDirectory} ({ex.Message})");
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

    public IStagedUpload BeginUpload(string key)
    {
        var destination = StorageKey.ToLocalPath(_rootDirectory, key);
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        // 別プロセスの Push と一時ファイル名が衝突しないよう GUID を含める。
        return new StagedFile(destination + ".building-" + Guid.NewGuid().ToString("N"), destination);
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

    public IManifestWatcher CreateManifestWatcher() => new CloudWatcher(this);

    private sealed class StagedFile : IStagedUpload
    {
        private readonly string _destination;
        private bool _committed;

        public StagedFile(string stagingPath, string destination)
        {
            LocalPath = stagingPath;
            _destination = destination;
        }

        public string LocalPath { get; }

        public void Commit()
        {
            if (File.Exists(_destination))
            {
                File.Replace(LocalPath, _destination, destinationBackupFileName: null);
            }
            else
            {
                File.Move(LocalPath, _destination);
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
