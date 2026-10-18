using System.Globalization;
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
/// <param name="KeptMaxRowId">
/// 振り直したとき、元の rowid のまま残った行の rowid の最大値。これより大きい rowid の
/// 行は入れ直したので、土台にした版の rowid とは対応しない。
/// </param>
/// <param name="KeptMaxRowId">振り直しの後も元の rowid のまま残った行の、rowid の最大値。</param>
/// <param name="KeptDigest">
/// 残った行のキーと後から書き換わる列のハッシュ。マージが合成する前の同期先の値で取る。
/// </param>
/// <param name="TailRows">
/// 振り直しで後ろへ移った同期先の行。キーのハッシュから、後から書き換わる列の内容の
/// ハッシュへ。これも合成する前の値で取る。
/// </param>
public sealed record TableMergeReport(
    string Table, long Inserted, long Updated, long Deleted, bool Renumbered, bool Created,
    long KeptMaxRowId = 0, string? KeptDigest = null, Dictionary<string, string>? TailRows = null);

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
    /// <summary>
    /// 振り直しで後ろへ移る同期先の行 (<see cref="TableSummary.TailRows"/>) の数の上限。
    /// 移った行は 1 行ずつ要約に残すので、手元で足す行より新しい同期先の行が多すぎると
    /// 要約が大きくなり、読み書きのたびに時間と記憶を食う。超えたら中止する。
    /// </summary>
    public const int DefaultMaxRenumberedTailRows = 100_000;

    public static MergeReport Merge(
        string hostPath, string localPath, MergeProfile profile, DatabaseSummary? baseline, bool dryRun,
        int maxRenumberedTailRows = DefaultMaxRenumberedTailRows)
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
                MergeTables(connection, profile, baseline, report, maxRenumberedTailRows);
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
        SqliteConnection connection, MergeProfile profile, DatabaseSummary? baseline, MergeReport report,
        int maxRenumberedTailRows)
    {
        foreach (var table in ListTables(connection, Local))
        {
            var rule = profile.Rules.FirstOrDefault(r => r.Matches(table));
            if (rule is null || rule.Kind == MergeTableKind.Watched) continue;

            var tableReport = MergeTable(
                connection, table, rule, baseline?.Tables.GetValueOrDefault(table), report, maxRenumberedTailRows);
            if (tableReport is null) return;
            report.Tables.Add(tableReport);
        }
    }

    /// <summary>テーブル 1 つを合わせる。中止すべき問題があれば理由を記録して null を返す。</summary>
    private static TableMergeReport? MergeTable(
        SqliteConnection connection, string table, MergeTableRule rule, TableSummary? baseline, MergeReport report,
        int maxRenumberedTailRows)
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
        // 変えていない行まで編集されたように見えるため。古い列で求められない場合は、
        // 祖先なしにせず中止する。祖先なしで合わせると、手元で消した行が同期先から戻る。
        var hashColumns = localColumns;
        if (created)
        {
            // 同期先に無くて作ったテーブルは空なので、祖先と比べると手元で変えていない
            // 行が足されない。祖先なしで手元の全行を写す。
            baseline = null;
        }
        else if (baseline is not null)
        {
            if (baseline.Columns is null)
            {
                // 列を残していない古い形式の要約。Push か Pull をすると今の形式で取り直される。
                report.AbortReason = $"前回の同期時点の要約が古い形式のため、テーブル {table} を祖先と比べられません。一度 Push か Pull をすると要約が取り直されます";
                return null;
            }
            if (!baseline.Columns.All(c => localColumns.Contains(c, StringComparer.Ordinal)))
            {
                report.AbortReason = $"テーブル {table} の列が前回の同期時点から減っているため、祖先と比べられません";
                return null;
            }
            hashColumns = baseline.Columns;
        }

        if (baseline is not null && rule.ValueColumns is null && hashColumns.Count < localColumns.Count
            && FindKeyWithDifferentAddedColumns(connection, table, rule, localColumns, hashColumns, baseline) is { } differing)
        {
            // 前回の同期の後に増えた列は祖先に無いので、どちらが変えたか分からない。
            // 古い列だけで合わせると、手元で変えた新しい列の値を同期先の値で上書きしうる。
            // 追加専用のテーブルも既存行は書き換えないので、同じく上書きになる。
            // 片方が消したキーも、もう片方が新しい列だけを変えていれば、古い列だけでは
            // 変えていないように見えて削除が勝ち、その変更が失われる。
            report.AbortReason = $"テーブル {table} の新しい列の値が同期先と手元で違い (キー {differing})、どちらの変更か判断できないため自動マージを中止しました";
            return null;
        }

        return rule.Kind == MergeTableKind.AppendOnly
            ? MergeAppendOnly(connection, table, rule, localColumns, baseline, created, report, maxRenumberedTailRows)
            : MergeKeyed(connection, table, rule, localColumns, hashColumns, baseline, created, report);
    }

    /// <summary>
    /// 追加専用のテーブルに、手元にしか無い行を足す。前回の同期の後に手元で増えた行
    /// (rowid が前回の最大値より大きいもの) だけを候補にする。それより前の行は前回の
    /// 同期で同期先にも渡っているので、全件を突き合わせる必要が無い。
    /// <see cref="MergeTableRule.GrowingColumns"/> だけは既存行も見て、大きい方の値を残す。
    /// </summary>
    private static TableMergeReport? MergeAppendOnly(
        SqliteConnection connection, string table, MergeTableRule rule,
        IReadOnlyList<string> columns, TableSummary? baseline, bool created, MergeReport report,
        int maxRenumberedTailRows)
    {
        var target = $"main.{Quote(table)}";
        var source = $"{Local}.{Quote(table)}";
        var rowIdAlias = FindRowIdAlias(connection, table);
        var insertColumns = string.Join(", ", columns.Where(c => c != rowIdAlias).Select(Quote));
        var sinceRowId = baseline is { Kind: MergeTableKind.AppendOnly } ? baseline.MaxRowId : 0;
        if (sinceRowId > 0 && WasDeletedLocally(connection, table, rule, baseline!))
        {
            // VRCX は履歴の行を消す操作を持つ。和集合を取ると、手元で消した行が同期先から
            // 戻ってしまう。どの行を消したかは要約から分からないので、マージを中止する。
            // 祖先が無い 2-way のマージでは、この削除を見分けられない。
            report.AbortReason = $"手元でテーブル {table} の行が消されています。消した行を同期先から戻さないよう、自動マージを中止しました";
            return null;
        }
        if (sinceRowId > 0 && WasRewrittenLocallyAndDeletedByHost(connection, table, rule, baseline!))
        {
            // 同期先で消した行は、前回の同期より前の行なので足し直さない。その行の滞在時間
            // などを手元で書き込んでいたら、黙って失われる。どの行を書き換えたかは要約から
            // 分からないので、マージを中止する。
            report.AbortReason = $"同期先でテーブル {table} の行が消されていて、手元では前回の同期より前の行の値 (滞在時間など) が書き換えられています。" +
                                 "消された行の値かどうか分からないため、自動マージを中止しました";
            return null;
        }
        var hostMaxRowId = ScalarLong(connection, $"SELECT coalesce(max(rowid), 0) FROM {target}");

        // 同期先にある行は足さない。突き合わせは、手元の写しの索引を使って同期先を順に読む
        // (CollectMatchedRowIds)。
        var matched = CollectMatchedRowIds(connection, table, rule, $"> {sinceRowId.ToString(CultureInfo.InvariantCulture)}");
        if (sinceRowId > 0 && baseline!.TailRows is { Count: > 0 } tail
            && ExcludeRenumberedAncestorRows(connection, table, rule, tail, sinceRowId, matched) is { } tailProblem)
        {
            Execute(connection, $"DROP TABLE {matched}");
            report.AbortReason = tailProblem;
            return null;
        }
        var match = string.Join(" AND ", rule.KeyColumns.Select(k => $"h.{Quote(k)} IS l.{Quote(k)}"));
        long inserted;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"INSERT INTO {target} ({insertColumns}) " +
                $"SELECT {insertColumns} FROM {source} AS l " +
                $"WHERE l.rowid > $since AND l.rowid NOT IN (SELECT rid FROM {matched}) " +
                $"ORDER BY l.rowid";
            command.Parameters.AddWithValue("$since", sinceRowId);
            inserted = command.ExecuteNonQuery();
        }
        Execute(connection, $"DROP TABLE {matched}");

        // 足した行が同期先の既存の行より古ければ、後で id を振り直す。振り直した後の祖先に
        // 使う値は、後から書き込まれる列を合成する前にここで取る。合成した後に取ると
        // 手元の書き換えが祖先に混ざり、次のマージでその書き換えを見分けられない。
        var oldestAdded = inserted > 0 && rule.OrderColumn is not null && rowIdAlias is not null
            ? FindInterleave(connection, table, rule.OrderColumn, hostMaxRowId)
            : null;
        long? keptMaxRowId = null;
        string? keptDigest = null;
        Dictionary<string, string>? tailRows = null;
        if (oldestAdded is not null)
        {
            keptMaxRowId = FindKeptMaxRowId(connection, table, rule.OrderColumn!, oldestAdded);
            var tailCount = ScalarLong(connection,
                $"SELECT count(*) FROM {target} WHERE rowid > {keptMaxRowId.Value.ToString(CultureInfo.InvariantCulture)} " +
                $"AND rowid <= {hostMaxRowId.ToString(CultureInfo.InvariantCulture)}");
            if (tailCount > maxRenumberedTailRows)
            {
                // 移った行は要約に 1 行ずつ残す。手元で足す行より新しい同期先の行が何十万と
                // あると、要約が何十 MB にもなって以後の同期のたびに重くなる。
                report.AbortReason = $"テーブル {table} で、手元で足す履歴より新しい行が同期先に {tailCount} 行あり、" +
                                     $"id を振り直した記録を要約に残せる上限 ({maxRenumberedTailRows} 行) を超えるため自動マージを中止しました";
                return null;
            }
            keptDigest = rule.GrowingColumns is { Count: > 0 } && keptMaxRowId > 0
                ? DigestGrowing(connection, "main", table, rule, keptMaxRowId.Value)
                : null;
            tailRows = HashRowsByKey(connection, "main", table, rule,
                    $"rowid > {keptMaxRowId.Value.ToString(CultureInfo.InvariantCulture)} " +
                    $"AND rowid <= {hostMaxRowId.ToString(CultureInfo.InvariantCulture)}")
                .ToDictionary(r => r.Key, r => r.Value.GrowingHash, StringComparer.Ordinal);
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

        if (oldestAdded is not null)
        {
            Renumber(connection, table, rule.OrderColumn!, rowIdAlias!, columns, oldestAdded);
        }

        return new TableMergeReport(table, inserted, updated, 0, keptMaxRowId is not null, created,
            keptMaxRowId ?? 0, keptDigest, tailRows);
    }

    /// <summary>
    /// 振り直しで <see cref="TableSummary.MaxRowId"/> より後ろへ移った祖先の行
    /// (<see cref="TableSummary.TailRows"/>) を、手元の写しの rowid が
    /// <paramref name="sinceRowId"/> より大きい行からキーで探す。手元に無ければ手元で消して
    /// いて、同期先に無くて手元で後から書き換わる列を変えていれば同期先の削除と手元の
    /// 書き換えの競合なので、どちらも理由を返す。見つかった行は祖先の行なので、足す候補
    /// から外すため <paramref name="matched"/> に加える。
    /// </summary>
    private static string? ExcludeRenumberedAncestorRows(
        SqliteConnection connection, string table, MergeTableRule rule,
        IReadOnlyDictionary<string, string> tailRows, long sinceRowId, string matched)
    {
        var localTail = HashRowsByKey(connection, Local, table, rule,
            $"rowid > {sinceRowId.ToString(CultureInfo.InvariantCulture)}");
        foreach (var (keyHash, growingHash) in tailRows)
        {
            if (!localTail.TryGetValue(keyHash, out var mine))
            {
                return $"手元でテーブル {table} の行が消されています。消した行を同期先から戻さないよう、自動マージを中止しました";
            }
            var rid = mine.RowId.ToString(CultureInfo.InvariantCulture);
            var hostHas = ScalarLong(connection, $"SELECT count(*) FROM {matched} WHERE rid = {rid}") > 0;
            if (!hostHas && !string.Equals(mine.GrowingHash, growingHash, StringComparison.Ordinal))
            {
                return $"同期先でテーブル {table} の行が消されていて、手元ではその行の値 (滞在時間など) が書き換えられています。自動マージを中止しました";
            }
            Execute(connection, $"INSERT OR IGNORE INTO {matched} (rid) VALUES ({rid})");
        }
        return null;
    }

    /// <summary>
    /// 前回の同期の時点にあった行が、手元で消されたかを調べる。前回の末尾の行が別の
    /// 行に入れ替わっているか、前回の最大 rowid までの行数が減っていれば消されている。
    /// </summary>
    private static bool WasDeletedLocally(
        SqliteConnection connection, string table, MergeTableRule rule, TableSummary baseline)
    {
        if (!string.Equals(
                HashRowKey(connection, Local, table, rule.KeyColumns, baseline.MaxRowId),
                baseline.LastRowHash, StringComparison.Ordinal))
        {
            return true;
        }
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {Local}.{Quote(table)} WHERE rowid <= $max";
        command.Parameters.AddWithValue("$max", baseline.MaxRowId);
        return (long)command.ExecuteScalar()! < baseline.RowCount;
    }

    /// <summary>
    /// 前回の同期の時点にあった行のうち、手元で後から書き換わる列が変わり、かつ同期先で
    /// 消された行があるかを調べる。書き換えた行の見分けは付かないので、両方が起きて
    /// いれば真とする。後から書き換わる列が無い規則や、古い要約では偽。
    /// </summary>
    private static bool WasRewrittenLocallyAndDeletedByHost(
        SqliteConnection connection, string table, MergeTableRule rule, TableSummary baseline)
    {
        if (rule.GrowingColumns is not { Count: > 0 } || baseline.Digest is null) return false;
        if (string.Equals(DigestGrowing(connection, Local, table, rule, baseline.MaxRowId), baseline.Digest, StringComparison.Ordinal))
        {
            return false;
        }
        var matched = CollectMatchedRowIds(connection, table, rule, $"<= {baseline.MaxRowId.ToString(CultureInfo.InvariantCulture)}");
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT count(*) FROM {Local}.{Quote(table)} AS l WHERE l.rowid <= $max " +
                $"AND l.rowid NOT IN (SELECT rid FROM {matched})";
            command.Parameters.AddWithValue("$max", baseline.MaxRowId);
            return (long)command.ExecuteScalar()! > 0;
        }
        finally
        {
            Execute(connection, $"DROP TABLE {matched}");
        }
    }

    /// <summary>
    /// 足した行の中に、同期先の既存の行より古い出来事があれば、足した行の中で最も古い
    /// 出来事の時刻を返す。無ければ null。
    /// </summary>
    private static string? FindInterleave(SqliteConnection connection, string table, string orderColumn, long hostMaxRowId)
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
        return oldestAdded is null || newestExisting is null || string.CompareOrdinal(oldestAdded, newestExisting) >= 0
            ? null
            : oldestAdded;
    }

    /// <summary>振り直しの後も元の rowid のまま残る行 (<paramref name="from"/> より古い出来事) の rowid の最大値。</summary>
    private static long FindKeptMaxRowId(SqliteConnection connection, string table, string orderColumn, string from)
    {
        var order = Quote(orderColumn);
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT coalesce(max(rowid), 0) FROM main.{Quote(table)} WHERE {order} < $from OR {order} IS NULL";
        command.Parameters.AddWithValue("$from", from);
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// <paramref name="from"/> 以降の出来事の行を、出来事の順に id を振り直す。
    /// VRCX は id の大きい行を「最新」として扱うため、古い出来事を後から足すと最新として
    /// 表示されてしまう。
    /// </summary>
    private static void Renumber(
        SqliteConnection connection, string table, string orderColumn, string rowIdAlias,
        IReadOnlyList<string> columns, string from)
    {
        var target = $"main.{Quote(table)}";
        var order = Quote(orderColumn);
        var insertColumns = string.Join(", ", columns.Where(c => c != rowIdAlias).Select(Quote));
        const string work = "temp.vrctds_renumber";
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"CREATE TABLE {work} AS SELECT {insertColumns} FROM {target} " +
                $"WHERE {order} >= $from ORDER BY {order}, {Quote(rowIdAlias)}";
            command.Parameters.AddWithValue("$from", from);
            command.ExecuteNonQuery();
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"DELETE FROM {target} WHERE {order} >= $from";
            command.Parameters.AddWithValue("$from", from);
            command.ExecuteNonQuery();
        }
        Execute(connection,
            $"INSERT INTO {target} ({insertColumns}) SELECT {insertColumns} FROM {work} ORDER BY rowid");
        Execute(connection, $"DROP TABLE {work}");
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

            // 両方が書き換えていたら、単調に変わる列と累計の列はどちらの行を残すかに関わらず
            // 合成する。置き換えると同期先の値が消えるので、合成した値は置き換えの前に読んでおく。
            var combined = conflict && mine is not null && theirs is not null
                ? ReadCombinedValues(connection, table, rule, mine.KeyValues,
                    baseline?.Counters?.GetValueOrDefault(key))
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

    /// <summary>
    /// 祖先の要約の後に増えた列だけを比べ、手元と同期先の両方にあって値が違うキーを
    /// 1 つ返す。祖先にあって片方だけが消したキーも、残った側で新しい列が足したときの
    /// 値から変わっていれば返す。古い列だけでは変えていないように見え、削除が勝って
    /// しまうため。追加専用のテーブルは要約にキーを残さないので、祖先の rowid の範囲に
    /// ある手元の行のうち、同期先に無いものを見る (手元で消した場合は
    /// <see cref="WasDeletedLocally"/> で別に中止する)。
    /// 無ければ null。
    /// </summary>
    private static string? FindKeyWithDifferentAddedColumns(
        SqliteConnection connection, string table, MergeTableRule rule,
        IReadOnlyList<string> columns, IReadOnlyList<string> hashColumns, TableSummary baseline)
    {
        var added = columns.Where(c => !hashColumns.Contains(c, StringComparer.Ordinal)).ToList();
        var local = ReadKeyedRows(connection, Local, table, rule, added);
        var host = ReadKeyedRows(connection, "main", table, rule, added);
        foreach (var (key, mine) in local)
        {
            if (host.TryGetValue(key, out var theirs) && mine.Hash != theirs.Hash) return key;
        }
        if (baseline is { Kind: MergeTableKind.AppendOnly, MaxRowId: > 0 })
        {
            return FindAncestorRowDeletedByHost(connection, table, rule, added, baseline);
        }
        foreach (var key in baseline.Rows?.Keys ?? Enumerable.Empty<string>())
        {
            if (local.TryGetValue(key, out var kept) == host.ContainsKey(key)) continue;
            var schema = kept is not null ? Local : "main";
            kept ??= host[key];
            if (HasSetAddedColumns(connection, schema, table, rule, added, kept.KeyValues)) return key;
        }
        return null;
    }

    /// <summary>
    /// そのキーの行で、増えた列のどれかが列を足したときの値 (既定値か NULL) から
    /// 変わっているかを返す。変わっていなければ、残った側も新しい列には触れていない。
    /// </summary>
    private static bool HasSetAddedColumns(
        SqliteConnection connection, string schema, string table, MergeTableRule rule,
        IReadOnlyList<string> added, object?[] keyValues)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT count(*) FROM {Quote(schema)}.{Quote(table)} " +
            $"WHERE {KeyCondition(command, rule, keyValues)} AND ({AddedColumnsSet(connection, schema, table, added, null)})";
        return (long)command.ExecuteScalar()! > 0;
    }

    /// <summary>
    /// 追加専用のテーブルで、祖先の行のうち手元にあって同期先に同じキーの行が無く、増えた
    /// 列が足したときの値から変わっているものを探す。そのキーを返す。祖先の行は rowid が
    /// <see cref="TableSummary.MaxRowId"/> 以下のものと、振り直しでそれより後ろへ移った
    /// <see cref="TableSummary.TailRows"/> のもの。
    /// </summary>
    private static string? FindAncestorRowDeletedByHost(
        SqliteConnection connection, string table, MergeTableRule rule, IReadOnlyList<string> added, TableSummary baseline)
    {
        var max = baseline.MaxRowId.ToString(CultureInfo.InvariantCulture);
        var keyExpression = string.Join(" || '|' || ", rule.KeyColumns.Select(k => $"coalesce(l.{Quote(k)}, '')"));
        var matched = CollectMatchedRowIds(connection, table, rule, $"<= {max}");
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT {keyExpression} FROM {Local}.{Quote(table)} AS l WHERE l.rowid <= $max " +
                $"AND l.rowid NOT IN (SELECT rid FROM {matched}) " +
                $"AND ({AddedColumnsSet(connection, Local, table, added, "l")}) LIMIT 1";
            command.Parameters.AddWithValue("$max", baseline.MaxRowId);
            if (command.ExecuteScalar() is { } key and not DBNull) return Convert.ToString(key, CultureInfo.InvariantCulture);
        }
        finally
        {
            Execute(connection, $"DROP TABLE {matched}");
        }

        if (baseline.TailRows is not { Count: > 0 } tail) return null;
        // 移った祖先の行は rowid では分からないので、キーで手元の行を探す。手元に無い行は
        // ExcludeRenumberedAncestorRows が別に中止する。
        var localTail = HashRowsByKey(connection, Local, table, rule, $"rowid > {max}");
        matched = CollectMatchedRowIds(connection, table, rule, $"> {max}");
        try
        {
            foreach (var keyHash in tail.Keys)
            {
                if (!localTail.TryGetValue(keyHash, out var mine)) continue;
                var rid = mine.RowId.ToString(CultureInfo.InvariantCulture);
                if (ScalarLong(connection, $"SELECT count(*) FROM {matched} WHERE rid = {rid}") > 0) continue;
                using var command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT {keyExpression} FROM {Local}.{Quote(table)} AS l WHERE l.rowid = {rid} " +
                    $"AND ({AddedColumnsSet(connection, Local, table, added, "l")})";
                if (command.ExecuteScalar() is { } key and not DBNull) return Convert.ToString(key, CultureInfo.InvariantCulture);
            }
            return null;
        }
        finally
        {
            Execute(connection, $"DROP TABLE {matched}");
        }
    }

    /// <summary>
    /// 手元の写しの行のうち、rowid が <paramref name="localRowIdCondition"/> を満たし、同期先に
    /// 同じキーの行があるものの rowid を一時テーブルに集めて、その名前を返す。
    /// <para>
    /// VRCX の feed には索引が無く、数百万行を索引なしで突き合わせると終わらない。索引は
    /// 捨てる側の写しに作り、同期先を順に読んで写しを引く。同期先 (マージ結果) に索引を
    /// 作ると、消しても使っていたページが空きとして残り、置き換えた後のローカルと
    /// バックアップがその分だけ大きくなるため。
    /// </para>
    /// </summary>
    private static string CollectMatchedRowIds(
        SqliteConnection connection, string table, MergeTableRule rule, string localRowIdCondition)
    {
        var keys = string.Join(", ", rule.KeyColumns.Select(Quote));
        Execute(connection, $"CREATE INDEX IF NOT EXISTS {Local}.{Quote("vrctds_merge_" + table)} ON {Quote(table)} ({keys})");
        var matched = Quote("vrctds_matched_" + table);
        var match = string.Join(" AND ", rule.KeyColumns.Select(k => $"h.{Quote(k)} IS l.{Quote(k)}"));
        Execute(connection, $"DROP TABLE IF EXISTS temp.{matched}");
        Execute(connection, $"CREATE TEMP TABLE {matched} (rid INTEGER PRIMARY KEY)");
        // CROSS JOIN は結合の順序を固定する。同期先を外側にして、写しの索引で引かせる。
        Execute(connection,
            $"INSERT OR IGNORE INTO temp.{matched} (rid) SELECT l.rowid " +
            $"FROM main.{Quote(table)} AS h CROSS JOIN {Local}.{Quote(table)} AS l ON {match} " +
            $"WHERE l.rowid {localRowIdCondition}");
        return $"temp.{matched}";
    }

    /// <summary>増えた列のどれかが、列を足したときの値 (既定値か NULL) から変わっている条件。</summary>
    private static string AddedColumnsSet(
        SqliteConnection connection, string schema, string table, IReadOnlyList<string> added, string? alias)
    {
        var defaults = new Dictionary<string, string?>(StringComparer.Ordinal);
        using (var info = connection.CreateCommand())
        {
            info.CommandText = $"SELECT name, dflt_value FROM pragma_table_info({Literal(table)}, {Literal(schema)})";
            using var reader = info.ExecuteReader();
            while (reader.Read()) defaults[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }
        var prefix = alias is null ? string.Empty : alias + ".";
        return string.Join(" OR ", added.Select(c => $"{prefix}{Quote(c)} IS NOT ({defaults.GetValueOrDefault(c) ?? "NULL"})"));
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
    /// <see cref="MergeTableRule.CounterColumns"/> は祖先の値 (<paramref name="ancestorCounters"/>)
    /// に双方の増分を足す。祖先の値が無ければ大きい方を取る。
    /// NULL は値が無いものとして扱う。合成する列が無ければ null を返す。
    /// </summary>
    private static Dictionary<string, object?>? ReadCombinedValues(
        SqliteConnection connection, string table, MergeTableRule rule, object?[] keyValues,
        IReadOnlyDictionary<string, long>? ancestorCounters)
    {
        var growing = rule.GrowingColumns ?? Array.Empty<string>();
        var shrinking = rule.ShrinkingColumns ?? Array.Empty<string>();
        var counters = rule.CounterColumns ?? Array.Empty<string>();
        if (growing.Count == 0 && shrinking.Count == 0 && counters.Count == 0) return null;

        var names = growing.Concat(shrinking).ToList();
        var expressions = growing.Select(c => Pick(c, ">"))
            .Concat(shrinking.Select(c => Pick(c, "<")))
            .Concat(counters.SelectMany(c => new[] { $"l.{Quote(c)}", $"h.{Quote(c)}" }));
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
        for (var i = 0; i < counters.Count; i++)
        {
            var mine = reader.IsDBNull(names.Count + 2 * i) ? null : reader.GetValue(names.Count + 2 * i);
            var theirs = reader.IsDBNull(names.Count + 2 * i + 1) ? null : reader.GetValue(names.Count + 2 * i + 1);
            values[counters[i]] = AddCounter(mine, theirs,
                ancestorCounters is not null && ancestorCounters.TryGetValue(counters[i], out var a) ? a : null);
        }
        return values;

        static string Pick(string column, string op)
        {
            var c = Quote(column);
            return $"CASE WHEN l.{c} IS NULL THEN h.{c} WHEN h.{c} IS NULL THEN l.{c} " +
                   $"WHEN l.{c} {op} h.{c} THEN l.{c} ELSE h.{c} END";
        }
    }

    /// <summary>
    /// 累計の列を合成する。祖先の値があれば、祖先に双方の増分を足す。減っていた側の
    /// 増分は 0 とみなす。祖先の値が無いか、整数でない値があれば大きい方を取り、
    /// 数値でなければ同期先の値を残す。
    /// </summary>
    internal static object? AddCounter(object? mine, object? theirs, long? ancestor)
    {
        if (mine is null) return theirs;
        if (theirs is null) return mine;
        if (mine is long l && theirs is long h)
        {
            if (ancestor is not { } a) return Math.Max(l, h);
            return a + Math.Max(0, l - a) + Math.Max(0, h - a);
        }
        if (mine is long or double && theirs is long or double)
        {
            return Convert.ToDouble(mine, CultureInfo.InvariantCulture) >= Convert.ToDouble(theirs, CultureInfo.InvariantCulture)
                ? mine
                : theirs;
        }
        return theirs;
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
