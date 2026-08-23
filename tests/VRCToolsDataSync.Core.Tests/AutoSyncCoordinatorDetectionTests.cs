using VRCToolsDataSync.Core.Settings;
using VRCToolsDataSync.Core.Sync;
using VRCToolsDataSync.Core.Watch;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// プロセス検出状況の通知を固定する。
/// <para>
/// 表示は「何も届かない」と「監視していないと届いた」を区別できない。届かない側に
/// 落ちると、設定で外しているツールが初期表示のまま残る (issue #11)。ここでは
/// <b>通知が出ること</b>を主に確かめる。
/// </para>
/// <para>
/// 同期先を組み立てられない設定で作る。監視そのものを走らせずに、binding を持たない
/// ツールへの通知だけを見たいため。
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

    private static (AutoSyncCoordinator Coordinator, List<ProcessDetectionEvent> Events) Watched(
        AutoSyncCoordinator coordinator)
    {
        var events = new List<ProcessDetectionEvent>();
        coordinator.ProcessDetectionChanged += e => events.Add(e);
        return (coordinator, events);
    }

    /// <summary>並び順に依存しないよう、比較する側と揃えて並べておく。</summary>
    private static readonly string[] BothTools =
        new[] { VrcxSyncService.Key, FriendConnectSyncService.Key }.Order().ToArray();

    [Fact(DisplayName = "自動同期を切っていても監視していないことを流す")]
    public void DisabledAutoSyncStillReportsThatNothingIsWatched()
    {
        // 黙ると表示が初期値のまま残り、設定で切っていることが読み取れない。
        using var coordinator = Coordinator(new SyncSettings { AutoSyncEnabled = false });
        var (_, events) = Watched(coordinator);

        coordinator.Start();

        Assert.Equal(BothTools, events.Select(e => e.ToolKey).Order().ToArray());
        Assert.All(events, e => Assert.False(e.IsWatching));
    }

    [Fact(DisplayName = "同期先を組み立てられない場合も監視していないことを流す")]
    public void AnUnusableTargetStillReportsThatNothingIsWatched()
    {
        // 保存先が未設定のまま自動同期を入れた状態。監視は始まらない。
        using var coordinator = Coordinator(new SyncSettings { AutoSyncEnabled = true });
        var (_, events) = Watched(coordinator);

        coordinator.Start();

        Assert.Equal(BothTools, events.Select(e => e.ToolKey).Order().ToArray());
        Assert.All(events, e => Assert.False(e.IsWatching));
    }

    [Fact(DisplayName = "同期対象から外したツールにも監視していないことを流す")]
    public void ToolsExcludedFromSyncAreReportedAsNotWatched()
    {
        using var coordinator = Coordinator(new SyncSettings
        {
            AutoSyncEnabled = false,
            SyncVrcx = true,
            SyncFriendConnect = false,
        });
        var (_, events) = Watched(coordinator);

        coordinator.Start();

        Assert.Contains(events, e => e.ToolKey == FriendConnectSyncService.Key && !e.IsWatching);
    }

    [Fact(DisplayName = "購読が遅れても、流し直せば現在の状態を受け取れる")]
    public void PublishingAgainDeliversTheCurrentStateToALateSubscriber()
    {
        // App は Coordinator.Start を背後で走らせてから画面を組み立てるため、
        // 購読が Start より後になることがある。既に起動していたプロセスは監視の
        // 開始時に黙って取り込まれるので、流し直さないとそのツールを閉じるまで
        // 表示が変わらない。
        using var coordinator = Coordinator(new SyncSettings { AutoSyncEnabled = false });
        coordinator.Start();

        // ここで初めて購読する。Start の通知は既に流れ終わっている。
        var (_, events) = Watched(coordinator);
        Assert.Empty(events);

        coordinator.PublishProcessDetection();

        Assert.Equal(BothTools, events.Select(e => e.ToolKey).Order().ToArray());
    }

    [Fact(DisplayName = "錠を離した後に次の世代が始まれば、古い通知は流さない")]
    public void StaleDetectionsAreDroppedOnceANewGenerationStarts()
    {
        // 通知は錠の外で組み立てて流すため、離してから流すまでの間に別のスレッドの
        // Stop や UpdateSettings が割り込める。そのまま流すと、停止済みなのに
        // 「検出中」が後から届き、次の変化まで表示が戻らない。
        using var coordinator = Coordinator(new SyncSettings { AutoSyncEnabled = false });
        var (_, events) = Watched(coordinator);

        // Start が錠を離してから流すまでの隙間に、Stop が割り込んだことにする。
        coordinator.OnBeforePublishForTests = () =>
        {
            coordinator.OnBeforePublishForTests = null;
            coordinator.Stop();
        };

        coordinator.Start();

        // 割り込んだ Stop の側は流れてよい。Start が抱えていた古い方は流れない。
        Assert.All(events, e => Assert.False(e.IsWatching));
        Assert.Equal(0, events.Count(e => e.IsWatching));
    }

    [Fact(DisplayName = "監視していないツールの検出名は空になる")]
    public void NotWatchedToolsCarryNoDetectedNames()
    {
        using var coordinator = Coordinator(new SyncSettings { AutoSyncEnabled = false });
        var (_, events) = Watched(coordinator);

        coordinator.Start();

        Assert.All(events, e =>
        {
            Assert.Empty(e.DetectedProcessNames);
            Assert.False(e.IsRunning);
        });
    }
}
