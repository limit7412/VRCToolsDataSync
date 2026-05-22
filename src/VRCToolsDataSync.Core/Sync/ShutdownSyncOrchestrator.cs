using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCToolsDataSync.Core.Settings;

namespace VRCToolsDataSync.Core.Sync;

public enum ShutdownSyncStepKind
{
    StopStarted,
    StopSucceeded,
    StopTimedOut,
    StopSkipped,
    PushStarted,
    PushSucceeded,
    PushFailed,
    PushSkipped,
}

public sealed class ShutdownSyncStep
{
    public required string ToolKey { get; init; }
    public required string DisplayName { get; init; }
    public required ShutdownSyncStepKind Kind { get; init; }
    public string? Message { get; init; }
    public SyncResult? PushResult { get; init; }
    public IReadOnlyList<string>? StillRunningProcessNames { get; init; }
}

public sealed class ShutdownSyncOptions
{
    // ツールごとの停止タイムアウト。WM_CLOSE 送信後ここまで待つ。
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(15);
    // 「同期して終了」「同期して再起動」のようなユーザ明示操作では設定の
    // StopOnAppExit を上書きしてすべてのツールを停止する。
    public bool ForceStopAllSyncedTools { get; init; }
    // SessionEnding のように猶予が短い経路で呼ぶ場合、Push まで含めて
    // 早めに切り上げたい。CancellationToken でキャンセル可能。
}

/// <summary>
/// VRCToolsDataSync 終了時の「ツール停止 → Push」の流れを取りまとめる。
/// 各ステップを <see cref="ShutdownSyncStep"/> として返す。
/// </summary>
public sealed class ShutdownSyncOrchestrator
{
    private readonly SyncRunner _runner;
    private readonly ToolProcessController _processController;
    private readonly ILogger<ShutdownSyncOrchestrator> _logger;

    public ShutdownSyncOrchestrator(
        SyncRunner runner,
        ToolProcessController? processController = null,
        ILogger<ShutdownSyncOrchestrator>? logger = null)
    {
        _runner = runner;
        _processController = processController ?? new ToolProcessController();
        _logger = logger ?? NullLogger<ShutdownSyncOrchestrator>.Instance;
    }

    public async Task<IReadOnlyList<ShutdownSyncStep>> RunAsync(
        SyncSettings settings,
        ShutdownSyncOptions options,
        CancellationToken ct = default)
    {
        var steps = new List<ShutdownSyncStep>();
        var cloud = settings.CloudFolderPath?.Trim() ?? string.Empty;
        var cloudAvailable = !string.IsNullOrEmpty(cloud) && Directory.Exists(cloud);

        // (1) 全ツールの停止を並列で依頼。停止待ちの間は他ツールも止まるので並列が妥当。
        var toolDefs = EnumerateTools(settings, _runner).ToArray();
        var stopTasks = new List<Task<(ToolDefinition def, ToolStopResult? result, bool skipped)>>();
        foreach (var def in toolDefs)
        {
            var shouldStop = options.ForceStopAllSyncedTools
                ? def.SyncEnabled
                : (settings.Launch.GetValueOrDefault(def.Key)?.StopOnAppExit ?? false);
            if (!shouldStop)
            {
                steps.Add(new ShutdownSyncStep
                {
                    ToolKey = def.Key,
                    DisplayName = def.DisplayName,
                    Kind = ShutdownSyncStepKind.StopSkipped,
                    Message = "停止設定が無効",
                });
                stopTasks.Add(Task.FromResult<(ToolDefinition, ToolStopResult?, bool)>((def, null, true)));
                continue;
            }

            steps.Add(new ShutdownSyncStep
            {
                ToolKey = def.Key,
                DisplayName = def.DisplayName,
                Kind = ShutdownSyncStepKind.StopStarted,
            });
            stopTasks.Add(StopOneAsync(def, options.StopTimeout, ct));
        }

        var stopResults = await Task.WhenAll(stopTasks).ConfigureAwait(false);

        foreach (var (def, result, skipped) in stopResults)
        {
            if (skipped || result is null) continue;
            steps.Add(new ShutdownSyncStep
            {
                ToolKey = def.Key,
                DisplayName = def.DisplayName,
                Kind = result.Outcome == ToolStopOutcome.TimedOut
                    ? ShutdownSyncStepKind.StopTimedOut
                    : ShutdownSyncStepKind.StopSucceeded,
                StillRunningProcessNames = result.StillRunningProcessNames,
            });
        }

        // (2) Push。CloudFolderPath が無ければスキップ。
        if (!cloudAvailable)
        {
            _logger.LogInformation("ShutdownSync push skipped: cloud folder not configured");
            foreach (var def in toolDefs)
            {
                steps.Add(new ShutdownSyncStep
                {
                    ToolKey = def.Key,
                    DisplayName = def.DisplayName,
                    Kind = ShutdownSyncStepKind.PushSkipped,
                    Message = "OneDrive フォルダ未設定",
                });
            }
            return steps;
        }

        foreach (var (def, stopResult, skipped) in stopResults)
        {
            ct.ThrowIfCancellationRequested();

            // 同期 OFF のツールは Push しない。
            if (!def.SyncEnabled)
            {
                steps.Add(new ShutdownSyncStep
                {
                    ToolKey = def.Key,
                    DisplayName = def.DisplayName,
                    Kind = ShutdownSyncStepKind.PushSkipped,
                    Message = "同期が無効化されています",
                });
                continue;
            }

            // 停止対象だったのにタイムアウトした → SQLite が解放されていない可能性が
            // 高い。Push しても RunningProcessException で失敗するだけなので skip。
            // ユーザは GUI で強制終了するか諦めるかを選んで再度同期する。
            if (!skipped && stopResult?.Outcome == ToolStopOutcome.TimedOut)
            {
                steps.Add(new ShutdownSyncStep
                {
                    ToolKey = def.Key,
                    DisplayName = def.DisplayName,
                    Kind = ShutdownSyncStepKind.PushSkipped,
                    Message = "ツールの停止待ちがタイムアウト",
                });
                continue;
            }

            steps.Add(new ShutdownSyncStep
            {
                ToolKey = def.Key,
                DisplayName = def.DisplayName,
                Kind = ShutdownSyncStepKind.PushStarted,
            });

            try
            {
                var pushResult = await Task.Run(() => _runner.Push(def.ServiceFactory(), settings, cloud, force: false), ct).ConfigureAwait(false);
                steps.Add(new ShutdownSyncStep
                {
                    ToolKey = def.Key,
                    DisplayName = def.DisplayName,
                    Kind = pushResult.Outcome == SyncOutcome.Success ? ShutdownSyncStepKind.PushSucceeded : ShutdownSyncStepKind.PushFailed,
                    Message = pushResult.Message,
                    PushResult = pushResult,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shutdown push failed: {Tool}", def.DisplayName);
                steps.Add(new ShutdownSyncStep
                {
                    ToolKey = def.Key,
                    DisplayName = def.DisplayName,
                    Kind = ShutdownSyncStepKind.PushFailed,
                    Message = ex.Message,
                });
            }
        }

        return steps;
    }

    private async Task<(ToolDefinition def, ToolStopResult? result, bool skipped)> StopOneAsync(
        ToolDefinition def,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var result = await _processController.RequestStopAsync(def.ProcessNames, timeout, ct).ConfigureAwait(false);
        return (def, result, false);
    }

    private static IEnumerable<ToolDefinition> EnumerateTools(SyncSettings settings, SyncRunner runner)
    {
        yield return new ToolDefinition
        {
            Key = VrcxSyncService.Key,
            DisplayName = "VRCX",
            SyncEnabled = settings.SyncVrcx,
            ServiceFactory = () => new VrcxSyncService(logger: runner.CreateLogger<VrcxSyncService>()),
            ProcessNames = ProcessGuard.VrcxProcessNames,
        };
        yield return new ToolDefinition
        {
            Key = FriendConnectSyncService.Key,
            DisplayName = "VRC Friend Connect",
            SyncEnabled = settings.SyncFriendConnect,
            ServiceFactory = () => new FriendConnectSyncService(logger: runner.CreateLogger<FriendConnectSyncService>()),
            ProcessNames = ProcessGuard.FriendConnectProcessNames,
        };
    }

    private sealed class ToolDefinition
    {
        public required string Key { get; init; }
        public required string DisplayName { get; init; }
        public required bool SyncEnabled { get; init; }
        public required Func<ISyncService> ServiceFactory { get; init; }
        public required IReadOnlyList<string> ProcessNames { get; init; }
    }
}
