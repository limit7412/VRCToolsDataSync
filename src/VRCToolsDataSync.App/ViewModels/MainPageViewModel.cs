using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public MainPageViewModel() : this(new SyncRunner()) { }

    public MainPageViewModel(SyncRunner runner)
    {
        _runner = runner;
        _settings = _runner.LoadSettings();
        MachineName = _settings.MachineName;
        CloudFolderPath = _settings.CloudFolderPath;
        SyncVrcx = _settings.SyncVrcx;
        SyncFriendConnect = _settings.SyncFriendConnect;
        AutoSyncEnabled = _settings.AutoSyncEnabled;
        RefreshStatusSummaries();
        RefreshStartupState();
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
        _runner.SaveSettings(_settings);
        _coordinator?.UpdateSettings(_settings);
        AppendLog($"設定を保存しました (auto-sync={(_settings.AutoSyncEnabled ? "ON" : "OFF")})");
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
        // 設定が未保存だった場合のために、同期実行時にも保存を反映しておく
        if (_settings.CloudFolderPath != cloud)
        {
            _settings.CloudFolderPath = cloud;
            _runner.SaveSettings(_settings);
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
