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
/// 終了を通知できない。<see cref="ProcessInstance"/> を並べて持ち、前回見えていた実体が
/// 残っているかどうかで判定する。
/// </para>
/// <para>
/// 通知の意味は<b>入れ替わり</b>である。見ていた実体が 1 つも残っていないときに
/// <see cref="ProcessExited"/>、見えている実体がどれも見ていなかったものであるときに
/// <see cref="ProcessStarted"/> を出す。同じ名前で複数動いているうちの 1 つが消えただけでは
/// 終了を通知しない。<see cref="AutoSyncCoordinator"/> は終了通知からファイル解放待ちの
/// 3 秒を数えるので、1 つ目が消えた時点で数え始めると、最後の書き手が終わってからの
/// 猶予を取り直せない。
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
    // _running は走査のスレッドが書き、DetectedProcessNames を UI のスレッドが読む。
    // 走査そのものは 1 本しか走らないが、読み手が別にいる以上は錠が要る。
    private readonly object _gate = new();
    private readonly Dictionary<string, List<ProcessInstance>> _running =
        new(StringComparer.OrdinalIgnoreCase);
    private Task? _loop;

    public event Action<string>? ProcessStarted;
    public event Action<string>? ProcessExited;

    /// <summary>
    /// いま実体が見えている名前。どの候補が実際に当たっているかを表示するために使う。
    /// <para>
    /// 返すのは呼んだ時点の写しである。次の走査で変わりうる。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> DetectedProcessNames
    {
        get
        {
            lock (_gate)
            {
                return _processNames.Where(name => _running[name].Count > 0).ToList();
            }
        }
    }

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
            _running[name] = new List<ProcessInstance>();
        }
    }

    public void Start()
    {
        if (_loop is not null) return;

        // 監視を始めた時点で動いているものは、起動を通知しない。利用者が開いたのではなく
        // 既にそこにあっただけなので、起動として扱うと後段が意味を取り違える。
        foreach (var name in _processNames)
        {
            var probed = TryProbe(name);
            if (probed is null) continue;
            lock (_gate) { _running[name] = Remember(probed, _running[name]); }
        }
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>1 回ぶんの走査。ループから呼ぶほか、テストから直に叩いて判定だけを確かめる。</summary>
    internal void Poll()
    {
        foreach (var name in _processNames)
        {
            // 列挙は錠の外で済ませる。ここは実際にプロセスを数えるぶん時間がかかるので、
            // 中に入れると DetectedProcessNames を読む側をそのあいだ待たせる。
            var probed = TryProbe(name);

            // 走査に失敗した名前は次回に回す。空と読むと、動いているものを終了として
            // 通知してしまう。
            if (probed is null) continue;

            bool exited, started;
            lock (_gate)
            {
                var previous = _running[name];

                // 見ていた実体が 1 つでも残っているか。残っていなければ、見ていたものは
                // すべて終わり、見えているものはすべて新しい。閉じてから開き直された場合も、
                // 残っている実体は無いのでここに入る。「動いている数」ではなく実体で見ている
                // ため、数が変わらなくても取り違えない。
                //
                // 1 つでも残っていれば、消えたものがあっても終了を通知しない。残りが動いて
                // いる間に Push へ進むと、最後の書き手が終わってからの猶予を取り直せない。
                var survives = previous.Any(p => probed.Any(c => IsSameInstance(p, c)));
                exited = previous.Count > 0 && !survives;
                started = probed.Count > 0 && !survives;
                _running[name] = Remember(probed, previous);
            }

            // 通知は錠の外で出す。購読側が何をするかはここからは分からず、時間の掛かる
            // 処理でも構わない。錠を持ったまま呼ぶと、そのあいだ
            // DetectedProcessNames を読む側 (別のスレッドにいる) が待たされる。
            //
            // 終了を先に通知する。同じ走査の中で閉じ直されていた場合、逆順にすると
            // 呼び出し側の持つ状態が「停止中」で終わり、実際は動いているのとずれる。
            if (exited) ProcessExited?.Invoke(name);
            if (started) ProcessStarted?.Invoke(name);
        }
    }

    /// <summary>
    /// 2 つの見え方が同じ実体を指すかを判定する。
    /// <para>
    /// 開始時刻はどちらの側でも読めないことがある (下記 <see cref="TryGetStartedAt"/>)。
    /// 片方でも読めていなければ PID だけで判断する。読める・読めないが揺れるだけで
    /// 別の実体と見なすと、動き続けているツールに対して終了を通知してしまう。
    /// </para>
    /// <para>
    /// 両方読めている場合は開始時刻まで一致を求める。Windows は終了したプロセスの PID を
    /// 再利用するため、PID だけでは別の実体を同じものと取り違える。
    /// </para>
    /// </summary>
    private static bool IsSameInstance(ProcessInstance a, ProcessInstance b)
        => a.Id == b.Id
            && (a.StartedAt is null || b.StartedAt is null || a.StartedAt == b.StartedAt);

    /// <summary>
    /// 次の走査へ持ち越す見え方を決める。一度読めた開始時刻は、読めなかった走査で捨てない。
    /// <para>
    /// null は「開始時刻が無い」ではなく「今回は読めなかった」でしかない。null のまま
    /// 覚えると、次に読めた時刻と突き合わせる相手を失う。読めない間に PID が再利用されて
    /// いても、null はどの時刻とも一致するため入れ替わりを見逃す。
    /// </para>
    /// <para>
    /// 読めない状態が続く間に入れ替わった場合は、突き合わせる材料が無いので見逃す。
    /// 次にどちらかの走査で読めた時点で気付く。
    /// </para>
    /// </summary>
    private static List<ProcessInstance> Remember(
        IReadOnlyList<ProcessInstance> probed,
        List<ProcessInstance> previous)
    {
        var result = new List<ProcessInstance>(probed.Count);
        foreach (var instance in probed)
        {
            if (instance.StartedAt is null)
            {
                // 見つからなければ既定値 (開始時刻は null) が返るので、そのまま判別に使える。
                var known = previous.FirstOrDefault(p => p.Id == instance.Id && p.StartedAt is not null);
                if (known.StartedAt is not null) { result.Add(known); continue; }
            }
            result.Add(instance);
        }
        return result;
    }

    /// <summary>
    /// 1 つの名前を走査する。失敗した場合は null を返し、呼び出し側がその名前を
    /// 次回に回せるようにする。空の一覧を返すと、実際には動いているものを終了として
    /// 通知してしまう。
    /// </summary>
    private IReadOnlyList<ProcessInstance>? TryProbe(string name)
    {
        try
        {
            return _probe(name);
        }
        catch
        {
            return null;
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
