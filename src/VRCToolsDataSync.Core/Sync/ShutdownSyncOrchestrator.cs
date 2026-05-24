using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCToolsDataSync.Core.Settings;

namespace VRCToolsDataSync.Core.Sync;

public enum ShutdownSyncStepKind
{
    // 「ツールが終了しているか」のチェック関連。
    // 「能動停止」の意味は持たない (VRCX/VRC Friend Connect は WM_CLOSE では
    // トレイ常駐に最小化されるだけでプロセスが死なないため、対応を諦めた)。
    StopStarted,        // 終了待機開始 (WaitForToolsToExit があるとき)
    StopSucceeded,      // ツールが終了済み (Push 可能)
    StopTimedOut,       // 終了待機がタイムアウト (起動中のまま)
    StopSkipped,        // 待たない経路で起動中だった (Push 不可)
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
    /// <summary>
    /// 起動中のツールが自然終了するのを待つタイムアウト。
    /// null = 待たない (Tray「終了」経路。即時チェックして起動中なら Push スキップ)。
    /// 値あり = 待つ (SessionEnding 経路。ログオフ/シャットダウン時にツールが順に
    /// 終了していくのを待ち、終わったツールから Push する)。
    /// </summary>
    public TimeSpan? WaitForToolsToExit { get; init; }

    /// <summary>
    /// 終了直前の Push をすべてスキップする。AutoPush 待機がタイムアウトしたなど、
    /// 並走 Push による manifest 競合リスクが高い経路で使う。
    /// </summary>
    public bool SkipPush { get; init; }

    /// <summary>
    /// 直近 AutoPush で Push 完了済みのツール ToolKey 集合。
    /// 呼び出し元は <see cref="AutoSyncCoordinator.WaitForInFlightPushAsync"/> から
    /// 受け取った値をそのまま渡す。この集合のツールについては Shutdown Push を
    /// スキップすることで二重 Push (= 無駄な version インクリメント) を回避する。
    /// </summary>
    public IReadOnlyCollection<string> SkipPushForTools { get; init; } = Array.Empty<string>();
}

/// <summary>
/// VRCToolsDataSync 終了時の流れを取りまとめる。
/// <para>
/// 旧設計では WM_CLOSE でツールを能動停止していたが、VRCX / VRC Friend Connect は
/// WM_CLOSE では「トレイに最小化」されるだけでプロセスが死なないため、自前で停止
/// することは諦めた。現設計では:
/// <list type="bullet">
/// <item><description>Tray「終了」: 各ツールが既に終了済みかチェック。終了済みのツールだけ Push する。</description></item>
/// <item><description>SessionEnding (Windows ログオフ/シャットダウン): タイムアウトまで自然終了を待ち、終わったツールから Push する。</description></item>
/// </list>
/// 各ステップは <see cref="ShutdownSyncStep"/> として返す。
/// </para>
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

        // (1) 各ツールについて「終了済みか」をチェックする。SessionEnding 経路
        //     (WaitForToolsToExit が指定されている) なら、最大そのタイムアウトまで
        //     自然終了を待つ。Tray「終了」経路 (null) なら即時判定。
        //
        //     ただし SyncEnabled=false のツールは Push しないので終了を待つ意味が
        //     無い。むしろ待ってしまうと SessionEnding 経路で同期 OFF のツール
        //     (例: SyncFriendConnect=false のとき常駐中の Friend Connect) に
        //     最大 WaitForToolsToExit (=15s) 引っ張られ、その間に外側の OnSessionEnding
        //     全体タイムアウトに達して、同期 ON ツール (例: VRCX) の終了時 Push が
        //     キャンセルで巻き添えになる。同期 OFF ツールは exitChecks に乗せず、
        //     即時に PushSkipped("同期が無効化されています") として処理する。
        var toolDefs = EnumerateTools(settings, _runner).ToArray();
        var exitChecks = new List<Task<(ToolDefinition def, bool exited)>>();
        foreach (var def in toolDefs)
        {
            if (!def.SyncEnabled)
            {
                continue;
            }
            steps.Add(new ShutdownSyncStep
            {
                ToolKey = def.Key,
                DisplayName = def.DisplayName,
                Kind = ShutdownSyncStepKind.StopStarted,
            });
            exitChecks.Add(CheckExitedAsync(def, options.WaitForToolsToExit, ct));
        }

        var exitResults = await Task.WhenAll(exitChecks).ConfigureAwait(false);

        foreach (var (def, exited) in exitResults)
        {
            if (exited)
            {
                steps.Add(new ShutdownSyncStep
                {
                    ToolKey = def.Key,
                    DisplayName = def.DisplayName,
                    Kind = ShutdownSyncStepKind.StopSucceeded,
                });
            }
            else
            {
                steps.Add(new ShutdownSyncStep
                {
                    ToolKey = def.Key,
                    DisplayName = def.DisplayName,
                    Kind = options.WaitForToolsToExit is null
                        ? ShutdownSyncStepKind.StopSkipped
                        : ShutdownSyncStepKind.StopTimedOut,
                    Message = options.WaitForToolsToExit is null
                        ? "ツールが起動中なので Push をスキップします"
                        : "ツールの自然終了待ちがタイムアウト",
                });
            }
        }

        // (2) Push。CloudFolderPath が無ければ / 呼び出し元から SkipPush が来ていたら全件スキップ。
        //     ここでは toolDefs ベース (= 同期 OFF も含む) で PushSkipped を出す。
        //     UI/ログでは全ツールの結果が並ぶことになるが、同期 OFF ツールは Stop
        //     フェーズには登場しない (上の (1) で exitChecks に投入していないため)。
        if (!cloudAvailable || options.SkipPush)
        {
            var reason = options.SkipPush
                ? "呼び出し元の指示で Push をスキップ"
                : "OneDrive フォルダ未設定";
            _logger.LogInformation("ShutdownSync push skipped: {Reason}", reason);
            foreach (var def in toolDefs)
            {
                steps.Add(new ShutdownSyncStep
                {
                    ToolKey = def.Key,
                    DisplayName = def.DisplayName,
                    Kind = ShutdownSyncStepKind.PushSkipped,
                    Message = def.SyncEnabled ? reason : "同期が無効化されています",
                });
            }
            return steps;
        }

        // 同期 OFF のツールは exitResults に乗っていないので、ここで先に
        // PushSkipped("同期が無効化されています") を出してから、同期 ON ツールの
        // 通常 Push 処理に進む。
        foreach (var def in toolDefs)
        {
            if (def.SyncEnabled) continue;
            steps.Add(new ShutdownSyncStep
            {
                ToolKey = def.Key,
                DisplayName = def.DisplayName,
                Kind = ShutdownSyncStepKind.PushSkipped,
                Message = "同期が無効化されています",
            });
        }

        foreach (var (def, exited) in exitResults)
        {
            ct.ThrowIfCancellationRequested();

            // ツールが起動中なら Push は不可能。SQLite が握られているので
            // RunningProcessException で失敗するだけ。スキップして次回起動時に手動 Push してもらう。
            if (!exited)
            {
                steps.Add(new ShutdownSyncStep
                {
                    ToolKey = def.Key,
                    DisplayName = def.DisplayName,
                    Kind = ShutdownSyncStepKind.PushSkipped,
                    Message = "ツールが起動中のため Push できません",
                });
                continue;
            }

            // 直近の AutoPush で既に Push 済みのツールはスキップ。
            // (ユーザがツールを閉じた → AutoPush が version=N で Push → 直後にトレイ「終了」
            //  → Shutdown Push で同じ内容を version=N+1 で再 Push、という無駄な
            //  version インクリメントを防ぐ。)
            if (options.SkipPushForTools.Contains(def.Key))
            {
                steps.Add(new ShutdownSyncStep
                {
                    ToolKey = def.Key,
                    DisplayName = def.DisplayName,
                    Kind = ShutdownSyncStepKind.PushSkipped,
                    Message = "直近の自動 Push で既に Push 済み",
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

    /// <summary>
    /// ツールが終了しているかチェックする。
    /// <paramref name="waitTimeout"/> が null なら即時、値ありなら最大そのタイムアウトまで自然終了を待つ。
    /// </summary>
    private async Task<(ToolDefinition def, bool exited)> CheckExitedAsync(
        ToolDefinition def,
        TimeSpan? waitTimeout,
        CancellationToken ct)
    {
        if (waitTimeout is null)
        {
            // 即時判定: 起動中なら false。
            var running = ProcessGuard.FindRunning(def.ProcessNames);
            return (def, exited: running.Count == 0);
        }

        // 自然終了を待つ。WaitUntilExitedAsync は起動中ツールに対しては
        // Process ハンドルを掴んで WaitForExitAsync で待つ実装。
        var exited = await _processController.WaitUntilExitedAsync(def.ProcessNames, waitTimeout.Value, ct).ConfigureAwait(false);
        return (def, exited);
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
