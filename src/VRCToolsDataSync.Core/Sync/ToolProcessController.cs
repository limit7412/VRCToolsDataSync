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

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var running = _launcher.FindRunning(processNames);
            if (running.Count == 0)
            {
                return new ToolStopResult { Outcome = ToolStopOutcome.StoppedGracefully };
            }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        var stillRunning = _launcher.FindRunning(processNames);
        return new ToolStopResult
        {
            Outcome = stillRunning.Count == 0 ? ToolStopOutcome.StoppedGracefully : ToolStopOutcome.TimedOut,
            StillRunningProcessNames = stillRunning,
        };
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
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_launcher.FindRunning(processNames).Count == 0)
            {
                return true;
            }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return _launcher.FindRunning(processNames).Count == 0;
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
