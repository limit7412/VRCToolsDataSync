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
    /// w1 へのメモ 1 件、稼働区間 2 件 (2 件目は手元で動作中)、1 件目の稼働中の a のステータスと
    /// アバター av1 の着用の履歴が 1 件ずつ、a の最初の滞在中に撮った写真 1 件。
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
            InsertLocationMaster(-5_000, "wrld_1:1~private(usr_a)", "wrld_1", "usr_a") +
            InsertLocation("usr_a", -5_000, 1, 2, "wrld_1") + InsertLocation("usr_a", -5_000, 2, 3, "wrld_1") +
            FavoritePhoto(1000) + NoteWorld("wrld_1", 10, "wn-a") +
            InsertScanRuntime(100_000, 160_000) + InsertScanRuntime(200_000, 250_000) +
            InsertAvatar("avtr_1") + InsertUserStatus("usr_a", 110_000, 120_000) + InsertUserAvatar("usr_a", "avtr_1", 110_000, 150_000) +
            InsertPhoto(1_500, "t-1500.webp", "usr_a", 1));
        var baseline = DatabaseSummary.Compute(ancestor, MergeProfiles.FriendConnect, out var reason);
        Assert.True(baseline is not null, reason);

        var host = Path.Combine(_root, "host.sqlite");
        var local = Path.Combine(_root, "local.sqlite");
        File.Copy(ancestor, host);
        File.Copy(ancestor, local);
        // 手元の id をずらす。外部キーは ON UPDATE CASCADE なので参照も付いて動く。
        // LocationMaster の id はアプリが決める値で PC 間で同じなので、ずらさない。追加専用の履歴 (Location など) の
        // id は前回の同期の後に増えた行の目印にするので、祖先からはずらさない (実機でも消して入れ直されない)。
        Exec(local, "UPDATE User SET id = id + 100; UPDATE Tag SET id = id + 200; UPDATE World SET id = id + 300; UPDATE Avatar SET id = id + 400; " +
                    "UPDATE Photo SET id = id + 600;");
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
        Exec(host, InsertLocation("usr_a", -5_000, 3, 4));

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

    private static string UserStatuses(string db) => string.Join(",", Rows(db,
        "SELECT u.apiId || '@' || s.startAt || '-' || s.endAt FROM UserStatus s JOIN User u ON u.id = s.userId ORDER BY s.startAt"));

    private static string UserAvatars(string db) => string.Join(",", Rows(db,
        "SELECT u.apiId || ':' || a.avatar || '@' || ua.startAt || '-' || ua.endAt FROM UserAvatar ua JOIN User u ON u.id = ua.userId JOIN Avatar a ON a.id = ua.avatarId ORDER BY ua.startAt"));

    /// <summary>
    /// 段階 6 の場面。前回の同期 (250000) の後、手元は動作中の区間を 290000 まで延ばして b の
    /// ステータスを記録し、300000〜400000 にもう一度動かして b のステータスと a の新しいアバター、
    /// 同期先に無いユーザー d のステータスを記録した。同期先は 330000〜380000 に動き、c の
    /// ステータスを記録した。手元も同じ c のステータスを、ポーリングの分ずれた時刻で記録している。
    /// </summary>
    private static void ArrangeHistories(string host, string local)
    {
        Exec(local,
            "UPDATE ScanRuntime SET endAt = 290000, seconds = 90 WHERE startAt = 200000;" + InsertScanRuntime(300_000, 400_000) +
            InsertUserStatus("usr_b", 260_000, 270_000) + InsertUserStatus("usr_b", 320_000, 330_000) +
            InsertAvatar("avtr_2") + InsertUserAvatar("usr_a", "avtr_2", 300_000, 310_000) +
            InsertUser("usr_d", "D") + InsertUserStatus("usr_d", 305_000, 306_000) +
            InsertUserStatus("usr_c", 340_500, 345_000));
        Exec(host, InsertScanRuntime(330_000, 380_000) + InsertUserStatus("usr_c", 340_000, 345_000));
    }

    [Fact(DisplayName = "履歴は id を同期先の値に付け替えて足し、相手が動いていた期間の行は足さない")]
    public void HistoriesAreRemappedAndRowsWhileHostRanAreSkipped()
    {
        var (host, local, baseline) = Arrange();
        ArrangeHistories(host, local);

        var report = Merge(host, local, baseline);

        // c の 340500 の行は同期先の 330000〜380000 の稼働中なので足さない。d はユーザーごと写す。
        Assert.Equal("usr_a@110000-120000,usr_b@260000-270000,usr_d@305000-306000,usr_b@320000-330000,usr_c@340000-345000", UserStatuses(host));
        Assert.Equal("usr_a:avtr_1@110000-150000,usr_a:avtr_2@300000-310000", UserAvatars(host));
        var statuses = Assert.Single(report.Tables, t => t.Table == "UserStatus");
        Assert.Equal((3L, 0L, 1L), (statuses.Inserted, statuses.Updated, statuses.SkippedWhileHostRunning));
        Assert.Equal(1, statuses.CopiedReferences?["User"]);
        var avatars = Assert.Single(report.Tables, t => t.Table == "UserAvatar");
        Assert.Equal((1L, 0L), (avatars.Inserted, avatars.SkippedWhileHostRunning));
        Assert.Equal(1, avatars.CopiedReferences?["Avatar"]);
        // 写したユーザー d は、最後の履歴を指す列を空にして写す。
        Assert.Equal("null|null|null", Assert.Single(Rows(host, "SELECT lastUserAvatarId, lastLocationId, lastUserStatusId FROM User WHERE apiId = 'usr_d'")));
        Assert.Contains("足しませんでした", report.Describe());
        Assert.Equal(0, Scalar(host, "SELECT count(*) FROM UserStatus WHERE userId NOT IN (SELECT id FROM User)"));
    }

    [Fact(DisplayName = "履歴の滞在中に伸びる endAt と seconds は、同期先の同じ行を大きい方の値にする")]
    public void HistoryGrowingColumnsAreExtended()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, "UPDATE UserAvatar SET endAt = 155000, seconds = 45 WHERE startAt = 110000;" +
                    "UPDATE UserStatus SET endAt = 125000, seconds = 15 WHERE startAt = 110000;");

        var report = Merge(host, local, baseline);

        Assert.Equal("usr_a:avtr_1@110000-155000", UserAvatars(host));
        Assert.Equal("usr_a@110000-125000", UserStatuses(host));
        Assert.Equal(45, Scalar(host, "SELECT seconds FROM UserAvatar WHERE startAt = 110000"));
        Assert.Equal(2, Assert.Single(report.Tables, t => t.Table == "UserAvatar").Updated);
        Assert.Equal(2, Assert.Single(report.Tables, t => t.Table == "UserStatus").Updated);
    }

    [Fact(DisplayName = "手元自身の稼働区間が同期先にもあっても、その間に手元が記録した行は足す")]
    public void OwnRuntimeKnownToHostDoesNotGate()
    {
        var (host, local, baseline) = Arrange();
        // 前回の同期で手元の区間 300000〜400000 が同期先に渡っていて (同期先の終了時刻は同期時点の
        // 350000)、手元はその後 360000 に b のステータスを記録した。同期先の表にあるこの区間は
        // 相手が動いていた期間ではない。
        Exec(host, InsertScanRuntime(300_000, 350_000) + InsertScanRuntime(500_000, 510_000));
        Exec(local, InsertScanRuntime(300_000, 400_000) + InsertUserStatus("usr_b", 360_000, 370_000) +
                    InsertUserStatus("usr_b", 505_000, 506_000));

        var report = Merge(host, local, baseline);

        // 505000 の行だけが同期先の区間 500000〜510000 に入る。
        Assert.Equal("usr_a@110000-120000,usr_b@360000-370000", UserStatuses(host));
        var table = Assert.Single(report.Tables, t => t.Table == "UserStatus");
        Assert.Equal((1L, 1L), (table.Inserted, table.SkippedWhileHostRunning));
    }

    [Fact(DisplayName = "履歴は、規則に加える前の要約に祖先が無くても中止せずに和集合を取る")]
    public void HistoriesUnionWithoutAncestor()
    {
        var (host, local, baseline) = Arrange();
        baseline.Tables.Remove("UserStatus");
        baseline.Tables.Remove("UserAvatar");
        baseline.RulesDigest = "older";
        ArrangeHistories(host, local);

        var report = Merge(host, local, baseline);

        Assert.Equal("usr_a@110000-120000,usr_b@260000-270000,usr_d@305000-306000,usr_b@320000-330000,usr_c@340000-345000", UserStatuses(host));
        Assert.Equal("usr_a:avtr_1@110000-150000,usr_a:avtr_2@300000-310000", UserAvatars(host));
        Assert.Equal(3, Assert.Single(report.Tables, t => t.Table == "UserStatus").Inserted);
    }

    [Fact(DisplayName = "履歴は、同じ手元を 2 回合わせても結果が変わらない")]
    public void HistoriesAreIdempotent()
    {
        var (host, local, baseline) = Arrange();
        ArrangeHistories(host, local);

        Merge(host, local, baseline);
        var first = (UserStatuses(host), UserAvatars(host), ScanRuntimes(host), Scalar(host, "SELECT count(*) FROM User"), Scalar(host, "SELECT count(*) FROM Avatar"));
        var again = Merge(host, local, baseline);

        Assert.Equal(first, (UserStatuses(host), UserAvatars(host), ScanRuntimes(host), Scalar(host, "SELECT count(*) FROM User"), Scalar(host, "SELECT count(*) FROM Avatar")));
        Assert.Equal(0, again.Tables.Where(t => t.Table is "UserStatus" or "UserAvatar").Sum(t => t.Inserted + t.Updated + t.Deleted));
    }

    [Fact(DisplayName = "稼働区間のテーブルが同期先に無ければ、履歴のマージを中止する")]
    public void MissingRuntimeTableAborts()
    {
        var (host, local, baseline) = Arrange();
        Exec(host, "DROP TABLE ScanRuntime");

        var report = SqliteMerger.Merge(host, local, MergeProfiles.FriendConnect, baseline, dryRun: false);

        Assert.False(report.Succeeded);
        Assert.Contains("ScanRuntime", report.AbortReason);
    }

    [Fact(DisplayName = "相手の停止後まで続いた出来事は、開始が相手の稼働中でも足す")]
    public void EventContinuingAfterHostStoppedIsKept()
    {
        var (host, local, baseline) = Arrange();
        // 同期先は 330000〜380000 に動いて c のステータスを 340000 から記録し、停止で 380000 に止まった。
        // 手元は同じステータスを 340500 から記録し、同期先の停止後の 400000 まで続いた。
        Exec(host, InsertScanRuntime(330_000, 380_000) + InsertUserStatus("usr_c", 340_000, 380_000));
        Exec(local, InsertScanRuntime(300_000, 400_000) + InsertUserStatus("usr_c", 340_500, 400_000) + InsertUserStatus("usr_b", 350_000, 360_000));

        var report = Merge(host, local, baseline);

        // 停止後の記録を失わないよう c の行は足す (重なる 2 行が残る)。b の行は相手の稼働中に収まるので足さない。
        Assert.Equal("usr_a@110000-120000,usr_c@340000-380000,usr_c@340500-400000", UserStatuses(host));
        var table = Assert.Single(report.Tables, t => t.Table == "UserStatus");
        Assert.Equal((1L, 1L), (table.Inserted, table.SkippedWhileHostRunning));
    }

    [Fact(DisplayName = "同期先に同じ VRChat ID のユーザー行が複数あっても、どちらを指す履歴も同じユーザーとして突き合わせる")]
    public void DuplicateUserRowsInHostAreAllMatched()
    {
        var (host, local, baseline) = Arrange();
        // 同期先では a の行が 2 つあり (apiId は UNIQUE でない)、既存の履歴は id の大きい方を指す。
        Exec(host, InsertUser("usr_a", "A2") +
                   "UPDATE UserStatus SET userId = (SELECT max(id) FROM User WHERE apiId = 'usr_a');" +
                   "UPDATE UserAvatar SET userId = (SELECT max(id) FROM User WHERE apiId = 'usr_a');");
        Exec(local, "UPDATE UserStatus SET endAt = 125000, seconds = 15 WHERE startAt = 110000;" +
                    InsertUserStatus("usr_a", 300_000, 310_000));

        var report = Merge(host, local, baseline);

        // 伸びた endAt は id の大きい方を指す既存行に反映され、110000 の行は二重にならない。
        Assert.Equal("usr_a@110000-125000,usr_a@300000-310000", UserStatuses(host));
        var table = Assert.Single(report.Tables, t => t.Table == "UserStatus");
        Assert.Equal((1L, 2L), (table.Inserted, table.Updated));
        Assert.Null(table.CopiedReferences);
    }

    private static string Locations(string db) => string.Join(",", Rows(db,
        "SELECT u.apiId || '@' || l.locationKey || '@' || l.startAt || '-' || l.endAt FROM Location l JOIN User u ON u.id = l.userId ORDER BY l.startAt, u.apiId"));

    private static string Videos(string db) => string.Join(",", Rows(db,
        "SELECT v.dateTime || ':' || u.apiId || '@' || l.startAt FROM Video v JOIN Location l ON l.id = v.locationId JOIN User u ON u.id = l.userId ORDER BY v.dateTime"));

    private static string Photos(string db) => string.Join(",", Rows(db,
        "SELECT p.shotAt || ':' || p.thumbnail || ':' || coalesce(u.apiId || '@' || l.startAt, '-') FROM Photo p " +
        "LEFT JOIN Location l ON l.id = p.locationId LEFT JOIN User u ON u.id = l.userId ORDER BY p.shotAt"));

    private static string LocationMasters(string db) => string.Join(",", Rows(db,
        "SELECT m.id || ':' || m.location || ':' || coalesce(w.apiId, '-') || ':' || coalesce(o.apiId, '-') || ':' || coalesce(g.apiId, '-') || ':' || m.nUsers " +
        "FROM LocationMaster m LEFT JOIN World w ON w.id = m.worldId LEFT JOIN User o ON o.id = m.ownerId LEFT JOIN \"Group\" g ON g.id = m.groupId ORDER BY m.id"));

    /// <summary>
    /// 段階 7 の場面。前回の同期 (250000) の後、手元は 300000〜400000 に動き、新しいワールド w2 の
    /// インスタンス (id は既存より小さい負の値と、グループ付きの正の値) を記録し、b の滞在 (305000)、
    /// 同期先の稼働中 (330000〜380000) にあたる c の滞在 (340500) と b の滞在 (350000) を記録した。
    /// 同期先は 330000〜380000 に動き、c の同じ滞在を 340000 から記録している。
    /// </summary>
    private static void ArrangeLocations(string host, string local)
    {
        Exec(local,
            InsertScanRuntime(300_000, 400_000) + InsertWorld("wrld_2", "W2") + InsertGroup("grp_1", "G1") +
            InsertLocationMaster(-9_000_000, "wrld_2:7~public", "wrld_2", nUsers: 5) +
            InsertLocationMaster(123_456, "wrld_2:8~group(grp_1)", "wrld_2", groupApiId: "grp_1") +
            InsertLocation("usr_b", -9_000_000, 305_000, 310_000, "wrld_2") +
            InsertLocation("usr_c", 123_456, 340_500, 345_000, "wrld_2") +
            InsertLocation("usr_b", -9_000_000, 350_000, 360_000, "wrld_2"));
        Exec(host, InsertScanRuntime(330_000, 380_000) + InsertLocationMaster(-7_000, "wrld_1:3~public", "wrld_1", nUsers: 2) +
                   InsertLocation("usr_c", -7_000, 340_000, 345_000, "wrld_1"));
    }

    [Fact(DisplayName = "インスタンスのマスタは id で突き合わせて id ごと写し、滞在は付け替えて足し、相手の稼働中の行は足さない")]
    public void LocationsAreRemappedAndMastersAreUnionedById()
    {
        var (host, local, baseline) = Arrange();
        ArrangeLocations(host, local);

        var report = Merge(host, local, baseline);

        // 手元の新しいマスタは id が小さくても足され、ワールドとグループは写される。同期先の人数はそのまま。
        Assert.Equal("-9000000:wrld_2:7~public:wrld_2:-:-:5,-7000:wrld_1:3~public:wrld_1:-:-:2,-5000:wrld_1:1~private(usr_a):wrld_1:usr_a:-:1,123456:wrld_2:8~group(grp_1):wrld_2:-:grp_1:1",
            LocationMasters(host));
        var masters = Assert.Single(report.Tables, t => t.Table == "LocationMaster");
        Assert.Equal(2, masters.Inserted);
        Assert.Equal(1, masters.CopiedReferences?["World"]);
        Assert.Equal(1, masters.CopiedReferences?["Group"]);
        // c の 340500 と b の 350000 の滞在は同期先の稼働中なので足さない。
        Assert.Equal("usr_a@-5000@1-2,usr_a@-5000@2-3,usr_b@-9000000@305000-310000,usr_c@-7000@340000-345000", Locations(host));
        var locations = Assert.Single(report.Tables, t => t.Table == "Location");
        Assert.Equal((1L, 2L), (locations.Inserted, locations.SkippedWhileHostRunning));
        Assert.Equal(0, Scalar(host, "SELECT count(*) FROM Location WHERE locationKey NOT IN (SELECT id FROM LocationMaster) OR worldId NOT IN (SELECT id FROM World)"));
    }

    [Fact(DisplayName = "同じ id のインスタンスの文字列が同期先と手元で違えば、id が別の実体を指しているので中止する")]
    public void LocationMasterWithDifferentContentAborts()
    {
        var (host, local, baseline) = Arrange();
        Exec(host, InsertLocationMaster(777, "wrld_1:2~public", "wrld_1"));
        Exec(local, InsertLocationMaster(777, "wrld_1:9~public", "wrld_1"));

        var report = SqliteMerger.Merge(host, local, MergeProfiles.FriendConnect, baseline, dryRun: false);

        Assert.False(report.Succeeded);
        Assert.Contains("LocationMaster", report.AbortReason);
        Assert.Contains("777", report.AbortReason);
        Assert.Contains("location", report.AbortReason);
    }

    [Fact(DisplayName = "動画は、同じ開始時刻の滞在が同期先に無ければ再生時刻を含む同じユーザーの滞在を指す")]
    public void VideosFallBackToHostStayContainingTheirTime()
    {
        var (host, local, baseline) = Arrange();
        ArrangeLocations(host, local);
        // 307000 の動画は足される b の滞在 (305000) の中、342000 の動画は足されない c の滞在 (340500) の中。
        Exec(local, InsertVideo(307_000, "https://v/1", "usr_b", 305_000) + InsertVideo(342_000, "https://v/2", "usr_c", 340_500));

        var report = Merge(host, local, baseline);

        Assert.Equal("307000:usr_b@305000,342000:usr_c@340000", Videos(host));
        var videos = Assert.Single(report.Tables, t => t.Table == "Video");
        Assert.Equal(2, videos.Inserted);
        Assert.Null(videos.CopiedReferences);
    }

    [Fact(DisplayName = "動画の滞在が同期先に無く、再生時刻を含む滞在も無ければ、滞在を写す")]
    public void VideoWithoutAnyHostStayCopiesTheStay()
    {
        var (host, local, baseline) = Arrange();
        ArrangeLocations(host, local);
        // b の 350000 の滞在は相手の稼働中で足されず、相手も b のその滞在を記録していない。
        Exec(local, InsertVideo(355_000, "https://v/3", "usr_b", 350_000));

        var report = Merge(host, local, baseline);

        Assert.Equal("355000:usr_b@350000", Videos(host));
        Assert.Equal(1, Assert.Single(report.Tables, t => t.Table == "Video").CopiedReferences?["Location"]);
        Assert.Equal("usr_a@-5000@1-2,usr_a@-5000@2-3,usr_b@-9000000@305000-310000,usr_c@-7000@340000-345000,usr_b@-9000000@350000-360000", Locations(host));
    }

    [Fact(DisplayName = "動画のために写す滞在が同期先に無いユーザーやインスタンスを指していれば、それらも先に写す")]
    public void CopyingStayForVideoCopiesItsUserAndInstance()
    {
        var (host, local, baseline) = Arrange();
        ArrangeLocations(host, local);
        // d は同期先に無いユーザーで、メモも無く、滞在は相手の稼働中 (足されない)。その滞在中の動画がある。
        // 滞在のインスタンスも同期先に無く、そのワールドも無い。
        Exec(local, InsertUser("usr_d", "D") + InsertWorld("wrld_3", "W3") + InsertLocationMaster(42, "wrld_3:1~public", "wrld_3") +
                    InsertLocation("usr_d", 42, 352_000, 353_000, "wrld_3") + InsertVideo(352_500, "https://v/4", "usr_d", 352_000));
        // インスタンスのマスタは先に全行が写るので、動画の時点で無いのはユーザーだけ。マスタが写る前の
        // 状態も確かめるため、写しの後に同期先からマスタを消しておく代わりに、ここでは順序どおりに任せる。

        var report = Merge(host, local, baseline);

        Assert.Equal("352500:usr_d@352000", Videos(host));
        var videos = Assert.Single(report.Tables, t => t.Table == "Video");
        Assert.Equal(1, videos.CopiedReferences?["Location"]);
        Assert.Equal(1, videos.CopiedReferences?["User"]);
        Assert.Equal("null|null|null", Assert.Single(Rows(host, "SELECT lastUserAvatarId, lastLocationId, lastUserStatusId FROM User WHERE apiId = 'usr_d'")));
        Assert.Equal(0, Scalar(host, "SELECT count(*) FROM Location WHERE userId NOT IN (SELECT id FROM User) OR locationKey NOT IN (SELECT id FROM LocationMaster)"));
    }

    [Fact(DisplayName = "写真は撮影時刻で合わせ、手元で足した・消したものを伝え、滞在が同期先に無ければ対応を空にする")]
    public void PhotosFollowLocalAddsAndRemovesAndLoseMissingStays()
    {
        var (host, local, baseline) = Arrange();
        ArrangeLocations(host, local);
        Exec(local, "DELETE FROM Photo WHERE shotAt = 1500;" +
                    InsertPhoto(307_500, "t-307500.webp", "usr_b", 305_000) +
                    InsertPhoto(342_500, "t-342500.webp", "usr_c", 340_500) +
                    InsertPhoto(355_500, "t-355500.webp", "usr_b", 350_000) +
                    InsertPhoto(999_000, "t-999000.webp"));
        Exec(host, InsertPhoto(2_500, "t-2500.webp", "usr_a", 2));

        var report = Merge(host, local, baseline);

        // 342500 は相手の記録した c の滞在 (340000) に結び付き、355500 は該当する滞在が無いので対応を空にする (滞在は写さない)。
        Assert.Equal("2500:t-2500.webp:usr_a@2,307500:t-307500.webp:usr_b@305000,342500:t-342500.webp:usr_c@340000,355500:t-355500.webp:-,999000:t-999000.webp:-", Photos(host));
        var photos = Assert.Single(report.Tables, t => t.Table == "Photo");
        Assert.Equal((4L, 0L, 1L), (photos.Inserted, photos.Updated, photos.Deleted));
        Assert.Null(photos.CopiedReferences);
        Assert.Equal(4, Scalar(host, "SELECT count(*) FROM Location"));
        // 元の写真のパスは手元の値のまま。
        Assert.Equal("C:\\photos\\p.png", Assert.Single(Rows(host, "SELECT source FROM Photo WHERE shotAt = 999000")));
    }

    [Fact(DisplayName = "Friend Connect が写真の行を消して入れ直しても、撮影時刻ごとの内容が同じなら変更ではない")]
    public void PhotosReinsertedByAppAreNotChanges()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, "DELETE FROM Photo;" + InsertPhoto(1_500, "t-1500.webp", "usr_a", 1));

        Assert.Empty(DatabaseSummary.Compute(local, MergeProfiles.FriendConnect, out _)!.FindChangedTables(baseline));
        var report = Merge(host, local, baseline);

        Assert.Equal(0, report.ChangedRows);
        Assert.Equal("1500:t-1500.webp:usr_a@1", Photos(host));
    }

    [Fact(DisplayName = "写真は、規則に加える前の要約に祖先が無くても中止せず、手元にしか無いものを足す")]
    public void PhotosUnionWithoutAncestor()
    {
        var (host, local, baseline) = Arrange();
        baseline.Tables.Remove("Photo");
        baseline.RulesDigest = "older";
        Exec(local, "DELETE FROM Photo WHERE shotAt = 1500;" + InsertPhoto(999_000, "t-999000.webp"));
        Exec(host, InsertPhoto(2_500, "t-2500.webp", "usr_a", 2));

        var report = Merge(host, local, baseline);

        // 祖先が無いので手元の削除は伝わらず、両方の写真が残る。
        Assert.Equal("1500:t-1500.webp:usr_a@1,2500:t-2500.webp:usr_a@2,999000:t-999000.webp:-", Photos(host));
        Assert.Equal(1, Assert.Single(report.Tables, t => t.Table == "Photo").Inserted);
    }

    [Fact(DisplayName = "インスタンスへのメモは、インスタンスの id と滞在のユーザー・開始時刻で合わせて付け替える")]
    public void LocationNotesAreRemapped()
    {
        var (host, local, baseline) = Arrange();
        ArrangeLocations(host, local);
        Exec(local, NoteLocation(-5_000, "usr_a", 1, 50, "ln-a") + NoteLocation(-9_000_000, "usr_b", 305_000, 60, "ln-b"));

        var report = Merge(host, local, baseline);

        Assert.Equal(new[] { "-5000|usr_a|1|ln-a", "-9000000|usr_b|305000|ln-b" }, Rows(host,
            "SELECT n.locationMasterId, u.apiId, l.startAt, n.note FROM LocationNote n JOIN Location l ON l.id = n.locationId JOIN User u ON u.id = l.userId ORDER BY n.createdAt"));
        Assert.Equal(2, Assert.Single(report.Tables, t => t.Table == "LocationNote").Inserted);
        Assert.Equal(0, Merge(host, local, baseline).Tables.Where(t => t.Table == "LocationNote").Sum(t => t.Inserted + t.Updated + t.Deleted));
    }

    [Fact(DisplayName = "滞在・動画・写真・メモは、同じ手元を 2 回合わせても結果が変わらない")]
    public void Phase7TablesAreIdempotent()
    {
        var (host, local, baseline) = Arrange();
        ArrangeLocations(host, local);
        Exec(local, InsertVideo(307_000, "https://v/1", "usr_b", 305_000) + InsertVideo(342_000, "https://v/2", "usr_c", 340_500) +
                    InsertVideo(355_000, "https://v/3", "usr_b", 350_000) +
                    InsertPhoto(342_500, "t-342500.webp", "usr_c", 340_500) + NoteLocation(-9_000_000, "usr_b", 305_000, 60, "ln-b"));

        Merge(host, local, baseline);
        var first = (LocationMasters(host), Locations(host), Videos(host), Photos(host), Scalar(host, "SELECT count(*) FROM LocationNote"));
        var again = Merge(host, local, baseline);

        Assert.Equal(first, (LocationMasters(host), Locations(host), Videos(host), Photos(host), Scalar(host, "SELECT count(*) FROM LocationNote")));
        Assert.Equal(0, again.Tables.Where(t => t.Table is "LocationMaster" or "Location" or "Video" or "Photo" or "LocationNote").Sum(t => t.Inserted + t.Updated + t.Deleted));
    }

    [Fact(DisplayName = "滞在の滞在中に伸びる endAt と seconds は、同期先の同じ行を大きい方の値にする")]
    public void LocationGrowingColumnsAreExtended()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, "UPDATE Location SET endAt = 9, seconds = 7 WHERE startAt = 2");

        var report = Merge(host, local, baseline);

        Assert.Equal("usr_a@-5000@1-2,usr_a@-5000@2-9", Locations(host));
        Assert.Equal(2, Assert.Single(report.Tables, t => t.Table == "Location").Updated);
    }
}
