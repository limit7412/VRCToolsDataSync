using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCToolsDataSync.Core.Domain;
using VRCToolsDataSync.Core.Infra;

namespace VRCToolsDataSync.Core.UseCase;

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
    // 既に完了している AutoPush の ToolKey を保持する。
    // _inFlightPushes は完了 → ContinueWith で即削除されるため、
    // 「WaitForInFlightPushAsync を呼ぶ直前に完了した AutoPush」が snapshot から
    // 漏れて二重 Push の原因になっていた (issue: 直近完了 AutoPush のキー保持)。
    // 完了時はこの集合に ToolKey を追加し、Start 時に明示クリアする
    // (= 同一 Coordinator 世代の中だけ覚えていれば十分)。
    private readonly HashSet<string> _recentlyCompletedAutoPushes = new(StringComparer.Ordinal);
    private IManifestWatcher? _manifestWatcher;
    // 監視と自動 Push で使い回す同期先。Start で作り、Stop で捨てる。
    private ISyncStorage? _storage;
    private SyncSettings _settings;
    private bool _started;
    // Start/Stop の世代を表す CancellationTokenSource。
    // Stop / UpdateSettings で Cancel し、Start で再生成する。
    // ProcessExited から切り離された HandleProcessExited タスクは、
    // この token を見て grace sleep / Push 直前で打ち切る。
    private CancellationTokenSource _generationCts = new();
    // 検出状況を読むための、_bindings の写し。_lifecycleLock を取らずに読めるよう、
    // 中身を変えずに丸ごと差し替える形で持つ。読み手は GUI のスレッドにいるため、
    // 進行中の Start の裏で待たせたくない。
    private volatile IReadOnlyList<ToolBinding> _detectionSources = Array.Empty<ToolBinding>();

    public event Action<AutoPushEvent>? AutoPushTriggered;
    public event Action<AutoPushEvent>? AutoPushCompleted;
    public event Action<AutoPushConflictEvent>? AutoPushConflict;
    public event Action<RemoteUpdateEvent>? RemoteUpdateAvailable;
    /// <summary>
    /// 検出状況が変わったことを知らせる。<b>中身は持たない。</b>
    /// <para>
    /// 状態そのものは <see cref="GetProcessDetections"/> から読む。通知に状態を載せると、
    /// 錠を離してから届くまでの間に次の変化が起きた場合に古い方を届けうる。載せなければ、
    /// 遅れて届いた通知は現在の状態を読み直させるだけになり、順序が問題にならない。
    /// </para>
    /// </summary>
    public event Action? ProcessDetectionChanged;


    public AutoSyncCoordinator(SyncRunner runner, SyncSettings settings, ILogger<AutoSyncCoordinator>? logger = null)
    {
        _runner = runner;
        _settings = settings;
        _logger = logger ?? NullLogger<AutoSyncCoordinator>.Instance;
    }

    public void Start()
    {
        lock (_lifecycleLock) { StartCore(); }
        // 通知は錠の外で出す。購読側が何をするかはここからは分からず、錠を持ったまま
        // 呼ぶと、そのあいだ Stop や UpdateSettings が待たされる。
        ProcessDetectionChanged?.Invoke();
    }

    private void StartCore()
    {
        if (_started) return;
        if (!_settings.AutoSyncEnabled) return;

        ISyncStorage storage;
        try
        {
            storage = _runner.CreateStorage(_settings);
        }
        catch (SyncStorageException ex)
        {
            _logger.LogInformation("AutoSync 起動スキップ: {Reason}", ex.Message);
            return;
        }
        _storage = storage;

        // 直前の Stop でキャンセル済みの可能性があるので、新しい世代用に張り直す。
        if (_generationCts.IsCancellationRequested)
        {
            _generationCts.Dispose();
            _generationCts = new CancellationTokenSource();
        }

        // 新しい監視世代に入るので、前回までの「完了済み AutoPush」の記録は捨てる。
        // (前回 Coordinator が動いていた間に Push したものを今回の Shutdown でスキップ
        //  対象にすると、その後ユーザがツールを再起動して再編集していた場合に
        //  Push したい変更まで取りこぼす可能性があるため。)
        lock (_inFlightLock)
        {
            _recentlyCompletedAutoPushes.Clear();
        }

        foreach (var tool in ToolCatalog.All)
        {
            if (!tool.IsSyncEnabled(_settings)) continue;
            _bindings.Add(CreateBinding(tool));
        }

        foreach (var binding in _bindings)
        {
            binding.Watcher.Start();
        }
        RefreshDetectionSources();

        // 同期先が変更を知らせる仕組みを作る。ローカルフォルダはファイル監視、
        // S3 互換モードは manifest の定期確認になる。
        _manifestWatcher = storage.CreateManifestWatcher();
        _manifestWatcher.ManifestChanged += OnManifestChanged;
        _manifestWatcher.Start();

        _started = true;
        _logger.LogInformation(
            "AutoSync 起動 bindings={Count} target={Target}", _bindings.Count, storage.DisplayName);
    }

    public void Stop()
    {
        lock (_lifecycleLock) { StopCore(); }
        ProcessDetectionChanged?.Invoke();
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
        RefreshDetectionSources();

        if (_manifestWatcher is not null)
        {
            _manifestWatcher.ManifestChanged -= OnManifestChanged;
            _manifestWatcher.Dispose();
            _manifestWatcher = null;
        }
        _storage = null;
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
        // 通知は 1 回で足りる。読み直す側が見るのは Start を終えた後の状態なので、
        // Stop と Start で 2 回流しても同じものを 2 度読むだけになる。
        ProcessDetectionChanged?.Invoke();
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

    private ToolBinding CreateBinding(ToolDefinition tool)
    {
        var watcher = new ProcessWatcher(tool.ProcessNames);
        var binding = new ToolBinding(
            tool.Key, tool.DisplayName, watcher, () => tool.CreateService(_runner));
        // 検出状況の通知。どの候補が実際に当たっているかを GUI に出すために使う
        // (issue #11)。起動でも終了でも検出状況は変わるので、両方から流す。
        //
        // Stop は監視の終了を 2 秒までしか待たないので、待ちきれなかった走査の通知が
        // 停止後に届きうる。通知は状態を持たないため、その場合も読み直させるだけで
        // 済み、停止中の表示を古い状態で上書きすることにはならない。
        watcher.ProcessStarted += _ => ProcessDetectionChanged?.Invoke();
        watcher.ProcessExited += _ => ProcessDetectionChanged?.Invoke();
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
                lock (_inFlightLock)
                {
                    _inFlightPushes.Remove(entry);
                    // Push まで成功したものは「直近完了」集合にも残す。
                    // _inFlightPushes から消えた後でも WaitForInFlightPushAsync が
                    // 拾えるようにする (= 終了処理直前に完了した AutoPush の
                    // 二重 Push を Shutdown 側でスキップさせる)。
                    if (entry.Pushed) _recentlyCompletedAutoPushes.Add(entry.ToolKey);
                }
            }, TaskScheduler.Default);
        };
        return binding;
    }

    /// <summary>
    /// Stop の Cancel 直後に呼んで、進行中の AutoPush タスクが終わるまで待つ。
    /// 終了シーケンス (ShutdownSyncOrchestrator) との二重 Push を防ぐ。
    /// 戻り値: Completed=true なら全 AutoPush 完了、false ならタイムアウト。
    /// PushedToolKeys には、現在の Coordinator 世代で「実際に Push まで成功したツール」の
    /// ToolKey が入る (進行中タスクの完了 + これより前に完了済みの AutoPush の両方を含む)。
    /// 呼び出し元はこの集合に含まれるツールについて Shutdown Push をスキップすることで、
    /// AutoPush と Shutdown Push の二重 Push (= 無駄な version インクリメント) を回避できる。
    /// </summary>
    public async Task<WaitForInFlightPushResult> WaitForInFlightPushAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        InFlightPush[] snapshot;
        lock (_inFlightLock) { snapshot = _inFlightPushes.ToArray(); }
        bool completedOk = true;
        if (snapshot.Length > 0)
        {
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
                // 通常 Task.WhenAny は例外を投げないが、念のため。タイムアウト扱い。
                completed = delay;
            }
            // allDone が先に完了 = 全 AutoPush 完了。delay が先に完了 = タイムアウト。
            completedOk = completed == allDone;
        }
        // 「直近完了 AutoPush」集合を取り出して合流させる。snapshot に入っていた
        // タスクは ContinueWith でこの集合に追加されているはずで、加えて
        // WaitForInFlightPushAsync が呼ばれる前に既に完了して _inFlightPushes
        // から消えていた AutoPush もここから拾える。
        string[] pushedKeys;
        lock (_inFlightLock)
        {
            pushedKeys = _recentlyCompletedAutoPushes.ToArray();
        }
        return new WaitForInFlightPushResult(completedOk, pushedKeys);
    }

    private sealed class InFlightPush
    {
        public string ToolKey { get; }
        public Task Task { get; set; } = Task.CompletedTask;
        public bool Pushed { get; set; }
        public InFlightPush(string toolKey) { ToolKey = toolKey; }
    }

    /// <summary>
    /// いまの検出状況。ツールごとに 1 件返す。
    /// <para>
    /// 通知は「変わった」ことしか伝えないので、購読側はここから読む。<b>読むたびに
    /// 現在の状態が返る</b>ため、通知が遅れて届いても古い状態を表示することはない。
    /// </para>
    /// <para>
    /// <see cref="_lifecycleLock"/> は取らない。読み手は GUI のスレッドにいるので、
    /// 進行中の <see cref="Start"/> の裏で待たせたくない。代わりに
    /// <see cref="_detectionSources"/> を丸ごと差し替える形で読ませる。
    /// </para>
    /// </summary>
    public IReadOnlyList<ProcessDetectionEvent> GetProcessDetections()
    {
        var sources = _detectionSources;
        var detections = sources.Select(DetectionOf).ToList();
        detections.AddRange(ToolCatalog.All
            .Where(tool => !sources.Any(b => b.ToolKey == tool.Key))
            .Select(tool => NotWatching(tool.Key, tool.DisplayName)));
        return detections;
    }

    /// <summary>
    /// 検出状況を読む先を、いまの <see cref="_bindings"/> に合わせる。
    /// <see cref="_lifecycleLock"/> の中で呼ぶ。
    /// </summary>
    private void RefreshDetectionSources() => _detectionSources = _bindings.ToArray();

    /// <summary>監視中のツール 1 つぶんの検出状況。</summary>
    private static ProcessDetectionEvent DetectionOf(ToolBinding binding)
        => new(binding.ToolKey, binding.DisplayName, IsWatching: true, binding.Watcher.DetectedProcessNames);

    private static ProcessDetectionEvent NotWatching(string toolKey, string displayName)
        => new(toolKey, displayName, IsWatching: false, Array.Empty<string>());


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
            ISyncStorage storage;
            lock (_autoPushLock)
            {
                // ロック取得後にも世代失効を確認 (長時間待ち後の二重Push防止)。
                if (token.IsCancellationRequested)
                {
                    _logger.LogInformation("AutoPush キャンセル (lock後) tool={Tool}", binding.ToolKey);
                    return false;
                }
                // Start 時に作った同期先を使う。Stop と競合して null になっていたら
                // 世代が切れているので Push しない。
                var current = _storage;
                if (current is null)
                {
                    _logger.LogInformation("AutoPush キャンセル (同期先が解放済み) tool={Tool}", binding.ToolKey);
                    return false;
                }
                storage = current;
                result = _runner.Push(service, _settings, storage, force: false);
            }
            switch (result.Outcome)
            {
                case SyncOutcome.Success:
                    _logger.LogInformation("AutoPush 完了 tool={Tool} version={Version}", binding.ToolKey, result.RemoteVersion);
                    // Push の後始末として、参照が切れた実体の回収を試みる (issue #55)。
                    // この AutoPush タスクは Shutdown シーケンスが完了を待つので、
                    // ここで同期実行すると回収の長さがそのまま終了を遅らせる。
                    // バックグラウンドに切り離し、途中打ち切りは次回に任せる。
                    // (Stop 後に _storage が捨てられても、掴んだ参照はそのまま使える。
                    //  ISyncStorage はスレッドセーフで、破棄の手続きも持たない。)
                    _runner.CollectGarbageInBackground(storage);
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
        // 監視のイベントは Stop と競合しうる。世代が切れていたら何もしない。
        var storage = _storage;
        if (storage is null) return;

        foreach (var binding in _bindings)
        {
            if (!manifest.Tools.TryGetValue(binding.ToolKey, out var entry)) continue;
            // キーを直に引かず SyncRunner を通す。更新前の settings.json からの
            // 引き継ぎが効かないと、直後は履歴なし扱いになって
            // 「リモートが新しい」と誤通知してしまう。
            var localState = SyncRunner.FindToolState(_settings, storage, binding.ToolKey);
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

/// <summary>
/// ツール 1 つのプロセス検出状況。
/// <para>
/// 起動しているかどうかだけでなく、<b>どの名前で見つかったか</b>を載せる。
/// 実行ファイル名は配布のされ方で変わりうるため候補を複数持っており
/// (<see cref="Sync.ProcessGuard.FriendConnectProcessNames"/>)、どれも当たらない場合、
/// 利用者には「自動 Push が動かない」ことしか見えない。当たった名前を出すことで、
/// 候補に無い名前で配布されていることに気付けるようにする。
/// </para>
/// </summary>
/// <param name="ToolKey">ツールの識別子。</param>
/// <param name="DisplayName">表示名。</param>
/// <param name="IsWatching">
/// 監視しているかどうか。自動同期を切っている間や停止中は false になる。
/// 「監視していない」と「動いていない」は別物なので、表示で混ぜないために分けて持つ。
/// </param>
/// <param name="DetectedProcessNames">実体が見つかった名前。</param>
public sealed record ProcessDetectionEvent(
    string ToolKey,
    string DisplayName,
    bool IsWatching,
    IReadOnlyList<string> DetectedProcessNames)
{
    /// <summary>1 つでも見つかっていれば起動中と見なす。</summary>
    public bool IsRunning => DetectedProcessNames.Count > 0;
}

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
