using Microsoft.Data.Sqlite;
using VRCToolsDataSync.Core.Merge;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// VRCX の DB のマージを確かめる。共通の祖先 (前回同期した時点) から、同期先と手元が
/// それぞれ変更を重ねた状態を作り、同期先を土台に手元の変更が足されることを見る。
/// </summary>
public sealed class SqliteMergerTests : IDisposable
{
    private const string Schema = """
        CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
        CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
        CREATE TABLE favorite_world (id INTEGER PRIMARY KEY, created_at TEXT, world_id TEXT, group_name TEXT);
        CREATE TABLE cache_world (id TEXT PRIMARY KEY, name TEXT);
        INSERT INTO gamelog_location (created_at, location) VALUES ('2026-10-01T00:00:00Z', 'wrld_a:1');
        INSERT INTO memos VALUES ('usr_a', '2026-10-01T00:00:00Z', 'a0'), ('usr_b', '2026-10-01T00:00:00Z', 'b0'), ('usr_c', '2026-10-01T00:00:00Z', 'c0'), ('usr_d', '2026-10-01T00:00:00Z', 'd0');
        INSERT INTO favorite_world (created_at, world_id, group_name) VALUES ('2026-10-01T00:00:00Z', 'wrld_a', 'g1');
        INSERT INTO cache_world VALUES ('wrld_a', 'A');
        """;

    private readonly string _root;

    public SqliteMergerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vrctds-merger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static void Exec(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<string> Rows(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount)
                .Select(i => reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i)))));
        }
        return rows;
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>
    /// 祖先を作り、その写しを同期先 (host) と手元 (local) にする。返す要約は祖先のもの。
    /// </summary>
    private (string Host, string Local, DatabaseSummary Baseline) Arrange()
    {
        var ancestor = Path.Combine(_root, "ancestor.sqlite3");
        Exec(ancestor, Schema);
        var baseline = DatabaseSummary.Compute(ancestor, MergeProfiles.Vrcx, out _)!;
        var host = Path.Combine(_root, "host.sqlite3");
        var local = Path.Combine(_root, "local.sqlite3");
        File.Copy(ancestor, host);
        File.Copy(ancestor, local);
        return (host, local, baseline);
    }

    private static MergeReport Merge(string host, string local, DatabaseSummary? baseline, bool dryRun = false)
    {
        var report = SqliteMerger.Merge(host, local, MergeProfiles.Vrcx, baseline, dryRun);
        Assert.True(report.Succeeded, report.AbortReason);
        return report;
    }

    [Fact(DisplayName = "履歴は和集合になり、出来事の順に id が振り直される")]
    public void AppendOnlyUnionInEventOrder()
    {
        var (host, local, baseline) = Arrange();
        Exec(host, "INSERT INTO gamelog_location (created_at, location) VALUES ('2026-10-03T00:00:00Z', 'wrld_host:1')");
        Exec(local, "INSERT INTO gamelog_location (created_at, location) VALUES ('2026-10-02T00:00:00Z', 'wrld_local:1')");

        var report = Merge(host, local, baseline);

        Assert.Equal(
            new[] { "wrld_a:1", "wrld_local:1", "wrld_host:1" },
            Rows(host, "SELECT location FROM gamelog_location ORDER BY id"));
        var table = Assert.Single(report.Tables, t => t.Table == "gamelog_location");
        Assert.Equal(1, table.Inserted);
        Assert.True(table.Renumbered);
        // 突き合わせのために作った索引は残さない。
        Assert.Empty(Rows(host, "SELECT name FROM sqlite_master WHERE name LIKE 'vrctds_%'"));
    }

    [Fact(DisplayName = "手元で末尾の履歴を消して rowid が再利用されたら、消した行を戻さずに中止する")]
    public void AppendOnlyAbortsOnReusedRowId()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, """
            DELETE FROM gamelog_location WHERE location = 'wrld_a:1';
            INSERT INTO gamelog_location (created_at, location) VALUES ('2026-10-02T00:00:00Z', 'wrld_local:1');
            """);

        var report = SqliteMerger.Merge(host, local, MergeProfiles.Vrcx, baseline, dryRun: false);

        Assert.False(report.Succeeded);
        Assert.Contains("gamelog_location", report.AbortReason);
    }

    [Fact(DisplayName = "id を振り直したマージの後も、土台の版の要約を縮めれば次のマージの祖先に使える")]
    public void TrimmedBaselineSurvivesRenumber()
    {
        var (host, local, baseline) = Arrange();
        Exec(host, "INSERT INTO gamelog_location (created_at, location) VALUES ('2026-10-03T00:00:00Z', 'wrld_host:1')");
        Exec(local, "INSERT INTO gamelog_location (created_at, location) VALUES ('2026-10-02T00:00:00Z', 'wrld_local:1')");
        var nextRemote = Path.Combine(_root, "next-remote.sqlite3");
        File.Copy(host, nextRemote);
        var hostBaseline = DatabaseSummary.Compute(host, MergeProfiles.Vrcx, out _)!;

        var report = Merge(host, local, baseline);
        DatabaseSummary.TrimRenumbered(hostBaseline, host, MergeProfiles.Vrcx, report);

        // マージ結果で手元を置き換えた後、Push が競合して、同期先が先に進んだ版とやり直す。
        Exec(nextRemote, "INSERT INTO gamelog_location (created_at, location) VALUES ('2026-10-04T00:00:00Z', 'wrld_next:1')");
        Merge(nextRemote, host, hostBaseline);

        Assert.Equal(
            new[] { "wrld_a:1", "wrld_local:1", "wrld_host:1", "wrld_next:1" },
            Rows(nextRemote, "SELECT location FROM gamelog_location ORDER BY id"));
    }

    [Fact(DisplayName = "手元で途中の履歴を消していたら、消した行を戻さずに中止する")]
    public void AppendOnlyAbortsOnLocalDeletion()
    {
        var (host, local, _) = Arrange();
        const string add = "INSERT INTO gamelog_location (created_at, location) VALUES ('2026-10-02T00:00:00Z', 'wrld_b:1')";
        Exec(host, add);
        Exec(local, add);
        var baseline = DatabaseSummary.Compute(local, MergeProfiles.Vrcx, out _)!;
        Exec(local, "DELETE FROM gamelog_location WHERE location = 'wrld_a:1'");

        var report = SqliteMerger.Merge(host, local, MergeProfiles.Vrcx, baseline, dryRun: false);

        Assert.False(report.Succeeded);
        Assert.Contains("gamelog_location", report.AbortReason);
    }

    [Fact(DisplayName = "両方に同じ出来事があれば、二重にしない")]
    public void AppendOnlyDoesNotDuplicate()
    {
        var (host, local, baseline) = Arrange();
        const string sameEvent = "INSERT INTO gamelog_location (created_at, location) VALUES ('2026-10-02T00:00:00Z', 'wrld_b:1')";
        Exec(host, sameEvent);
        Exec(local, sameEvent);

        Merge(host, local, baseline);

        Assert.Equal(2, Rows(host, "SELECT id FROM gamelog_location").Count);
    }

    [Fact(DisplayName = "後から書き込まれた滞在時間は、大きい方を残す")]
    public void GrowingColumnsKeepLarger()
    {
        var (host, local, baseline) = Arrange();
        Exec(host, """
            INSERT INTO gamelog_location (created_at, location, time) VALUES ('2026-10-02T00:00:00Z', 'wrld_b:1', 0);
            """);
        File.Delete(local);
        File.Copy(host, local);
        baseline = DatabaseSummary.Compute(local, MergeProfiles.Vrcx, out _)!;
        // 手元はワールドを出たときに滞在時間を書き込んだ。同期先は書き込んでいない。
        Exec(local, "UPDATE gamelog_location SET time = 3600000 WHERE location = 'wrld_b:1'");
        // 同期先の方が長い滞在時間を持つ行は、そのまま残る。
        Exec(host, "UPDATE gamelog_location SET time = 5000 WHERE location = 'wrld_a:1'");
        Exec(local, "UPDATE gamelog_location SET time = 1000 WHERE location = 'wrld_a:1'");

        var report = Merge(host, local, baseline);

        Assert.Equal(
            new[] { "wrld_a:1|5000", "wrld_b:1|3600000" },
            Rows(host, "SELECT location, time FROM gamelog_location ORDER BY id"));
        Assert.Equal(1, Assert.Single(report.Tables, t => t.Table == "gamelog_location").Updated);
    }

    [Fact(DisplayName = "新しい形式の通知は、手元で既読にしたものを伝える")]
    public void NotificationsV2PropagateSeen()
    {
        var (host, local, _) = Arrange();
        const string create = """
            CREATE TABLE usr0123abcd_notifications_v2 (id TEXT PRIMARY KEY, created_at TEXT, updated_at TEXT, expires_at TEXT, type TEXT, link TEXT, link_text TEXT, message TEXT, title TEXT, image_url TEXT, seen INTEGER, sender_user_id TEXT, sender_username TEXT, data TEXT, responses TEXT, details TEXT);
            INSERT INTO usr0123abcd_notifications_v2 (id, created_at, updated_at, seen) VALUES ('not_a', '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z', 0);
            """;
        Exec(host, create);
        Exec(local, create);
        var baseline = DatabaseSummary.Compute(local, MergeProfiles.Vrcx, out _)!;
        Exec(local, "UPDATE usr0123abcd_notifications_v2 SET seen = 1 WHERE id = 'not_a'");

        Merge(host, local, baseline);

        Assert.Equal(new[] { "1" }, Rows(host, "SELECT seen FROM usr0123abcd_notifications_v2"));
    }

    [Fact(DisplayName = "新しい形式の通知は、既読と期限切れを相手の新しい内容で取り消さない")]
    public void NotificationsV2KeepSeenAndExpiryOnConflict()
    {
        var (host, local, _) = Arrange();
        const string create = """
            CREATE TABLE usr0123abcd_notifications_v2 (id TEXT PRIMARY KEY, created_at TEXT, updated_at TEXT, expires_at TEXT, type TEXT, link TEXT, link_text TEXT, message TEXT, title TEXT, image_url TEXT, seen INTEGER, sender_user_id TEXT, sender_username TEXT, data TEXT, responses TEXT, details TEXT);
            INSERT INTO usr0123abcd_notifications_v2 (id, created_at, updated_at, expires_at, message, seen) VALUES ('not_a', '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z', '2026-11-01T00:00:00Z', 'm0', 0);
            INSERT INTO usr0123abcd_notifications_v2 (id, created_at, updated_at, expires_at, message, seen) VALUES ('not_b', '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z', '2026-11-01T00:00:00Z', 'm0', 0);
            """;
        Exec(host, create);
        Exec(local, create);
        var baseline = DatabaseSummary.Compute(local, MergeProfiles.Vrcx, out _)!;
        // a: 手元で期限切れにし、同期先は新しい内容を受け取った (updated_at は同期先が新しい)。
        // b: 同期先で既読にし、手元は新しい内容を受け取った (updated_at は手元が新しい)。
        Exec(local, """
            UPDATE usr0123abcd_notifications_v2 SET expires_at = '2026-10-05T00:00:00.000Z', seen = 1 WHERE id = 'not_a';
            UPDATE usr0123abcd_notifications_v2 SET updated_at = '2026-10-06T00:00:00Z', message = 'm1' WHERE id = 'not_b';
            """);
        Exec(host, """
            UPDATE usr0123abcd_notifications_v2 SET updated_at = '2026-10-06T00:00:00Z', message = 'm1' WHERE id = 'not_a';
            UPDATE usr0123abcd_notifications_v2 SET seen = 1 WHERE id = 'not_b';
            """);

        Merge(host, local, baseline);

        Assert.Equal(
            new[] { "not_a|m1|1|2026-10-05T00:00:00.000Z", "not_b|m1|1|2026-11-01T00:00:00Z" },
            Rows(host, "SELECT id, message, seen, expires_at FROM usr0123abcd_notifications_v2 ORDER BY id"));
    }

    [Fact(DisplayName = "アバターの着用履歴は、両方で延びた着用時間の大きい方を残す")]
    public void AvatarHistoryKeepsLongerTime()
    {
        var (host, local, _) = Arrange();
        const string create = """
            CREATE TABLE usr0123abcd_avatar_history (avatar_id TEXT PRIMARY KEY, created_at TEXT, time INTEGER);
            INSERT INTO usr0123abcd_avatar_history VALUES ('avtr_a', '2026-10-01T00:00:00Z', 100), ('avtr_b', '2026-10-01T00:00:00Z', 100);
            """;
        Exec(host, create);
        Exec(local, create);
        var baseline = DatabaseSummary.Compute(local, MergeProfiles.Vrcx, out _)!;
        // a: 着け直さずに両方で延びた (手元の方が長い)。
        // b: 同期先で着け直し、手元では着用時間が延びた。
        Exec(local, """
            UPDATE usr0123abcd_avatar_history SET time = 500 WHERE avatar_id = 'avtr_a';
            UPDATE usr0123abcd_avatar_history SET time = 400 WHERE avatar_id = 'avtr_b';
            """);
        Exec(host, """
            UPDATE usr0123abcd_avatar_history SET time = 200 WHERE avatar_id = 'avtr_a';
            UPDATE usr0123abcd_avatar_history SET created_at = '2026-10-03T00:00:00Z', time = 150 WHERE avatar_id = 'avtr_b';
            """);

        Merge(host, local, baseline);

        Assert.Equal(
            new[] { "avtr_a|2026-10-01T00:00:00Z|500", "avtr_b|2026-10-03T00:00:00Z|400" },
            Rows(host, "SELECT avatar_id, created_at, time FROM usr0123abcd_avatar_history ORDER BY avatar_id"));
    }

    [Fact(DisplayName = "メモは前回の同期時点と比べ、変えた側の内容を残す")]
    public void KeyedThreeWay()
    {
        var (host, local, baseline) = Arrange();
        // a: 手元だけが編集、b: 同期先だけが編集、c: 両方が編集 (手元が新しい)、d: 手元で削除。
        Exec(local, """
            UPDATE memos SET memo = 'a1', edited_at = '2026-10-02T00:00:00Z' WHERE user_id = 'usr_a';
            UPDATE memos SET memo = 'c-local', edited_at = '2026-10-03T00:00:00Z' WHERE user_id = 'usr_c';
            DELETE FROM memos WHERE user_id = 'usr_d';
            INSERT INTO memos VALUES ('usr_e', '2026-10-02T00:00:00Z', 'e-local');
            """);
        Exec(host, """
            UPDATE memos SET memo = 'b1', edited_at = '2026-10-02T00:00:00Z' WHERE user_id = 'usr_b';
            UPDATE memos SET memo = 'c-host', edited_at = '2026-10-02T00:00:00Z' WHERE user_id = 'usr_c';
            """);

        var report = Merge(host, local, baseline);

        Assert.Equal(
            new[] { "usr_a|a1", "usr_b|b1", "usr_c|c-local", "usr_e|e-local" },
            Rows(host, "SELECT user_id, memo FROM memos ORDER BY user_id"));
        var conflict = Assert.Single(report.Conflicts);
        Assert.True(conflict.KeptLocal);
    }

    [Fact(DisplayName = "両方が編集していて同期先の方が新しければ、同期先の内容を残す")]
    public void KeyedConflictKeepsNewerRemote()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, "UPDATE memos SET memo = 'a-local', edited_at = '2026-10-02T00:00:00Z' WHERE user_id = 'usr_a'");
        Exec(host, "UPDATE memos SET memo = 'a-host', edited_at = '2026-10-03T00:00:00Z' WHERE user_id = 'usr_a'");

        var report = Merge(host, local, baseline);

        Assert.Equal(new[] { "a-host" }, Rows(host, "SELECT memo FROM memos WHERE user_id = 'usr_a'"));
        Assert.False(Assert.Single(report.Conflicts).KeptLocal);
    }

    [Fact(DisplayName = "前回の要約が無ければ、手元で消したものは消さない (2-way)")]
    public void WithoutBaselineDeletesDoNotPropagate()
    {
        var (host, local, _) = Arrange();
        Exec(local, """
            DELETE FROM memos WHERE user_id = 'usr_d';
            INSERT INTO favorite_world (created_at, world_id, group_name) VALUES ('2026-10-02T00:00:00Z', 'wrld_b', 'g1');
            """);

        Merge(host, local, baseline: null);

        Assert.Contains("usr_d|d0", Rows(host, "SELECT user_id, memo FROM memos"));
        Assert.Contains("wrld_b|g1", Rows(host, "SELECT world_id, group_name FROM favorite_world"));
    }

    [Fact(DisplayName = "お気に入りの削除は、前回の要約があれば伝わる")]
    public void FavoriteDeletionPropagates()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, "DELETE FROM favorite_world");

        var report = Merge(host, local, baseline);

        Assert.Empty(Rows(host, "SELECT id FROM favorite_world"));
        Assert.Equal(1, Assert.Single(report.Tables, t => t.Table == "favorite_world").Deleted);
    }

    [Fact(DisplayName = "規則に無いテーブルは同期先の内容のまま")]
    public void UnruledTablesKeepHost()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, "UPDATE cache_world SET name = 'local'");
        Exec(host, "UPDATE cache_world SET name = 'host'");

        Merge(host, local, baseline);

        Assert.Equal(new[] { "host" }, Rows(host, "SELECT name FROM cache_world"));
    }

    [Fact(DisplayName = "同期先に無いユーザー別のテーブルは、手元の定義で作って写す")]
    public void CreatesMissingTables()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, """
            CREATE TABLE usr0123abcd_feed_gps (id INTEGER PRIMARY KEY, created_at TEXT, user_id TEXT, display_name TEXT, location TEXT, world_name TEXT, previous_location TEXT, time INTEGER, group_name TEXT);
            INSERT INTO usr0123abcd_feed_gps (created_at, user_id, location) VALUES ('2026-10-02T00:00:00Z', 'usr_x', 'wrld_a:1');
            """);

        var report = Merge(host, local, baseline);

        Assert.Single(Rows(host, "SELECT id FROM usr0123abcd_feed_gps"));
        Assert.True(Assert.Single(report.Tables, t => t.Table == "usr0123abcd_feed_gps").Created);
    }

    [Fact(DisplayName = "同期先に無くて作った履歴のテーブルには、手元の全行を写す")]
    public void CreatedTableCopiesAllRows()
    {
        var feed = """
            CREATE TABLE usr0123abcd_feed_gps (id INTEGER PRIMARY KEY, created_at TEXT, user_id TEXT, display_name TEXT, location TEXT, world_name TEXT, previous_location TEXT, time INTEGER, group_name TEXT);
            INSERT INTO usr0123abcd_feed_gps (created_at, user_id, location) VALUES ('2026-10-01T00:00:00Z', 'usr_x', 'wrld_a:1');
            """;
        var (host, local, _) = Arrange();
        Exec(local, feed);
        var baseline = DatabaseSummary.Compute(local, MergeProfiles.Vrcx, out _)!;
        Exec(local, "INSERT INTO usr0123abcd_feed_gps (created_at, user_id, location) VALUES ('2026-10-02T00:00:00Z', 'usr_x', 'wrld_b:1')");

        Merge(host, local, baseline);

        Assert.Equal(2, Rows(host, "SELECT id FROM usr0123abcd_feed_gps").Count);
    }

    [Fact(DisplayName = "前回の同期の後に列が増えても、変えていない行を編集されたとは見ない")]
    public void ComparesWithBaselineColumns()
    {
        var (host, local, baseline) = Arrange();
        const string addColumn = "ALTER TABLE memos ADD COLUMN extra TEXT";
        Exec(host, addColumn);
        Exec(local, addColumn);
        Exec(local, "DELETE FROM memos WHERE user_id = 'usr_d'");

        var report = Merge(host, local, baseline);

        // 今の列でハッシュを求めると、同期先の usr_d が編集されたように見え、
        // 手元の削除が「編集と削除の競合」として取り消される。
        Assert.Empty(report.Conflicts);
        Assert.DoesNotContain("usr_d|d0", Rows(host, "SELECT user_id, memo FROM memos"));
    }

    [Fact(DisplayName = "前回の同期の後に増えた列の値が両方で違えば、上書きせずに中止する")]
    public void AbortsWhenAddedColumnDiffers()
    {
        var (host, local, baseline) = Arrange();
        const string addColumn = "ALTER TABLE memos ADD COLUMN extra TEXT";
        Exec(host, addColumn);
        Exec(local, addColumn);
        Exec(local, "UPDATE memos SET extra = 'x' WHERE user_id = 'usr_a'");

        var report = SqliteMerger.Merge(host, local, MergeProfiles.Vrcx, baseline, dryRun: false);

        Assert.False(report.Succeeded);
        Assert.Contains("memos", report.AbortReason);
    }

    [Fact(DisplayName = "履歴のテーブルでも、増えた列の値が両方で違えば上書きせずに中止する")]
    public void AbortsWhenAddedColumnDiffersInAppendOnly()
    {
        var (host, local, baseline) = Arrange();
        const string addColumn = "ALTER TABLE gamelog_location ADD COLUMN extra TEXT";
        Exec(host, addColumn);
        Exec(local, addColumn);
        Exec(local, "UPDATE gamelog_location SET extra = 'x' WHERE location = 'wrld_a:1'");

        var report = SqliteMerger.Merge(host, local, MergeProfiles.Vrcx, baseline, dryRun: false);

        Assert.False(report.Succeeded);
        Assert.Contains("gamelog_location", report.AbortReason);
    }

    [Fact(DisplayName = "同期先に無くて作ったキー付きのテーブルには、手元の全行を写す")]
    public void CreatedKeyedTableCopiesAllRows()
    {
        var (host, local, _) = Arrange();
        Exec(local, """
            CREATE TABLE usr0123abcd_avatar_history (avatar_id TEXT PRIMARY KEY, created_at TEXT, time INTEGER);
            INSERT INTO usr0123abcd_avatar_history VALUES ('avtr_a', '2026-10-01T00:00:00Z', 0), ('avtr_b', '2026-10-01T00:00:00Z', 0);
            """);
        var baseline = DatabaseSummary.Compute(local, MergeProfiles.Vrcx, out _)!;

        Merge(host, local, baseline);

        Assert.Equal(2, Rows(host, "SELECT avatar_id FROM usr0123abcd_avatar_history").Count);
    }

    [Fact(DisplayName = "列が食い違えば中止し、同期先の写しには何も書かない")]
    public void AbortsOnColumnMismatch()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, """
            ALTER TABLE memos ADD COLUMN extra TEXT;
            INSERT INTO gamelog_location (created_at, location) VALUES ('2026-10-02T00:00:00Z', 'wrld_local:1');
            """);
        var before = File.ReadAllBytes(host);

        var report = SqliteMerger.Merge(host, local, MergeProfiles.Vrcx, baseline, dryRun: false);

        Assert.False(report.Succeeded);
        Assert.Contains("memos", report.AbortReason);
        Assert.Single(Rows(host, "SELECT id FROM gamelog_location"));
        Assert.Equal(before, File.ReadAllBytes(host));
    }

    [Fact(DisplayName = "dry-run は件数を数えるだけで、書き込まない")]
    public void DryRunWritesNothing()
    {
        var (host, local, baseline) = Arrange();
        Exec(local, "INSERT INTO gamelog_location (created_at, location) VALUES ('2026-10-02T00:00:00Z', 'wrld_local:1')");

        var report = Merge(host, local, baseline, dryRun: true);

        Assert.Equal(1, report.ChangedRows);
        Assert.Single(Rows(host, "SELECT id FROM gamelog_location"));
    }

    [Fact(DisplayName = "Friend Connect の規則ではマージしない")]
    public void RefusesFriendConnect()
    {
        var (host, local, _) = Arrange();

        var report = SqliteMerger.Merge(host, local, MergeProfiles.FriendConnect, null, dryRun: true);

        Assert.False(report.Succeeded);
    }
}
