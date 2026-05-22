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
    // Start / Stop / UpdateSettings のライフサイクル系操作を直列化する。
    // App.OnLaunched 内で Coordinator.Start が Task.Run で非同期に走るように
    // なったため、起動直後にユーザが「設定を保存」(UpdateSettings → Stop+Start)
    // するとレースして _bindings が二重に並ぶ可能性がある。
    private readonly object _lifecycleLock = new();
    // 進行中の HandleProcessExited タスクをここに記録し、Stop / 終了時に
    // join + 「どのツールが Push 完了したか」を把握できるようにする。
    // ShutdownSyncOrchestrator は AutoPush で既に Push 済みのツールを
    // 二重 Push しないために使う。
    private readonly object _inFlightLock = new();
    private readonly List<InFlightPush> _inFlightPushes = new();
    private CloudWatcher? _cloudWatcher;
    private SyncSettings _settings;
    private bool _started;
    // Start/Stop の世代を表す CancellationTokenSource。
    // Stop / UpdateSettings で Cancel し、Start で再生成する。
    // ProcessExited から切り離された HandleProcessExited タスクは、
    // この token を見て grace sleep / Push 直前で打ち切る。
    private CancellationTokenSource _generationCts = new();

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
        lock (_lifecycleLock) { StartCore(); }
    }

    private void StartCore()
    {
        if (_started) return;
        if (!_settings.AutoSyncEnabled) return;
        if (string.IsNullOrWhiteSpace(_settings.CloudFolderPath) || !Directory.Exists(_settings.CloudFolderPath))
        {
            _logger.LogInformation("AutoSync 起動スキップ: クラウドフォルダ未設定");
            return;
        }

        // 直前の Stop でキャンセル済みの可能性があるので、新しい世代用に張り直す。
        if (_generationCts.IsCancellationRequested)
        {
            _generationCts.Dispose();
            _generationCts = new CancellationTokenSource();
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
        lock (_lifecycleLock) { StopCore(); }
    }

    private void StopCore()
    {
        if (!_started) return;

        // 切り離された HandleProcessExited タスクを中断するため、
        // Watcher 破棄より先に Cancel する。
        try { _generationCts.Cancel(); } catch { /* best-effort */ }

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
        // Start / Stop と同じ lock を取って、未完了の Start と並走しないようにする。
        // 内部で StopCore / StartCore を呼ぶことで再入を避ける (同じ lock を二重取得しない)。
        lock (_lifecycleLock)
        {
            _settings = settings;
            StopCore();
            StartCore();
        }
    }

    /// <summary>
    /// Watcher 構成を変えずに、Coordinator が保持する settings 参照
    /// だけを差し替える。手動同期で ToolState が更新された後に呼んで、
    /// 続く自動 Push が古い LastPulledVersion を使わないようにする。
    /// </summary>
    public void RefreshSettings(SyncSettings settings)
    {
        _settings = settings;
    }

    private ToolBinding CreateBinding(
        string toolKey,
        string displayName,
        IReadOnlyList<string> processNames,
        Func<ISyncService> serviceFactory)
    {
        var watcher = new ProcessWatcher(processNames);
        var binding = new ToolBinding(toolKey, displayName, watcher, serviceFactory);
        // 現世代の CancellationToken をキャプチャしてタスクに渡す。Stop / UpdateSettings
        // で世代が切り替わると、それより前にキューに入ったタスクはこの token で中断される。
        var token = _generationCts.Token;
        watcher.ProcessExited += _processName =>
        {
            // 進行中の AutoPush を ToolKey 付きで記録し、Stop / 終了シーケンスから
            // 待ち合わせ + どのツールが Push 完了したかを把握できるようにする。
            var entry = new InFlightPush(binding.ToolKey);
            entry.Task = Task.Run(() =>
            {
                var pushed = HandleProcessExited(binding, token);
                entry.Pushed = pushed;
            });
            lock (_inFlightLock) { _inFlightPushes.Add(entry); }
            entry.Task.ContinueWith(_ =>
            {
                lock (_inFlightLock) { _inFlightPushes.Remove(entry); }
            }, TaskScheduler.Default);
        };
        return binding;
    }

    /// <summary>
    /// Stop の Cancel 直後に呼んで、進行中の AutoPush タスクが終わるまで待つ。
    /// 終了シーケンス (ShutdownSyncOrchestrator) との二重 Push を防ぐ。
    /// 戻り値: Completed=true なら全 AutoPush 完了、false ならタイムアウト。
    /// PushedToolKeys には、待機中に「実際に Push まで成功したツール」の ToolKey が入る。
    /// 呼び出し元はこの集合に含まれるツールについて Shutdown Push をスキップすることで、
    /// AutoPush と Shutdown Push の二重 Push (= 無駄な version インクリメント) を回避できる。
    /// </summary>
    public async Task<WaitForInFlightPushResult> WaitForInFlightPushAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        InFlightPush[] snapshot;
        lock (_inFlightLock) { snapshot = _inFlightPushes.ToArray(); }
        if (snapshot.Length == 0) return new WaitForInFlightPushResult(true, Array.Empty<string>());
        _logger.LogInformation("AutoPush in-flight: waiting count={Count}", snapshot.Length);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var allDone = Task.WhenAll(snapshot.Select(e => e.Task));
        var delay = Task.Delay(Timeout.Infinite, cts.Token);
        Task completed;
        try
        {
            completed = await Task.WhenAny(allDone, delay).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 通常 Task.WhenAny は例外を投げないが、念のため。
            return new WaitForInFlightPushResult(false, Array.Empty<string>());
        }
        // allDone が先に完了 = 全 AutoPush 完了。delay が先に完了 = タイムアウト。
        var pushedKeys = snapshot
            .Where(e => e.Pushed)
            .Select(e => e.ToolKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new WaitForInFlightPushResult(completed == allDone, pushedKeys);
    }

    private sealed class InFlightPush
    {
        public string ToolKey { get; }
        public Task Task { get; set; } = Task.CompletedTask;
        public bool Pushed { get; set; }
        public InFlightPush(string toolKey) { ToolKey = toolKey; }
    }

    /// <summary>
    /// プロセス終了検知後の AutoPush 本体。Push まで成功したら true を返す。
    /// 戻り値は <see cref="InFlightPush"/> に記録され、<see cref="WaitForInFlightPushAsync"/>
    /// を経由して呼び出し元 (Shutdown シーケンス) に「直近 AutoPush で Push 完了したツール」
    /// として伝わる。これでShutdownSyncOrchestrator が同じツールを二重 Push しないようにする。
    /// </summary>
    private bool HandleProcessExited(ToolBinding binding, CancellationToken token)
    {
        // プロセス終了直後はファイル解放待ちで数秒置く (キャンセル対応)。
        try
        {
            Task.Delay(TimeSpan.FromSeconds(3), token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("AutoPush キャンセル (grace中) tool={Tool}", binding.ToolKey);
            return false;
        }

        // grace 中に Stop / UpdateSettings で世代が切れていたら中断。
        if (token.IsCancellationRequested)
        {
            _logger.LogInformation("AutoPush キャンセル tool={Tool}", binding.ToolKey);
            return false;
        }

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
                // ロック取得後にも世代失効を確認 (長時間待ち後の二重Push防止)。
                if (token.IsCancellationRequested)
                {
                    _logger.LogInformation("AutoPush キャンセル (lock後) tool={Tool}", binding.ToolKey);
                    return false;
                }
                result = _runner.Push(service, _settings, _settings.CloudFolderPath, force: false);
            }
            switch (result.Outcome)
            {
                case SyncOutcome.Success:
                    _logger.LogInformation("AutoPush 完了 tool={Tool} version={Version}", binding.ToolKey, result.RemoteVersion);
                    AutoPushCompleted?.Invoke(pushEvent with { Result = result });
                    return true;
                case SyncOutcome.ConflictDetected:
                    _logger.LogInformation("AutoPush 競合 tool={Tool} remote={Remote}", binding.ToolKey, result.RemoteVersion);
                    AutoPushConflict?.Invoke(new AutoPushConflictEvent(
                        binding.ToolKey, binding.DisplayName,
                        result.RemoteVersion ?? 0,
                        result.LastPulledVersion ?? 0,
                        binding.ServiceFactory));
                    return false;
                default:
                    AutoPushCompleted?.Invoke(pushEvent with { Result = result });
                    return false;
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
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AutoPush 失敗 tool={Tool}", binding.ToolKey);
            AutoPushCompleted?.Invoke(pushEvent with { Result = new SyncResult
            {
                Outcome = SyncOutcome.Aborted,
                Message = ex.Message,
            }});
            return false;
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

    public void Dispose()
    {
        Stop();
        try { _generationCts.Dispose(); } catch { /* best-effort */ }
    }

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

/// <summary>
/// <see cref="AutoSyncCoordinator.WaitForInFlightPushAsync"/> の戻り値。
/// </summary>
/// <param name="Completed">true なら全 AutoPush が完了 / false ならタイムアウト。</param>
/// <param name="PushedToolKeys">待機中に「実際に Push まで成功したツール」の ToolKey 集合。
/// 呼び出し元 (Shutdown シーケンス) はこの集合のツールについて Shutdown Push を
/// スキップすることで二重 Push を回避できる。</param>
public sealed record WaitForInFlightPushResult(bool Completed, IReadOnlyList<string> PushedToolKeys);
