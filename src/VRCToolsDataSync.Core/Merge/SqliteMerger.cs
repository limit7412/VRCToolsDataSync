using Microsoft.Data.Sqlite;
using static VRCToolsDataSync.Core.Merge.DatabaseSummary;

namespace VRCToolsDataSync.Core.Merge;

/// <summary>マージの結果。中止した場合は <see cref="AbortReason"/> に理由が入る。</summary>
public sealed class MergeReport
{
    public List<TableMergeReport> Tables { get; } = new();

    public List<MergeConflict> Conflicts { get; } = new();

    public string? AbortReason { get; set; }

    public bool Succeeded => AbortReason is null;

    /// <summary>
    /// 土台にした同期先の DB の、マージ前の要約。マージの後は、手元の DB はこの版に
    /// 手元の変更を足したものになるので、次に比べる基準 (祖先) はこれになる。
    /// </summary>
    public DatabaseSummary? HostBaseline { get; set; }

    /// <summary>手元から足した・書き換えた・消した行の合計。</summary>
    public long ChangedRows => Tables.Sum(t => t.Inserted + t.Updated + t.Deleted);

    /// <summary>ログと画面に出す一行の要約。</summary>
    public string Describe()
    {
        if (!Succeeded) return $"マージを中止しました: {AbortReason}";
        var changed = Tables.Where(t => t.Inserted + t.Updated + t.Deleted > 0).ToList();
        if (changed.Count == 0) return "手元から足すものはありませんでした";
        var parts = changed.Select(t =>
        {
            var items = new List<string>();
            if (t.Inserted > 0) items.Add($"追加 {t.Inserted}");
            if (t.Updated > 0) items.Add($"更新 {t.Updated}");
            if (t.Deleted > 0) items.Add($"削除 {t.Deleted}");
            return $"{t.Table} ({string.Join(", ", items)})";
        });
        var text = "手元の変更を足しました: " + string.Join(", ", parts);
        if (Conflicts.Count > 0) text += $"。両方で編集されていたもの {Conflicts.Count} 件は新しい方を残しました";
        return text;
    }
}

/// <param name="Table">テーブル名。</param>
/// <param name="Inserted">手元から足した行 (キー付きでは手元にだけあったキー)。</param>
/// <param name="Updated">手元の内容で置き換えたキー。</param>
/// <param name="Deleted">手元で消されていたので消したキー。</param>
/// <param name="Renumbered">出来事の順に id を振り直したか。</param>
/// <param name="Created">同期先に無かったので、手元の定義で作ったか。</param>
public sealed record TableMergeReport(
    string Table, long Inserted, long Updated, long Deleted, bool Renumbered, bool Created);

/// <summary>両方で別々に編集されていたキー。どちらを残したかを記録する。</summary>
/// <param name="Table">テーブル名。</param>
/// <param name="Key">キー (型付きで文字列にしたもの)。</param>
/// <param name="KeptLocal">手元の内容を残したなら true、同期先の内容を残したなら false。</param>
public sealed record MergeConflict(string Table, string Key, bool KeptLocal);

/// <summary>
/// 同期先の DB を土台にして、手元の DB にしか無い変更を書き足す。
/// <para>
/// 土台を同期先の側に置くのは、規則が扱わない部分を今の置き換えと同じ結果にするため。
/// 規則に無いテーブル、キャッシュ、規則が対応しない状態は同期先の内容がそのまま残り、
/// マージをしない場合より悪い結果にはならない。
/// </para>
/// <para>
/// 書き込みは土台のファイル (同期先のスナップショットの写し) に対してだけ行い、
/// 手元の DB は読むだけである。すべてを一つのトランザクションで行い、整合性の検査に
/// 通らなければ取り消す。
/// </para>
/// </summary>
public static class SqliteMerger
{
    private const string Local = "local";

    /// <param name="hostPath">土台にする DB (同期先のスナップショットの写し)。ここに書き込む。</param>
    /// <param name="localPath">手元の DB の写し。読むだけ。</param>
    /// <param name="profile">ツールの規則。</param>
    /// <param name="baseline">
    /// 前回同期した時点の要約。無ければ 2-way になり、手元で消したものは消えない
    /// (同期先に残っていれば戻ってくる)。
    /// </param>
    /// <param name="dryRun">true なら結果を数えるだけで、最後に取り消す。</param>
    public static MergeReport Merge(
        string hostPath, string localPath, MergeProfile profile, DatabaseSummary? baseline, bool dryRun)
    {
        var report = new MergeReport();
        if (!profile.SupportsMerge)
        {
            report.AbortReason = $"{profile.ToolKey} はマージに対応していません";
            return report;
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = hostPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();

        if (CheckSchemas(connection, localPath, profile, baseline) is { } schemaProblem)
        {
            report.AbortReason = schemaProblem;
            return report;
        }

        Execute(connection, $"ATTACH DATABASE {Literal(localPath)} AS {Local}");
        try
        {
            // ATTACH と DETACH はトランザクションの外でしか行えないので、
            // その内側で BEGIN と COMMIT / ROLLBACK を明示する。
            Execute(connection, "BEGIN IMMEDIATE");
            var commit = false;
            try
            {
                MergeTables(connection, profile, baseline, report);
                if (report.Succeeded && FindIntegrityProblem(connection) is { } problem)
                {
                    report.AbortReason = problem;
                }
                commit = report.Succeeded && !dryRun;
            }
            finally
            {
                Execute(connection, commit ? "COMMIT" : "ROLLBACK");
            }
        }
        finally
        {
            Execute(connection, $"DETACH DATABASE {Local}");
        }
        return report;
    }

    /// <summary>両方の DB が規則の対応する版で、互いに同じ版かを確かめる。問題があれば理由を返す。</summary>
    private static string? CheckSchemas(
        SqliteConnection host, string localPath, MergeProfile profile, DatabaseSummary? baseline)
    {
        var (hostVersion, hostReason) = profile.CheckSchema(host);
        if (hostVersion is null) return $"同期先の DB: {hostReason}";

        using var local = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = localPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        local.Open();
        var (localVersion, localReason) = profile.CheckSchema(local);
        if (localVersion is null) return $"手元の DB: {localReason}";

        if (!string.Equals(hostVersion, localVersion, StringComparison.Ordinal))
        {
            return $"同期先と手元でスキーマの版が違います (同期先 {hostVersion}, 手元 {localVersion})。" +
                   "両方の PC でツールを同じ版に揃えてから同期してください";
        }
        return null;
    }

    private static void MergeTables(
        SqliteConnection connection, MergeProfile profile, DatabaseSummary? baseline, MergeReport report)
    {
        foreach (var table in ListTables(connection, Local))
        {
            var rule = profile.Rules.FirstOrDefault(r => r.Matches(table));
            if (rule is null || rule.Kind == MergeTableKind.Watched) continue;

            var tableReport = MergeTable(connection, table, rule, baseline?.Tables.GetValueOrDefault(table), report);
            if (tableReport is null) return;
            report.Tables.Add(tableReport);
        }
    }

    /// <summary>テーブル 1 つを合わせる。中止すべき問題があれば理由を記録して null を返す。</summary>
    private static TableMergeReport? MergeTable(
        SqliteConnection connection, string table, MergeTableRule rule, TableSummary? baseline, MergeReport report)
    {
        var localColumns = ListColumns(connection, Local, table);
        if (FindMissingColumns(rule, localColumns) is { Count: > 0 } missing)
        {
            report.AbortReason = $"手元のテーブル {table} に想定した列がありません: {string.Join(", ", missing)}";
            return null;
        }

        var created = false;
        var hostColumns = ListColumns(connection, "main", table);
        if (hostColumns.Count == 0)
        {
            // 同期先に無いテーブル (この PC でだけ使った VRChat アカウントの分など) は、
            // 手元の定義で作ってから行を足す。
            CopyTableDefinition(connection, table);
            hostColumns = ListColumns(connection, "main", table);
            created = true;
        }
        if (!hostColumns.SequenceEqual(localColumns, StringComparer.Ordinal))
        {
            report.AbortReason = $"テーブル {table} の列が同期先と手元で違います";
            return null;
        }

        // キー付きの内容ハッシュは、要約を取ったときの列で求める。前回の同期の後に
        // VRCX の更新で列が増えると、今の列で求めたハッシュは祖先と必ず食い違い、
        // 変えていない行まで編集されたように見えるため。列が減ったなど古い列で
        // 求められない場合は、祖先なしで合わせる。
        var hashColumns = localColumns;
        if (created)
        {
            // 同期先に無くて作ったテーブルは空なので、祖先と比べると手元で変えていない
            // 行が足されない。祖先なしで手元の全行を写す。
            baseline = null;
        }
        else if (baseline is not null)
        {
            if (baseline.Columns is null
                || !baseline.Columns.All(c => localColumns.Contains(c, StringComparer.Ordinal)))
            {
                baseline = null;
            }
            else
            {
                hashColumns = baseline.Columns;
            }
        }

        return rule.Kind == MergeTableKind.AppendOnly
            ? MergeAppendOnly(connection, table, rule, localColumns, baseline, created)
            : MergeKeyed(connection, table, rule, localColumns, hashColumns, baseline, created, report);
    }

    /// <summary>
    /// 追加専用のテーブルに、手元にしか無い行を足す。前回の同期の後に手元で増えた行
    /// (rowid が前回の最大値より大きいもの) だけを候補にする。それより前の行は前回の
    /// 同期で同期先にも渡っているので、全件を突き合わせる必要が無い。
    /// <see cref="MergeTableRule.GrowingColumns"/> だけは既存行も見て、大きい方の値を残す。
    /// </summary>
    private static TableMergeReport MergeAppendOnly(
        SqliteConnection connection, string table, MergeTableRule rule,
        IReadOnlyList<string> columns, TableSummary? baseline, bool created)
    {
        var target = $"main.{Quote(table)}";
        var source = $"{Local}.{Quote(table)}";
        var rowIdAlias = FindRowIdAlias(connection, table);
        var insertColumns = string.Join(", ", columns.Where(c => c != rowIdAlias).Select(Quote));
        var sinceRowId = baseline is { Kind: MergeTableKind.AppendOnly } ? baseline.MaxRowId : 0;
        if (sinceRowId > 0
            && !string.Equals(
                HashRowKey(connection, Local, table, rule.KeyColumns, sinceRowId),
                baseline!.LastRowHash, StringComparison.Ordinal))
        {
            // 前回の末尾の行が手元で消され、その rowid が別の行に再利用されている。
            // どこから先が新しい行か分からないので、全行を候補にする。
            sinceRowId = 0;
        }
        var hostMaxRowId = ScalarLong(connection, $"SELECT coalesce(max(rowid), 0) FROM {target}");

        // キーで突き合わせるための索引。VRCX の feed には索引が無く、数百万行を
        // 索引なしで突き合わせると終わらない。最後に消すので、結果の DB には残らない。
        var index = Quote("vrctds_merge_" + table);
        Execute(connection,
            $"CREATE INDEX main.{index} ON {Quote(table)} ({string.Join(", ", rule.KeyColumns.Select(Quote))})");

        var match = string.Join(" AND ", rule.KeyColumns.Select(k => $"h.{Quote(k)} IS l.{Quote(k)}"));
        long inserted;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"INSERT INTO {target} ({insertColumns}) " +
                $"SELECT {insertColumns} FROM {source} AS l " +
                $"WHERE l.rowid > $since AND NOT EXISTS (SELECT 1 FROM {target} AS h WHERE {match}) " +
                $"ORDER BY l.rowid";
            command.Parameters.AddWithValue("$since", sinceRowId);
            inserted = command.ExecuteNonQuery();
        }

        // 既存行に後から書き込まれる列 (滞在時間など) は、大きい方を残す。値は増える
        // 方向にしか変わらないので、祖先と比べなくても新しい方が分かる。書き込みは
        // 前回の同期より前の行にも起きるので、ここは全行を突き合わせる。
        long updated = 0;
        foreach (var column in rule.GrowingColumns ?? Array.Empty<string>())
        {
            var c = Quote(column);
            updated += Execute(connection,
                $"UPDATE {target} AS h SET {c} = l.{c} FROM {source} AS l " +
                $"WHERE {match} AND l.{c} IS NOT NULL AND (h.{c} IS NULL OR l.{c} > h.{c})");
        }
        Execute(connection, $"DROP INDEX main.{index}");

        var renumbered = inserted > 0
                         && rule.OrderColumn is not null
                         && rowIdAlias is not null
                         && RenumberIfInterleaved(connection, table, rule.OrderColumn, rowIdAlias, columns, hostMaxRowId);

        return new TableMergeReport(table, inserted, updated, 0, renumbered, created);
    }

    /// <summary>
    /// 足した行の中に、同期先の既存の行より古い出来事があれば、その時点以降の行を
    /// 出来事の順に入れ直して id を振り直す。
    /// <para>
    /// 入れ直すのは、足した行のうち最も古い出来事以降の行だけである。それより前の行は
    /// id の順と出来事の順が既に揃っており、入れ直した行には残った行より大きい id が
    /// 振られるので、全体として順序が揃う。
    /// </para>
    /// </summary>
    private static bool RenumberIfInterleaved(
        SqliteConnection connection, string table, string orderColumn, string rowIdAlias,
        IReadOnlyList<string> columns, long hostMaxRowId)
    {
        var target = $"main.{Quote(table)}";
        var order = Quote(orderColumn);
        string? oldestAdded;
        string? newestExisting;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT min({order}) FROM {target} WHERE rowid > $max";
            command.Parameters.AddWithValue("$max", hostMaxRowId);
            oldestAdded = command.ExecuteScalar() as string;
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT max({order}) FROM {target} WHERE rowid <= $max";
            command.Parameters.AddWithValue("$max", hostMaxRowId);
            newestExisting = command.ExecuteScalar() as string;
        }
        if (oldestAdded is null || newestExisting is null
            || string.CompareOrdinal(oldestAdded, newestExisting) >= 0)
        {
            return false;
        }

        var insertColumns = string.Join(", ", columns.Where(c => c != rowIdAlias).Select(Quote));
        const string work = "temp.vrctds_renumber";
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"CREATE TABLE {work} AS SELECT {insertColumns} FROM {target} " +
                $"WHERE {order} >= $from ORDER BY {order}, {Quote(rowIdAlias)}";
            command.Parameters.AddWithValue("$from", oldestAdded);
            command.ExecuteNonQuery();
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"DELETE FROM {target} WHERE {order} >= $from";
            command.Parameters.AddWithValue("$from", oldestAdded);
            command.ExecuteNonQuery();
        }
        Execute(connection,
            $"INSERT INTO {target} ({insertColumns}) SELECT {insertColumns} FROM {work} ORDER BY rowid");
        Execute(connection, $"DROP TABLE {work}");
        return true;
    }

    /// <summary>
    /// キー付きのテーブルを 3-way で合わせる。前回同期した時点 (祖先) と比べて、
    /// 手元だけが変えたキーは手元の内容に、同期先だけが変えたキーは同期先の内容のままにする。
    /// 両方が別々に変えたキーは、編集時刻の新しい方を残す。編集時刻が無ければ
    /// 同期先の内容を残す。
    /// </summary>
    private static TableMergeReport MergeKeyed(
        SqliteConnection connection, string table, MergeTableRule rule,
        IReadOnlyList<string> columns, IReadOnlyList<string> hashColumns,
        TableSummary? baseline, bool created, MergeReport report)
    {
        var local = ReadKeyedRows(connection, Local, table, rule, hashColumns);
        var host = ReadKeyedRows(connection, "main", table, rule, hashColumns);
        var ancestor = baseline is { Kind: MergeTableKind.Keyed, Rows: { } rows }
            ? rows
            : new Dictionary<string, string>(StringComparer.Ordinal);

        long inserted = 0, updated = 0, deleted = 0;
        var keys = new SortedSet<string>(local.Keys, StringComparer.Ordinal);
        keys.UnionWith(host.Keys);
        keys.UnionWith(ancestor.Keys);
        foreach (var key in keys)
        {
            local.TryGetValue(key, out var mine);
            host.TryGetValue(key, out var theirs);
            ancestor.TryGetValue(key, out var original);

            if (mine?.Hash == theirs?.Hash) continue;   // 同じ内容 (両方に無いことも含む)
            if (mine?.Hash == original) continue;       // 手元は変えていない。同期先のまま

            var conflict = theirs?.Hash != original;    // 同期先も変えている
            var takeLocal = !conflict
                || ResolveConflict(connection, table, rule, mine, theirs, key, report);

            // 両方が書き換えていたら、単調に変わる列はどちらの行を残すかに関わらず合成する。
            // 置き換えると同期先の値が消えるので、合成した値は置き換えの前に読んでおく。
            var combined = conflict && mine is not null && theirs is not null
                ? ReadCombinedValues(connection, table, rule, mine.KeyValues)
                : null;

            if (takeLocal)
            {
                var keyValues = (mine ?? theirs)!.KeyValues;
                ReplaceKey(connection, table, rule, columns, keyValues, copyFromLocal: mine is not null);
                if (mine is null) deleted++;
                else if (theirs is null) inserted++;
                else updated++;
            }
            if (combined is not null
                && WriteValues(connection, table, rule, mine!.KeyValues, combined) > 0
                && !takeLocal)
            {
                updated++;
            }
        }

        return new TableMergeReport(table, inserted, updated, deleted, false, created);
    }

    /// <summary>両方が別々に変えたキーで、手元の内容を採るかを決める。</summary>
    private static bool ResolveConflict(
        SqliteConnection connection, string table, MergeTableRule rule,
        DatabaseSummary.KeyedRow? mine, DatabaseSummary.KeyedRow? theirs, string key, MergeReport report)
    {
        bool takeLocal;
        if (mine is null || theirs is null)
        {
            // 片方が消し、もう片方が編集した。編集を残す。消した側の意図より、
            // 書いた内容が失われる方が取り返しがつかないため。
            takeLocal = mine is not null;
        }
        else if (rule.EditedAtColumn is { } editedAt)
        {
            var localTime = ReadEditedAt(connection, Local, table, rule, editedAt, mine.KeyValues);
            var hostTime = ReadEditedAt(connection, "main", table, rule, editedAt, theirs.KeyValues);
            takeLocal = string.CompareOrdinal(localTime, hostTime) > 0;
        }
        else
        {
            takeLocal = false;
        }
        report.Conflicts.Add(new MergeConflict(table, key, takeLocal));
        return takeLocal;
    }

    /// <summary>
    /// 手元と同期先の同じキーの行から、<see cref="MergeTableRule.GrowingColumns"/> は
    /// 大きい方、<see cref="MergeTableRule.ShrinkingColumns"/> は小さい方の値を取る。
    /// NULL は値が無いものとして扱う。合成する列が無ければ null を返す。
    /// </summary>
    private static Dictionary<string, object?>? ReadCombinedValues(
        SqliteConnection connection, string table, MergeTableRule rule, object?[] keyValues)
    {
        var growing = rule.GrowingColumns ?? Array.Empty<string>();
        var shrinking = rule.ShrinkingColumns ?? Array.Empty<string>();
        if (growing.Count == 0 && shrinking.Count == 0) return null;

        var names = growing.Concat(shrinking).ToList();
        var expressions = growing.Select(c => Pick(c, ">"))
            .Concat(shrinking.Select(c => Pick(c, "<")));
        using var command = connection.CreateCommand();
        var keys = new List<string>();
        for (var i = 0; i < rule.KeyColumns.Count; i++)
        {
            var name = "$k" + i;
            command.Parameters.AddWithValue(name, keyValues[i] ?? DBNull.Value);
            var k = Quote(rule.KeyColumns[i]);
            keys.Add($"h.{k} IS {name} AND l.{k} IS {name}");
        }
        command.CommandText =
            $"SELECT {string.Join(", ", expressions)} " +
            $"FROM main.{Quote(table)} AS h, {Local}.{Quote(table)} AS l " +
            $"WHERE {string.Join(" AND ", keys)} LIMIT 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++) values[names[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return values;

        static string Pick(string column, string op)
        {
            var c = Quote(column);
            return $"CASE WHEN l.{c} IS NULL THEN h.{c} WHEN h.{c} IS NULL THEN l.{c} " +
                   $"WHEN l.{c} {op} h.{c} THEN l.{c} ELSE h.{c} END";
        }
    }

    /// <summary>同期先のそのキーの行に値を書き、書き換わった行数を返す。</summary>
    private static int WriteValues(
        SqliteConnection connection, string table, MergeTableRule rule, object?[] keyValues,
        IReadOnlyDictionary<string, object?> values)
    {
        using var command = connection.CreateCommand();
        var sets = new List<string>();
        var differs = new List<string>();
        var i = 0;
        foreach (var (column, value) in values)
        {
            var name = "$v" + i++;
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            sets.Add($"{Quote(column)} = {name}");
            differs.Add($"{Quote(column)} IS NOT {name}");
        }
        command.CommandText =
            $"UPDATE main.{Quote(table)} SET {string.Join(", ", sets)} " +
            $"WHERE {KeyCondition(command, rule, keyValues)} AND ({string.Join(" OR ", differs)})";
        return command.ExecuteNonQuery();
    }

    private static string? ReadEditedAt(
        SqliteConnection connection, string schema, string table, MergeTableRule rule, string column, object?[] keyValues)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT max({Quote(column)}) FROM {Quote(schema)}.{Quote(table)} WHERE {KeyCondition(command, rule, keyValues)}";
        return command.ExecuteScalar() as string;
    }

    /// <summary>同期先にあるそのキーの行を消し、手元の行で置き換える (手元に無ければ消すだけ)。</summary>
    private static void ReplaceKey(
        SqliteConnection connection, string table, MergeTableRule rule,
        IReadOnlyList<string> columns, object?[] keyValues, bool copyFromLocal)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"DELETE FROM main.{Quote(table)} WHERE {KeyCondition(command, rule, keyValues)}";
            command.ExecuteNonQuery();
        }
        if (!copyFromLocal) return;

        var rowIdAlias = FindRowIdAlias(connection, table);
        var insertColumns = string.Join(", ", columns.Where(c => c != rowIdAlias).Select(Quote));
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"INSERT INTO main.{Quote(table)} ({insertColumns}) " +
                $"SELECT {insertColumns} FROM {Local}.{Quote(table)} WHERE {KeyCondition(command, rule, keyValues)} " +
                "ORDER BY rowid";
            command.ExecuteNonQuery();
        }
    }

    private static string KeyCondition(SqliteCommand command, MergeTableRule rule, object?[] keyValues)
    {
        var parts = new List<string>();
        for (var i = 0; i < rule.KeyColumns.Count; i++)
        {
            var name = "$k" + i;
            command.Parameters.AddWithValue(name, keyValues[i] ?? DBNull.Value);
            parts.Add($"{Quote(rule.KeyColumns[i])} IS {name}");
        }
        if (rule.Filter is not null) parts.Add($"({rule.Filter})");
        return string.Join(" AND ", parts);
    }

    /// <summary>
    /// rowid の別名になっている列 (INTEGER PRIMARY KEY の単独主キー) の名前。無ければ null。
    /// この列は行を足すときに書かず、SQLite に振らせる。
    /// </summary>
    private static string? FindRowIdAlias(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name, type FROM pragma_table_info({Literal(table)}, 'main') WHERE pk > 0";
        using var reader = command.ExecuteReader();
        var keys = new List<(string Name, string Type)>();
        while (reader.Read()) keys.Add((reader.GetString(0), reader.GetString(1)));
        return keys.Count == 1 && string.Equals(keys[0].Type, "INTEGER", StringComparison.OrdinalIgnoreCase)
            ? keys[0].Name
            : null;
    }

    /// <summary>手元のテーブルと、その索引の定義を同期先に写す。</summary>
    private static void CopyTableDefinition(SqliteConnection connection, string table)
    {
        var statements = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"SELECT sql FROM {Local}.sqlite_master WHERE tbl_name = $t AND sql IS NOT NULL " +
                "ORDER BY CASE type WHEN 'table' THEN 0 ELSE 1 END";
            command.Parameters.AddWithValue("$t", table);
            using var reader = command.ExecuteReader();
            while (reader.Read()) statements.Add(reader.GetString(0));
        }
        // 接頭辞の無い CREATE 文は main に作られる。
        foreach (var sql in statements) Execute(connection, sql);
    }

    private static string? FindIntegrityProblem(SqliteConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA main.foreign_key_check";
            using var reader = command.ExecuteReader();
            if (reader.Read()) return $"外部キーの整合性が取れません (テーブル {reader.GetString(0)})";
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA main.integrity_check";
            var result = command.ExecuteScalar() as string;
            if (!string.Equals(result, "ok", StringComparison.Ordinal))
            {
                return $"マージ結果の整合性の検査に通りません: {result}";
            }
        }
        return null;
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>SQL を実行し、変わった行数を返す。</summary>
    private static long Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }
}
