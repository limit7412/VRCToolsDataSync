using VRCToolsDataSync.Core.Sync;

namespace VRCToolsDataSync.Core.Watch;

public sealed class CloudWatcher : IDisposable
{
    private readonly string _cloudFolder;
    private readonly TimeSpan _debounce;
    private readonly System.Timers.Timer _debounceTimer;
    private FileSystemWatcher? _watcher;

    public event Action<SyncManifest>? ManifestChanged;

    public CloudWatcher(string cloudFolder, TimeSpan? debounce = null)
    {
        _cloudFolder = cloudFolder;
        _debounce = debounce ?? TimeSpan.FromSeconds(2);
        _debounceTimer = new System.Timers.Timer(_debounce.TotalMilliseconds)
        {
            AutoReset = false,
        };
        _debounceTimer.Elapsed += (_, _) => EmitManifestChanged();
    }

    public void Start()
    {
        if (_watcher is not null) return;
        if (!Directory.Exists(_cloudFolder)) return;

        _watcher = new FileSystemWatcher(_cloudFolder, "manifest.json")
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
            var store = new ManifestStore(_cloudFolder);
            if (!File.Exists(store.FilePath)) return;
            var manifest = store.Load();
            ManifestChanged?.Invoke(manifest);
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
