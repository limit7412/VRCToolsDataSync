using System.ComponentModel;
using System.Diagnostics;

namespace VRCToolsDataSync.Core.Watch;

/// <summary>
/// プロセス 1 つを他と取り違えずに指すための識別子。
/// <para>
/// PID だけでは足りない。Windows は終了したプロセスの PID を再利用するため、
/// 同じ番号が別の実体を指しうる。開始時刻を併せて持つことで区別する。
/// </para>
/// </summary>
/// <param name="Id">プロセス ID。</param>
/// <param name="StartedAt">開始時刻。読めなかった場合は null。</param>
internal readonly record struct ProcessInstance(int Id, DateTime? StartedAt);

/// <summary>
/// 指定した名前のプロセスの起動と終了を監視する。
/// <para>
/// 見ているのは名前ではなく<b>実体</b>である。名前ごとに「動いているか」の真偽値だけを
/// 持つと、閉じてから次の走査までの間に同じ名前で開き直された場合、真のままなので
/// 終了を通知できない。<see cref="ProcessInstance"/> の集合を持ち、前回見えていた実体が
/// 消えたかどうかで判定する。
/// </para>
/// <para>
/// 走査の間隔を詰めても、この取りこぼしは狭くなるだけで無くならない。間隔は 1 秒のまま
/// 変えていない。終了を受けた <see cref="AutoSyncCoordinator"/> はファイル解放待ちに
/// 3 秒置いてから Push するので、通知が 1 秒遅れても後段には響かない。
/// </para>
/// </summary>
public sealed class ProcessWatcher : IDisposable
{
    private readonly IReadOnlyList<string> _processNames;
    private readonly TimeSpan _interval;
    private readonly Func<string, IReadOnlyList<ProcessInstance>> _probe;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, HashSet<ProcessInstance>> _running =
        new(StringComparer.OrdinalIgnoreCase);
    private Task? _loop;

    public event Action<string>? ProcessStarted;
    public event Action<string>? ProcessExited;

    public ProcessWatcher(IEnumerable<string> processNames, TimeSpan? interval = null)
        : this(processNames, ProbeByName, interval)
    {
    }

    /// <summary>プロセスの見え方を差し替えられる形。テストから使う。</summary>
    internal ProcessWatcher(
        IEnumerable<string> processNames,
        Func<string, IReadOnlyList<ProcessInstance>> probe,
        TimeSpan? interval = null)
    {
        // 名前の重複を落とす。_running は大文字小文字を区別しない辞書なので、
        // 揃えておかないと同じ実体について通知が二重になる。
        _processNames = processNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _probe = probe;
        _interval = interval ?? TimeSpan.FromSeconds(1);
        foreach (var name in _processNames)
        {
            _running[name] = new HashSet<ProcessInstance>();
        }
    }

    public void Start()
    {
        if (_loop is not null) return;

        // 監視を始めた時点で動いているものは、起動を通知しない。利用者が開いたのではなく
        // 既にそこにあっただけなので、起動として扱うと後段が意味を取り違える。
        foreach (var name in _processNames)
        {
            _running[name] = Reconcile(SafeProbe(name), _running[name]);
        }
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>1 回ぶんの走査。ループから呼ぶほか、テストから直に叩いて判定だけを確かめる。</summary>
    internal void Poll()
    {
        foreach (var name in _processNames)
        {
            var previous = _running[name];
            var current = Reconcile(SafeProbe(name), previous);

            // 前回見えていた実体が 1 つでも消えていれば終了、前回に無かった実体が
            // 見えていれば起動。集合どうしの比較なので、閉じてから開き直されて
            // 「動いている数」が変わらない場合でも取り違えない。
            var exited = previous.Any(p => !current.Contains(p));
            var started = current.Any(c => !previous.Contains(c));
            _running[name] = current;

            // 終了を先に通知する。同じ走査の中で閉じ直されていた場合、逆順にすると
            // 呼び出し側の持つ状態が「停止中」で終わり、実際は動いているのとずれる。
            if (exited) ProcessExited?.Invoke(name);
            if (started) ProcessStarted?.Invoke(name);
        }
    }

    /// <summary>
    /// 開始時刻を読めなかった実体に、前回の同じ PID の見え方を引き継ぐ。
    /// <para>
    /// 開始時刻は読めないことがある (下記 <see cref="TryGetStartedAt"/>)。読めたり読めなかったり
    /// 揺れるだけで識別子が変わると、同じプロセスが入れ替わったように見えてしまう。
    /// </para>
    /// </summary>
    private static HashSet<ProcessInstance> Reconcile(
        IReadOnlyList<ProcessInstance> current,
        HashSet<ProcessInstance> previous)
    {
        var result = new HashSet<ProcessInstance>();
        foreach (var instance in current)
        {
            if (instance.StartedAt is null)
            {
                ProcessInstance? carried = null;
                foreach (var seen in previous)
                {
                    if (seen.Id == instance.Id) { carried = seen; break; }
                }
                if (carried is not null) { result.Add(carried.Value); continue; }
            }
            result.Add(instance);
        }
        return result;
    }

    /// <summary>
    /// 1 つの名前の走査に失敗しても、その名前を次回に回すだけにする。
    /// 空を返すと、実際には動いているものを終了として通知してしまう。
    /// </summary>
    private IReadOnlyList<ProcessInstance> SafeProbe(string name)
    {
        try
        {
            return _probe(name);
        }
        catch
        {
            return _running.TryGetValue(name, out var previous)
                ? previous.ToList()
                : Array.Empty<ProcessInstance>();
        }
    }

    private static IReadOnlyList<ProcessInstance> ProbeByName(string name)
    {
        // GetProcessesByName が返す要素はネイティブハンドルを持つ。ここは 1 秒ごとに
        // 通るため、Dispose を漏らすと溜まり続ける (ProcessGuard.FindRunning と同じ理由)。
        var processes = Process.GetProcessesByName(name);
        try
        {
            var instances = new List<ProcessInstance>(processes.Length);
            foreach (var process in processes)
            {
                instances.Add(new ProcessInstance(process.Id, TryGetStartedAt(process)));
            }
            return instances;
        }
        finally
        {
            foreach (var process in processes)
            {
                try { process.Dispose(); } catch { /* best-effort */ }
            }
        }
    }

    /// <summary>
    /// 開始時刻を読む。読めない場合は null。
    /// <para>
    /// 権限が足りない相手 (管理者権限で動いているツールなど) は <see cref="Win32Exception"/>、
    /// 列挙してから読むまでに終わっていれば <see cref="InvalidOperationException"/> になる。
    /// どちらも PID だけで見分ける形に落とす。1 秒の間に同じ PID が別の実体へ割り当てられる
    /// 可能性は実際上無視できる。
    /// </para>
    /// </summary>
    private static DateTime? TryGetStartedAt(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                Poll();
            }
            catch
            {
                // 走査は継続。個別の例外でループを止めない
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
