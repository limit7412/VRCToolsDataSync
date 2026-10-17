using Microsoft.Data.Sqlite;
using VRCToolsDataSync.Core.Merge;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// 前回同期時点の要約が、マージの対象になる変化だけを拾うことを確かめる。
/// ツールが自動で書き換える列やキャッシュの変化まで拾うと、ツールを開いただけで
/// 「手元に変更がある」と判定してしまう。
/// </summary>
public sealed class DatabaseSummaryTests : IDisposable
{
    private readonly string _root;

    public DatabaseSummaryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vrctds-summary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Db(string name) => Path.Combine(_root, name);

    private static void Exec(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static DatabaseSummary Summarize(string path, MergeProfile profile)
    {
        var summary = DatabaseSummary.Compute(path, profile, out var reason);
        Assert.True(summary is not null, reason);
        return summary!;
    }

    /// <summary>VRCX のテーブル定義 (index.js) から、規則の対象と対象外を一つずつ持つ DB を作る。</summary>
    private string CreateVrcx()
    {
        var path = Db("vrcx.sqlite3");
        Exec(path, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE usr0123abcd_feed_gps (id INTEGER PRIMARY KEY, created_at TEXT, user_id TEXT, display_name TEXT, location TEXT, world_name TEXT, previous_location TEXT, time INTEGER, group_name TEXT);
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            CREATE TABLE favorite_world (id INTEGER PRIMARY KEY, created_at TEXT, world_id TEXT, group_name TEXT);
            CREATE TABLE cache_world (id TEXT PRIMARY KEY, added_at TEXT, author_id TEXT, author_name TEXT, created_at TEXT, description TEXT, image_url TEXT, name TEXT, release_status TEXT, thumbnail_image_url TEXT, updated_at TEXT, version INTEGER);
            INSERT INTO gamelog_location (created_at, location) VALUES ('2026-10-01T00:00:00Z', 'wrld_a:1');
            INSERT INTO usr0123abcd_feed_gps (created_at, user_id, location) VALUES ('2026-10-01T00:00:00Z', 'usr_x', 'wrld_a:1');
            INSERT INTO memos VALUES ('usr_x', '2026-10-01T00:00:00Z', 'first');
            INSERT INTO favorite_world (created_at, world_id, group_name) VALUES ('2026-10-01T00:00:00Z', 'wrld_a', 'g1');
            INSERT INTO cache_world (id, name, updated_at) VALUES ('wrld_a', 'A', '2026-10-01');
            """);
        return path;
    }

    [Fact(DisplayName = "VRCX: 何も変えなければ、要約は同じになる")]
    public void VrcxUnchanged()
    {
        var path = CreateVrcx();
        var baseline = Summarize(path, MergeProfiles.Vrcx);

        Assert.Empty(Summarize(path, MergeProfiles.Vrcx).FindChangedTables(baseline));
        Assert.Contains("usr0123abcd_feed_gps", baseline.Tables.Keys);
        Assert.DoesNotContain("cache_world", baseline.Tables.Keys);
    }

    [Fact(DisplayName = "VRCX: 履歴の追加とメモの編集とお気に入りの削除を、それぞれ拾う")]
    public void VrcxDetectsChanges()
    {
        var path = CreateVrcx();
        var baseline = Summarize(path, MergeProfiles.Vrcx);

        Exec(path, """
            INSERT INTO gamelog_location (created_at, location) VALUES ('2026-10-02T00:00:00Z', 'wrld_b:1');
            UPDATE memos SET memo = 'second', edited_at = '2026-10-02T00:00:00Z';
            DELETE FROM favorite_world;
            """);

        Assert.Equal(
            new[] { "favorite_world", "gamelog_location", "memos" },
            Summarize(path, MergeProfiles.Vrcx).FindChangedTables(baseline));
    }

    [Fact(DisplayName = "VRCX: 既存の履歴に後から書き込まれた滞在時間を拾う")]
    public void VrcxDetectsLocationTimeUpdate()
    {
        var path = CreateVrcx();
        var baseline = Summarize(path, MergeProfiles.Vrcx);

        // VRCX はワールドを出たときに、入ったときの行へ滞在時間を書き込む。行は増えない。
        Exec(path, "UPDATE gamelog_location SET time = 3600000 WHERE created_at = '2026-10-01T00:00:00Z'");

        Assert.Equal(
            new[] { "gamelog_location" },
            Summarize(path, MergeProfiles.Vrcx).FindChangedTables(baseline));
    }

    [Fact(DisplayName = "VRCX: 新しい形式の通知の追加と既読を拾う")]
    public void VrcxDetectsNotificationsV2()
    {
        var path = CreateVrcx();
        Exec(path, """
            CREATE TABLE usr0123abcd_notifications_v2 (id TEXT PRIMARY KEY, created_at TEXT, updated_at TEXT, expires_at TEXT, type TEXT, link TEXT, link_text TEXT, message TEXT, title TEXT, image_url TEXT, seen INTEGER, sender_user_id TEXT, sender_username TEXT, data TEXT, responses TEXT, details TEXT);
            INSERT INTO usr0123abcd_notifications_v2 (id, created_at, updated_at, seen) VALUES ('not_a', '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z', 0);
            """);
        var baseline = Summarize(path, MergeProfiles.Vrcx);
        Assert.Contains("usr0123abcd_notifications_v2", baseline.Tables.Keys);

        Exec(path, "UPDATE usr0123abcd_notifications_v2 SET seen = 1 WHERE id = 'not_a'");
        var afterSeen = Summarize(path, MergeProfiles.Vrcx);
        Assert.Equal(new[] { "usr0123abcd_notifications_v2" }, afterSeen.FindChangedTables(baseline));

        Exec(path, "INSERT INTO usr0123abcd_notifications_v2 (id, created_at, updated_at, seen) VALUES ('not_b', '2026-10-02T00:00:00Z', '2026-10-02T00:00:00Z', 0)");
        Assert.Equal(
            new[] { "usr0123abcd_notifications_v2" },
            Summarize(path, MergeProfiles.Vrcx).FindChangedTables(afterSeen));
    }

    [Fact(DisplayName = "VRCX: キャッシュの更新は手元の変更として数えない")]
    public void VrcxIgnoresCaches()
    {
        var path = CreateVrcx();
        var baseline = Summarize(path, MergeProfiles.Vrcx);

        Exec(path, "UPDATE cache_world SET name = 'A2', updated_at = '2026-10-02'");

        Assert.Empty(Summarize(path, MergeProfiles.Vrcx).FindChangedTables(baseline));
    }

    [Fact(DisplayName = "規則が前提にした列が無ければ、要約を取らない")]
    public void RefusesUnexpectedSchema()
    {
        var path = Db("vrcx-old.sqlite3");
        Exec(path, "CREATE TABLE memos (user_id TEXT PRIMARY KEY, memo TEXT)");

        var summary = DatabaseSummary.Compute(path, MergeProfiles.Vrcx, out var reason);

        Assert.Null(summary);
        Assert.Contains("memos", reason);
    }

    /// <summary>調査で確かめた Friend Connect の DB から、規則に関わる部分だけを作る。</summary>
    private string CreateFriendConnect(string migration)
    {
        var path = Db("db_1.1.sqlite");
        Exec(path, $"""
            CREATE TABLE _prisma_migrations (id TEXT PRIMARY KEY, checksum TEXT, finished_at DATETIME, migration_name TEXT, logs TEXT, rolled_back_at DATETIME, started_at DATETIME, applied_steps_count INTEGER);
            INSERT INTO _prisma_migrations (id, finished_at, migration_name) VALUES ('1', 1, '20250202000000_init');
            INSERT INTO _prisma_migrations (id, finished_at, migration_name) VALUES ('2', 2, '{migration}');
            CREATE TABLE User (id INTEGER PRIMARY KEY AUTOINCREMENT, apiId TEXT, displayName TEXT, note TEXT, updatedAt DATETIME);
            CREATE TABLE Tag (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, createdAt DATETIME, updatedAt DATETIME);
            INSERT INTO User (apiId, displayName, note, updatedAt) VALUES ('usr_a', 'A', 'memo', 1), ('usr_b', 'B', NULL, 1);
            INSERT INTO Tag (name, createdAt, updatedAt) VALUES ('friend', 1, 1);
            """);
        return path;
    }

    [Fact(DisplayName = "Friend Connect: 自動で書き換わる列は拾わず、メモとタグの変化は拾う")]
    public void FriendConnectDetectsOnlyUserEdits()
    {
        var path = CreateFriendConnect(MergeProfiles.KnownFriendConnectMigrations.First());
        var baseline = Summarize(path, MergeProfiles.FriendConnect);

        // 起動しているだけで起きる書き換え。
        Exec(path, "UPDATE User SET displayName = 'A2', updatedAt = 2; UPDATE Tag SET updatedAt = 2;");
        Assert.Empty(Summarize(path, MergeProfiles.FriendConnect).FindChangedTables(baseline));

        Exec(path, "UPDATE User SET note = 'memo2' WHERE apiId = 'usr_a'; INSERT INTO Tag (name) VALUES ('work');");
        Assert.Equal(
            new[] { "Tag", "User" },
            Summarize(path, MergeProfiles.FriendConnect).FindChangedTables(baseline));
    }

    [Fact(DisplayName = "Friend Connect: 確かめていないスキーマの版なら、要約を取らない")]
    public void FriendConnectRefusesUnknownSchema()
    {
        var path = CreateFriendConnect("20991231000000_future");

        var summary = DatabaseSummary.Compute(path, MergeProfiles.FriendConnect, out var reason);

        Assert.Null(summary);
        Assert.Contains("20991231000000_future", reason);
    }

    [Fact(DisplayName = "保存した要約を読み直しても、同じ内容として比べられる")]
    public void StoreRoundTrips()
    {
        var path = CreateVrcx();
        var store = new BaseSummaryStore(Path.Combine(_root, "base"));
        store.Save("fake|vrcx", Summarize(path, MergeProfiles.Vrcx));

        var loaded = store.Load("fake|vrcx");

        Assert.NotNull(loaded);
        Assert.Empty(Summarize(path, MergeProfiles.Vrcx).FindChangedTables(loaded!));
        Assert.Null(store.Load("fake|friend-connect"));
    }
    [Fact(DisplayName = "同時に保存しても、要約は壊れず一時ファイルも残らない")]
    public void StoreSavesConcurrently()
    {
        var path = CreateVrcx();
        var store = new BaseSummaryStore(Path.Combine(_root, "base"));
        var summary = Summarize(path, MergeProfiles.Vrcx);

        Parallel.For(0, 16, _ => store.Save("fake|vrcx", summary));

        Assert.NotNull(store.Load("fake|vrcx"));
        Assert.Empty(Directory.GetFiles(store.RootDirectory, "*.tmp"));
    }
}
