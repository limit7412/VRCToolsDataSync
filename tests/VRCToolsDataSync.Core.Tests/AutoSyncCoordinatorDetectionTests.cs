using VRCToolsDataSync.Core.Settings;
using VRCToolsDataSync.Core.Sync;
using VRCToolsDataSync.Core.Watch;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// プロセス検出状況の読み出しと通知を固定する。
/// <para>
/// 通知は「変わった」ことしか伝えず、状態は <see cref="AutoSyncCoordinator.GetProcessDetections"/>
/// から読む。<b>読むたびに現在の状態が返る</b>ので、通知が遅れて届いても古い状態を
/// 表示することはない。ここでは読み出しが常に現在を返すことを確かめる。
/// </para>
/// <para>
/// 同期先を組み立てられない設定で作る。監視そのものを走らせずに、binding を持たない
/// ツールの扱いだけを見たいため。
/// </para>
/// </summary>
public sealed class AutoSyncCoordinatorDetectionTests : IDisposable
{
    private readonly string _settingsPath;

    public AutoSyncCoordinatorDetectionTests()
    {
        // 既定の場所を読みに行かせない。設定の中身はここでは使わない。
        _settingsPath = Path.Combine(
            Path.GetTempPath(), "vrctds-coord-" + Guid.NewGuid().ToString("N"), "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_settingsPath)!, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private AutoSyncCoordinator Coordinator(SyncSettings settings)
        => new(new SyncRunner(new SettingsStore(_settingsPath)), settings);

    /// <summary>
    /// 一覧から導く。ツールを足したときにここを直し忘れると、監視が扱っていない
    /// ことに気付けないまま通ってしまう。並び順に依存しないよう揃えておく。
    /// </summary>
    private static readonly string[] AllTools =
        ToolCatalog.All.Select(t => t.Key).Order().ToArray();

    private static string[] ToolKeysOf(AutoSyncCoordinator coordinator)
        => coordinator.GetProcessDetections().Select(d => d.ToolKey).Order().ToArray();

    [Fact(DisplayName = "自動同期を切っていれば、どのツールも監視していないと読める")]
    public void DisabledAutoSyncReportsThatNothingIsWatched()
    {
        // 表示は「何も読めない」と「監視していないと読めた」を区別できない。
        // 一覧から漏れると、設定で切っていることが読み取れないまま初期表示が残る。
        using var coordinator = Coordinator(new SyncSettings { AutoSyncEnabled = false });

        coordinator.Start();

        var detections = coordinator.GetProcessDetections();
        Assert.Equal(AllTools, detections.Select(d => d.ToolKey).Order().ToArray());
        Assert.All(detections, d => Assert.False(d.IsWatching));
    }

    [Fact(DisplayName = "同期先を組み立てられない場合も監視していないと読める")]
    public void AnUnusableTargetReportsThatNothingIsWatched()
    {
        // 保存先が未設定のまま自動同期を入れた状態。監視は始まらない。
        using var coordinator = Coordinator(new SyncSettings { AutoSyncEnabled = true });

        coordinator.Start();

        Assert.Equal(AllTools, ToolKeysOf(coordinator));
        Assert.All(coordinator.GetProcessDetections(), d => Assert.False(d.IsWatching));
    }

    [Fact(DisplayName = "同期対象から外したツールも一覧から漏れない")]
    public void ToolsExcludedFromSyncAreStillListed()
    {
        using var coordinator = Coordinator(new SyncSettings
        {
            AutoSyncEnabled = false,
            SyncVrcx = true,
            SyncFriendConnect = false,
        });

        coordinator.Start();

        Assert.Contains(
            coordinator.GetProcessDetections(),
            d => d.ToolKey == FriendConnectSyncService.Key && !d.IsWatching);
    }

    [Fact(DisplayName = "監視していないツールの検出名は空になる")]
    public void NotWatchedToolsCarryNoDetectedNames()
    {
        using var coordinator = Coordinator(new SyncSettings { AutoSyncEnabled = false });

        coordinator.Start();

        Assert.All(coordinator.GetProcessDetections(), d =>
        {
            Assert.Empty(d.DetectedProcessNames);
            Assert.False(d.IsRunning);
        });
    }

    [Fact(DisplayName = "ライフサイクルが動けば通知が出る")]
    public void LifecycleChangesRaiseTheNotification()
    {
        using var coordinator = Coordinator(new SyncSettings { AutoSyncEnabled = false });
        var raised = 0;
        coordinator.ProcessDetectionChanged += () => raised++;

        coordinator.Start();
        Assert.Equal(1, raised);

        coordinator.Stop();
        Assert.Equal(2, raised);

        coordinator.UpdateSettings(new SyncSettings { AutoSyncEnabled = false });
        Assert.Equal(3, raised);
    }

    [Fact(DisplayName = "通知が遅れて届いても、読み直せば現在の状態が返る")]
    public void ReadingAfterALateNotificationStillReturnsTheCurrentState()
    {
        // 通知に状態を載せると、錠を離してから届くまでの間に次の変化が起きた場合に
        // 古い方を届けうる。載せなければ、遅れて届いた通知は読み直させるだけになる。
        //
        // ここでは通知の中で読み直し、その時点の状態が返ることを確かめる。通知が
        // 出た時点より後にライフサイクルが動いていても、読むのは現在である。
        using var coordinator = Coordinator(new SyncSettings { AutoSyncEnabled = false });
        coordinator.Start();

        IReadOnlyList<ProcessDetectionEvent>? readInsideHandler = null;
        coordinator.ProcessDetectionChanged += () =>
            readInsideHandler ??= coordinator.GetProcessDetections();

        coordinator.Stop();

        Assert.NotNull(readInsideHandler);
        Assert.Equal(AllTools, readInsideHandler!.Select(d => d.ToolKey).Order().ToArray());
        Assert.All(readInsideHandler!, d => Assert.False(d.IsWatching));
    }

    [Fact(DisplayName = "Start を重ねて呼んでも、その後の読み出しは壊れない")]
    public void CallingStartTwiceKeepsTheReadingIntact()
    {
        // 2 度目の Start は何も変えずに戻る。ここで内部の状態を進めてしまうと、
        // 以後の通知や読み出しが噛み合わなくなる。
        using var coordinator = Coordinator(new SyncSettings { AutoSyncEnabled = false });

        coordinator.Start();
        coordinator.Start();

        Assert.Equal(AllTools, ToolKeysOf(coordinator));
    }
}
