using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCToolsDataSync.Core.Settings;
using VRCToolsDataSync.Core.Sync;

namespace VRCToolsDataSync.Core.Watch;

/// <summary>
/// プロセス監視とクラウド側 manifest 監視を束ね、
/// 終了検知時の自動 Push と、リモート更新検知時の通知イベントを行う。
/// GUI からはイベント購読のみで利用する。
/// </summary>
public sealed class AutoSyncCoordinator : IDisposable
{
    private readonly SyncRunner _runner;
    private readonly ILogger<AutoSyncCoordinator> _logger;
    private readonly List<ToolBinding> _bindings = new();
    private readonly object _autoPushLock = new();
    private CloudWatcher? _cloudWatcher;
    private SyncSettings _settings;
    private bool _started;

    public event Action<AutoPushEvent>? AutoPushTriggered;
    public event Action<AutoPushEvent>? AutoPushCompleted;
    public event Action<AutoPushConflictEvent>? AutoPushConflict;
    public event Action<RemoteUpdateEvent>? RemoteUpdateAvailable;

    public AutoSyncCoordinator(SyncRunner runner, SyncSettings settings, ILogger<AutoSyncCoordinator>? logger = null)
    {
        _runner = runner;
        _settings = settings;
        _logger = logger ?? NullLogger<AutoSyncCoordinator>.Instance;
    }

    public void Start()
    {
        if (_started) return;
        if (!_settings.AutoSyncEnabled) return;
        if (string.IsNullOrWhiteSpace(_settings.CloudFolderPath) || !Directory.Exists(_settings.CloudFolderPath))
        {
            _logger.LogInformation("AutoSync 起動スキップ: クラウドフォルダ未設定");
            return;
        }

        if (_settings.SyncVrcx)
        {
            _bindings.Add(CreateBinding(
                VrcxSyncService.Key,
                "VRCX",
                ProcessGuard.VrcxProcessNames,
                () => new VrcxSyncService(logger: _runner.CreateLogger<VrcxSyncService>())));
        }

        if (_settings.SyncFriendConnect)
        {
            _bindings.Add(CreateBinding(
                FriendConnectSyncService.Key,
                "VRC Friend Connect",
                ProcessGuard.FriendConnectProcessNames,
                () => new FriendConnectSyncService(logger: _runner.CreateLogger<FriendConnectSyncService>())));
        }

        foreach (var binding in _bindings)
        {
            binding.Watcher.Start();
        }

        _cloudWatcher = new CloudWatcher(_settings.CloudFolderPath);
        _cloudWatcher.ManifestChanged += OnManifestChanged;
        _cloudWatcher.Start();

        _started = true;
        _logger.LogInformation("AutoSync 起動 bindings={Count}", _bindings.Count);
    }

    public void Stop()
    {
        if (!_started) return;
        foreach (var binding in _bindings)
        {
            binding.Watcher.Dispose();
        }
        _bindings.Clear();

        if (_cloudWatcher is not null)
        {
            _cloudWatcher.ManifestChanged -= OnManifestChanged;
            _cloudWatcher.Dispose();
            _cloudWatcher = null;
        }
        _started = false;
    }

    public void UpdateSettings(SyncSettings settings)
    {
        _settings = settings;
        Stop();
        Start();
    }

    private ToolBinding CreateBinding(
        string toolKey,
        string displayName,
        IReadOnlyList<string> processNames,
        Func<ISyncService> serviceFactory)
    {
        var watcher = new ProcessWatcher(processNames);
        var binding = new ToolBinding(toolKey, displayName, watcher, serviceFactory);
        watcher.ProcessExited += _ => Task.Run(() => HandleProcessExited(binding));
        return binding;
    }

    private void HandleProcessExited(ToolBinding binding)
    {
        // プロセス終了直後はファイル解放待ちで数秒置く
        Thread.Sleep(TimeSpan.FromSeconds(3));

        var pushEvent = new AutoPushEvent(binding.ToolKey, binding.DisplayName);
        AutoPushTriggered?.Invoke(pushEvent);
        _logger.LogInformation("AutoPush 開始 tool={Tool}", binding.ToolKey);

        try
        {
            var service = binding.ServiceFactory();
            // 自動 Push は VRCX と Friend Connect が近いタイミングで
            // 並行発火する可能性があるため、同一プロセス内では直列化して
            // manifest.json の read-modify-write 競合を回避する。
            SyncResult result;
            lock (_autoPushLock)
            {
                result = _runner.Push(service, _settings, _settings.CloudFolderPath, force: false);
            }
            switch (result.Outcome)
            {
                case SyncOutcome.Success:
                    _logger.LogInformation("AutoPush 完了 tool={Tool} version={Version}", binding.ToolKey, result.RemoteVersion);
                    AutoPushCompleted?.Invoke(pushEvent with { Result = result });
                    break;
                case SyncOutcome.ConflictDetected:
                    _logger.LogInformation("AutoPush 競合 tool={Tool} remote={Remote}", binding.ToolKey, result.RemoteVersion);
                    AutoPushConflict?.Invoke(new AutoPushConflictEvent(
                        binding.ToolKey, binding.DisplayName,
                        result.RemoteVersion ?? 0,
                        result.LastPulledVersion ?? 0,
                        binding.ServiceFactory));
                    break;
                default:
                    AutoPushCompleted?.Invoke(pushEvent with { Result = result });
                    break;
            }
        }
        catch (RunningProcessException ex)
        {
            // 終了検知直後にユーザが再起動した等
            _logger.LogInformation(ex, "AutoPush 中止: プロセス再起動");
            AutoPushCompleted?.Invoke(pushEvent with { Result = new SyncResult
            {
                Outcome = SyncOutcome.Aborted,
                Message = ex.Message,
            }});
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AutoPush 失敗 tool={Tool}", binding.ToolKey);
            AutoPushCompleted?.Invoke(pushEvent with { Result = new SyncResult
            {
                Outcome = SyncOutcome.Aborted,
                Message = ex.Message,
            }});
        }
    }

    private void OnManifestChanged(SyncManifest manifest)
    {
        foreach (var binding in _bindings)
        {
            if (!manifest.Tools.TryGetValue(binding.ToolKey, out var entry)) continue;
            var localState = _settings.ToolState.GetValueOrDefault(binding.ToolKey);
            var localVersion = localState?.LastPulledVersion ?? 0;
            // 自分が最後に push した分も version は進むので、自分のマシン名で更新された
            // entry は無視する。リモートからの新着のみ通知する。
            if (entry.Version > localVersion && !string.Equals(entry.MachineName, _settings.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "リモート更新検知 tool={Tool} remote={Remote} local={Local} by={Machine}",
                    binding.ToolKey, entry.Version, localVersion, entry.MachineName);
                RemoteUpdateAvailable?.Invoke(new RemoteUpdateEvent(
                    binding.ToolKey, binding.DisplayName,
                    entry.Version, localVersion, entry.MachineName,
                    binding.ServiceFactory));
            }
        }
    }

    public void Dispose() => Stop();

    private sealed record ToolBinding(
        string ToolKey,
        string DisplayName,
        ProcessWatcher Watcher,
        Func<ISyncService> ServiceFactory);
}

public sealed record AutoPushEvent(string ToolKey, string DisplayName)
{
    public SyncResult? Result { get; init; }
}

public sealed record AutoPushConflictEvent(
    string ToolKey,
    string DisplayName,
    long RemoteVersion,
    long LastPulledVersion,
    Func<ISyncService> ServiceFactory);

public sealed record RemoteUpdateEvent(
    string ToolKey,
    string DisplayName,
    long RemoteVersion,
    long LocalVersion,
    string MachineName,
    Func<ISyncService> ServiceFactory);
