using Microsoft.Data.Sqlite;
using VRCToolsDataSync.Core.Domain;
using VRCToolsDataSync.Core.Infra;
using VRCToolsDataSync.Core.UseCase;
using Xunit;
using static VRCToolsDataSync.Core.Tests.FriendConnectSchema;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// Friend Connect のマージを、2 台の PC の Push / Pull の流れで確かめる。マージの規則そのものは
/// <see cref="FriendConnectMergeTests"/> で見るので、ここでは設定で有効にしたときだけマージに
/// 回ること、結果が同期先に載って相手に届くことを見る。
/// </summary>
public sealed class FriendConnectMergeSyncTests : IDisposable
{
    private readonly string _root;
    private readonly FakeSyncStorage _storage = new();

    public FriendConnectMergeSyncTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vrctds-fc-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class Machine
    {
        public required SyncRunner Runner { get; init; }
        public required SyncSettings Settings { get; init; }
        public required FriendConnectSyncService Service { get; init; }
        public required FriendConnectPaths Paths { get; init; }
    }

    private Machine CreateMachine(string name)
    {
        var dir = Path.Combine(_root, name);
        var store = new SettingsStore(Path.Combine(dir, "settings.json"));
        var paths = new FriendConnectPaths(Path.Combine(dir, "VRC Friend Connect"));
        Directory.CreateDirectory(paths.DbDirectory);
        return new Machine
        {
            Runner = new SyncRunner(store),
            Settings = new SyncSettings { MachineName = name },
            Service = new FriendConnectSyncService(paths, new LocalBackup(Path.Combine(dir, "backup"))),
            Paths = paths,
        };
    }

    private SyncResult Push(Machine machine) => machine.Runner.Push(machine.Service, machine.Settings, _storage, force: false);

    private SyncResult Pull(Machine machine, bool startup = false)
        => machine.Runner.Pull(machine.Service, machine.Settings, _storage, skipBackup: false, skipIfNotNewer: startup);

    private static string Notes(string db) => string.Join(",", Rows(db, "SELECT apiId || ':' || coalesce(note, '') FROM User ORDER BY apiId"));

    private static string Taggings(string db) => string.Join(",", Rows(db,
        "SELECT u.apiId || ':' || t.name FROM UserTag ut JOIN User u ON u.id = ut.userId JOIN Tag t ON t.id = ut.tagId ORDER BY u.apiId, t.name"));

    /// <summary>A が作ったデータを B が取り込み、その後 A がメモを書き足して Push した状態。</summary>
    private (Machine A, Machine B) ArrangeRemoteNewer()
    {
        var a = CreateMachine("a");
        FriendConnectSchema.Create(a.Paths.DbV11File);
        Exec(a.Paths.DbV11File, InsertUser("usr_a", "A", "memo-a") + InsertUser("usr_b", "B") + InsertTag("friend") + TagUser("usr_a", "friend"));
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);

        var b = CreateMachine("b");
        Assert.Equal(SyncOutcome.Success, Pull(b).Outcome);

        Exec(a.Paths.DbV11File, "UPDATE User SET note = 'memo-a2' WHERE apiId = 'usr_a'");
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        return (a, b);
    }

    [Fact(DisplayName = "Friend Connect のマージが有効なら、競合した Push はマージしてから送り、相手の Pull に届く")]
    public void ConflictingPushMergesWhenEnabled()
    {
        var (a, b) = ArrangeRemoteNewer();
        b.Settings.MergeFriendConnect = true;
        // B は同期先に無いユーザーにタグを付ける。id は B の DB のもの。
        Exec(b.Paths.DbV11File, InsertUser("usr_c", "C") + InsertTag("work") + TagUser("usr_c", "work") + TagUser("usr_b", "friend"));

        var result = Push(b);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.NotNull(result.MergeReport);
        Assert.Equal("usr_a:memo-a2,usr_b:,usr_c:", Notes(b.Paths.DbV11File));
        Assert.Equal("usr_a:friend,usr_b:friend,usr_c:work", Taggings(b.Paths.DbV11File));
        // Push の後は要約が取り直され、手元に未 Push の変更は残っていない。
        Assert.Equal(SyncOutcome.NothingToDo, Pull(b, startup: true).Outcome);

        Assert.Equal(SyncOutcome.Success, Pull(a, startup: true).Outcome);
        Assert.Equal("usr_a:friend,usr_b:friend,usr_c:work", Taggings(a.Paths.DbV11File));
    }

    [Fact(DisplayName = "Friend Connect のマージが有効なら、起動時 Pull は手元のメモを残したまま取り込む")]
    public void StartupPullMergesWhenEnabled()
    {
        var (_, b) = ArrangeRemoteNewer();
        b.Settings.MergeFriendConnect = true;
        Exec(b.Paths.DbV11File, "UPDATE User SET note = 'memo-b' WHERE apiId = 'usr_b'");

        var result = Pull(b, startup: true);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.NotNull(result.MergeReport);
        Assert.Equal("usr_a:memo-a2,usr_b:memo-b", Notes(b.Paths.DbV11File));
    }

    [Fact(DisplayName = "Friend Connect のマージが無効なら、起動時 Pull は手元の変更を守って見送り、Push の競合は利用者に返す")]
    public void DisabledKeepsReplaceSemantics()
    {
        var (_, b) = ArrangeRemoteNewer();
        Exec(b.Paths.DbV11File, "UPDATE User SET note = 'memo-b' WHERE apiId = 'usr_b'");

        Assert.Equal(SyncOutcome.LocalChangesPending, Pull(b, startup: true).Outcome);
        Assert.Equal(SyncOutcome.ConflictDetected, Push(b).Outcome);
        Assert.Equal("usr_a:memo-a,usr_b:memo-b", Notes(b.Paths.DbV11File));
    }

    [Fact(DisplayName = "VRCX のマージ設定は Friend Connect に効かない")]
    public void VrcxSettingDoesNotEnableFriendConnect()
    {
        var (_, b) = ArrangeRemoteNewer();
        b.Settings.MergeVrcx = true;
        Exec(b.Paths.DbV11File, "UPDATE User SET note = 'memo-b' WHERE apiId = 'usr_b'");

        Assert.Equal(SyncOutcome.ConflictDetected, Push(b).Outcome);
    }
}
