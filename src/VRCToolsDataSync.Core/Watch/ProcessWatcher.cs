using System.Diagnostics;

namespace VRCToolsDataSync.Core.Watch;

public sealed class ProcessWatcher : IDisposable
{
    private readonly IReadOnlyList<string> _processNames;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<string> _runningNames = new(StringComparer.OrdinalIgnoreCase);
    private Task? _loop;

    public event Action<string>? ProcessStarted;
    public event Action<string>? ProcessExited;

    public ProcessWatcher(IEnumerable<string> processNames, TimeSpan? interval = null)
    {
        _processNames = processNames.ToList();
        _interval = interval ?? TimeSpan.FromSeconds(1);
    }

    public void Start()
    {
        if (_loop is not null) return;
        foreach (var name in _processNames)
        {
            if (Process.GetProcessesByName(name).Length > 0)
            {
                _runningNames.Add(name);
            }
        }
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                foreach (var name in _processNames)
                {
                    var isRunning = Process.GetProcessesByName(name).Length > 0;
                    var wasRunning = _runningNames.Contains(name);
                    if (isRunning && !wasRunning)
                    {
                        _runningNames.Add(name);
                        ProcessStarted?.Invoke(name);
                    }
                    else if (!isRunning && wasRunning)
                    {
                        _runningNames.Remove(name);
                        ProcessExited?.Invoke(name);
                    }
                }
            }
            catch
            {
                // ポーリングは継続。個別の例外でループを止めない
            }

            try
            {
                await Task.Delay(_interval, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* best-effort */ }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
        _cts.Dispose();
    }
}
