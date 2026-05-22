using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCToolsDataSync.Core.Settings;

namespace VRCToolsDataSync.Core.Sync;

public enum ToolLaunchOutcome
{
    Launched,
    AlreadyRunning,
    ExecutableNotFound,
    LaunchFailed,
}

public sealed class ToolLaunchResult
{
    public required ToolLaunchOutcome Outcome { get; init; }
    public string? Error { get; init; }
}

public enum ToolStopOutcome
{
    NotRunning,
    StoppedGracefully,
    TimedOut,
}

public sealed class ToolStopResult
{
    public required ToolStopOutcome Outcome { get; init; }
    public IReadOnlyList<string> StillRunningProcessNames { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 各ツール (VRCX / VRC Friend Connect) のプロセスを起動・停止する。
/// テスト容易性のために <see cref="IProcessLauncher"/> 経由で実プロセスにアクセスする。
/// プロセス検索は <see cref="ProcessGuard"/> の候補リストを再利用する。
/// </summary>
public sealed class ToolProcessController
{
    private readonly IProcessLauncher _launcher;
    private readonly ILogger<ToolProcessController> _logger;

    public ToolProcessController(IProcessLauncher? launcher = null, ILogger<ToolProcessController>? logger = null)
    {
        _launcher = launcher ?? new DefaultProcessLauncher();
        _logger = logger ?? NullLogger<ToolProcessController>.Instance;
    }

    /// <summary>
    /// 指定された <paramref name="config"/> でツールを起動する。
    /// 既に <paramref name="processNames"/> のいずれかが動作中なら <see cref="ToolLaunchOutcome.AlreadyRunning"/>。
    /// 実行ファイルが解決できない場合は <see cref="ToolLaunchOutcome.ExecutableNotFound"/>。
    /// </summary>
    public ToolLaunchResult TryLaunch(
        string toolKey,
        ToolLaunchConfig config,
        IReadOnlyList<string> processNames,
        string? fallbackExecutablePath)
    {
        var running = _launcher.FindRunning(processNames);
        if (running.Count > 0)
        {
            _logger.LogInformation("Tool {Tool} already running ({Processes}); skip launch", toolKey, string.Join(",", running));
            return new ToolLaunchResult { Outcome = ToolLaunchOutcome.AlreadyRunning };
        }

        var exe = !string.IsNullOrWhiteSpace(config.ExecutablePath) ? config.ExecutablePath : fallbackExecutablePath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            _logger.LogWarning("Tool {Tool} executable not found (configured='{Configured}', fallback='{Fallback}')", toolKey, config.ExecutablePath, fallbackExecutablePath);
            return new ToolLaunchResult
            {
                Outcome = ToolLaunchOutcome.ExecutableNotFound,
                Error = $"実行ファイルが見つかりません: {exe ?? "(未設定)"}",
            };
        }

        try
        {
            _launcher.Start(exe, config.Arguments);
            _logger.LogInformation("Tool {Tool} launched: {Exe}", toolKey, exe);
            return new ToolLaunchResult { Outcome = ToolLaunchOutcome.Launched };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool {Tool} launch failed: {Exe}", toolKey, exe);
            return new ToolLaunchResult
            {
                Outcome = ToolLaunchOutcome.LaunchFailed,
                Error = ex.Message,
            };
        }
    }

    /// <summary>
    /// <paramref name="processNames"/> のいずれかにマッチするプロセスへ WM_CLOSE を送って終了を要求し、
    /// <paramref name="timeout"/> まで待機する。タイムアウト時は Kill せず TimedOut を返す。
    /// Kill するかどうかは呼び出し側の判断 (ユーザ確認等)。
    /// </summary>
    public async Task<ToolStopResult> RequestStopAsync(
        IReadOnlyList<string> processNames,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var initial = _launcher.GetProcesses(processNames);
        if (initial.Count == 0)
        {
            return new ToolStopResult { Outcome = ToolStopOutcome.NotRunning };
        }

        try
        {
            foreach (var p in initial)
            {
                try
                {
                    if (!p.HasExited && p.MainWindowHandle != IntPtr.Zero)
                    {
                        p.CloseMainWindow();
                    }
                    else if (!p.HasExited)
                    {
                        // メインウィンドウが無い (バックグラウンド) 場合は CloseMainWindow が
                        // 効かないので何もしない。WM_CLOSE 送信失敗時は呼び出し側で Kill 判断。
                        _logger.LogDebug("Process {Pid} has no MainWindow; CloseMainWindow skipped", p.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "CloseMainWindow failed for pid={Pid}", p.Id);
                }
            }

            // initial で掴んだ Process ハンドルそのものに対して WaitForExitAsync を回す。
            // ProcessGuard.FindRunning のポーリングだとループのたびに Process を取得し
            // ハンドルリークが起こる + 200ms 粒度になる。WaitForExitAsync は完了通知ベース。
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var waitTasks = new List<Task>(initial.Count);
            foreach (var p in initial)
            {
                if (p.HasExited) continue;
                waitTasks.Add(WaitForExitSafeAsync(p, cts.Token));
            }
            if (waitTasks.Count > 0)
            {
                try
                {
                    await Task.WhenAll(waitTasks).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // CancelAfter によるタイムアウト。下で TimedOut として返す。
                }
            }

            // initial の各 HasExited で最終判定する。生きていれば名前を返す。
            var stillRunning = new List<string>();
            foreach (var p in initial)
            {
                if (!p.HasExited)
                {
                    var name = SafeProcessName(p);
                    if (name is not null && !stillRunning.Contains(name)) stillRunning.Add(name);
                }
            }
            return new ToolStopResult
            {
                Outcome = stillRunning.Count == 0 ? ToolStopOutcome.StoppedGracefully : ToolStopOutcome.TimedOut,
                StillRunningProcessNames = stillRunning,
            };
        }
        finally
        {
            foreach (var p in initial)
            {
                try { p.Dispose(); } catch { /* best-effort */ }
            }
        }
    }

    private static async Task WaitForExitSafeAsync(Process p, CancellationToken ct)
    {
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // 既に終了済み等の状態異常は無視。
        }
    }

    private static string? SafeProcessName(Process p)
    {
        try { return p.ProcessName; }
        catch { return null; }
    }

    /// <summary>
    /// <paramref name="processNames"/> がすべて終了するまで待機する。
    /// タイムアウト時は false を返す。Kill はしない。
    /// </summary>
    public async Task<bool> WaitUntilExitedAsync(
        IReadOnlyList<string> processNames,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var initial = _launcher.GetProcesses(processNames);
        if (initial.Count == 0) return true;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var waitTasks = new List<Task>(initial.Count);
            foreach (var p in initial)
            {
                if (p.HasExited) continue;
                waitTasks.Add(WaitForExitSafeAsync(p, cts.Token));
            }
            try
            {
                await Task.WhenAll(waitTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* timeout */ }

            foreach (var p in initial)
            {
                if (!p.HasExited) return false;
            }
            return true;
        }
        finally
        {
            foreach (var p in initial)
            {
                try { p.Dispose(); } catch { /* best-effort */ }
            }
        }
    }
}

/// <summary>
/// <see cref="ToolProcessController"/> がプロセス操作を行うための抽象。
/// テストでは fake を差し込んで実プロセスを起動せずに検証できる。
/// </summary>
public interface IProcessLauncher
{
    void Start(string executablePath, string? arguments);
    IReadOnlyList<string> FindRunning(IEnumerable<string> processNames);
    IReadOnlyList<Process> GetProcesses(IEnumerable<string> processNames);
}

internal sealed class DefaultProcessLauncher : IProcessLauncher
{
    public void Start(string executablePath, string? arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments ?? string.Empty,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
        };
        using var _ = Process.Start(psi);
    }

    public IReadOnlyList<string> FindRunning(IEnumerable<string> processNames)
        => ProcessGuard.FindRunning(processNames);

    public IReadOnlyList<Process> GetProcesses(IEnumerable<string> processNames)
    {
        var list = new List<Process>();
        foreach (var name in processNames)
        {
            list.AddRange(Process.GetProcessesByName(name));
        }
        return list;
    }
}
