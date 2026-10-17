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
