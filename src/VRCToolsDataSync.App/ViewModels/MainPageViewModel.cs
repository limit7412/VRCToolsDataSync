using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VRCToolsDataSync.Core.Settings;
using VRCToolsDataSync.Core.Sync;

namespace VRCToolsDataSync_App.ViewModels;

public partial class MainPageViewModel : ObservableObject
{
    private readonly SyncRunner _runner;
    private SyncSettings _settings;

    public MainPageViewModel() : this(new SyncRunner()) { }

    public MainPageViewModel(SyncRunner runner)
    {
        _runner = runner;
        _settings = _runner.LoadSettings();
        MachineName = _settings.MachineName;
        CloudFolderPath = _settings.CloudFolderPath;
        SyncVrcx = _settings.SyncVrcx;
        SyncFriendConnect = _settings.SyncFriendConnect;
        RefreshStatusSummaries();
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
    public partial string VrcxStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FriendConnectStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public ObservableCollection<string> LogEntries { get; } = new();

    public event Func<ConflictPrompt, Task<ConflictChoice>>? ConflictRequested;

    [RelayCommand]
    private void SaveSettings()
    {
        _settings.MachineName = string.IsNullOrWhiteSpace(MachineName) ? Environment.MachineName : MachineName.Trim();
        _settings.CloudFolderPath = CloudFolderPath?.Trim() ?? string.Empty;
        _settings.SyncVrcx = SyncVrcx;
        _settings.SyncFriendConnect = SyncFriendConnect;
        _runner.SaveSettings(_settings);
        AppendLog("設定を保存しました");
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
