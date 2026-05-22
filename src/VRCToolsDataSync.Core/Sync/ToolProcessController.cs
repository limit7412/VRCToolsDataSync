using System.Diagnostics;
using System.Runtime.InteropServices;
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
                    if (p.HasExited) continue;
                    // Process.CloseMainWindow / p.MainWindowHandle は
                    // 「タスクトレイ常駐でメインウィンドウが Hide 状態」のツール
                    // (VRCX / VRC Friend Connect など) では IntPtr.Zero になり
                    // WM_CLOSE が一切送られないため、15 秒タイムアウトで停止失敗 →
                    // 終了時 Push 全件スキップ、という挙動になる。
                    // 代わりに EnumWindows でプロセス所有の全トップレベル HWND を
                    // 列挙し、それぞれに PostMessage(WM_CLOSE) を送る。非表示
                    // ウィンドウにも届くので、Electron / WinForms 系のトレイ
                    // 常駐ツールでもクリーン終了できる。
                    var posted = PostWmCloseToAllProcessWindows((uint)p.Id);
                    if (posted == 0)
                    {
                        _logger.LogDebug("Process {Pid} has no top-level window; WM_CLOSE skipped", p.Id);
                    }
                    else
                    {
                        _logger.LogDebug("Posted WM_CLOSE to {Count} window(s) of pid={Pid}", posted, p.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "WM_CLOSE post failed for pid={Pid}", p.Id);
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

            // 最終判定。initial で掴んだ Process が全て HasExited でも、
            // 待機中に同名で再起動されたケース (ユーザが立ち上げ直した、ツールの
            // 自己リスタート機能など) があるので、processNames で再スキャンする。
            // ここで生き残りを取りこぼすと、Stop が StoppedGracefully を返した直後の
            // Push が実行中プロセスにぶつかって失敗する。
            var stillRunning = new List<string>();
            foreach (var p in initial)
            {
                if (!p.HasExited)
                {
                    var name = SafeProcessName(p);
                    if (name is not null && !stillRunning.Contains(name)) stillRunning.Add(name);
                }
            }
            foreach (var name in _launcher.FindRunning(processNames))
            {
                if (!stillRunning.Contains(name)) stillRunning.Add(name);
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
    /// 指定 PID が所有する全トップレベルウィンドウに WM_CLOSE を投げる。
    /// 戻り値は送信に成功したウィンドウ数。Process.CloseMainWindow と違って、
    /// 非表示 (タスクトレイ常駐) のメインウィンドウにも届くので、
    /// VRCX や VRC Friend Connect のような常駐型ツールでも閉じられる。
    /// </summary>
    private static int PostWmCloseToAllProcessWindows(uint targetPid)
    {
        var posted = 0;
        EnumWindows((hwnd, _) =>
        {
            try
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == targetPid)
                {
                    if (PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
                    {
                        posted++;
                    }
                }
            }
            catch { /* best-effort: 1 ウィンドウの失敗で列挙全体を止めない */ }
            return true; // continue enumeration
        }, IntPtr.Zero);
        return posted;
    }

    private const uint WM_CLOSE = 0x0010;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

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
            // initial が全て終了済みでも、待機中に同名で再起動された可能性が
            // あるため、processNames で再スキャンして生き残りが無いことを確認する。
            return _launcher.FindRunning(processNames).Count == 0;
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
