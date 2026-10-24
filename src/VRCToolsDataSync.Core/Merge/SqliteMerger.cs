using System.Globalization;
using Microsoft.Data.Sqlite;
using static VRCToolsDataSync.Core.Merge.DatabaseSummary;

namespace VRCToolsDataSync.Core.Merge;

/// <summary>マージの結果。中止した場合は <see cref="AbortReason"/> に理由が入る。</summary>
public sealed class MergeReport
{
    public List<TableMergeReport> Tables { get; } = new();

    public List<MergeConflict> Conflicts { get; } = new();

    /// <summary>
    /// 手元で消していたが、同期先では前回の同期の後にその行を指す行が足されていたので残したキー。
    /// 消すと指している行も ON DELETE CASCADE で消え、別の PC の追加が失われる。
    /// </summary>
    public List<MergeConflict> KeptReferencedRows { get; } = new();

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
        var skipped = Tables.Where(t => t.SkippedWhileHostRunning > 0).ToList();
        if (changed.Count == 0 && Conflicts.Count == 0 && KeptReferencedRows.Count == 0 && skipped.Count == 0) return "手元から足すものはありませんでした";
        var parts = changed.Select(t =>
        {
            var items = new List<string>();
            if (t.Inserted > 0) items.Add($"追加 {t.Inserted}");
            if (t.Updated > 0) items.Add($"更新 {t.Updated}");
            if (t.Deleted > 0) items.Add($"削除 {t.Deleted}");
            return $"{t.Table} ({string.Join(", ", items)})";
        });
        // 両方で編集されていて同期先の内容を残したものだけなら、足した行は無い。
        var text = changed.Count == 0
            ? "手元から足すものはありませんでした"
            : "手元の変更を足しました: " + string.Join(", ", parts);
        var copied = Tables
            .Where(t => t.CopiedReferences is { Count: > 0 })
            .SelectMany(t => t.CopiedReferences!)
            .GroupBy(c => c.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key} {g.Sum(c => c.Value)}")
            .ToList();
        if (copied.Count > 0) text += $"。参照先の行も写しました: {string.Join(", ", copied)}";
        if (skipped.Count > 0)
        {
            text += $"。相手の PC が動いていた期間の履歴 {skipped.Sum(t => t.SkippedWhileHostRunning)} 件 " +
                    $"({string.Join(", ", skipped.Select(t => t.Table))}) は、相手も記録しているので足しませんでした";
        }
        if (Conflicts.Count > 0)
        {
            var keptLocal = Conflicts.Count(c => c.KeptLocal);
            text += $"。両方で編集されていたもの {Conflicts.Count} 件は新しい方を残しました" +
                    (keptLocal == Conflicts.Count ? string.Empty
                        : keptLocal == 0 ? " (編集時刻が無いものは同期先の内容)"
                        : $" (手元 {keptLocal} 件、同期先 {Conflicts.Count - keptLocal} 件)");
        }
        if (KeptReferencedRows.Count > 0)
        {
            var tables = KeptReferencedRows.Select(k => k.Table).Distinct(StringComparer.Ordinal);
            text += $"。手元で消した行 {KeptReferencedRows.Count} 件 ({string.Join(", ", tables)}) は、同期先で新しくその行を指す行が足されているので残しました";
        }
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
/// <param name="CopiedReferences">
/// 手元の行を写すときに、その行が指す先が同期先に無かったので手元から写した行の数。
/// 参照先のテーブル名から行数へ。
/// </param>
/// <param name="SkippedWhileHostRunning">
/// 手元にしか無いが、相手の PC のアプリが動いていた期間 (<see cref="MergeTableRule.RuntimeGate"/>)
/// の出来事なので足さなかった行の数。
/// </param>
public sealed record TableMergeReport(
    string Table, long Inserted, long Updated, long Deleted, bool Renumbered, bool Created,
    long KeptMaxRowId = 0, string? KeptDigest = null, Dictionary<string, string>? TailRows = null,
    Dictionary<string, long>? CopiedReferences = null, long SkippedWhileHostRunning = 0);

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
        var localTables = ListTables(connection, Local);
        // 要約を取ったときと規則が違えば、そのとき規則に無かったテーブルは要約に入っていない。
        var ancestorRulesChanged = baseline is not null
            && !string.Equals(baseline.RulesDigest, profile.RulesDigest, StringComparison.Ordinal);
        // テーブルは規則の順に合わせる。id で参照される側 (Friend Connect の User や Tag) を
        // 参照する側 (UserTag) より先に合わせないと、写した行の指す先がまだ無い。
        var ruleIndex = new Dictionary<MergeTableRule, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < profile.Rules.Count; i++) ruleIndex[profile.Rules[i]] = i;
        var targets = localTables
            .Select(table => (Table: table, Rule: profile.Rules.FirstOrDefault(r => r.Matches(table))))
            .Where(t => t.Rule is not null && t.Rule.Kind != MergeTableKind.Watched)
            .OrderBy(t => ruleIndex[t.Rule!])
            .ThenBy(t => t.Table, StringComparer.Ordinal);
        // 稼働区間による絞り込みに使う同期先の区間は、稼働区間のテーブル自身を合わせる前に取る。
        // 合わせた後の表には手元の区間も入り、手元の行がすべて「相手が動いていた期間」に見える。
        if (PrepareRuntimeGates(connection, profile, localTables) is { } gateProblem)
        {
            report.AbortReason = gateProblem;
            return;
        }
        var deferredDeletes = new List<DeferredDelete>();
        foreach (var (table, rule) in targets)
        {
            TableMergeReport? tableReport;
            try
            {
                tableReport = MergeTable(
                    connection, table, rule!, profile, baseline?.Tables.GetValueOrDefault(table), ancestorRulesChanged, report,
                    maxRenumberedTailRows, deferredDeletes);
            }
            catch (SqliteException ex)
            {
                // 制約に掛かった (参照先の行が手元にも無い、など)。取り消して従来の動作に戻す。
                report.AbortReason = $"テーブル {table} を合わせられません: {ex.Message}";
                return;
            }
            if (tableReport is null) return;
            report.Tables.Add(tableReport);
        }
        try
        {
            ApplyDeferredDeletes(connection, profile, deferredDeletes, report);
        }
        catch (SqliteException ex)
        {
            report.AbortReason = $"参照される行を消せません: {ex.Message}";
            return;
        }
        if (baseline is null) return;

        // 前回の同期時点にはあって手元から消えたテーブル。手元のテーブルだけを見ると、
        // 同期先のテーブルがそのまま残り、置き換えと Push で手元の削除が取り消される。
        // 同期先が前回から変わっていなければ削除に従い、変わっていれば中止する。
        foreach (var (table, ancestor) in baseline.Tables)
        {
            if (localTables.Contains(table, StringComparer.Ordinal)) continue;
            var rule = profile.Rules.FirstOrDefault(r => r.Matches(table));
            if (rule is null || rule.Kind == MergeTableKind.Watched) continue;
            var hostColumns = ListColumns(connection, "main", table);
            if (hostColumns.Count == 0) continue;
            var host = DatabaseSummary.SummarizeTable(connection, "main", table, rule, hostColumns);
            if (!ancestor.HasSameContent(host))
            {
                report.AbortReason = $"テーブル {table} が手元から消えていますが、同期先では前回の同期の後に変わっているか、" +
                                     "変わっていないことを確かめられません。手元でテーブルを消した理由と同期先の変更のどちらを正とするか決めてから、" +
                                     "Push か Pull をしてください";
                return;
            }
            Execute(connection, $"DROP TABLE main.{Quote(table)}");
            report.Tables.Add(new TableMergeReport(table, 0, 0, host.RowCount, false, false));
        }
    }

    /// <summary>
    /// 手元で消していた、別のテーブルから参照される行。参照する側のテーブルを合わせた後に消す。
    /// </summary>
    private sealed record DeferredDelete(string Table, MergeTableRule Rule, object?[] KeyValues, string Key);

    /// <summary>
    /// <paramref name="table"/> を id で参照する規則があるか。参照される行を消すと、参照する行が
    /// ON DELETE CASCADE で消える。
    /// </summary>
    private static bool IsReferenced(MergeProfile profile, string table)
        => profile.Rules.Any(r => r.References?.Any(x => string.Equals(x.Table, table, StringComparison.Ordinal)) == true);

    /// <summary>
    /// 参照される行の削除を、参照する側のテーブルを合わせた後に行う。
    /// <para>
    /// 参照される行 (お気に入りのグループなど) を参照する側 (お気に入り) より先に消すと、同期先で
    /// そのグループへ足されたお気に入りが ON DELETE CASCADE で消え、参照する側の比較では同期先の
    /// 追加として見えなくなる。参照する側を先に合わせれば、手元で消した行を指す同期先の行のうち、
    /// 前回の同期時点にあったものは手元の削除 (連鎖で消えている) に従って消え、残るのは前回の後に
    /// 同期先で足されたものだけになる。残っていれば、その追加を失わないよう行を消さずに残す。
    /// </para>
    /// </summary>
    private static void ApplyDeferredDeletes(
        SqliteConnection connection, MergeProfile profile, List<DeferredDelete> deferred, MergeReport report)
    {
        if (deferred.Count == 0) return;
        var hostTables = ListTables(connection, "main");
        foreach (var item in deferred)
        {
            if (HasReferencingRows(connection, profile, hostTables, item))
            {
                report.KeptReferencedRows.Add(new MergeConflict(item.Table, item.Key, KeptLocal: false));
                continue;
            }
            DeleteKey(connection, item.Table, item.Rule, item.KeyValues);
            var index = report.Tables.FindIndex(t => string.Equals(t.Table, item.Table, StringComparison.Ordinal));
            if (index >= 0) report.Tables[index] = report.Tables[index] with { Deleted = report.Tables[index].Deleted + 1 };
        }
    }

    /// <summary>同期先に、<paramref name="item"/> の行を指す行が残っているか。</summary>
    private static bool HasReferencingRows(
        SqliteConnection connection, MergeProfile profile, IReadOnlyList<string> hostTables, DeferredDelete item)
    {
        foreach (var rule in profile.Rules)
        {
            foreach (var reference in rule.References ?? Array.Empty<ColumnReference>())
            {
                if (!string.Equals(reference.Table, item.Table, StringComparison.Ordinal)) continue;
                foreach (var child in hostTables.Where(rule.Matches))
                {
                    using var command = connection.CreateCommand();
                    command.CommandText =
                        $"SELECT count(*) FROM main.{Quote(child)} AS c WHERE c.{Quote(reference.Column)} IN " +
                        $"(SELECT t.{Quote(reference.IdColumn)} FROM main.{Quote(item.Table)} AS t " +
                        $"WHERE {KeyCondition(command, item.Rule, item.KeyValues, "main", "t", includeFilter: false)})";
                    if ((long)command.ExecuteScalar()! > 0) return true;
                }
            }
        }
        return false;
    }

    /// <summary>テーブル 1 つを合わせる。中止すべき問題があれば理由を記録して null を返す。</summary>
    private static TableMergeReport? MergeTable(
        SqliteConnection connection, string table, MergeTableRule rule, MergeProfile profile, TableSummary? baseline,
        bool ancestorRulesChanged, MergeReport report, int maxRenumberedTailRows, List<DeferredDelete> deferredDeletes)
    {
        var localColumns = ListColumns(connection, Local, table);
        if (FindMissingColumns(rule, localColumns) is { Count: > 0 } missing)
        {
            report.AbortReason = $"手元のテーブル {table} に想定した列がありません: {string.Join(", ", missing)}";
            return null;
        }
        if (baseline is not null && baseline.Kind != rule.Kind)
        {
            // 要約を取った版のツールは、このテーブルを別の種類 (変化を見るだけなど) で扱っていた。
            // その要約に祖先として使える内容は無いので、規則に無かったテーブルと同じに扱う。
            baseline = null;
        }

        var created = false;
        var hostColumns = ListColumns(connection, "main", table);
        if (hostColumns.Count == 0)
        {
            if (baseline is not null)
            {
                // 前回の同期時点にはあったテーブルが同期先から消えている。新しいテーブルとして
                // 作り直すと、同期先での削除を次の Push で取り消してしまう。手元が前回から
                // 変わっていなければ削除に従い、変わっていれば中止する。
                var local = DatabaseSummary.SummarizeTable(connection, Local, table, rule, localColumns);
                if (!baseline.HasSameContent(local))
                {
                    report.AbortReason = $"テーブル {table} が同期先から消えていますが、前回の同期の後に手元で変わっているか、" +
                                         "変わっていないことを確かめられません。同期先のテーブルを消した PC と手元のどちらを正とするか決めてから、" +
                                         "Push か Pull をしてください";
                    return null;
                }
                return new TableMergeReport(table, 0, 0, 0, false, false);
            }
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
        if (!created && baseline is null && ancestorRulesChanged && !rule.UnionWithoutAncestor)
        {
            // 前回の要約を取った版のツールは、このテーブルを規則に持っていなかった。祖先が無いまま
            // 合わせると、手元で消した行を同期先から戻す。内容が同じなら合わせるものが無い。
            // アプリが行を消さない追加専用のテーブルや、手元にしか無いキーを足せれば足りるキー付きの
            // テーブル (UnionWithoutAncestor) は、祖先なしで合わせる (キー付きでは手元の削除は伝わらない)。
            var local = DatabaseSummary.SummarizeTable(connection, Local, table, rule, localColumns);
            var host = DatabaseSummary.SummarizeTable(connection, "main", table, rule, hostColumns);
            if (!local.HasSameContent(host))
            {
                report.AbortReason = $"テーブル {table} は前回の同期時点の要約に無く (要約を取った版のツールはこのテーブルをマージの対象にしていません)、" +
                                     "同期先と手元で内容が違うため、祖先なしでは手元の削除を見分けられません。" +
                                     "同期先の内容を正とするなら Pull を、手元の内容を正とするなら非常用の強制 Push をしてください";
                return null;
            }
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

        // 比べる列を固定した規則 (favorite_* など) でも、祖先に無い列は見る。固定した列だけで
        // 合わせると、手元で変えた新しい列の値が同期先の行で置き換わる。
        if (baseline is not null && hashColumns.Count < localColumns.Count
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
            ? MergeAppendOnly(connection, table, rule, profile, localColumns, baseline, created, report, maxRenumberedTailRows)
            : MergeKeyed(connection, table, rule, profile, localColumns, hashColumns, baseline, created, report, deferredDeletes);
    }

    /// <summary>
    /// 追加専用のテーブルに、手元にしか無い行を足す。前回の同期の後に手元で増えた行
    /// (rowid が前回の最大値より大きいもの) だけを候補にする。それより前の行は前回の
    /// 同期で同期先にも渡っているので、全件を突き合わせる必要が無い。
    /// <see cref="MergeTableRule.GrowingColumns"/> だけは既存行も見て、大きい方の値を残す。
    /// </summary>
    private static TableMergeReport? MergeAppendOnly(
        SqliteConnection connection, string table, MergeTableRule rule, MergeProfile profile,
        IReadOnlyList<string> columns, TableSummary? baseline, bool created, MergeReport report,
        int maxRenumberedTailRows)
    {
        var target = $"main.{Quote(table)}";
        var source = $"{Local}.{Quote(table)}";
        var rowIdAlias = FindRowIdAlias(connection, table);
        // キーに rowid の別名 (id) を含み全行をキーで突き合わせるテーブルは、id をアプリが決めていて
        // PC 間で同じ値を取るので、id も変えずに写す。他では SQLite に振らせる。
        var keepRowId = rule.UnionByKey && rowIdAlias is not null && rule.KeyColumns.Contains(rowIdAlias, StringComparer.Ordinal);
        var copiedColumns = keepRowId ? columns : columns.Where(c => c != rowIdAlias).ToList();
        var insertColumns = string.Join(", ", copiedColumns.Select(Quote));
        var ancestorMaxRowId = baseline is { Kind: MergeTableKind.AppendOnly } ? baseline.MaxRowId : 0;
        if (ancestorMaxRowId > 0 && WasDeletedLocally(connection, table, rule, baseline!))
        {
            // VRCX は履歴の行を消す操作を持つ。和集合を取ると、手元で消した行が同期先から
            // 戻ってしまう。どの行を消したかは要約から分からないので、マージを中止する。
            // 祖先が無い 2-way のマージでは、この削除を見分けられない。
            report.AbortReason = $"手元でテーブル {table} の行が消されています。消した行を同期先から戻さないよう、自動マージを中止しました";
            return null;
        }
        if (ancestorMaxRowId > 0 && WasRewrittenLocallyAndDeletedByHost(connection, table, rule, baseline!))
        {
            // 同期先で消した行は、前回の同期より前の行なので足し直さない。その行の滞在時間
            // などを手元で書き込んでいたら、黙って失われる。どの行を書き換えたかは要約から
            // 分からないので、マージを中止する。
            report.AbortReason = $"同期先でテーブル {table} の行が消されていて、手元では前回の同期より前の行の値 (滞在時間など) が書き換えられています。" +
                                 "消された行の値かどうか分からないため、自動マージを中止しました";
            return null;
        }
        var hostMaxRowId = ScalarLong(connection, $"SELECT coalesce(max(rowid), 0) FROM {target}");
        // 新しい行の id が小さい (負のこともある) テーブル (UnionByKey) は、rowid の範囲では前回の同期の
        // 後に増えた行を取れないので、全行を突き合わせる。
        var sinceRowId = rule.UnionByKey ? 0 : ancestorMaxRowId;
        var sinceCondition = rule.UnionByKey ? "IS NOT NULL" : $"> {sinceRowId.ToString(CultureInfo.InvariantCulture)}";

        // 同期先にある行は足さない。突き合わせは、手元の写しの索引を使って同期先を順に読む
        // (CollectMatchedRowIds)。
        var matched = CollectMatchedRowIds(connection, table, rule, sinceCondition);
        if (rule.IdentityColumns is { Count: > 0 }
            && FindMatchedRowWithDifferentIdentity(connection, table, rule, matched) is { } differentIdentity)
        {
            // キーが同じなのに内容が違う。アプリが決める id が PC ごとに別の実体に付いているなど、
            // 突き合わせの前提が崩れている。足すと手元の履歴が同期先の別の実体に結び付く。
            Execute(connection, $"DROP TABLE {matched}");
            report.AbortReason = $"テーブル {table} で、キー {differentIdentity} の行の内容 ({string.Join(", ", rule.IdentityColumns)}) が同期先と手元で違います。" +
                                 "同じキーが別の実体を指しているため、自動マージを中止しました";
            return null;
        }
        // 移った祖先の行は、前回の最大 rowid が 0 (足した行が同期先の全行より古かった) でもある。
        if (baseline?.TailRows is { Count: > 0 } tail
            && ExcludeRenumberedAncestorRows(connection, table, rule, tail, sinceRowId, matched) is { } tailProblem)
        {
            Execute(connection, $"DROP TABLE {matched}");
            report.AbortReason = tailProblem;
            return null;
        }
        // 足す候補: 前回の同期の後に増えた手元の行のうち、同期先に同じキーの行が無いもの。
        var candidates = $"l.rowid {sinceCondition} AND l.rowid NOT IN (SELECT rid FROM {matched})";
        long skipped = 0;
        if (rule.RuntimeGate is { } gate)
        {
            // 相手の PC のアプリが動いていた期間の出来事は、相手も記録しているので足さない。
            var gated = RuntimeGateCondition(gate, "l");
            skipped = ScalarLong(connection, $"SELECT count(*) FROM {source} AS l WHERE {candidates} AND {gated}");
            candidates += $" AND NOT {gated}";
        }
        // 候補の行が id で指す先 (ユーザー、アバター) が同期先に無ければ、先に手元から写す。
        var copiedReferences = CopyReferencedRowsOf(connection, table, rule, profile, candidates);
        long inserted;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"INSERT INTO {target} ({insertColumns}) " +
                $"SELECT {string.Join(", ", copiedColumns.Select(c => CopyExpression(rule, c, "l")))} " +
                $"FROM {source} AS l WHERE {candidates} ORDER BY l.rowid";
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
            // 後ろへ移るのは、振り直しが入れ直す行 (出来事が足した行の最古以降のもの) のうち
            // 同期先にあったもの。同期先の rowid と出来事の順が既に食い違っていれば、残る行の
            // 最大 rowid より小さい rowid の行も移るので、rowid の範囲では選ばない。
            var moves = $"{Quote(rule.OrderColumn!)} >= {Literal(oldestAdded)}";
            var movedHostRows = $"rowid <= {hostMaxRowId.ToString(CultureInfo.InvariantCulture)} AND {moves}";
            var tailCount = ScalarLong(connection, $"SELECT count(*) FROM {target} WHERE {movedHostRows}");
            if (tailCount > maxRenumberedTailRows)
            {
                // 移った行は要約に 1 行ずつ残す。手元で足す行より新しい同期先の行が何十万と
                // あると、要約が何十 MB にもなって以後の同期のたびに重くなる。
                report.AbortReason = $"テーブル {table} で、手元で足す履歴より新しい行が同期先に {tailCount} 行あり、" +
                                     $"id を振り直した記録を要約に残せる上限 ({maxRenumberedTailRows} 行) を超えるため自動マージを中止しました";
                return null;
            }
            keptDigest = rule.GrowingColumns is { Count: > 0 } && keptMaxRowId > 0
                ? DigestGrowing(connection, "main", table, rule, keptMaxRowId.Value, moves)
                : null;
            tailRows = HashRowsByKey(connection, "main", table, rule, movedHostRows, out var duplicated)
                .ToDictionary(r => r.Key, r => r.Value.GrowingHash, StringComparer.Ordinal);
            if (duplicated)
            {
                // 移った行はキーで記録するので、同じキーの行が複数あると 1 行しか残せず、
                // 次のマージで残りを手元の追加と見誤る。
                report.AbortReason = $"テーブル {table} で、id を振り直す行に同じキーの行が複数あり、振り直した記録を要約に残せないため自動マージを中止しました";
                return null;
            }
        }

        // 既存行に後から書き込まれる列 (滞在時間など) は、大きい方を残す。値は増える
        // 方向にしか変わらないので、祖先と比べなくても新しい方が分かる。書き込みは
        // 前回の同期より前の行にも起きるので、ここは全行を突き合わせる。
        long updated = 0;
        if (rule.GrowingColumns is { Count: > 0 })
        {
            // 写した参照先の行も同期先の id を得たので、対応表を取り直してから突き合わせる。
            var (maps, match) = KeyJoin(connection, rule, "h", "l");
            var from = string.Concat(maps.Select(m => $", {m.Table} AS {m.Alias}"));
            foreach (var column in rule.GrowingColumns)
            {
                var c = Quote(column);
                updated += Execute(connection,
                    $"UPDATE {target} AS h SET {c} = l.{c} FROM {source} AS l{from} " +
                    $"WHERE {match} AND l.{c} IS NOT NULL AND (h.{c} IS NULL OR l.{c} > h.{c})");
            }
        }

        if (oldestAdded is not null)
        {
            Renumber(connection, table, rule.OrderColumn!, rowIdAlias!, columns, oldestAdded);
        }

        return new TableMergeReport(table, inserted, updated, 0, keptMaxRowId is not null, created,
            keptMaxRowId ?? 0, keptDigest, tailRows,
            copiedReferences.Count > 0 ? copiedReferences : null, skipped);
    }

    /// <summary>
    /// 同期先に同じキーの行がある手元の行 (<paramref name="matched"/>) のうち、
    /// <see cref="MergeTableRule.IdentityColumns"/> の値が同期先の行と違うものを探し、そのキーを返す。
    /// 無ければ null。
    /// </summary>
    private static string? FindMatchedRowWithDifferentIdentity(
        SqliteConnection connection, string table, MergeTableRule rule, string matched)
    {
        var (maps, match) = KeyJoin(connection, rule, "h", "l");
        var crossMaps = string.Concat(maps.Select(m => $" CROSS JOIN {m.Table} AS {m.Alias}"));
        var differs = string.Join(" OR ", rule.IdentityColumns!.Select(c => $"h.{Quote(c)} IS NOT l.{Quote(c)}"));
        var key = string.Join(" || '|' || ", KeyExpressions(rule, Local, "l").Select(e => $"coalesce({e}, '')"));
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {key} FROM main.{Quote(table)} AS h{crossMaps} CROSS JOIN {Local}.{Quote(table)} AS l " +
            $"WHERE {match} AND l.rowid IN (SELECT rid FROM {matched}) AND ({differs}) LIMIT 1";
        return command.ExecuteScalar() is { } value and not DBNull ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>稼働区間による絞り込みの、同期先の区間を写した一時テーブルの名前。</summary>
    private static string RuntimeGateTable(RuntimeGate gate) => $"temp.{Quote("vrctds_gate_" + gate.Table)}";

    /// <summary>
    /// 規則が稼働区間による絞り込み (<see cref="MergeTableRule.RuntimeGate"/>) を持てば、相手の PC の
    /// アプリが動いていた期間を一時テーブルに取る。期間は、同期先の稼働区間のうち手元が知らない
    /// 部分 (<see cref="RuntimeGate"/> の説明のとおり) で、(開始, 終了] の形で持つ。
    /// 稼働区間のテーブルがどちらかの DB に無ければ理由を返す。
    /// </summary>
    private static string? PrepareRuntimeGates(SqliteConnection connection, MergeProfile profile, IReadOnlyList<string> localTables)
    {
        var gates = profile.Rules
            .Where(r => r.Kind == MergeTableKind.AppendOnly && r.RuntimeGate is not null && localTables.Any(r.Matches))
            .Select(r => r.RuntimeGate!)
            .DistinctBy(g => (g.Table, g.StartColumn, g.EndColumn));
        foreach (var gate in gates)
        {
            if (ListColumns(connection, "main", gate.Table).Count == 0 || !localTables.Contains(gate.Table, StringComparer.Ordinal))
            {
                return $"稼働区間のテーブル {gate.Table} が同期先か手元に無く、2 台で同時に動かしていた期間の履歴を見分けられません";
            }
            var work = RuntimeGateTable(gate);
            var start = Quote(gate.StartColumn);
            var end = Quote(gate.EndColumn);
            Execute(connection, $"DROP TABLE IF EXISTS {work}");
            Execute(connection, $"CREATE TEMP TABLE {Quote("vrctds_gate_" + gate.Table)} (after_at, until_at)");
            // 手元に同じ開始時刻の区間があれば、手元の終了時刻より後の部分だけが手元の知らない期間。
            // 無ければ区間の全体 (開始時刻を含める)。
            Execute(connection,
                $"INSERT INTO {work} (after_at, until_at) " +
                $"SELECT coalesce(max(l.{end}), h.{start} - 1), h.{end} FROM main.{Quote(gate.Table)} AS h " +
                $"LEFT JOIN {Local}.{Quote(gate.Table)} AS l ON l.{start} IS h.{start} " +
                $"WHERE h.{start} IS NOT NULL AND h.{end} IS NOT NULL GROUP BY h.rowid HAVING h.{end} > coalesce(max(l.{end}), h.{start} - 1)");
            Execute(connection, $"CREATE INDEX {Quote("vrctds_gate_" + gate.Table + "_idx")} ON {Quote("vrctds_gate_" + gate.Table)} (after_at, until_at)");
        }
        return null;
    }

    /// <summary>
    /// <paramref name="alias"/> の行の出来事が、相手の PC が動いていた期間に入る条件。開始時刻が期間に
    /// 入り、終了時刻の列があればそれも期間の終わりを越えていないこと。
    /// </summary>
    private static string RuntimeGateCondition(RuntimeGate gate, string alias)
    {
        var start = $"{alias}.{Quote(gate.TimeColumn)}";
        var end = gate.TimeEndColumn is null ? string.Empty : $" AND {alias}.{Quote(gate.TimeEndColumn)} <= g.until_at";
        return $"EXISTS (SELECT 1 FROM {RuntimeGateTable(gate)} AS g WHERE {start} > g.after_at AND {start} <= g.until_at{end})";
    }

    /// <summary>参照先のテーブルの、手元の id から同期先の id への対応表の名前。</summary>
    private static string ReferenceMapTable(ColumnReference reference) => $"temp.{Quote("vrctds_map_" + reference.Table)}";

    /// <summary>
    /// 規則のキーが id で指す参照先ごとに、手元の id から同期先の id への対応表を作り直す。自然キーで
    /// 突き合わせ、同期先に同じ自然キーの行が複数あれば (User.apiId は UNIQUE ではない) そのすべてを
    /// 入れる。どの行を指していても同じ参照先として突き合わせるため。同期先に無い行は対応表に
    /// 入らない (突き合わせでは「同期先に無い」になる)。列には参照先の id 列と同じ型を付ける。
    /// 型が無いと、同期先の id (INTEGER) との比較で対応表の索引が使われず全走査になる。
    /// </summary>
    private static void BuildReferenceMaps(SqliteConnection connection, MergeTableRule rule)
    {
        foreach (var reference in rule.References ?? Array.Empty<ColumnReference>())
        {
            if (!rule.KeyColumns.Contains(reference.Column, StringComparer.Ordinal)) continue;
            var work = ReferenceMapTable(reference);
            var name = Quote("vrctds_map_" + reference.Table);
            var type = DeclaredType(connection, "main", reference.Table, reference.IdColumn);
            // 参照先のキーの列がさらに別の行を指していれば (滞在のユーザー)、その先の自然キーで比べる。
            var hostKeys = ReferencedKeyColumnExpressions(reference, "main", "h");
            var localKeys = ReferencedKeyColumnExpressions(reference, Local, "l");
            var match = string.Join(" AND ", hostKeys.Zip(localKeys, (h, l) => $"{h} IS {l}"));
            Execute(connection, $"DROP TABLE IF EXISTS {work}");
            Execute(connection, $"CREATE TEMP TABLE {name} (local_id {type} NOT NULL, host_id {type} NOT NULL, PRIMARY KEY (local_id, host_id)) WITHOUT ROWID");
            Execute(connection,
                $"INSERT OR IGNORE INTO {work} (local_id, host_id) " +
                $"SELECT l.{Quote(reference.IdColumn)}, h.{Quote(reference.IdColumn)} " +
                $"FROM {Local}.{Quote(reference.Table)} AS l JOIN main.{Quote(reference.Table)} AS h ON {match} " +
                $"WHERE l.{Quote(reference.IdColumn)} IS NOT NULL AND h.{Quote(reference.IdColumn)} IS NOT NULL");
            Execute(connection, $"CREATE INDEX {Quote("vrctds_map_" + reference.Table + "_host")} ON {name} (host_id, local_id)");
        }
    }

    /// <summary>列の宣言された型。無ければ INTEGER (本ツールが参照に使う id は整数が既定)。</summary>
    private static string DeclaredType(SqliteConnection connection, string schema, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT type FROM pragma_table_info({Literal(table)}, {Literal(schema)}) WHERE name = $c";
        command.Parameters.AddWithValue("$c", column);
        var type = command.ExecuteScalar() as string;
        return string.IsNullOrWhiteSpace(type) ? "INTEGER" : type;
    }

    /// <summary>
    /// 同期先の行 <paramref name="host"/> と手元の行 <paramref name="local"/> が同じキーを持つ条件。
    /// id で参照先を指すキーの列は、対応表 (<see cref="BuildReferenceMaps"/>、ここで作り直す) を
    /// 通して比べる。Maps は FROM に加える対応表 (名前と別名)、Match は WHERE に書く条件。
    /// 条件は「同期先の id = 対応表の host_id、対応表の local_id = 手元の id」の形で、同期先から
    /// 対応表を引き、対応表から手元の索引を引ける。
    /// </summary>
    private static (List<(string Table, string Alias)> Maps, string Match) KeyJoin(
        SqliteConnection connection, MergeTableRule rule, string host, string local)
    {
        BuildReferenceMaps(connection, rule);
        var maps = new List<(string, string)>();
        var match = new List<string>();
        foreach (var key in rule.KeyColumns)
        {
            if (rule.ReferenceOf(key) is { } reference)
            {
                var m = $"m{maps.Count}";
                maps.Add((ReferenceMapTable(reference), m));
                match.Add($"{host}.{Quote(key)} = {m}.host_id AND {m}.local_id = {local}.{Quote(key)}");
            }
            else
            {
                match.Add($"{host}.{Quote(key)} IS {local}.{Quote(key)}");
            }
        }
        return (maps, string.Join(" AND ", match));
    }

    /// <summary>
    /// 手元の行のうち <paramref name="candidates"/> (別名 l) を満たすものが id で指す先が同期先に
    /// 無ければ、その行を手元から写す。参照先のテーブル名から写した行数へ。
    /// </summary>
    private static Dictionary<string, long> CopyReferencedRowsOf(
        SqliteConnection connection, string table, MergeTableRule rule, MergeProfile profile, string candidates)
    {
        var copied = new Dictionary<string, long>(StringComparer.Ordinal);
        CopyReferencedRows(connection, table, rule, profile, _ => candidates, copied);
        return copied;
    }

    /// <summary>
    /// 手元の行のうち <paramref name="localCondition"/> (別名 l、引数は command に足す) を満たすものが
    /// id で指す先が同期先に無ければ、その行を手元から写す。写す行の列は参照先の規則 (あれば) に従って
    /// 付け替えや消去をする。写す行がさらに別の行を指していれば (滞在のユーザーやインスタンス)、その先も
    /// 同じ要領で先に写す。指す先を id そのもので見分ける参照 (<see cref="ColumnReference.KeepsId"/>)
    /// では id も写す。無ければ NULL にする参照 (<see cref="ColumnReference.NullIfMissing"/>) では写さない。
    /// 区間での代替 (<see cref="ColumnReference.Fallback"/>) がある参照では、代わりに指せる行も無いときだけ写す。
    /// 写した行数は参照先のテーブル名ごとに <paramref name="copied"/> へ足す。
    /// </summary>
    private static void CopyReferencedRows(
        SqliteConnection connection, string table, MergeTableRule rule, MergeProfile profile,
        Func<SqliteCommand, string> localCondition, Dictionary<string, long> copied, HashSet<string>? copying = null)
    {
        // 参照が巡回していても (規則にはないが) 同じテーブルを写している最中に再び写しに来ないようにする。
        copying ??= new HashSet<string>(StringComparer.Ordinal);
        if (!copying.Add(table)) return;
        try
        {
            foreach (var reference in rule.References ?? Array.Empty<ColumnReference>())
            {
                if (reference.NullIfMissing) continue;
                var referencedRule = profile.Rules.FirstOrDefault(r => r.Matches(reference.Table));
                var columns = ListColumns(connection, Local, reference.Table);
                var rowIdAlias = FindRowIdAlias(connection, reference.Table);
                var targets = reference.KeepsId ? columns : columns.Where(c => c != rowIdAlias).ToList();
                var localId = $"l.{Quote(reference.Column)}";
                // 候補の行の指す先が同期先に無い条件。手元の指す先の行は l の id から引く (x は参照先の行)
                // ので、候補を選ぶ副問い合わせは外側の行に依らず 1 回で済む。
                var missing = $"NOT EXISTS (SELECT 1 FROM main.{Quote(reference.Table)} AS r WHERE {ReferencedRowMatch(reference, "r", localId)})";
                if (reference.Fallback is { } fallback)
                {
                    missing += $" AND NOT EXISTS (SELECT 1 FROM main.{Quote(reference.Table)} AS r WHERE {FallbackMatch(reference, fallback, "r", localId, "l")})";
                }
                // 写す行の手元での id を先に集める。写す前に、その行が指す先 (滞在ならユーザーやインスタンス)
                // を同じ要領で写すため。
                var ids = Quote("vrctds_copy_" + reference.Table);
                Execute(connection, $"DROP TABLE IF EXISTS temp.{ids}");
                Execute(connection, $"CREATE TEMP TABLE {ids} (id PRIMARY KEY) WITHOUT ROWID");
                long pending;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        $"INSERT OR IGNORE INTO temp.{ids} (id) SELECT {localId} FROM {Local}.{Quote(table)} AS l " +
                        $"WHERE ({localCondition(command)}) AND {localId} IS NOT NULL AND {missing}";
                    pending = command.ExecuteNonQuery();
                }
                try
                {
                    if (pending == 0) continue;
                    if (referencedRule?.References is { Count: > 0 })
                    {
                        CopyReferencedRows(connection, reference.Table, referencedRule, profile,
                            _ => $"l.{Quote(reference.IdColumn)} IN (SELECT id FROM temp.{ids})", copied, copying);
                    }
                    var count = Execute(connection,
                        $"INSERT INTO main.{Quote(reference.Table)} ({string.Join(", ", targets.Select(Quote))}) " +
                        $"SELECT {string.Join(", ", targets.Select(c => CopyExpression(referencedRule, c, "x")))} " +
                        $"FROM {Local}.{Quote(reference.Table)} AS x " +
                        $"WHERE x.{Quote(reference.IdColumn)} IN (SELECT id FROM temp.{ids}) ORDER BY x.rowid");
                    if (count > 0) copied[reference.Table] = copied.GetValueOrDefault(reference.Table) + count;
                }
                finally
                {
                    Execute(connection, $"DROP TABLE IF EXISTS temp.{ids}");
                }
            }
        }
        finally
        {
            copying.Remove(table);
        }
    }

    /// <summary>
    /// 同期先の参照先の行 (<paramref name="hostAlias"/>) が、手元の行の id <paramref name="localId"/>
    /// の指す手元の参照先の行と同じ自然キーを持つ条件。<paramref name="exceptColumn"/> のキーの列は比べない。
    /// </summary>
    private static string ReferencedRowMatch(ColumnReference reference, string hostAlias, string localId, string? exceptColumn = null)
    {
        var parts = new List<string>();
        foreach (var key in reference.KeyColumns)
        {
            if (string.Equals(key, exceptColumn, StringComparison.Ordinal)) continue;
            var host = KeyColumnExpressions(reference, key, "main", hostAlias);
            var local = KeyColumnExpressions(reference, key, Local, "x")
                .Select(e => $"(SELECT {e} FROM {Local}.{Quote(reference.Table)} AS x WHERE x.{Quote(reference.IdColumn)} = {localId})");
            parts.AddRange(host.Zip(local, (h, l) => $"{h} IS {l}"));
        }
        return string.Join(" AND ", parts);
    }

    /// <summary>
    /// 自然キーの一致する行が無いときの代替 (<see cref="ColumnReference.Fallback"/>) の条件。開始時刻の列を
    /// 除いたキーが一致し、参照する側の行 (<paramref name="referrerAlias"/>) の時刻を区間に含む。
    /// </summary>
    private static string FallbackMatch(
        ColumnReference reference, IntervalMatch fallback, string hostAlias, string localId, string referrerAlias)
    {
        var time = $"{referrerAlias}.{Quote(fallback.TimeColumn)}";
        return $"{ReferencedRowMatch(reference, hostAlias, localId, fallback.StartColumn)} " +
               $"AND {hostAlias}.{Quote(fallback.StartColumn)} <= {time} AND {hostAlias}.{Quote(fallback.EndColumn)} >= {time}";
    }

    /// <summary>参照先のキーの列 <paramref name="key"/> 1 つぶんの、参照先の行 (<paramref name="alias"/>) から読む式。</summary>
    private static List<string> KeyColumnExpressions(ColumnReference reference, string key, string schema, string alias)
        => reference.KeyReferenceOf(key) is { } nested
            ? ReferencedKeyExpressions(nested, schema, alias + "_r", column => $"{alias}.{Quote(column)}")
            : new List<string> { $"{alias}.{Quote(key)}" };

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
            $"rowid > {sinceRowId.ToString(CultureInfo.InvariantCulture)}", out var duplicated);
        if (duplicated)
        {
            // 同じキーの行が複数あると、どれが移った祖先の行か見分けられない。
            return $"テーブル {table} で、前回の同期の後の行に同じキーの行が複数あり、振り直しで移った祖先の行を見分けられないため自動マージを中止しました";
        }
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
                HashRowKey(connection, Local, table, rule, baseline.MaxRowId),
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
        SqliteConnection connection, string table, MergeTableRule rule, MergeProfile profile,
        IReadOnlyList<string> columns, IReadOnlyList<string> hashColumns,
        TableSummary? baseline, bool created, MergeReport report, List<DeferredDelete> deferredDeletes)
    {
        var local = ReadKeyedRows(connection, Local, table, rule, hashColumns);
        // 参照される行の削除は、参照する側のテーブルを合わせた後まで遅らせる (ApplyDeferredDeletes)。
        var deferDeletes = !rule.ClearOnDelete && IsReferenced(profile, table);
        var host = ReadKeyedRows(connection, "main", table, rule, hashColumns);
        var ancestor = baseline is { Kind: MergeTableKind.Keyed, Rows: { } rows }
            ? rows
            : new Dictionary<string, string>(StringComparer.Ordinal);

        long inserted = 0, updated = 0, deleted = 0;
        var copiedReferences = new Dictionary<string, long>(StringComparer.Ordinal);
        var keys = new SortedSet<string>(local.Keys, StringComparer.Ordinal);
        keys.UnionWith(host.Keys);
        keys.UnionWith(ancestor.Keys);
        foreach (var key in keys)
        {
            local.TryGetValue(key, out var mine);
            host.TryGetValue(key, out var theirs);
            ancestor.TryGetValue(key, out var original);

            if (mine?.Hash == theirs?.Hash)
            {
                // 同じ内容 (両方に無いことも含む)。ただし累計の列は、祖先から双方が同じだけ
                // 延ばした結果かもしれないので、祖先の値があれば双方の増分を足す。
                if (mine is not null && mine.Hash != original && rule.CounterColumns is { Count: > 0 }
                    && baseline?.Counters?.GetValueOrDefault(key) is { } ancestorCounters
                    && ReadCombinedValues(connection, table, rule, mine.KeyValues, ancestorCounters) is { } sum
                    && WriteValues(connection, table, rule, mine.KeyValues, sum) > 0)
                {
                    updated++;
                    RecomputeDerivedColumns(connection, table, rule, mine.KeyValues);
                }
                continue;
            }
            if (mine?.Hash == original) continue;       // 手元は変えていない。同期先のまま
            if (mine is null && rule.KeepHostOnLocalDelete) continue;   // 手元の削除は伝えない

            var conflict = theirs?.Hash != original;    // 同期先も変えている
            var takeLocal = !conflict
                || ResolveConflict(connection, table, rule, mine, theirs, key, report);

            // 両方が書き換えていたら、単調に変わる列と累計の列はどちらの行を残すかに関わらず
            // 合成する。置き換えると同期先の値が消えるので、合成した値は置き換えの前に読んでおく。
            var combined = conflict && mine is not null && theirs is not null
                ? ReadCombinedValues(connection, table, rule, mine.KeyValues,
                    baseline?.Counters?.GetValueOrDefault(key))
                : null;

            if (takeLocal && mine is null && deferDeletes)
            {
                deferredDeletes.Add(new DeferredDelete(table, rule, theirs!.KeyValues, key));
            }
            else if (takeLocal)
            {
                var keyValues = (mine ?? theirs)!.KeyValues;
                ApplyLocalKey(connection, table, rule, profile, columns, keyValues, localHas: mine is not null, copiedReferences);
                if (mine is null) deleted++;
                else if (theirs is null) inserted++;
                else updated++;
            }
            if (combined is not null)
            {
                // 同期先の行を残し、合成した値も同期先と同じなら、何も書き換えていない。
                var written = WriteValues(connection, table, rule, mine!.KeyValues, combined);
                if (!takeLocal && written > 0) updated++;
                if (written > 0) RecomputeDerivedColumns(connection, table, rule, mine.KeyValues);
            }
        }

        return new TableMergeReport(table, inserted, updated, deleted, false, created,
            CopiedReferences: copiedReferences.Count > 0 ? copiedReferences : null);
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
        var local = ReadKeyedRows(connection, Local, table, rule, added, exactColumns: true);
        var host = ReadKeyedRows(connection, "main", table, rule, added, exactColumns: true);
        foreach (var (key, mine) in local)
        {
            if (host.TryGetValue(key, out var theirs) && mine.Hash != theirs.Hash) return key;
        }
        if (baseline is { Kind: MergeTableKind.AppendOnly } && (baseline.MaxRowId > 0 || baseline.TailRows is { Count: > 0 }))
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
            $"SELECT count(*) FROM {Quote(schema)}.{Quote(table)} AS t " +
            $"WHERE {KeyCondition(command, rule, keyValues, schema, "t")} AND ({AddedColumnsSet(connection, schema, table, added, "t")})";
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
        var localTail = HashRowsByKey(connection, Local, table, rule, $"rowid > {max}", out _);
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
        // id で参照先を指すキーの列 (Friend Connect の履歴の userId など) は、PC ごとに別の値を
        // 取るので、参照先の自然キーで対応付けた id の対応表を通して比べる。
        var (maps, match) = KeyJoin(connection, rule, "h", "l");
        Execute(connection, $"DROP TABLE IF EXISTS temp.{matched}");
        Execute(connection, $"CREATE TEMP TABLE {matched} (rid INTEGER PRIMARY KEY)");
        // CROSS JOIN は結合の順序を固定する。同期先を外側にして、対応表を経て写しの索引で引かせる。
        var crossMaps = string.Concat(maps.Select(m => $" CROSS JOIN {m.Table} AS {m.Alias}"));
        Execute(connection,
            $"INSERT OR IGNORE INTO temp.{matched} (rid) SELECT l.rowid " +
            $"FROM main.{Quote(table)} AS h{crossMaps} CROSS JOIN {Local}.{Quote(table)} AS l " +
            $"WHERE {match} AND l.rowid {localRowIdCondition}");
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
    /// NULL は値が無いものとして扱う。合成する列が無いか、合成した値がどれも両方の値と
    /// 同じで書く必要が無ければ null を返す。
    /// </summary>
    private static Dictionary<string, object?>? ReadCombinedValues(
        SqliteConnection connection, string table, MergeTableRule rule, object?[] keyValues,
        IReadOnlyDictionary<string, double>? ancestorCounters)
    {
        var growing = rule.GrowingColumns ?? Array.Empty<string>();
        var shrinking = rule.ShrinkingColumns ?? Array.Empty<string>();
        var counters = rule.CounterColumns ?? Array.Empty<string>();
        if (growing.Count == 0 && shrinking.Count == 0 && counters.Count == 0) return null;

        var names = growing.Concat(shrinking).ToList();
        var all = names.Concat(counters).ToList();
        var expressions = growing.Select(c => Pick(c, ">"))
            .Concat(shrinking.Select(c => Pick(c, "<")))
            .Concat(all.SelectMany(c => new[] { $"l.{Quote(c)}", $"h.{Quote(c)}" }));
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {string.Join(", ", expressions)} " +
            $"FROM main.{Quote(table)} AS h, {Local}.{Quote(table)} AS l " +
            $"WHERE {KeyCondition(command, rule, keyValues, "main", "h", prefix: "$h")} " +
            $"AND {KeyCondition(command, rule, keyValues, Local, "l", prefix: "$l")} LIMIT 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < all.Count; i++)
        {
            var mine = At(names.Count + 2 * i);
            var theirs = At(names.Count + 2 * i + 1);
            var value = i < names.Count
                ? At(i)
                : AddCounter(mine, theirs,
                    ancestorCounters is not null && ancestorCounters.TryGetValue(all[i], out var a) ? a : null);
            // 両方の値と同じなら、どちらの行を残しても書く必要が無い。
            if (Equals(value, mine) && Equals(value, theirs)) continue;
            values[all[i]] = value;
        }
        return values.Count == 0 ? null : values;

        object? At(int i) => reader.IsDBNull(i) ? null : reader.GetValue(i);

        static string Pick(string column, string op)
        {
            var c = Quote(column);
            return $"CASE WHEN l.{c} IS NULL THEN h.{c} WHEN h.{c} IS NULL THEN l.{c} " +
                   $"WHEN l.{c} {op} h.{c} THEN l.{c} ELSE h.{c} END";
        }
    }

    /// <summary>
    /// 累計の列を合成する。祖先の値があれば、祖先に双方の増分を足す。祖先より減っていた側は、
    /// アプリが行を消して数え直した (Friend Connect が保持期間で消したワールド訪問を、次の訪問で
    /// 0 から作り直す) とみなし、その値をそのまま増分にする。祖先の値が無ければ大きい方を取り、
    /// 数値でなければ同期先の値を残す。両方と祖先がすべて整数なら整数のまま足し、実数が混ざれば
    /// 実数で足す。
    /// </summary>
    internal static object? AddCounter(object? mine, object? theirs, double? ancestor)
    {
        if (mine is null) return theirs;
        if (theirs is null) return mine;
        if (mine is long l && theirs is long h)
        {
            if (ancestor is not { } a) return Math.Max(l, h);
            if (a == Math.Floor(a) && Math.Abs(a) <= long.MaxValue / 2)
            {
                var integer = (long)a;
                return integer + IntegerIncrement(l, integer) + IntegerIncrement(h, integer);
            }
            return a + RealIncrement(l, a) + RealIncrement(h, a);
        }
        if (mine is long or double && theirs is long or double)
        {
            var m = Convert.ToDouble(mine, CultureInfo.InvariantCulture);
            var t = Convert.ToDouble(theirs, CultureInfo.InvariantCulture);
            if (ancestor is not { } a) return m >= t ? mine : theirs;
            return a + RealIncrement(m, a) + RealIncrement(t, a);
        }
        return theirs;

        // 祖先からの増分。祖先より小さければ、0 から数え直した値そのものが増分。
        static long IntegerIncrement(long value, long ancestor) => value >= ancestor ? value - ancestor : value;
        static double RealIncrement(double value, double ancestor) => value >= ancestor ? value - ancestor : value;
    }

    /// <summary>
    /// 累計の列などを合成して書いた同期先の行で、他の列から決まる列
    /// (<see cref="MergeTableRule.DerivedColumns"/>) を求め直す。
    /// </summary>
    private static void RecomputeDerivedColumns(
        SqliteConnection connection, string table, MergeTableRule rule, object?[] keyValues)
    {
        if (rule.DerivedColumns is not { Count: > 0 } derived) return;
        using var command = connection.CreateCommand();
        var sets = string.Join(", ", derived.Select(d => $"{Quote(d.Column)} = ({d.Expression})"));
        var differs = string.Join(" OR ", derived.Select(d => $"{Quote(d.Column)} IS NOT ({d.Expression})"));
        command.CommandText =
            $"UPDATE main.{Quote(table)} AS t SET {sets} " +
            $"WHERE {KeyCondition(command, rule, keyValues, "main", "t")} AND ({differs})";
        command.ExecuteNonQuery();
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
            $"UPDATE main.{Quote(table)} AS t SET {string.Join(", ", sets)} " +
            $"WHERE {KeyCondition(command, rule, keyValues, "main", "t")} AND ({string.Join(" OR ", differs)})";
        return command.ExecuteNonQuery();
    }

    private static string? ReadEditedAt(
        SqliteConnection connection, string schema, string table, MergeTableRule rule, string column, object?[] keyValues)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT max(t.{Quote(column)}) FROM {Quote(schema)}.{Quote(table)} AS t WHERE {KeyCondition(command, rule, keyValues, schema, "t")}";
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// 同期先のそのキーを手元の内容にそろえる。手元に無ければ消す (<see cref="MergeTableRule.ClearOnDelete"/>
    /// なら比べる列を消す)。あれば、同期先の行を書き換える (<see cref="MergeTableRule.UpdateInPlace"/>) か、
    /// 消して手元の行を写す。写すときは、id で参照する列を同期先の id に付け替え、指す先が同期先に
    /// 無ければその行も写す。
    /// </summary>
    private static void ApplyLocalKey(
        SqliteConnection connection, string table, MergeTableRule rule, MergeProfile profile,
        IReadOnlyList<string> columns, object?[] keyValues, bool localHas, Dictionary<string, long> copiedReferences)
    {
        if (!localHas)
        {
            if (rule.ClearOnDelete)
            {
                var sets = string.Join(", ", ValueColumnsOf(rule, columns).Select(c => $"{Quote(c)} = NULL"));
                using var command = connection.CreateCommand();
                command.CommandText =
                    $"UPDATE main.{Quote(table)} AS t SET {sets} " +
                    $"WHERE {KeyCondition(command, rule, keyValues, "main", "t", includeFilter: false)}";
                command.ExecuteNonQuery();
            }
            else
            {
                DeleteKey(connection, table, rule, keyValues);
            }
            return;
        }

        if (rule.UpdateInPlace)
        {
            // 行を消して入れ直すと id が変わり、この行を id で参照している行が ON DELETE CASCADE で
            // 消える。同期先に行があれば、比べる列だけを手元の値にする。絞り込み (Filter) は
            // 掛けない。手元で初めてメモを書いた User は同期先では絞り込みから外れているが、
            // 行はあるので書き換えで足りる。
            var values = ValueColumnsOf(rule, columns);
            using var command = connection.CreateCommand();
            command.CommandText =
                $"UPDATE main.{Quote(table)} AS t SET ({string.Join(", ", values.Select(Quote))}) = " +
                $"(SELECT {string.Join(", ", values.Select(c => $"l.{Quote(c)}"))} FROM {Local}.{Quote(table)} AS l " +
                $"WHERE {KeyCondition(command, rule, keyValues, Local, "l", prefix: "$l")} ORDER BY l.rowid LIMIT 1) " +
                $"WHERE {KeyCondition(command, rule, keyValues, "main", "t", includeFilter: false)}";
            if (command.ExecuteNonQuery() > 0) return;
        }
        else
        {
            DeleteKey(connection, table, rule, keyValues);
        }

        EnsureReferencedRows(connection, table, rule, profile, keyValues, copiedReferences);
        using (var command = connection.CreateCommand())
        {
            var rowIdAlias = FindRowIdAlias(connection, table);
            var targets = columns.Where(c => c != rowIdAlias).ToList();
            command.CommandText =
                $"INSERT INTO main.{Quote(table)} ({string.Join(", ", targets.Select(Quote))}) " +
                $"SELECT {string.Join(", ", targets.Select(c => CopyExpression(rule, c, "l")))} " +
                $"FROM {Local}.{Quote(table)} AS l WHERE {KeyCondition(command, rule, keyValues, Local, "l")} " +
                "ORDER BY l.rowid";
            command.ExecuteNonQuery();
        }
    }

    private static void DeleteKey(SqliteConnection connection, string table, MergeTableRule rule, object?[] keyValues)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"DELETE FROM main.{Quote(table)} AS t WHERE {KeyCondition(command, rule, keyValues, "main", "t")}";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 手元の行を同期先へ写すときの、列 <paramref name="column"/> の値の式。別のテーブルを id で
    /// 指す列は、手元で指している行の自然キーから同期先での id を引く。自然キーの一致する行が無く
    /// 区間での代替 (<see cref="ColumnReference.Fallback"/>) があれば、その行の id にする。どちらも
    /// 無ければ NULL になる。参照先に無い id を持つ列 (<see cref="MergeTableRule.ClearedColumns"/>) は NULL にする。
    /// </summary>
    private static string CopyExpression(MergeTableRule? rule, string column, string alias)
    {
        if (rule?.ClearedColumns?.Contains(column, StringComparer.Ordinal) == true) return "NULL";
        if (rule?.ReferenceOf(column) is not { } reference) return $"{alias}.{Quote(column)}";
        var localId = $"{alias}.{Quote(reference.Column)}";
        var id = $"r.{Quote(reference.IdColumn)}";
        var exact = $"(SELECT {id} FROM main.{Quote(reference.Table)} AS r WHERE {ReferencedRowMatch(reference, "r", localId)})";
        var lookup = reference.Fallback is { } fallback
            ? $"coalesce({exact}, (SELECT {id} FROM main.{Quote(reference.Table)} AS r WHERE {FallbackMatch(reference, fallback, "r", localId, alias)} " +
              $"ORDER BY r.{Quote(fallback.StartColumn)} DESC LIMIT 1))"
            : exact;
        return $"CASE WHEN {localId} IS NULL THEN NULL ELSE {lookup} END";
    }

    /// <summary>
    /// 手元のそのキーの行が id で指している先が同期先に無ければ、その行を手元から写す
    /// (<see cref="CopyReferencedRows"/>)。
    /// </summary>
    private static void EnsureReferencedRows(
        SqliteConnection connection, string table, MergeTableRule rule, MergeProfile profile,
        object?[] keyValues, Dictionary<string, long> copiedReferences)
        => CopyReferencedRows(connection, table, rule, profile,
            command => KeyCondition(command, rule, keyValues, Local, "l"), copiedReferences);

    /// <summary>
    /// <paramref name="alias"/> の付いたテーブルの行がそのキー (<paramref name="keyValues"/>、
    /// <see cref="KeyExpressions"/> の式の順) に一致する条件。値は <paramref name="prefix"/> から
    /// 始まる名前の引数として <paramref name="command"/> に足す。
    /// </summary>
    private static string KeyCondition(
        SqliteCommand command, MergeTableRule rule, object?[] keyValues, string schema, string alias,
        string prefix = "$k", bool includeFilter = true)
    {
        var expressions = KeyExpressions(rule, schema, alias);
        var parts = new List<string>();
        for (var i = 0; i < expressions.Count; i++)
        {
            var name = prefix + i;
            command.Parameters.AddWithValue(name, keyValues[i] ?? DBNull.Value);
            parts.Add($"{expressions[i]} IS {name}");
        }
        if (includeFilter && rule.Filter is not null) parts.Add($"({rule.Filter})");
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
