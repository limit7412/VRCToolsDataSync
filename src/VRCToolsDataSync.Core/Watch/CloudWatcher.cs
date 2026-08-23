using VRCToolsDataSync.Core.Storage;
using VRCToolsDataSync.Core.Sync;

namespace VRCToolsDataSync.Core.Watch;

/// <summary>
/// ローカル同期フォルダの manifest.json をファイル監視で追いかける。
/// OneDrive などのクライアントが他 PC の更新を書き戻したタイミングで発火する。
/// </summary>
public sealed class CloudWatcher : IManifestWatcher
{
    private readonly LocalFolderSyncStorage _storage;
    private readonly System.Timers.Timer _debounceTimer;
    private FileSystemWatcher? _watcher;

    public event Action<SyncManifest>? ManifestChanged;

    public CloudWatcher(LocalFolderSyncStorage storage, TimeSpan? debounce = null)
    {
        _storage = storage;
        _debounceTimer = new System.Timers.Timer((debounce ?? TimeSpan.FromSeconds(2)).TotalMilliseconds)
        {
            AutoReset = false,
        };
        _debounceTimer.Elapsed += (_, _) => EmitManifestChanged();
    }

    public void Start()
    {
        if (_watcher is not null) return;
        if (!Directory.Exists(_storage.RootDirectory)) return;

        _watcher = new FileSystemWatcher(_storage.RootDirectory, ManifestStore.ManifestKey)
        {
            NotifyFilter = NotifyFilters.LastWrite
                         | NotifyFilters.FileName
                         | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void EmitManifestChanged()
    {
        try
        {
            var snapshot = _storage.LoadManifest();
            if (snapshot.Manifest.Tools.Count == 0) return;
            ManifestChanged?.Invoke(snapshot.Manifest);
        }
        catch
        {
            // 書き込み途中などで読めなかった場合は次のイベントを待つ
        }
    }

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileEvent;
            _watcher.Created -= OnFileEvent;
            _watcher.Renamed -= OnFileEvent;
            _watcher.Dispose();
            _watcher = null;
        }
        _debounceTimer.Stop();
        _debounceTimer.Dispose();
    }
}
