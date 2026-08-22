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
    private readonly string _rootDirectory;

    public LocalFolderSyncStorage(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new SyncStorageConfigurationException("同期フォルダのパスが未設定です");
        }
        _rootDirectory = Path.GetFullPath(rootDirectory.Trim());
    }

    public string RootDirectory => _rootDirectory;

    public string DisplayName => _rootDirectory;

    // 既存の settings.json はツールキーをそのまま ToolState のキーに使っている。
    // ローカルフォルダは接頭辞なしにして、更新後も同期履歴を引き継げるようにする。
    public string StateKeyPrefix => string.Empty;

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
    /// manifest を書き出す。ローカルフォルダには内容を比較して弾く手段が無いため
    /// <paramref name="expectedTag"/> は使わず、常に成功を返す。
    /// 同一 PC 内の並走は <see cref="ManifestStore.UpdateToolEntry"/> の
    /// 「保存直前の読み直し」で、別 PC との衝突はフォルダ同期クライアントの
    /// 競合検出でそれぞれ扱う。
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

    public void Delete(string key)
    {
        var path = StorageKey.ToLocalPath(_rootDirectory, key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public IReadOnlyList<string> List(string keyPrefix)
    {
        // キーの接頭辞はフォルダ境界とは限らないので、対象フォルダを列挙してから
        // 接頭辞で絞る。S3 互換実装の ListObjectsV2 と同じ意味になる。
        var separator = keyPrefix.LastIndexOf('/');
        var directoryKey = separator < 0 ? string.Empty : keyPrefix[..separator];
        var directory = directoryKey.Length == 0
            ? _rootDirectory
            : StorageKey.ToLocalPath(_rootDirectory, directoryKey);

        if (!Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var key = StorageKey.FromRelativePath(Path.GetRelativePath(_rootDirectory, file));
            if (key.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                results.Add(key);
            }
        }
        return results;
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
