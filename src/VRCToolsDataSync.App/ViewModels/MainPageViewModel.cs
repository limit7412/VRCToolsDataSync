using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VRCToolsDataSync.Core.Paths;
using VRCToolsDataSync.Core.Settings;
using VRCToolsDataSync.Core.Startup;
using VRCToolsDataSync.Core.Sync;
using VRCToolsDataSync.Core.Watch;

namespace VRCToolsDataSync_App.ViewModels;

public partial class MainPageViewModel : ObservableObject
{
    private readonly SyncRunner _runner;
    private SyncSettings _settings;
    private AutoSyncCoordinator? _coordinator;
    private Action<Action>? _uiDispatch;
    // ContentDialog は WinUI 上で同時に複数表示できないため、自動通知の
    // ダイアログ呼び出しはここでシリアライズして待ち合わせる。
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    // x:Bind 用の引数なしコンストラクタ。GUI ホストでは必ず App.Runner を共有して、
    // App 側で構成された FileLoggerProvider 経由でログが出るようにする。
    // (テスト等から MainPageViewModel 単体で生成したい場合は引数付きを使う)
    public MainPageViewModel() : this(App.Runner) { }

    public MainPageViewModel(SyncRunner runner)
    {
        _runner = runner;
        _settings = _runner.LoadSettings();
        MachineName = _settings.MachineName;
        CloudFolderPath = _settings.CloudFolderPath;
        SyncVrcx = _settings.SyncVrcx;
        SyncFriendConnect = _settings.SyncFriendConnect;
        AutoSyncEnabled = _settings.AutoSyncEnabled;
        LoadLaunchConfigToProperties();
        RefreshStatusSummaries();
        RefreshStartupState();
    }

    /// <summary>
    /// 起動時の SyncRunner.Run のログを GUI に流す。MainPage が VM を取得した
    /// 直後に呼び出される想定 (Window 構築前に走った StartupSyncOrchestrator
    /// のステップを GUI 上のログに反映するため)。
    /// </summary>
    public void IngestStartupSteps(IReadOnlyList<StartupSyncStep> steps, string logPrefix = "startup")
    {
        foreach (var step in steps)
        {
            switch (step.Kind)
            {
                case StartupSyncStepKind.PullStarted:
                    AppendLog($"[{logPrefix}] {step.DisplayName} Pull 開始...");
                    break;
                case StartupSyncStepKind.PullSucceeded:
                    AppendLog($"[{logPrefix}] {step.DisplayName} Pull 完了 v{step.PullResult?.RemoteVersion}");
                    break;
                case StartupSyncStepKind.PullFailed:
                    AppendLog($"[{logPrefix}] {step.DisplayName} Pull 失敗: {step.Message}");
                    break;
                case StartupSyncStepKind.PullSkipped:
                    AppendLog($"[{logPrefix}] {step.DisplayName} Pull スキップ: {step.Message}");
                    break;
                case StartupSyncStepKind.LaunchAttempted:
                    var outcome = step.LaunchResult?.Outcome;
                    var msg = outcome switch
                    {
                        ToolLaunchOutcome.Launched => "起動しました",
                        ToolLaunchOutcome.AlreadyRunning => "既に起動中",
                        ToolLaunchOutcome.ExecutableNotFound => $"実行ファイル未検出: {step.Message}",
                        ToolLaunchOutcome.LaunchFailed => $"起動失敗: {step.Message}",
                        _ => outcome?.ToString() ?? "不明",
                    };
                    AppendLog($"[{logPrefix}] {step.DisplayName} {msg}");
                    break;
                case StartupSyncStepKind.LaunchSkipped:
                    // 自動起動 OFF 時のログはノイズなので出さない。
                    break;
            }
        }
        RefreshSettingsAndStatus();
    }

    /// <summary>
    /// 起動同期 / 終了同期 / 再起動同期 のいずれかが Push/Pull を行った後に呼び、
    /// VM が保持する settings をディスクから読み直して Coordinator にも反映する。
    /// 古い ToolState で続く処理が動かないようにするための共通後処理。
    /// </summary>
    private void RefreshSettingsAndStatus()
    {
        _settings = _runner.LoadSettings();
        _coordinator?.RefreshSettings(_settings);
        RefreshStatusSummaries();
    }

    public void AttachCoordinator(AutoSyncCoordinator coordinator, Action<Action> uiDispatch)
    {
        _coordinator = coordinator;
        _uiDispatch = uiDispatch;
        coordinator.AutoPushTriggered += e => OnUi(() => AppendLog($"[auto] {e.DisplayName} 終了検知 → Push 開始"));
        coordinator.AutoPushCompleted += e => OnUi(() =>
        {
            if (e.Result is null) return;
            switch (e.Result.Outcome)
            {
                case SyncOutcome.Success:
                    AppendLog($"[auto] {e.DisplayName} Push 完了 v{e.Result.RemoteVersion}");
                    // Coordinator 側の SyncRunner.Push が settings を保存しているが、
                    // VM 側の _settings は別インスタンスのため、再読み込みしないと
                    // 古い LastPulledVersion で次回手動 Push が無駄なコンフリクトを起こす。
                    _settings = _runner.LoadSettings();
                    // VM と Coordinator で同じインスタンスを共有させて、これ以降は
                    // どちらの経路で更新しても両者が即座に最新を見るようにする。
                    coordinator.RefreshSettings(_settings);
                    break;
                case SyncOutcome.ConflictDetected:
                    AppendLog($"[auto] {e.DisplayName} Push 競合 v{e.Result.RemoteVersion}");
                    break;
                case SyncOutcome.Aborted:
                    AppendLog($"[auto] {e.DisplayName} Push 中止: {e.Result.Message}");
                    break;
                default:
                    AppendLog($"[auto] {e.DisplayName} Push: {e.Result.Outcome} {e.Result.Message}");
                    break;
            }
            RefreshStatusSummaries();
        });
        coordinator.AutoPushConflict += e => OnUi(() => _ = HandleAutoPushConflictAsync(e));
        coordinator.RemoteUpdateAvailable += e => OnUi(() => _ = HandleRemoteUpdateAsync(e));
    }

    private void OnUi(Action action)
    {
        if (_uiDispatch is null) action();
        else _uiDispatch(action);
    }

    [ObservableProperty]
    public partial string MachineName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CloudFolderPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool SyncVrcx { get; set; }

    [ObservableProperty]
    public partial bool SyncFriendConnect { get; set; }

    [ObservableProperty]
    public partial bool AutoSyncEnabled { get; set; }

    [ObservableProperty]
    public partial string VrcxStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FriendConnectStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool StartupRegistered { get; set; }

    [ObservableProperty]
    public partial string StartupStatus { get; set; } = string.Empty;

    // VRCX Launch 設定
    [ObservableProperty]
    public partial string VrcxExecutablePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool VrcxLaunchOnAppStart { get; set; }

    // VRC Friend Connect Launch 設定
    [ObservableProperty]
    public partial string FriendConnectExecutablePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool FriendConnectLaunchOnAppStart { get; set; }

    public ObservableCollection<string> LogEntries { get; } = new();

    public event Func<ConflictPrompt, Task<ConflictChoice>>? ConflictRequested;

    public event Func<RemoteUpdatePrompt, Task<RemoteUpdateChoice>>? RemoteUpdateRequested;

    public event Action? ShowWindowRequested;

    public event Action<string, string>? ToastRequested;

    [RelayCommand]
    private void SaveSettings()
    {
        _settings.MachineName = string.IsNullOrWhiteSpace(MachineName) ? Environment.MachineName : MachineName.Trim();
        _settings.CloudFolderPath = CloudFolderPath?.Trim() ?? string.Empty;
        _settings.SyncVrcx = SyncVrcx;
        _settings.SyncFriendConnect = SyncFriendConnect;
        _settings.AutoSyncEnabled = AutoSyncEnabled;
        ApplyLaunchPropertiesToSettings();
        _runner.SaveSettings(_settings);
        _coordinator?.UpdateSettings(_settings);
        AppendLog($"設定を保存しました (auto-sync={(_settings.AutoSyncEnabled ? "ON" : "OFF")})");
    }

    private void LoadLaunchConfigToProperties()
    {
        var vrcx = _settings.Launch.GetValueOrDefault(VrcxSyncService.Key) ?? new ToolLaunchConfig();
        VrcxExecutablePath = vrcx.ExecutablePath ?? string.Empty;
        VrcxLaunchOnAppStart = vrcx.LaunchOnAppStart;

        var fc = _settings.Launch.GetValueOrDefault(FriendConnectSyncService.Key) ?? new ToolLaunchConfig();
        FriendConnectExecutablePath = fc.ExecutablePath ?? string.Empty;
        FriendConnectLaunchOnAppStart = fc.LaunchOnAppStart;
    }

    private void ApplyLaunchPropertiesToSettings()
    {
        // 既存の Arguments は GUI で編集できないが、JSON を手編集して
        // 起動オプションを与えているユーザもいる。設定保存のたびに
        // 新規 ToolLaunchConfig を作ると Arguments が消えるので、
        // 既存 entry の値を引き継いでから上書きする。
        var existingVrcx = _settings.Launch.GetValueOrDefault(VrcxSyncService.Key);
        _settings.Launch[VrcxSyncService.Key] = new ToolLaunchConfig
        {
            ExecutablePath = string.IsNullOrWhiteSpace(VrcxExecutablePath) ? null : VrcxExecutablePath.Trim(),
            Arguments = existingVrcx?.Arguments,
            LaunchOnAppStart = VrcxLaunchOnAppStart,
        };
        var existingFc = _settings.Launch.GetValueOrDefault(FriendConnectSyncService.Key);
        _settings.Launch[FriendConnectSyncService.Key] = new ToolLaunchConfig
        {
            ExecutablePath = string.IsNullOrWhiteSpace(FriendConnectExecutablePath) ? null : FriendConnectExecutablePath.Trim(),
            Arguments = existingFc?.Arguments,
            LaunchOnAppStart = FriendConnectLaunchOnAppStart,
        };
    }

    /// <summary>
    /// トレイ「同期して起動」と MainPage の同名ボタンから呼ばれる。
    /// 同期 ON のツールを Pull → Launch する。既に動いていれば Launch は no-op。
    /// 未保存の CloudFolderPath が UI にあれば実行前に反映する (TryGetCloud)。
    /// </summary>
    [RelayCommand]
    private async Task SyncAndLaunchAsync()
    {
        if (IsBusy) return;
        // UI で編集中の CloudFolderPath を _settings へ反映してから走らせる。
        // RunPushAsync/RunPullAsync が TryGetCloud で行っているのと同じ前処理。
        if (!TryGetCloud(out _)) return;
        IsBusy = true;
        try
        {
            var orchestrator = new StartupSyncOrchestrator(
                _runner,
                logger: _runner.CreateLogger<StartupSyncOrchestrator>());
            var steps = await Task.Run(() => orchestrator.Run(_settings));
            IngestStartupSteps(steps);
        }
        catch (Exception ex)
        {
            AppendLog($"同期して起動 エラー: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// トレイ「同期して再起動」と MainPage の同名ボタンから呼ばれる。
    /// VRCToolsDataSync 側からツールを能動停止することは諦めたので、この操作は
    /// 「ユーザが既にツールを終了している前提で」: 終了済みツールだけ Push → Pull
    /// → 設定通りに Launch、という流れになる。起動中のツールについては Push も
    /// 行わずスキップする (Pull はツールが起動中だと RunningProcessException で失敗するため
    /// StartupSyncOrchestrator が内部で扱う)。
    /// AutoSync 監視中は ShutdownSyncOrchestrator と並走する AutoPush を避けるため
    /// Coordinator.Stop → WaitForInFlightPushAsync で AutoPush を吸い切ってから
    /// 走らせ、最後に Coordinator.Start で監視を復帰する。
    /// 未保存の CloudFolderPath が UI にあれば実行前に反映する。
    /// </summary>
    [RelayCommand]
    private async Task SyncAndRestartAsync()
    {
        if (IsBusy) return;
        if (!TryGetCloud(out _)) return;
        IsBusy = true;
        var coordinatorWasRunning = false;
        try
        {
            // (0) AutoSync 監視を止め、進行中の HandleProcessExited による
            //     自動 Push が ShutdownSyncOrchestrator と並走しないようにする。
            //     再起動完了後に Start() で復帰させる。
            IReadOnlyList<string> autoPushedTools = System.Array.Empty<string>();
            if (_coordinator is not null)
            {
                coordinatorWasRunning = true;
                _coordinator.Stop();
                var waitResult = await _coordinator.WaitForInFlightPushAsync(TimeSpan.FromSeconds(20));
                autoPushedTools = waitResult.PushedToolKeys;
                if (!waitResult.Completed)
                {
                    AppendLog("[restart] 進行中の自動 Push 待機がタイムアウト (manifest 競合の可能性あり)");
                }
            }

            // (1) 終了済みツールだけ Push (起動中はスキップ)。
            //     直近 AutoPush で Push 済みのツール (autoPushedTools) は二重 Push を避けてスキップ。
            var shutdown = new ShutdownSyncOrchestrator(
                _runner,
                logger: _runner.CreateLogger<ShutdownSyncOrchestrator>());
            var shutdownSteps = await shutdown.RunAsync(_settings, new ShutdownSyncOptions
            {
                SkipPushForTools = autoPushedTools,
                WaitForToolsToExit = null,
            });
            IngestShutdownSteps(shutdownSteps, logPrefix: "restart");

            // (2) Pull → Launch を回す
            var startup = new StartupSyncOrchestrator(
                _runner,
                logger: _runner.CreateLogger<StartupSyncOrchestrator>());
            var startupSteps = await Task.Run(() => startup.Run(_settings));
            IngestStartupSteps(startupSteps, logPrefix: "restart");
        }
        catch (Exception ex)
        {
            AppendLog($"同期して再起動 エラー: {ex.Message}");
        }
        finally
        {
            // (3) AutoSync を停止していたなら、Launch されたツールに対する
            //     監視を復帰させる。Start は AutoSyncEnabled=false 等で no-op。
            if (coordinatorWasRunning)
            {
                try { _coordinator?.Start(); }
                catch (Exception ex) { AppendLog($"[restart] Coordinator.Start 復帰失敗: {ex.Message}"); }
            }
            IsBusy = false;
        }
    }

    /// <summary>
    /// ShutdownSyncOrchestrator のステップを GUI ログに流し、最後に
    /// settings / Coordinator を最新化する。<paramref name="logPrefix"/> は
    /// 呼び出し元 (再起動 / 終了など) を区別するためのログ接頭辞。
    /// </summary>
    public void IngestShutdownSteps(IReadOnlyList<ShutdownSyncStep> steps, string logPrefix = "shutdown")
    {
        foreach (var step in steps)
        {
            var line = step.Kind switch
            {
                ShutdownSyncStepKind.StopStarted => $"[{logPrefix}] {step.DisplayName} 停止要求",
                ShutdownSyncStepKind.StopSucceeded => $"[{logPrefix}] {step.DisplayName} 停止完了",
                ShutdownSyncStepKind.StopTimedOut => $"[{logPrefix}] {step.DisplayName} 停止タイムアウト",
                ShutdownSyncStepKind.PushStarted => $"[{logPrefix}] {step.DisplayName} Push 開始",
                ShutdownSyncStepKind.PushSucceeded => $"[{logPrefix}] {step.DisplayName} Push 完了 v{step.PushResult?.RemoteVersion}",
                ShutdownSyncStepKind.PushFailed => $"[{logPrefix}] {step.DisplayName} Push 失敗: {step.Message}",
                ShutdownSyncStepKind.PushSkipped => $"[{logPrefix}] {step.DisplayName} Push スキップ: {step.Message}",
                _ => null,
            };
            if (line is not null) AppendLog(line);
        }
        RefreshSettingsAndStatus();
    }

    /// <summary>
    /// 「自動検出」ボタン用。実行ファイルパスを TryFindExecutable から埋める。
    /// 見つからなければ何もしない (ユーザに「参照…」ボタンを使わせる)。
    /// </summary>
    [RelayCommand]
    private void DetectVrcxExecutable()
    {
        var path = VrcxPaths.TryFindExecutable();
        if (path is null)
        {
            AppendLog("VRCX 実行ファイルを自動検出できませんでした。参照ボタンで指定してください。");
            return;
        }
        VrcxExecutablePath = path;
        AppendLog($"VRCX 実行ファイルを検出: {path}");
    }

    [RelayCommand]
    private void DetectFriendConnectExecutable()
    {
        var path = FriendConnectPaths.TryFindExecutable();
        if (path is null)
        {
            AppendLog("VRC Friend Connect 実行ファイルを自動検出できませんでした。参照ボタンで指定してください。");
            return;
        }
        FriendConnectExecutablePath = path;
        AppendLog($"VRC Friend Connect 実行ファイルを検出: {path}");
    }

    [RelayCommand]
    private void RegisterStartup()
    {
        var path = ResolveExecutablePath();
        if (path is null)
        {
            AppendLog("起動ファイルパスを特定できませんでした (Environment.ProcessPath が null)");
            return;
        }
        try
        {
            StartupRegistration.Register(path);
            AppendLog($"スタートアップに登録しました: {path}");
        }
        catch (Exception ex)
        {
            AppendLog($"スタートアップ登録に失敗: {ex.Message}");
        }
        RefreshStartupState();
    }

    [RelayCommand]
    private void UnregisterStartup()
    {
        try
        {
            StartupRegistration.Unregister();
            AppendLog("スタートアップから解除しました");
        }
        catch (Exception ex)
        {
            AppendLog($"スタートアップ解除に失敗: {ex.Message}");
        }
        RefreshStartupState();
    }

    private static string? ResolveExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) return null;
        // dotnet run の場合は dotnet.exe が返るため、その場合は登録に不向きであることを許容して
        // そのまま返す（実機配布の exe では VRCToolsDataSync.App.exe が返る）
        return path;
    }

    private void RefreshStartupState()
    {
        var registered = StartupRegistration.IsRegistered();
        StartupRegistered = registered;
        if (registered)
        {
            var cmd = StartupRegistration.GetRegisteredCommand();
            StartupStatus = $"登録済み: {cmd}";
        }
        else
        {
            StartupStatus = "未登録";
        }
    }

    [RelayCommand]
    private Task PushVrcx() => RunPushAsync("VRCX", new VrcxSyncService(logger: _runner.CreateLogger<VrcxSyncService>()));

    [RelayCommand]
    private Task PullVrcx() => RunPullAsync("VRCX", new VrcxSyncService(logger: _runner.CreateLogger<VrcxSyncService>()));

    [RelayCommand]
    private Task PushFriendConnect() => RunPushAsync("VRC Friend Connect", new FriendConnectSyncService(logger: _runner.CreateLogger<FriendConnectSyncService>()));

    [RelayCommand]
    private Task PullFriendConnect() => RunPullAsync("VRC Friend Connect", new FriendConnectSyncService(logger: _runner.CreateLogger<FriendConnectSyncService>()));

    private async Task RunPushAsync(string displayName, ISyncService service)
    {
        if (!TryGetCloud(out var cloud)) return;
        IsBusy = true;
        try
        {
            AppendLog($"{displayName} Push 開始...");
            var result = await Task.Run(() => _runner.Push(service, _settings, cloud, force: false));

            if (result.Outcome == SyncOutcome.ConflictDetected && ConflictRequested is not null)
            {
                var choice = await ConflictRequested.Invoke(new ConflictPrompt
                {
                    ToolDisplayName = displayName,
                    RemoteVersion = result.RemoteVersion ?? 0,
                    LastPulledVersion = result.LastPulledVersion ?? 0,
                });
                switch (choice)
                {
                    case ConflictChoice.ForceOverwrite:
                        AppendLog($"{displayName} 強制 Push 実行");
                        var forced = await Task.Run(() => _runner.Push(service, _settings, cloud, force: true));
                        ReportPushResult(displayName, forced);
                        break;
                    case ConflictChoice.PullFirst:
                        AppendLog($"{displayName} 先に Pull を実行");
                        var pulled = await Task.Run(() => _runner.Pull(service, _settings, cloud, skipBackup: false));
                        ReportPullResult(displayName, pulled);
                        break;
                    default:
                        AppendLog($"{displayName} Push をキャンセル");
                        break;
                }
            }
            else
            {
                ReportPushResult(displayName, result);
            }
        }
        catch (RunningProcessException ex)
        {
            AppendLog($"{displayName}: {ex.Message}");
        }
        catch (Exception ex)
        {
            AppendLog($"{displayName} エラー: {ex.Message}");
        }
        finally
        {
            RefreshStatusSummaries();
            IsBusy = false;
        }
    }

    private async Task RunPullAsync(string displayName, ISyncService service)
    {
        if (!TryGetCloud(out var cloud)) return;
        IsBusy = true;
        try
        {
            AppendLog($"{displayName} Pull 開始...");
            var result = await Task.Run(() => _runner.Pull(service, _settings, cloud, skipBackup: false));
            ReportPullResult(displayName, result);
        }
        catch (RunningProcessException ex)
        {
            AppendLog($"{displayName}: {ex.Message}");
        }
        catch (Exception ex)
        {
            AppendLog($"{displayName} エラー: {ex.Message}");
        }
        finally
        {
            RefreshStatusSummaries();
            IsBusy = false;
        }
    }

    private void ReportPushResult(string displayName, SyncResult result)
    {
        switch (result.Outcome)
        {
            case SyncOutcome.Success:
                AppendLog($"{displayName} Push 完了 version={result.RemoteVersion} files={result.AffectedFiles.Count}");
                // 常駐側 Coordinator が保持する settings の LastPulledVersion を
                // 同期させ、続く自動 Push が古いバージョンで不要な競合通知を
                // 起こさないようにする。
                _coordinator?.RefreshSettings(_settings);
                break;
            case SyncOutcome.SourceMissing:
                AppendLog($"{displayName} Push 中止: {result.Message}");
                break;
            case SyncOutcome.ConflictDetected:
                AppendLog($"{displayName} Push コンフリクト: remote v{result.RemoteVersion}, lastPulled v{result.LastPulledVersion}");
                break;
            default:
                AppendLog($"{displayName} Push: {result.Outcome} {result.Message}");
                break;
        }
    }

    private void ReportPullResult(string displayName, SyncResult result)
    {
        switch (result.Outcome)
        {
            case SyncOutcome.Success:
                AppendLog($"{displayName} Pull 完了 version={result.RemoteVersion} backup={result.BackupPath ?? "(none)"}");
                // 常駐側 Coordinator にも反映 (ReportPushResult と同様の理由)。
                _coordinator?.RefreshSettings(_settings);
                break;
            case SyncOutcome.NothingToDo:
            case SyncOutcome.SourceMissing:
                AppendLog($"{displayName} Pull: {result.Message}");
                break;
            default:
                AppendLog($"{displayName} Pull: {result.Outcome} {result.Message}");
                break;
        }
    }

    private async Task HandleAutoPushConflictAsync(AutoPushConflictEvent e)
    {
        AppendLog($"[auto] {e.DisplayName} Push 競合 remote=v{e.RemoteVersion} (要操作)");
        ToastRequested?.Invoke(
            $"{e.DisplayName}: 自動 Push が競合しました",
            $"リモート v{e.RemoteVersion} と未同期です。ウィンドウで操作を選択してください。");
        ShowWindowRequested?.Invoke();

        if (ConflictRequested is null) return;

        // ContentDialog は WinUI 上で同時に複数表示できないため、
        // 自動通知のダイアログは _dialogGate で1件ずつ処理する。
        await _dialogGate.WaitAsync();
        try
        {
            var choice = await ConflictRequested.Invoke(new ConflictPrompt
            {
                ToolDisplayName = e.DisplayName,
                RemoteVersion = e.RemoteVersion,
                LastPulledVersion = e.LastPulledVersion,
            });

            if (!TryGetCloud(out var cloud)) return;

            switch (choice)
            {
                case ConflictChoice.ForceOverwrite:
                    AppendLog($"[auto] {e.DisplayName} 強制 Push 実行");
                    var pushResult = await Task.Run(() => _runner.Push(e.ServiceFactory(), _settings, cloud, force: true));
                    ReportPushResult(e.DisplayName, pushResult);
                    break;
                case ConflictChoice.PullFirst:
                    AppendLog($"[auto] {e.DisplayName} 先に Pull を実行");
                    var pullResult = await Task.Run(() => _runner.Pull(e.ServiceFactory(), _settings, cloud, skipBackup: false));
                    ReportPullResult(e.DisplayName, pullResult);
                    break;
                default:
                    AppendLog($"[auto] {e.DisplayName} Push をキャンセル");
                    break;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[auto] {e.DisplayName} 競合処理エラー: {ex.Message}");
        }
        finally
        {
            RefreshStatusSummaries();
            _dialogGate.Release();
        }
    }

    private async Task HandleRemoteUpdateAsync(RemoteUpdateEvent e)
    {
        AppendLog($"[auto] {e.DisplayName} リモート更新 v{e.RemoteVersion} (by {e.MachineName})");
        ToastRequested?.Invoke(
            $"{e.DisplayName}: リモートに更新があります",
            $"{e.MachineName} が v{e.RemoteVersion} を Push しました。Pull しますか？");
        ShowWindowRequested?.Invoke();

        if (RemoteUpdateRequested is null) return;

        // ContentDialog の同時表示は不可。AutoPushConflict と共通の
        // _dialogGate で1件ずつ処理する。
        await _dialogGate.WaitAsync();
        try
        {
            var choice = await RemoteUpdateRequested.Invoke(new RemoteUpdatePrompt
            {
                ToolDisplayName = e.DisplayName,
                RemoteVersion = e.RemoteVersion,
                LocalVersion = e.LocalVersion,
                MachineName = e.MachineName,
            });

            if (choice != RemoteUpdateChoice.PullNow) return;
            if (!TryGetCloud(out var cloud)) return;

            AppendLog($"[auto] {e.DisplayName} Pull 実行");
            var pullResult = await Task.Run(() => _runner.Pull(e.ServiceFactory(), _settings, cloud, skipBackup: false));
            ReportPullResult(e.DisplayName, pullResult);
        }
        catch (Exception ex)
        {
            AppendLog($"[auto] {e.DisplayName} Pull エラー: {ex.Message}");
        }
        finally
        {
            RefreshStatusSummaries();
            _dialogGate.Release();
        }
    }

    private bool TryGetCloud(out string cloud)
    {
        cloud = CloudFolderPath?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(cloud))
        {
            AppendLog("OneDrive フォルダパスを指定して「設定を保存」してください");
            return false;
        }
        if (!System.IO.Directory.Exists(cloud))
        {
            AppendLog($"指定フォルダが存在しません: {cloud}");
            return false;
        }
        // 設定が未保存だった場合のために、同期実行時にも保存を反映しておく。
        // CloudFolderPath が変わった場合は常駐 Coordinator の CloudWatcher も
        // 旧パスを監視したままになってしまうので、UpdateSettings で再起動して
        // 新パスに張り替える (Watcher 再構築を伴う)。
        if (_settings.CloudFolderPath != cloud)
        {
            _settings.CloudFolderPath = cloud;
            _runner.SaveSettings(_settings);
            _coordinator?.UpdateSettings(_settings);
        }
        return true;
    }

    private void RefreshStatusSummaries()
    {
        VrcxStatus = FormatStatus(_settings.ToolState.GetValueOrDefault(VrcxSyncService.Key));
        FriendConnectStatus = FormatStatus(_settings.ToolState.GetValueOrDefault(FriendConnectSyncService.Key));
    }

    private static string FormatStatus(ToolSyncState? state)
    {
        if (state is null) return "未同期";
        var parts = new List<string>();
        if (state.LastPushedAt is { } pushed) parts.Add($"push v{state.LastPushedVersion} @ {pushed.LocalDateTime:yyyy-MM-dd HH:mm}");
        if (state.LastPulledAt is { } pulled) parts.Add($"pull v{state.LastPulledVersion} @ {pulled.LocalDateTime:yyyy-MM-dd HH:mm}");
        return parts.Count == 0 ? "未同期" : string.Join(" / ", parts);
    }

    private void AppendLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        LogEntries.Insert(0, line);
        while (LogEntries.Count > 200) LogEntries.RemoveAt(LogEntries.Count - 1);
    }
}

public sealed class ConflictPrompt
{
    public required string ToolDisplayName { get; init; }
    public required long RemoteVersion { get; init; }
    public required long LastPulledVersion { get; init; }
}

public enum ConflictChoice
{
    Cancel,
    PullFirst,
    ForceOverwrite,
}

public sealed class RemoteUpdatePrompt
{
    public required string ToolDisplayName { get; init; }
    public required long RemoteVersion { get; init; }
    public required long LocalVersion { get; init; }
    public required string MachineName { get; init; }
}

public enum RemoteUpdateChoice
{
    Later,
    PullNow,
}
