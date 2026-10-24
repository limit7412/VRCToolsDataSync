using Microsoft.Data.Sqlite;
using VRCToolsDataSync.Core.Merge;
using Xunit;
using static VRCToolsDataSync.Core.Tests.FriendConnectSchema;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// Friend Connect の DB のマージを確かめる。共通の祖先から同期先 (host) と手元 (local) が
/// それぞれ変更を重ねた状態を作る。手元の id は祖先からずらし、2 台の PC で id が
/// 一致しない実際の状態に合わせる。
/// </summary>
public sealed class FriendConnectMergeTests : IDisposable
{
    private readonly string _root;

    public FriendConnectMergeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vrctds-fc-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// 祖先: ユーザー a (メモあり)、b、c、ワールド w1、タグ friend / work、a に friend、
    /// お気に入りグループ fav に a、a の通知設定、a の滞在履歴 2 件、写真のお気に入り 1 件、
    /// w1 へのメモ 1 件、稼働区間 2 件 (2 件目は手元で動作中)。
    /// </summary>
    private (string Host, string Local, DatabaseSummary Baseline) Arrange()
    {
        var ancestor = Path.Combine(_root, "ancestor.sqlite");
        FriendConnectSchema.Create(ancestor);
        Exec(ancestor,
            InsertUser("usr_a", "A", "memo-a", lastLocationId: 1) + InsertUser("usr_b", "B") + InsertUser("usr_c", "C") +
            InsertWorld("wrld_1", "W1") +
            InsertTag("friend") + InsertTag("work") + InsertTag("scenic", type: "world") +
            TagUser("usr_a", "friend") +
            InsertFavoriteGroup("fav", "Favorites") + FavoriteUser("fav", "usr_a") +
            SetNotification("usr_a", "login", "discord") +
            "INSERT INTO Location (userId, startAt) SELECT id, 1 FROM User WHERE apiId = 'usr_a';" +
            "INSERT INTO Location (userId, startAt) SELECT id, 2 FROM User WHERE apiId = 'usr_a';" +
            FavoritePhoto(1000) + NoteWorld("wrld_1", 10, "wn-a") +
            InsertScanRuntime(100_000, 160_000) + InsertScanRuntime(200_000, 250_000));
        var baseline = DatabaseSummary.Compute(ancestor, MergeProfiles.FriendConnect, out var reason);
        Assert.True(baseline is not null, reason);

        var host = Path.Combine(_root, "host.sqlite");
        var local = Path.Combine(_root, "local.sqlite");
        File.Copy(ancestor, host);
        File.Copy(ancestor, local);
        // 手元の id をずらす。外部キーは ON UPDATE CASCADE なので参照も付いて動く。
        Exec(local, "UPDATE User SET id = id + 100; UPDATE Tag SET id = id + 200; UPDATE World SET id = id + 300;");
        return (host, local, baseline!);
    }

    private static MergeReport Merge(string host, string local, DatabaseSummary? baseline, bool dryRun = false)
    {
        var report = SqliteMerger.Merge(host, local, MergeProfiles.FriendConnect, baseline, dryRun);
        Assert.True(report.Succeeded, report.AbortReason);
        return report;
    }

    private static long UserId(string path, string apiId) => Scalar(path, $"SELECT id FROM User WHERE apiId = '{apiId}'");

    [Fact(DisplayName = "メモの編集は User の行を入れ直さず書き換え、履歴やタグ付けを巻き込まない")]
    public void NoteEditIsAppliedInPlace()
    {
        var (host, local, baseline) = Arrange();
        var hostUserId = UserId(host, "usr_a");
        Exec(local, "UPDATE User SET note = 'memo-a2', updatedAt = 9 WHERE apiId = 'usr_a'");
        // 同期先ではその間も履歴が増えている。
        Exec(host, "INSERT INTO Location (userId, startAt) SELECT id, 3 FROM User WHERE apiId = 'usr_a'");

        var report = Merge(host, local, baseline);

        Assert.Equal(new[] { "memo-a2" }, Rows(host, "SELECT note FROM User WHERE apiId = 'usr_a'"));
        Assert.Equal(hostUserId, UserId(host, "usr_a"));
        Assert.Equal(3, Scalar(host, "SELECT count(*) FROM Location"));
        Assert.Equal(1, Scalar(host, "SELECT count(*) FROM UserTag"));
        Assert.Equal(1, Assert.Single(report.Tables, t => t.Table == "User").Updated);
        // 2 回目は足すものが無い。
        Assert.Equal(0, Merge(host, local, baseline).ChangedRows);
    }

    [Fact(DisplayName = "メモを消すと、同期先の User の行は残して note だけ消す")]
    public void ClearedNoteClearsHostNote()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, "UPDATE User SET note = NULL WHERE apiId = 'usr_a'");

        var report = Merge(host, local, baseline);

        Assert.Equal(new[] { "null" }, Rows(host, "SELECT note FROM User WHERE apiId = 'usr_a'"));
        Assert.Equal(3, Scalar(host, "SELECT count(*) FROM User"));
        Assert.Equal(2, Scalar(host, "SELECT count(*) FROM Location"));
        Assert.Equal(1, Assert.Single(report.Tables, t => t.Table == "User").Deleted);
    }

    [Fact(DisplayName = "同期先に無いユーザーのメモは User の行ごと写し、履歴を指す id は消す")]
    public void NoteOnUnknownUserCopiesUserRow()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, InsertUser("usr_d", "D", "memo-d", lastLocationId: 777));

        var report = Merge(host, local, baseline);

        Assert.Equal(new[] { "D|memo-d|null" }, Rows(host, "SELECT displayName, note, lastLocationId FROM User WHERE apiId = 'usr_d'"));
        Assert.Equal(1, Assert.Single(report.Tables, t => t.Table == "User").Inserted);
    }

    [Fact(DisplayName = "同期先で初めてメモが書かれたユーザーは、行があるので書き換えで足りる")]
    public void FirstNoteOnExistingUserUpdates()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, "UPDATE User SET note = 'memo-b' WHERE apiId = 'usr_b'");

        Merge(host, local, baseline);

        Assert.Equal(new[] { "memo-b" }, Rows(host, "SELECT note FROM User WHERE apiId = 'usr_b'"));
        Assert.Equal(1, Scalar(host, "SELECT count(*) FROM User WHERE apiId = 'usr_b'"));
    }

    [Fact(DisplayName = "タグ付けは同期先の id に付け替えて足し、2 回目は足さない")]
    public void UserTagIsTranslatedToHostIds()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, TagUser("usr_b", "work"));

        var report = Merge(host, local, baseline);

        Assert.Equal(
            new[] { "usr_a|friend", "usr_b|work" },
            Rows(host, "SELECT u.apiId, t.name FROM UserTag ut JOIN User u ON u.id = ut.userId JOIN Tag t ON t.id = ut.tagId ORDER BY ut.id"));
        Assert.Equal(1, Assert.Single(report.Tables, t => t.Table == "UserTag").Inserted);
        Assert.Equal(0, Merge(host, local, baseline).ChangedRows);
    }

    [Fact(DisplayName = "手元で外したタグ付けは同期先からも外し、同期先で付けたものは残す")]
    public void RemovedUserTagIsDeleted()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, "DELETE FROM UserTag");
        Exec(host, TagUser("usr_c", "friend"));

        var report = Merge(host, local, baseline);

        Assert.Equal(
            new[] { "usr_c|friend" },
            Rows(host, "SELECT u.apiId, t.name FROM UserTag ut JOIN User u ON u.id = ut.userId JOIN Tag t ON t.id = ut.tagId"));
        Assert.Equal(1, Assert.Single(report.Tables, t => t.Table == "UserTag").Deleted);
    }

    [Fact(DisplayName = "新しいタグとそのタグ付けは、タグを先に写してから付ける")]
    public void NewTagWithTaggingIsCopiedInOrder()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, InsertTag("vip", description: "important", sort: 5) + TagUser("usr_a", "vip") + TagUser("usr_c", "vip"));

        var report = Merge(host, local, baseline);

        Assert.Equal(new[] { "vip|important|5" }, Rows(host, "SELECT name, description, sort FROM Tag WHERE name = 'vip'"));
        Assert.Equal(2, Scalar(host, "SELECT count(*) FROM UserTag ut JOIN Tag t ON t.id = ut.tagId WHERE t.name = 'vip'"));
        Assert.Equal(1, Assert.Single(report.Tables, t => t.Table == "Tag").Inserted);
        Assert.Equal(2, Assert.Single(report.Tables, t => t.Table == "UserTag").Inserted);
    }

    [Fact(DisplayName = "タグの説明の変更は Tag の行を入れ直さず、タグ付けを巻き込まない")]
    public void TagEditKeepsTaggings()
    {
        var (host, local, baseline) = Arrange();
        var hostTagId = Scalar(host, "SELECT id FROM Tag WHERE name = 'friend'");
        Exec(local, "UPDATE Tag SET description = 'close friends', updatedAt = 9 WHERE name = 'friend'");

        Merge(host, local, baseline);

        Assert.Equal(new[] { "close friends" }, Rows(host, "SELECT description FROM Tag WHERE name = 'friend'"));
        Assert.Equal(hostTagId, Scalar(host, "SELECT id FROM Tag WHERE name = 'friend'"));
        Assert.Equal(1, Scalar(host, "SELECT count(*) FROM UserTag"));
    }

    [Fact(DisplayName = "ワールドのタグ付けで、同期先に無いワールドは行を写してから付ける")]
    public void WorldTagCopiesMissingWorld()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, InsertWorld("wrld_2", "W2") + TagWorld("wrld_2", "scenic") + TagWorld("wrld_1", "scenic"));

        var report = Merge(host, local, baseline);

        Assert.Equal(
            new[] { "wrld_1|scenic", "wrld_2|scenic" },
            Rows(host, "SELECT w.apiId, t.name FROM WorldTag wt JOIN World w ON w.id = wt.worldId JOIN Tag t ON t.id = wt.tagId ORDER BY w.apiId"));
        Assert.Equal(new[] { "W2" }, Rows(host, "SELECT name FROM World WHERE apiId = 'wrld_2'"));
        var table = Assert.Single(report.Tables, t => t.Table == "WorldTag");
        Assert.Equal(2, table.Inserted);
        Assert.Equal(1, table.CopiedReferences!["World"]);
        Assert.Contains("World 1", report.Describe());
    }

    [Fact(DisplayName = "新しいお気に入りグループとその中身は、グループを先に写してから足す")]
    public void FavoriteGroupWithEntries()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, InsertFavoriteGroup("worlds", "Worlds", type: "world") + FavoriteWorld("worlds", "wrld_1") + FavoriteUser("fav", "usr_c"));

        var report = Merge(host, local, baseline);

        Assert.Equal(
            new[] { "fav|usr_a|null", "fav|usr_c|null", "worlds|null|wrld_1" },
            Rows(host, "SELECT f.groupKey, u.apiId, w.apiId FROM FavoriteLocal f LEFT JOIN User u ON u.id = f.userId LEFT JOIN World w ON w.id = f.worldId ORDER BY f.groupKey, u.apiId"));
        Assert.Equal(1, Assert.Single(report.Tables, t => t.Table == "FavoriteLocalGroup").Inserted);
        Assert.Equal(2, Assert.Single(report.Tables, t => t.Table == "FavoriteLocal").Inserted);
        Assert.Equal(0, Merge(host, local, baseline).ChangedRows);
    }

    [Fact(DisplayName = "通知設定の値の変更は手元の内容になる")]
    public void NotificationSettingValueFollowsLocal()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, "UPDATE UserNotificationSetting SET value = 'https://hook' WHERE type = 'login'");
        Exec(local, SetNotification("usr_b", "move", "webhook", "x"));

        var report = Merge(host, local, baseline);

        Assert.Equal(
            new[] { "usr_a|login|https://hook", "usr_b|move|x" },
            Rows(host, "SELECT u.apiId, s.type, s.value FROM UserNotificationSetting s JOIN User u ON u.id = s.userId ORDER BY u.apiId"));
        var table = Assert.Single(report.Tables, t => t.Table == "UserNotificationSetting");
        Assert.Equal(1, table.Updated);
        Assert.Equal(1, table.Inserted);
    }

    [Fact(DisplayName = "両方で別のメモを書いていたら、編集時刻が無いので同期先の内容を残す")]
    public void ConflictingNotesKeepHost()
    {
        var (host, local, baseline) = Arrange();
        Exec(host, "UPDATE User SET note = 'host' WHERE apiId = 'usr_a'");
        Exec(local, "UPDATE User SET note = 'local' WHERE apiId = 'usr_a'");

        var report = Merge(host, local, baseline);

        Assert.Equal(new[] { "host" }, Rows(host, "SELECT note FROM User WHERE apiId = 'usr_a'"));
        var conflict = Assert.Single(report.Conflicts);
        Assert.False(conflict.KeptLocal);
        Assert.Contains("同期先", report.Describe());
    }

    [Fact(DisplayName = "同期先だけが変えたものは、手元が変えていなければそのまま残る")]
    public void HostOnlyChangesSurvive()
    {
        var (host, local, baseline) = Arrange();
        Exec(host, "UPDATE User SET note = 'host' WHERE apiId = 'usr_a'; DELETE FROM UserTag;" + TagUser("usr_b", "work"));
        // 手元では起動しているだけで起きる書き換え。
        Exec(local, "UPDATE User SET updatedAt = 99, displayName = 'A!'");

        var report = Merge(host, local, baseline);

        Assert.Equal(0, report.ChangedRows);
        Assert.Equal(new[] { "host" }, Rows(host, "SELECT note FROM User WHERE apiId = 'usr_a'"));
        Assert.Equal(new[] { "usr_b|work" },
            Rows(host, "SELECT u.apiId, t.name FROM UserTag ut JOIN User u ON u.id = ut.userId JOIN Tag t ON t.id = ut.tagId"));
    }

    [Fact(DisplayName = "祖先が無ければ 2-way になり、手元で外したタグ付けは同期先から戻る")]
    public void WithoutBaselineDeletionsDoNotPropagate()
    {
        var (host, local, _) = Arrange();
        Exec(local, "DELETE FROM UserTag;" + TagUser("usr_b", "work"));

        Merge(host, local, null);

        Assert.Equal(2, Scalar(host, "SELECT count(*) FROM UserTag"));
    }

    [Fact(DisplayName = "dry-run は結果を数えるだけで同期先の写しにも書かない")]
    public void DryRunWritesNothing()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, TagUser("usr_b", "work") + "UPDATE User SET note = 'x' WHERE apiId = 'usr_a'");

        var report = Merge(host, local, baseline, dryRun: true);

        Assert.Equal(2, report.ChangedRows);
        Assert.Equal(1, Scalar(host, "SELECT count(*) FROM UserTag"));
        Assert.Equal(new[] { "memo-a" }, Rows(host, "SELECT note FROM User WHERE apiId = 'usr_a'"));
    }

    [Fact(DisplayName = "前回の要約が変化を見るだけの古い規則で取られていれば、内容が違うテーブルは中止する")]
    public void OldWatchedSummaryAbortsWhenDifferent()
    {
        var (host, local, baseline) = Arrange();
        baseline.RulesDigest = "old";
        baseline.Tables["Tag"] = new TableSummary { Kind = MergeTableKind.Watched, RowCount = 3, Digest = "x", Columns = baseline.Tables["Tag"].Columns };
        Exec(local, InsertTag("vip"));

        var report = SqliteMerger.Merge(host, local, MergeProfiles.FriendConnect, baseline, dryRun: true);

        Assert.False(report.Succeeded);
        Assert.Contains("Tag", report.AbortReason);
        Assert.Contains("マージの対象にしていません", report.AbortReason);

        // 内容が同じなら合わせるものが無いので進む。
        Exec(local, "DELETE FROM Tag WHERE name = 'vip'");
        Assert.True(SqliteMerger.Merge(host, local, MergeProfiles.FriendConnect, baseline, dryRun: true).Succeeded);
    }

    [Fact(DisplayName = "確かめていない版のスキーマなら中止する")]
    public void UnknownSchemaVersionAborts()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, "INSERT INTO _prisma_migrations (id, checksum, finished_at, migration_name) VALUES ('3', '', 3, '20991231000000_future')");

        var report = SqliteMerger.Merge(host, local, MergeProfiles.FriendConnect, baseline, dryRun: true);

        Assert.False(report.Succeeded);
        Assert.Contains("20991231000000_future", report.AbortReason);
    }

    [Fact(DisplayName = "参照先が手元にも無い行は制約に掛かるので、取り消して中止する")]
    public void DanglingReferenceAborts()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, "PRAGMA foreign_keys = OFF; INSERT INTO UserTag (userId, tagId, updatedAt) VALUES (9999, 201, 1);");

        var report = SqliteMerger.Merge(host, local, MergeProfiles.FriendConnect, baseline, dryRun: false);

        Assert.False(report.Succeeded);
        Assert.Contains("UserTag", report.AbortReason);
        Assert.Equal(1, Scalar(host, "SELECT count(*) FROM UserTag"));
    }

    [Fact(DisplayName = "要約は id ではなく自然キーで取るので、id がずれた写しどうしで同じになる")]
    public void SummaryIgnoresIds()
    {
        var (host, local, baseline) = Arrange();

        var hostSummary = DatabaseSummary.Compute(host, MergeProfiles.FriendConnect, out _)!;
        var localSummary = DatabaseSummary.Compute(local, MergeProfiles.FriendConnect, out _)!;

        Assert.Empty(hostSummary.FindChangedTables(baseline));
        Assert.Empty(localSummary.FindChangedTables(baseline));
        Exec(local, TagUser("usr_b", "work"));
        Assert.Equal(new[] { "UserTag" }, DatabaseSummary.Compute(local, MergeProfiles.FriendConnect, out _)!.FindChangedTables(baseline));
    }

    [Fact(DisplayName = "手元で消したグループに同期先だけでお気に入りが足されていたら、グループを残す")]
    public void KeepsGroupDeletedLocallyWhenHostAddedFavorites()
    {
        // 祖先: グループ fav に usr_a。手元は fav を消した (連鎖でお気に入りも消える)。
        // 同期先は fav に usr_b を足した。
        var (host, local, baseline) = Arrange();
        Exec(local, "DELETE FROM FavoriteLocalGroup WHERE groupKey = 'fav'");
        Exec(host, FavoriteUser("fav", "usr_b"));

        var report = Merge(host, local, baseline);

        Assert.Equal(1, Scalar(host, "SELECT count(*) FROM FavoriteLocalGroup WHERE groupKey = 'fav'"));
        // 手元で消した usr_a のお気に入りは消え、同期先で足した usr_b は残る。
        Assert.Equal("usr_b", Rows(host, "SELECT u.apiId FROM FavoriteLocal f JOIN User u ON u.id = f.userId WHERE f.groupKey = 'fav'").Single());
        Assert.Single(report.KeptReferencedRows);
        Assert.Equal(0, report.Tables.Single(t => t.Table == "FavoriteLocalGroup").Deleted);
        Assert.Contains("残しました", report.Describe());
    }

    [Fact(DisplayName = "手元で消したグループを同期先が触っていなければ、お気に入りごと消す")]
    public void DeletesGroupDeletedLocallyWhenHostUnchanged()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, "DELETE FROM FavoriteLocalGroup WHERE groupKey = 'fav'");

        var report = Merge(host, local, baseline);

        Assert.Equal(0, Scalar(host, "SELECT count(*) FROM FavoriteLocalGroup WHERE groupKey = 'fav'"));
        Assert.Equal(0, Scalar(host, "SELECT count(*) FROM FavoriteLocal"));
        Assert.Empty(report.KeptReferencedRows);
        Assert.Equal(1, report.Tables.Single(t => t.Table == "FavoriteLocalGroup").Deleted);
    }

    [Fact(DisplayName = "手元でタグ名を変えても、同期先だけで旧名を付けたユーザーがいれば旧名のタグを残す")]
    public void KeepsRenamedTagWhenHostTaggedWithOldName()
    {
        // 祖先: タグ friend を usr_a に付けている。手元は friend を buddy に改名。同期先は usr_b に friend を付けた。
        var (host, local, baseline) = Arrange();
        Exec(local, "UPDATE Tag SET name = 'buddy' WHERE name = 'friend'");
        Exec(host, TagUser("usr_b", "friend"));

        Merge(host, local, baseline);

        Assert.Equal("buddy,friend,work", string.Join(",", Rows(host, "SELECT name FROM Tag WHERE type = 'user' ORDER BY name")));
        Assert.Equal("usr_a:buddy,usr_b:friend", string.Join(",", Rows(host,
            "SELECT u.apiId || ':' || t.name FROM UserTag ut JOIN User u ON u.id = ut.userId JOIN Tag t ON t.id = ut.tagId ORDER BY u.apiId")));
    }

    [Fact(DisplayName = "手元でタグ名を変え、同期先が旧名を触っていなければ、旧名のタグは消える")]
    public void RenamedTagRemovesOldNameWhenHostUnchanged()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, "UPDATE Tag SET name = 'buddy' WHERE name = 'friend'");

        Merge(host, local, baseline);

        Assert.Equal("buddy,work", string.Join(",", Rows(host, "SELECT name FROM Tag WHERE type = 'user' ORDER BY name")));
        Assert.Equal("usr_a:buddy", string.Join(",", Rows(host,
            "SELECT u.apiId || ':' || t.name FROM UserTag ut JOIN User u ON u.id = ut.userId JOIN Tag t ON t.id = ut.tagId ORDER BY u.apiId")));
    }

    private static string PhotoFavorites(string db) => string.Join(",", Rows(db, "SELECT shotAt FROM PhotoFavorite ORDER BY shotAt"));

    private static string WorldNotes(string db) => string.Join(",", Rows(db,
        "SELECT w.apiId || ':' || n.createdAt || ':' || n.note FROM WorldNote n JOIN World w ON w.id = n.worldId ORDER BY w.apiId, n.createdAt"));

    private static string ScanRuntimes(string db) => string.Join(",", Rows(db, "SELECT startAt || '-' || endAt || '/' || seconds FROM ScanRuntime ORDER BY startAt"));

    [Fact(DisplayName = "写真のお気に入りは、手元で足した・消した撮影時刻を同期先に伝える")]
    public void PhotoFavoritesFollowLocalAddsAndRemoves()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, "DELETE FROM PhotoFavorite WHERE shotAt = 1000;" + FavoritePhoto(2000));
        Exec(host, FavoritePhoto(3000));

        var report = Merge(host, local, baseline);

        Assert.Equal("2000,3000", PhotoFavorites(host));
        var table = Assert.Single(report.Tables, t => t.Table == "PhotoFavorite");
        Assert.Equal((1L, 0L, 1L), (table.Inserted, table.Updated, table.Deleted));
    }

    [Fact(DisplayName = "ワールドのメモは同期先の World の id に付け替えて足し、ワールドが無ければ写す")]
    public void WorldNotesAreRemappedAndMissingWorldsCopied()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, InsertWorld("wrld_2", "W2") + NoteWorld("wrld_2", 20, "wn-b") + NoteWorld("wrld_1", 30, "wn-c") +
                    "UPDATE WorldNote SET note = 'wn-a2', updatedAt = 9 WHERE createdAt = 10");
        var hostWorldId = Scalar(host, "SELECT id FROM World WHERE apiId = 'wrld_1'");

        var report = Merge(host, local, baseline);

        Assert.Equal("wrld_1:10:wn-a2,wrld_1:30:wn-c,wrld_2:20:wn-b", WorldNotes(host));
        Assert.Equal(hostWorldId, Scalar(host, "SELECT id FROM World WHERE apiId = 'wrld_1'"));
        Assert.Equal(1, Scalar(host, "SELECT count(*) FROM World WHERE apiId = 'wrld_2'"));
        var table = Assert.Single(report.Tables, t => t.Table == "WorldNote");
        Assert.Equal((2L, 1L, 0L), (table.Inserted, table.Updated, table.Deleted));
        Assert.Equal(1, table.CopiedReferences?["World"]);
    }

    [Fact(DisplayName = "両方で同じワールドのメモを書き換えていたら、同期先の内容を残す")]
    public void ConflictingWorldNotesKeepHost()
    {
        var (host, local, baseline) = Arrange();
        Exec(host, "UPDATE WorldNote SET note = 'host' WHERE createdAt = 10");
        Exec(local, "UPDATE WorldNote SET note = 'local' WHERE createdAt = 10");

        var report = Merge(host, local, baseline);

        Assert.Equal("wrld_1:10:host", WorldNotes(host));
        var conflict = Assert.Single(report.Conflicts);
        Assert.Equal("WorldNote", conflict.Table);
        Assert.False(conflict.KeptLocal);
    }

    [Fact(DisplayName = "稼働区間は和集合を取り、動作中に伸びた endAt と seconds は大きい方を残す")]
    public void ScanRuntimesAreUnionedAndGrowingColumnsExtended()
    {
        var (host, local, baseline) = Arrange();
        // 手元では前回の同期の後も 2 件目が動き続け、その後もう一度起動した。同期先でも 1 回起動した。
        Exec(local, "UPDATE ScanRuntime SET endAt = 290000, seconds = 90 WHERE startAt = 200000;" + InsertScanRuntime(300_000, 310_000));
        Exec(host, InsertScanRuntime(400_000, 410_000));

        var report = Merge(host, local, baseline);

        Assert.Equal("100000-160000/60,200000-290000/90,300000-310000/10,400000-410000/10", ScanRuntimes(host));
        var table = Assert.Single(report.Tables, t => t.Table == "ScanRuntime");
        Assert.Equal((1L, 2L, 0L), (table.Inserted, table.Updated, table.Deleted));
        Assert.False(table.Renumbered);
    }

    [Fact(DisplayName = "稼働区間は、規則に加える前の要約に祖先が無くても中止せずに和集合を取る")]
    public void ScanRuntimesUnionWithoutAncestor()
    {
        var (host, local, baseline) = Arrange();
        // ScanRuntime を規則に持たなかった版のツールが取った要約。両方の PC で起動した回数は違う。
        baseline.Tables.Remove("ScanRuntime");
        baseline.RulesDigest = "older";
        Exec(local, InsertScanRuntime(300_000, 310_000));
        Exec(host, InsertScanRuntime(400_000, 410_000));

        var report = Merge(host, local, baseline);

        Assert.Equal("100000-160000/60,200000-250000/50,300000-310000/10,400000-410000/10", ScanRuntimes(host));
        var table = Assert.Single(report.Tables, t => t.Table == "ScanRuntime");
        Assert.Equal(1, table.Inserted);
    }

    [Fact(DisplayName = "写真のお気に入りは、規則に加える前の要約に祖先が無く内容が違えば中止する")]
    public void PhotoFavoritesAbortWithoutAncestor()
    {
        var (host, local, baseline) = Arrange();
        baseline.Tables.Remove("PhotoFavorite");
        baseline.RulesDigest = "older";
        // 祖先なしでは、手元で消したお気に入りを同期先から戻してしまう。
        Exec(local, "DELETE FROM PhotoFavorite WHERE shotAt = 1000");

        var report = SqliteMerger.Merge(host, local, MergeProfiles.FriendConnect, baseline, dryRun: false);

        Assert.False(report.Succeeded);
        Assert.Contains("PhotoFavorite", report.AbortReason);
        Assert.Equal("1000", PhotoFavorites(host));
    }

    [Fact(DisplayName = "写真のお気に入り・ワールドのメモ・稼働区間は、同じ手元を 2 回合わせても結果が変わらない")]
    public void Phase5TablesAreIdempotent()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, FavoritePhoto(2000) + InsertWorld("wrld_2", "W2") + NoteWorld("wrld_2", 20, "wn-b") +
                    "UPDATE ScanRuntime SET endAt = 290000, seconds = 90 WHERE startAt = 200000;" + InsertScanRuntime(300_000, 310_000));

        Merge(host, local, baseline);
        var first = (PhotoFavorites(host), WorldNotes(host), ScanRuntimes(host));
        var again = Merge(host, local, baseline);

        Assert.Equal(first, (PhotoFavorites(host), WorldNotes(host), ScanRuntimes(host)));
        Assert.Equal(0, again.Tables.Where(t => t.Table is "PhotoFavorite" or "WorldNote" or "ScanRuntime").Sum(t => t.Inserted + t.Updated + t.Deleted));
    }
}
