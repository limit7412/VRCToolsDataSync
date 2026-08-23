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
    private static (ProcessWatcher Watcher, List<string> Events) Watch(FakeProcesses processes)
    {
        var watcher = new ProcessWatcher(new[] { Name }, processes.Probe);
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
}
