using VRCToolsDataSync.Core.Watch;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// プロセス監視の判定を固定する。実際のプロセスを起動せず、見え方を差し替えて
/// 走査 1 回ぶんずつ確かめる。閉じ直しの取りこぼし (#8) は、実プロセスでは
/// 1 秒未満の間に閉じて開き直す必要があり、再現を時間に頼ることになるため。
/// </summary>
public sealed class ProcessWatcherTests
{
    private const string Name = "VRCX";

    /// <summary>差し替えられるプロセスの見え方。</summary>
    private sealed class FakeProcesses
    {
        private readonly Dictionary<string, List<ProcessInstance>> _byName = new(StringComparer.OrdinalIgnoreCase);

        public Exception? ProbeFailure { get; set; }

        public IReadOnlyList<ProcessInstance> Probe(string name)
        {
            if (ProbeFailure is not null) throw ProbeFailure;
            return _byName.TryGetValue(name, out var instances)
                ? instances.ToList()
                : Array.Empty<ProcessInstance>();
        }

        public void Set(string name, params ProcessInstance[] instances)
            => _byName[name] = instances.ToList();
    }

    private static ProcessInstance Instance(int id, int startedAtHour = 9)
        => new(id, new DateTime(2026, 8, 23, startedAtHour, 0, 0, DateTimeKind.Local));

    /// <summary>監視を組み立て、通知を記録する一覧を返す。</summary>
    private static (ProcessWatcher Watcher, List<string> Events) Watch(
        FakeProcesses processes, params string[] names)
    {
        var watcher = new ProcessWatcher(names.Length == 0 ? new[] { Name } : names, processes.Probe);
        var events = new List<string>();
        watcher.ProcessExited += name => events.Add("exited:" + name);
        watcher.ProcessStarted += name => events.Add("started:" + name);
        return (watcher, events);
    }

    [Fact(DisplayName = "閉じてから同じ走査のうちに開き直しても終了を通知する")]
    public void RestartWithinOnePollStillReportsTheExit()
    {
        // #8 の本体。名前ごとに真偽値だけを持つと、閉じた直後に開き直された場合は
        // 「動いている」のままなので、終了を一度も通知できない。
        var processes = new FakeProcesses();
        processes.Set(Name, Instance(1000));
        var (watcher, events) = Watch(processes);
        watcher.Poll();
        events.Clear();

        // 走査と走査の間に、閉じて別の実体で開き直された。
        processes.Set(Name, Instance(2000, startedAtHour: 10));
        watcher.Poll();

        Assert.Equal(new[] { "exited:" + Name, "started:" + Name }, events);
    }

    [Fact(DisplayName = "閉じ直された場合、終了を起動より先に通知する")]
    public void ExitIsReportedBeforeTheRestart()
    {
        // 逆順にすると、呼び出し側の持つ状態が「停止中」で終わり、実際は
        // 動いているのとずれる。
        var processes = new FakeProcesses();
        processes.Set(Name, Instance(1000));
        var (watcher, events) = Watch(processes);
        watcher.Poll();
        events.Clear();

        processes.Set(Name, Instance(2000, startedAtHour: 10));
        watcher.Poll();

        Assert.Equal("exited:" + Name, events[0]);
    }

    [Fact(DisplayName = "動き続けている間は何も通知しない")]
    public void NothingIsReportedWhileTheProcessKeepsRunning()
    {
        var processes = new FakeProcesses();
        processes.Set(Name, Instance(1000));
        var (watcher, events) = Watch(processes);
        watcher.Poll();
        events.Clear();

        watcher.Poll();
        watcher.Poll();

        Assert.Empty(events);
    }

    [Fact(DisplayName = "閉じれば終了を、開けば起動を通知する")]
    public void ReportsAPlainExitAndAPlainStart()
    {
        var processes = new FakeProcesses();
        processes.Set(Name, Instance(1000));
        var (watcher, events) = Watch(processes);
        watcher.Poll();
        events.Clear();

        processes.Set(Name);
        watcher.Poll();
        processes.Set(Name, Instance(3000, startedAtHour: 11));
        watcher.Poll();

        Assert.Equal(new[] { "exited:" + Name, "started:" + Name }, events);
    }

    [Fact(DisplayName = "監視開始時に動いていたものは起動として通知しない")]
    public void ProcessesAlreadyRunningAtStartAreNotReportedAsStarted()
    {
        // 利用者が開いたのではなく、既にそこにあっただけ。
        //
        // ここだけは Start を通す。監視開始時の取り込みは Start にしか無いため。
        // 間隔を長く取り、背景のループが走る前に判定を確かめる。
        var processes = new FakeProcesses();
        processes.Set(Name, Instance(1000));
        var events = new List<string>();
        using var watcher = new ProcessWatcher(
            new[] { Name }, processes.Probe, TimeSpan.FromMinutes(10));
        watcher.ProcessStarted += name => events.Add("started:" + name);

        watcher.Start();

        Assert.Empty(events);
    }

    [Fact(DisplayName = "PID が再利用されても、開始時刻が違えば入れ替わりと見なす")]
    public void SamePidWithADifferentStartTimeCountsAsAReplacement()
    {
        // Windows は終了したプロセスの PID を再利用する。PID だけで見分けると
        // 別の実体を同じものと取り違える。
        var processes = new FakeProcesses();
        processes.Set(Name, Instance(1000, startedAtHour: 9));
        var (watcher, events) = Watch(processes);
        watcher.Poll();
        events.Clear();

        processes.Set(Name, Instance(1000, startedAtHour: 10));
        watcher.Poll();

        Assert.Equal(new[] { "exited:" + Name, "started:" + Name }, events);
    }

    [Fact(DisplayName = "開始時刻を読めなくなっただけでは入れ替わりと見なさない")]
    public void AnUnreadableStartTimeDoesNotLookLikeAReplacement()
    {
        // 権限やタイミングで開始時刻を読めないことがある。読めたり読めなかったり
        // 揺れるだけで通知が出ると、動き続けているツールに対して Push が走る。
        var processes = new FakeProcesses();
        processes.Set(Name, Instance(1000));
        var (watcher, events) = Watch(processes);
        watcher.Poll();
        events.Clear();

        processes.Set(Name, new ProcessInstance(1000, null));
        watcher.Poll();

        Assert.Empty(events);
    }

    [Fact(DisplayName = "一時的に読めなかった開始時刻は、読めていた値を覚えておく")]
    public void ATemporarilyUnreadableStartTimeDoesNotDiscardTheKnownValue()
    {
        // null は「開始時刻が無い」ではなく「今回は読めなかった」でしかない。null のまま
        // 覚えると次に読めた時刻と突き合わせる相手を失い、その間に PID が再利用されて
        // いても入れ替わりを見逃す。
        var processes = new FakeProcesses();
        processes.Set(Name, Instance(1000, startedAtHour: 9));
        var (watcher, events) = Watch(processes);
        watcher.Poll();
        events.Clear();

        // 一度読めなくなり、次は読めた。ただし別の実体が同じ PID を割り当てられている。
        processes.Set(Name, new ProcessInstance(1000, null));
        watcher.Poll();
        Assert.Empty(events);

        processes.Set(Name, Instance(1000, startedAtHour: 10));
        watcher.Poll();

        Assert.Equal(new[] { "exited:" + Name, "started:" + Name }, events);
    }

    [Fact(DisplayName = "開始時刻を読めない実体でも、消えれば終了を通知する")]
    public void AnInstanceWithoutAStartTimeStillReportsItsExit()
    {
        var processes = new FakeProcesses();
        processes.Set(Name, new ProcessInstance(1000, null));
        var (watcher, events) = Watch(processes);
        watcher.Poll();
        events.Clear();

        processes.Set(Name);
        watcher.Poll();

        Assert.Equal(new[] { "exited:" + Name }, events);
    }

    [Fact(DisplayName = "複数動いているうち 1 つが消えただけでは終了を通知しない")]
    public void LosingOneOfSeveralInstancesDoesNotReportAnExit()
    {
        // AutoSyncCoordinator は終了通知からファイル解放待ちの 3 秒を数える。1 つ目が
        // 消えた時点で数え始めると、その 3 秒が経つ間際に最後の 1 つが消えた場合、
        // 最後の書き手が終わってから 3 秒を待たずに読み始めることになる。
        var processes = new FakeProcesses();
        processes.Set(Name, Instance(1000, 9), Instance(1001, 10));
        var (watcher, events) = Watch(processes);
        watcher.Poll();
        events.Clear();

        processes.Set(Name, Instance(1001, 10));
        watcher.Poll();

        Assert.Empty(events);
    }

    [Fact(DisplayName = "複数動いているうち最後の 1 つが消えれば終了を通知する")]
    public void LosingTheLastInstanceReportsAnExit()
    {
        var processes = new FakeProcesses();
        processes.Set(Name, Instance(1000, 9), Instance(1001, 10));
        var (watcher, events) = Watch(processes);
        watcher.Poll();
        events.Clear();

        processes.Set(Name, Instance(1001, 10));
        watcher.Poll();
        processes.Set(Name);
        watcher.Poll();

        Assert.Equal(new[] { "exited:" + Name }, events);
    }

    [Fact(DisplayName = "開始時刻を読めるようになっただけでは入れ替わりと見なさない")]
    public void AStartTimeBecomingReadableDoesNotLookLikeAReplacement()
    {
        // 読めない側から読める側への遷移も、読める側から読めない側への遷移と同じく
        // 揺れでしかない。片方向だけ抑えても、もう一方で不要な AutoPush が走る。
        var processes = new FakeProcesses();
        processes.Set(Name, new ProcessInstance(1000, null));
        var (watcher, events) = Watch(processes);
        watcher.Poll();
        events.Clear();

        processes.Set(Name, Instance(1000));
        watcher.Poll();

        Assert.Empty(events);
    }

    [Fact(DisplayName = "走査に失敗した名前は、動いているものを終了として通知しない")]
    public void AFailedProbeDoesNotLookLikeAnExit()
    {
        // 失敗を「1 つも動いていない」と読むと、実際には動いているツールに対して
        // Push が走る。次の走査に回す。
        var processes = new FakeProcesses();
        processes.Set(Name, Instance(1000));
        var (watcher, events) = Watch(processes);
        watcher.Poll();
        events.Clear();

        processes.ProbeFailure = new InvalidOperationException("列挙に失敗");
        watcher.Poll();
        Assert.Empty(events);

        // 失敗が解けたら、その間の変化を通常どおり拾う。
        processes.ProbeFailure = null;
        processes.Set(Name);
        watcher.Poll();

        Assert.Equal(new[] { "exited:" + Name }, events);
    }

    [Fact(DisplayName = "同じ名前を重ねて渡しても通知は 1 回にする")]
    public void DuplicateNamesDoNotDoubleTheNotification()
    {
        var processes = new FakeProcesses();
        processes.Set(Name, Instance(1000));
        var watcher = new ProcessWatcher(new[] { Name, Name.ToLowerInvariant() }, processes.Probe);
        var events = new List<string>();
        watcher.ProcessExited += name => events.Add("exited:" + name);
        watcher.Poll();

        processes.Set(Name);
        watcher.Poll();

        Assert.Equal(new[] { "exited:" + Name }, events);
    }

    [Fact(DisplayName = "検出中の名前を読み出せる")]
    public void DetectedProcessNamesReportsTheNamesThatMatched()
    {
        // 実行ファイル名は配布のされ方で変わりうるため候補を複数持っている。
        // どれが実際に当たっているかを出せないと、利用者には「自動 Push が
        // 動かない」ことしか見えない (issue #11)。
        const string alternate = "VRCFriendConnect";
        var processes = new FakeProcesses();
        processes.Set(alternate, Instance(1000));
        var (watcher, _) = Watch(processes, Name, alternate);

        watcher.Poll();

        Assert.Equal(new[] { alternate }, watcher.DetectedProcessNames);
    }

    [Fact(DisplayName = "どれも当たっていなければ検出中の名前は空になる")]
    public void DetectedProcessNamesIsEmptyWhenNothingMatched()
    {
        var (watcher, _) = Watch(new FakeProcesses(), Name, "VRCFriendConnect");

        watcher.Poll();

        Assert.Empty(watcher.DetectedProcessNames);
    }

    [Fact(DisplayName = "検出中の名前は起動と終了に追従する")]
    public void DetectedProcessNamesFollowsStartAndExit()
    {
        var processes = new FakeProcesses();
        var (watcher, _) = Watch(processes);
        watcher.Poll();
        Assert.Empty(watcher.DetectedProcessNames);

        processes.Set(Name, Instance(1000));
        watcher.Poll();
        Assert.Equal(new[] { Name }, watcher.DetectedProcessNames);

        processes.Set(Name);
        watcher.Poll();
        Assert.Empty(watcher.DetectedProcessNames);
    }

    [Fact(DisplayName = "通知の処理中でも検出中の名前を読める")]
    public void ANotificationHandlerDoesNotBlockReaders()
    {
        // 購読側が何をするかは監視からは分からない。通知を錠の中で出していると、
        // 購読側が動いているあいだ読み出しが待たされる。
        //
        // 同一スレッドの読み返しでは確かめられない。lock は同じスレッドから再入できる
        // ので、錠の中で通知を出していても素通りする。別のスレッドから読む。
        var processes = new FakeProcesses();
        var (watcher, _) = Watch(processes);
        watcher.Poll();

        using var handlerEntered = new ManualResetEventSlim();
        using var readerDone = new ManualResetEventSlim();
        var readerSucceeded = false;

        var reader = new Thread(() =>
        {
            handlerEntered.Wait(TimeSpan.FromSeconds(10));
            readerSucceeded = watcher.DetectedProcessNames.Count == 1;
            readerDone.Set();
        });
        reader.Start();

        // 通知の処理を、読み出しが済むまで抜けないようにする。
        watcher.ProcessStarted += _ =>
        {
            handlerEntered.Set();
            readerDone.Wait(TimeSpan.FromSeconds(10));
        };

        processes.Set(Name, Instance(1000));
        watcher.Poll();

        Assert.True(reader.Join(TimeSpan.FromSeconds(10)), "読み出しが終わらない");
        Assert.True(readerSucceeded, "通知の処理中に読み出せていない");
    }

    [Fact(DisplayName = "走査と並行して検出中の名前を読める")]
    public void DetectedProcessNamesCanBeReadWhilePolling()
    {
        // 走査は監視のスレッドが、読み出しは UI のスレッドが行う。
        // 辞書を守らないまま両方から触ると、読み出し側が壊れた状態を見る。
        var processes = new FakeProcesses();
        processes.Set(Name, Instance(1000));
        var (watcher, _) = Watch(processes);
        watcher.Poll();

        var stop = false;
        Exception? failure = null;
        var reader = new Thread(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop)) _ = watcher.DetectedProcessNames;
            }
            catch (Exception ex) { failure = ex; }
        });

        reader.Start();
        try
        {
            for (var i = 0; i < 200; i++)
            {
                processes.Set(Name, Instance(2000 + i, startedAtHour: 9));
                watcher.Poll();
                processes.Set(Name);
                watcher.Poll();
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            Assert.True(reader.Join(TimeSpan.FromSeconds(30)), "読み出しが終わらない");
        }

        Assert.Null(failure);
    }
}
