using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCToolsDataSync.Core.Paths;
using VRCToolsDataSync.Core.Settings;

namespace VRCToolsDataSync.Core.Sync;

public enum StartupSyncStepKind
{
    PullStarted,
    PullSucceeded,
    PullFailed,
    PullSkipped,
    LaunchAttempted,
    LaunchSkipped,
}

public sealed class StartupSyncStep
{
    public required string ToolKey { get; init; }
    public required string DisplayName { get; init; }
    public required StartupSyncStepKind Kind { get; init; }
    public string? Message { get; init; }
    public SyncResult? PullResult { get; init; }
    public ToolLaunchResult? LaunchResult { get; init; }
}

/// <summary>
/// VRCToolsDataSync 起動時の「同期 → ツール起動」の流れを取りまとめる。
/// 各ステップを <see cref="StartupSyncStep"/> として返し、UI 側でログ表示やトースト通知を行う。
/// </summary>
public sealed class StartupSyncOrchestrator
{
    private readonly SyncRunner _runner;
    private readonly ToolProcessController _processController;
    private readonly ILogger<StartupSyncOrchestrator> _logger;

    public StartupSyncOrchestrator(
        SyncRunner runner,
        ToolProcessController? processController = null,
        ILogger<StartupSyncOrchestrator>? logger = null)
    {
        _runner = runner;
        _processController = processController ?? new ToolProcessController();
        _logger = logger ?? NullLogger<StartupSyncOrchestrator>.Instance;
    }

    public IReadOnlyList<StartupSyncStep> Run(SyncSettings settings)
    {
        var steps = new List<StartupSyncStep>();
        var cloud = settings.CloudFolderPath?.Trim() ?? string.Empty;

        // CloudFolderPath が未設定の場合は何もしない。GUI で設定を促す。
        // (StartupRegistration の自動起動経路で誤って Push 等を走らせないため)
        if (string.IsNullOrEmpty(cloud) || !Directory.Exists(cloud))
        {
            _logger.LogInformation("StartupSync skipped: cloud folder not configured ('{Path}')", cloud);
            return steps;
        }

        foreach (var def in EnumerateTools(settings))
        {
            // (1) Pull
            var pullResult = TryPull(def, settings, cloud, steps);

            // (2) Launch
            // Pull 失敗時もユーザの設定通りに Launch を試みる。Pull が失敗していても
            // ツール起動自体は止めない (ローカルにデータが残っている可能性があるため)。
            TryLaunch(def, settings, steps, pullResult);
        }

        return steps;
    }

    private SyncResult? TryPull(ToolDefinition def, SyncSettings settings, string cloud, List<StartupSyncStep> steps)
    {
        if (!def.SyncEnabled)
        {
            steps.Add(new StartupSyncStep
            {
                ToolKey = def.Key,
                DisplayName = def.DisplayName,
                Kind = StartupSyncStepKind.PullSkipped,
                Message = "同期が無効化されています",
            });
            return null;
        }

        steps.Add(new StartupSyncStep
        {
            ToolKey = def.Key,
            DisplayName = def.DisplayName,
            Kind = StartupSyncStepKind.PullStarted,
        });

        try
        {
            var result = _runner.Pull(def.ServiceFactory(), settings, cloud, skipBackup: false);
            steps.Add(new StartupSyncStep
            {
                ToolKey = def.Key,
                DisplayName = def.DisplayName,
                Kind = result.Outcome == SyncOutcome.Success ? StartupSyncStepKind.PullSucceeded : StartupSyncStepKind.PullFailed,
                Message = result.Message,
                PullResult = result,
            });
            return result;
        }
        catch (RunningProcessException ex)
        {
            // ツールが既に動いている場合 Pull できない。GUI 側で「先にツールを終了
            // してください」のメッセージを出すのに使う。Launch は skip しない -
            // 既に動いてるなら次の TryLaunch 内で AlreadyRunning として処理される。
            _logger.LogWarning("Pull skipped because tool is running: {Tool}", def.DisplayName);
            steps.Add(new StartupSyncStep
            {
                ToolKey = def.Key,
                DisplayName = def.DisplayName,
                Kind = StartupSyncStepKind.PullFailed,
                Message = ex.Message,
            });
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pull failed: {Tool}", def.DisplayName);
            steps.Add(new StartupSyncStep
            {
                ToolKey = def.Key,
                DisplayName = def.DisplayName,
                Kind = StartupSyncStepKind.PullFailed,
                Message = ex.Message,
            });
            return null;
        }
    }

    private void TryLaunch(ToolDefinition def, SyncSettings settings, List<StartupSyncStep> steps, SyncResult? pullResult)
    {
        var config = settings.Launch.GetValueOrDefault(def.Key);
        if (config is null || !config.LaunchOnAppStart)
        {
            steps.Add(new StartupSyncStep
            {
                ToolKey = def.Key,
                DisplayName = def.DisplayName,
                Kind = StartupSyncStepKind.LaunchSkipped,
                Message = "起動設定が無効",
            });
            return;
        }

        var launchResult = _processController.TryLaunch(
            def.Key,
            config,
            def.ProcessNames,
            def.FindExecutable());

        steps.Add(new StartupSyncStep
        {
            ToolKey = def.Key,
            DisplayName = def.DisplayName,
            Kind = StartupSyncStepKind.LaunchAttempted,
            LaunchResult = launchResult,
            Message = launchResult.Error,
        });
    }

    private static IEnumerable<ToolDefinition> EnumerateTools(SyncSettings settings)
    {
        yield return new ToolDefinition
        {
            Key = VrcxSyncService.Key,
            DisplayName = "VRCX",
            SyncEnabled = settings.SyncVrcx,
            ServiceFactory = () => new VrcxSyncService(),
            ProcessNames = ProcessGuard.VrcxProcessNames,
            FindExecutable = VrcxPaths.TryFindExecutable,
        };
        yield return new ToolDefinition
        {
            Key = FriendConnectSyncService.Key,
            DisplayName = "VRC Friend Connect",
            SyncEnabled = settings.SyncFriendConnect,
            ServiceFactory = () => new FriendConnectSyncService(),
            ProcessNames = ProcessGuard.FriendConnectProcessNames,
            FindExecutable = FriendConnectPaths.TryFindExecutable,
        };
    }

    private sealed class ToolDefinition
    {
        public required string Key { get; init; }
        public required string DisplayName { get; init; }
        public required bool SyncEnabled { get; init; }
        public required Func<ISyncService> ServiceFactory { get; init; }
        public required IReadOnlyList<string> ProcessNames { get; init; }
        public required Func<string?> FindExecutable { get; init; }
    }
}
