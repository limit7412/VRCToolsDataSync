using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace VRCToolsDataSync.Core.Merge;

/// <summary>
/// DB のうちマージの対象になるテーブルだけの要約。前回同期した時点のものを PC ごとに
/// 残しておき、手元の DB と比べて「同期の後に手元で変わったか」を判定する。後の段階では
/// 3-way マージの祖先としても使う。
/// <para>
/// DB ファイルのハッシュを比べないのは、Friend Connect が起動しているだけで多数の行の
/// 更新時刻を書き換えるため。ファイル単位で比べると、ツールを開いただけで毎回
/// 「手元に変更がある」と判定してしまう。
/// </para>
/// </summary>
public sealed class DatabaseSummary
{
    /// <summary>要約の形式。形式を変えたら上げる。古い形式の要約は捨てて取り直す。</summary>
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;

    /// <summary>DB のスキーマの版 (<see cref="MergeProfile.CheckSchema"/> が返したもの)。</summary>
    public string SchemaVersion { get; set; } = string.Empty;

    /// <summary>
    /// 要約を取ったときの規則一式 (<see cref="MergeProfile.RulesDigest"/>)。無ければ、
    /// この項目を知らない版のツールが取った要約。今の規則と違えば、規則に無かった
    /// テーブルの祖先がこの要約に無い。
    /// </summary>
    public string? RulesDigest { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// この要約を取ったときの同期先の version。同期履歴の LastPulledVersion と
    /// 同じでなければ、要約は別の時点のものなので基準に使わない。同期履歴と要約は
    /// 別のファイルに保存するので、片方だけ書いた所で止まることがある。
    /// </summary>
    public long RemoteVersion { get; set; }

    /// <summary>テーブル名から、そのテーブルの要約へ。</summary>
    public Dictionary<string, TableSummary> Tables { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// この要約を取った同期の時点で、DB と一緒に同期したフォルダにあったファイル (manifest での
    /// 相対パス)。マージでフォルダを和集合にするとき、同期先にあって手元に無いファイルのうち、
    /// 前回の同期の後に同期先で増えたもの (ここに無いもの) だけを取り出し、手元で消したものを
    /// 戻さないために使う。DB から取る要約には入らないので、同期の手順が埋める。無ければ
    /// (古い版のツールが取った要約や、DB だけから取り直した要約)、同期先にあって手元に無いもの
    /// をすべて取り出す。内容の比較 (<see cref="FindChangedTables"/>) には使わない。
    /// </summary>
    public List<string>? Files { get; set; }

    /// <summary>
    /// <paramref name="baseline"/> と内容が違うテーブルの名前を返す。片方にしか無い
    /// テーブルも違いとして数える。
    /// </summary>
    public IReadOnlyList<string> FindChangedTables(DatabaseSummary baseline)
    {
        var names = new SortedSet<string>(Tables.Keys, StringComparer.Ordinal);
        names.UnionWith(baseline.Tables.Keys);
        return names
            .Where(name => !Tables.TryGetValue(name, out var mine)
                           || !baseline.Tables.TryGetValue(name, out var theirs)
                           || !mine.HasSameContent(theirs))
            .ToList();
    }

    /// <summary>
    /// DB を読み取って要約を作る。スキーマが規則と合わない場合は null を返し、
    /// 理由を <paramref name="reason"/> に入れる。DB には書き込まない。
    /// </summary>
    public static DatabaseSummary? Compute(string databasePath, MergeProfile profile, out string? reason)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            // 要約を取った後もファイルを掴み続けると、直後の差し替えや削除に失敗する。
            Pooling = false,
        }.ToString());
        connection.Open();

        var (version, schemaReason) = profile.CheckSchema(connection);
        if (version is null)
        {
            reason = schemaReason ?? "スキーマを確認できません";
            return null;
        }

        var summary = new DatabaseSummary
        {
            SchemaVersion = version,
            RulesDigest = profile.RulesDigest,
            CreatedAt = DateTimeOffset.Now,
        };

        foreach (var table in ListTables(connection, "main"))
        {
            var rule = profile.Rules.FirstOrDefault(r => r.Matches(table));
            if (rule is null) continue;

            var columns = ListColumns(connection, "main", table);
            if (FindMissingColumns(rule, columns) is { Count: > 0 } missing)
            {
                reason = $"テーブル {table} に想定した列がありません: {string.Join(", ", missing)}";
                return null;
            }

            summary.Tables[table] = SummarizeTable(connection, "main", table, rule, columns);
        }

        reason = null;
        return summary;
    }

    /// <summary>
    /// テーブル 1 つの要約を取る。マージでは、同期先に無いテーブルが手元で変わったかを
    /// 祖先と比べるために、ATTACH した手元の DB からも取る。
    /// </summary>
    internal static TableSummary SummarizeTable(
        SqliteConnection connection, string schema, string table, MergeTableRule rule, IReadOnlyList<string> columns)
    {
        var tableSummary = rule.Kind switch
        {
            MergeTableKind.AppendOnly => SummarizeAppendOnly(connection, schema, table, rule),
            MergeTableKind.Keyed => SummarizeKeyed(connection, schema, table, rule, columns),
            MergeTableKind.Watched => SummarizeWatched(connection, schema, table, rule, columns),
            _ => throw new InvalidOperationException($"未知の種類です: {rule.Kind}"),
        };
        tableSummary.Columns = columns.ToList();
        return tableSummary;
    }

    /// <summary>
    /// 土台にした版の要約 <paramref name="summary"/> を、マージで id を振り直したテーブル
    /// について、振り直しの後も元の rowid のまま残った行だけの要約に縮める。
    /// <para>
    /// この要約は、マージ結果で置き換えたローカルの祖先になる。振り直したテーブルでは
    /// 土台の版の末尾の rowid に手元から足した行が入るので、そのままでは次のマージが
    /// 手元で行を消したと見誤る。縮めた範囲より後の行は、次のマージで足す候補に戻るが、
    /// キーで突き合わせるので二重にはならない。
    /// </para>
    /// </summary>
    public static void TrimRenumbered(DatabaseSummary summary, string mergedPath, MergeProfile profile, MergeReport report)
    {
        var renumbered = report.Tables
            .Where(t => t.Renumbered && summary.Tables.ContainsKey(t.Table))
            .ToList();
        if (renumbered.Count == 0) return;

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = mergedPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        foreach (var table in renumbered)
        {
            var rule = profile.Rules.First(r => r.Matches(table.Table));
            var kept = table.KeptMaxRowId;
            var entry = summary.Tables[table.Table];
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT count(*) FROM main.{Quote(table.Table)} WHERE rowid <= $max";
                command.Parameters.AddWithValue("$max", kept);
                entry.RowCount = (long)command.ExecuteScalar()!;
            }
            entry.MaxRowId = kept;
            entry.LastRowHash = kept == 0 ? null : HashRowKey(connection, "main", table.Table, rule, kept);
            // 後から書き換わる列のハッシュと、縮めた範囲から外れた祖先の行は、マージが
            // 合成する前の同期先の値で取ってある。マージ結果から取ると手元の書き換えが
            // 祖先に混ざり、次のマージでその書き換えを見分けられない。
            entry.Digest = table.KeptDigest;
            entry.TailRows = table.TailRows;
        }
    }

    /// <summary>
    /// 規則が前提にしている列のうち、テーブルに無いものを返す。編集時刻の列は要約には
    /// 使わないが、3-way のマージで使うので、ここで欠けていないことを確かめる。
    /// </summary>
    internal static List<string> FindMissingColumns(MergeTableRule rule, IReadOnlyList<string> columns)
        => rule.KeyColumns
            .Concat(rule.ValueColumns ?? Array.Empty<string>())
            .Concat(rule.ClearedColumns ?? Array.Empty<string>())
            .Concat(rule.References?.Select(r => r.Column) ?? Array.Empty<string>())
            .Concat(rule.References?.Where(r => r.Fallback is not null).Select(r => r.Fallback!.TimeColumn) ?? Array.Empty<string>())
            .Concat(rule.IdentityColumns ?? Array.Empty<string>())
            .Concat(rule.EditedAtColumn is null ? Array.Empty<string>() : new[] { rule.EditedAtColumn })
            .Concat(rule.OrderColumn is null ? Array.Empty<string>() : new[] { rule.OrderColumn })
            .Concat(rule.GrowingColumns ?? Array.Empty<string>())
            .Concat(rule.ShrinkingColumns ?? Array.Empty<string>())
            .Concat(rule.CounterColumns ?? Array.Empty<string>())
            .Concat(rule.RuntimeGate is null ? Array.Empty<string>() : new[] { rule.RuntimeGate.TimeColumn })
            .Concat(rule.RuntimeGate?.TimeEndColumn is { } timeEnd ? new[] { timeEnd } : Array.Empty<string>())
            .Where(c => !columns.Contains(c, StringComparer.Ordinal))
            .ToList();

    internal static List<string> ListTables(SqliteConnection connection, string schema)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM {Quote(schema)}.sqlite_master WHERE type = 'table' ORDER BY name";
        using var reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read()) tables.Add(reader.GetString(0));
        return tables;
    }

    internal static List<string> ListColumns(SqliteConnection connection, string schema, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info({Literal(table)}, {Literal(schema)}) ORDER BY cid";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(0));
        return columns;
    }

    private static TableSummary SummarizeAppendOnly(SqliteConnection connection, string schema, string table, MergeTableRule rule)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*), coalesce(max(rowid), 0) FROM {Quote(schema)}.{Quote(table)}";
        using var reader = command.ExecuteReader();
        reader.Read();
        var summary = new TableSummary
        {
            Kind = MergeTableKind.AppendOnly,
            RowCount = reader.GetInt64(0),
            MaxRowId = reader.GetInt64(1),
        };
        // 末尾の行を消した後に 1 行足すと、rowid が再利用されて行数も最大値も
        // 変わらないことがある。末尾の行のキーも残して、入れ替わりを拾う。
        summary.LastRowHash = summary.MaxRowId == 0
            ? null
            : HashRowKey(connection, schema, table, rule, summary.MaxRowId);
        if (rule.GrowingColumns is { Count: > 0 })
        {
            // 行数と rowid だけでは、既存行の書き換え (滞在時間の書き込みなど) を拾えない。
            summary.Digest = DigestGrowing(connection, schema, table, rule, summary.MaxRowId);
        }
        return summary;
    }

    /// <summary>
    /// <paramref name="whereSql"/> を満たす行を、キーのハッシュから rowid と
    /// <see cref="MergeTableRule.GrowingColumns"/> の内容のハッシュへ写す。振り直しで
    /// rowid が変わった祖先の行を、次のマージでキーで見分けるのに使う。同じキーの行が
    /// 複数あれば <paramref name="duplicated"/> を真にする (後の行で上書きされる)。
    /// </summary>
    internal static Dictionary<string, (long RowId, string GrowingHash)> HashRowsByKey(
        SqliteConnection connection, string schema, string table, MergeTableRule rule, string whereSql,
        out bool duplicated)
    {
        duplicated = false;
        var growing = rule.GrowingColumns ?? Array.Empty<string>();
        var keys = KeyExpressions(rule, schema, "t");
        var selected = keys.Concat(growing.Select(c => $"t.{Quote(c)}")).ToList();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT t.rowid, {string.Join(", ", selected)} FROM {Quote(schema)}.{Quote(table)} AS t WHERE {whereSql}";
        using var reader = command.ExecuteReader();

        var rows = new Dictionary<string, (long, string)>(StringComparer.Ordinal);
        var keyCount = keys.Count;
        var key = new StringBuilder();
        var values = new StringBuilder();
        while (reader.Read())
        {
            key.Clear();
            values.Clear();
            for (var i = 0; i < keyCount; i++) AppendValue(key, reader, i + 1);
            for (var i = keyCount; i < selected.Count; i++) AppendValue(values, reader, i + 1);
            var keyHash = Hash(key.ToString());
            if (rows.ContainsKey(keyHash)) duplicated = true;
            rows[keyHash] = (reader.GetInt64(0), Hash(values.ToString()));
        }
        return rows;
    }

    /// <summary>
    /// rowid が <paramref name="maxRowId"/> 以下の行について、キーと
    /// <see cref="MergeTableRule.GrowingColumns"/> の内容を rowid の順にハッシュにする。
    /// 要約とマージで同じ方法で求めるため、両方がここを通る。<paramref name="excludeSql"/>
    /// を満たす行は除く (振り直しで後ろへ移る行を、移す前に除いて取るのに使う)。
    /// </summary>
    internal static string DigestGrowing(
        SqliteConnection connection, string schema, string table, MergeTableRule rule, long maxRowId,
        string? excludeSql = null)
    {
        var columns = KeyExpressions(rule, schema, "t")
            .Concat((rule.GrowingColumns ?? Array.Empty<string>()).Select(c => $"t.{Quote(c)}")).ToList();
        var exclude = excludeSql is null ? "" : $" AND NOT ({excludeSql})";
        return DigestQuery(connection, columns.Count,
            $"SELECT {string.Join(", ", columns)} FROM {Quote(schema)}.{Quote(table)} AS t " +
            $"WHERE t.rowid <= {maxRowId.ToString(CultureInfo.InvariantCulture)}{exclude} ORDER BY t.rowid").Digest;
    }

    private static TableSummary SummarizeKeyed(
        SqliteConnection connection, string schema, string table, MergeTableRule rule, IReadOnlyList<string> columns)
    {
        var rows = ReadKeyedRows(connection, schema, table, rule, columns);
        return new TableSummary
        {
            Kind = MergeTableKind.Keyed,
            RowCount = rows.Count,
            Rows = rows.ToDictionary(r => r.Key, r => r.Value.Hash, StringComparer.Ordinal),
            Counters = rule.CounterColumns is { Count: > 0 } ? ReadCounters(connection, schema, table, rule) : null,
        };
    }

    /// <summary>
    /// <see cref="MergeTableRule.CounterColumns"/> の値を、キー (<see cref="ReadKeyedRows"/>
    /// と同じ形) ごとに読む。整数でない値は残さない。
    /// </summary>
    private static Dictionary<string, Dictionary<string, long>> ReadCounters(
        SqliteConnection connection, string schema, string table, MergeTableRule rule)
    {
        var counters = rule.CounterColumns!;
        var keys = KeyExpressions(rule, schema, "t");
        var selected = keys.Concat(counters.Select(c => $"t.{Quote(c)}")).ToList();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {string.Join(", ", selected)} FROM {Quote(schema)}.{Quote(table)} AS t" +
            (rule.Filter is null ? string.Empty : $" WHERE {rule.Filter}");
        using var reader = command.ExecuteReader();

        var result = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        var keyCount = keys.Count;
        while (reader.Read())
        {
            var key = new StringBuilder();
            for (var i = 0; i < keyCount; i++) AppendValue(key, reader, i);
            var values = new Dictionary<string, long>(StringComparer.Ordinal);
            for (var i = 0; i < counters.Count; i++)
            {
                if (!reader.IsDBNull(keyCount + i) && reader.GetValue(keyCount + i) is long value)
                {
                    values[counters[i]] = value;
                }
            }
            result[key.ToString()] = values;
        }
        return result;
    }

    /// <summary>
    /// キー付きのテーブルの 1 キーぶん。キーの値そのものと、内容のハッシュを持つ。キーの値は
    /// <see cref="KeyExpressions"/> の式の順で、id で参照する列はその行の自然キーの値になっている。
    /// </summary>
    internal sealed record KeyedRow(object?[] KeyValues, string Hash);

    /// <summary>
    /// キーの列を、<paramref name="alias"/> の付いたテーブルから読む SQL の式にする。別のテーブルの
    /// 行を id で指す列 (<see cref="MergeTableRule.References"/>) は、その行の自然キーを読む
    /// 副問い合わせに置き換える。1 列が複数の式になることがあるので、キーの幅は式の数で数える。
    /// 要約とマージで同じキーを使うため、両方がここを通る。
    /// </summary>
    internal static List<string> KeyExpressions(MergeTableRule rule, string schema, string alias)
    {
        var expressions = new List<string>();
        foreach (var column in rule.KeyColumns)
        {
            if (rule.ReferenceOf(column) is { } reference)
            {
                expressions.AddRange(ReferencedKeyExpressions(reference, schema, alias + "_r", column => $"{alias}.{Quote(column)}"));
            }
            else
            {
                expressions.Add($"{alias}.{Quote(column)}");
            }
        }
        return expressions;
    }

    /// <summary>
    /// <paramref name="reference"/> の指す先の行の自然キーを、参照する側の行の id の式
    /// (<paramref name="localId"/> に <see cref="ColumnReference.Column"/> を渡して得る) から引く
    /// SQL の式にする。参照先のキーの列がさらに別の行を指していれば (<see cref="ColumnReference.KeyReferences"/>)、
    /// その先の自然キーまで辿る。<paramref name="alias"/> は参照先の行に付ける別名で、入れ子ごとに変える。
    /// </summary>
    internal static List<string> ReferencedKeyExpressions(
        ColumnReference reference, string schema, string alias, Func<string, string> localId)
        => ReferencedKeyColumnExpressions(reference, schema, alias)
            .Select(e => $"(SELECT {e} FROM {Quote(schema)}.{Quote(reference.Table)} AS {alias} " +
                         $"WHERE {alias}.{Quote(reference.IdColumn)} = {localId(reference.Column)})")
            .ToList();

    /// <summary>
    /// 参照先のテーブルの行 (<paramref name="alias"/>) から、その行の自然キーを読む式。
    /// キーの列がさらに別の行を指していれば、その先の自然キーを読む副問い合わせになる。
    /// </summary>
    internal static List<string> ReferencedKeyColumnExpressions(ColumnReference reference, string schema, string alias)
    {
        var expressions = new List<string>();
        foreach (var key in reference.KeyColumns)
        {
            if (reference.KeyReferenceOf(key) is { } nested)
            {
                expressions.AddRange(ReferencedKeyExpressions(nested, schema, alias + "_r", column => $"{alias}.{Quote(column)}"));
            }
            else
            {
                expressions.Add($"{alias}.{Quote(key)}");
            }
        }
        return expressions;
    }

    /// <summary>
    /// キー付きのテーブルを読み、キー (型付きで文字列にしたもの) から行へ写す。
    /// 要約とマージで同じハッシュを使うため、両方がここを通る。
    /// </summary>
    /// <param name="exactColumns">
    /// 真なら、規則の <see cref="MergeTableRule.ValueColumns"/> に関わらず <paramref name="columns"/>
    /// のうちキー以外の列をそのまま比べる。前回の同期の後に増えた列だけを比べるのに使う。
    /// </param>
    internal static Dictionary<string, KeyedRow> ReadKeyedRows(
        SqliteConnection connection, string schema, string table, MergeTableRule rule, IReadOnlyList<string> columns,
        bool exactColumns = false)
    {
        var valueColumns = exactColumns
            ? columns.Where(c => !rule.KeyColumns.Contains(c, StringComparer.Ordinal)).ToList()
            : ValueColumnsOf(rule, columns);
        var keys = KeyExpressions(rule, schema, "t");
        var selected = keys.Concat(valueColumns.Select(c => $"t.{Quote(c)}")).ToList();

        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {string.Join(", ", selected)} FROM {Quote(schema)}.{Quote(table)} AS t" +
            (rule.Filter is null ? string.Empty : $" WHERE {rule.Filter}");
        using var reader = command.ExecuteReader();

        var keyValuesByKey = new Dictionary<string, object?[]>(StringComparer.Ordinal);
        var hashesByKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var keyCount = keys.Count;
        while (reader.Read())
        {
            var key = new StringBuilder();
            var keyValues = new object?[keyCount];
            for (var i = 0; i < keyCount; i++)
            {
                AppendValue(key, reader, i);
                keyValues[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            var value = new StringBuilder();
            for (var i = keyCount; i < selected.Count; i++) AppendValue(value, reader, i);
            var keyText = key.ToString();
            if (!hashesByKey.TryGetValue(keyText, out var hashes))
            {
                hashes = new List<string>(1);
                hashesByKey[keyText] = hashes;
                keyValuesByKey[keyText] = keyValues;
            }
            hashes.Add(Hash(value.ToString()));
        }

        // キーが重複するテーブル (UNIQUE 制約の無い favorite_* など) では、同じキーの行を
        // まとめて一つの値として扱う。rowid の順は PC ごとに違いうるので、行のハッシュを
        // 並べ替えてから束ねる。2 行ずつ順に束ねると 3 行以上で順序に左右される。
        var rows = new Dictionary<string, KeyedRow>(StringComparer.Ordinal);
        foreach (var (keyText, hashes) in hashesByKey)
        {
            rows[keyText] = new KeyedRow(keyValuesByKey[keyText], CombineUnordered(hashes));
        }
        return rows;
    }

    /// <summary><see cref="MergeTableKind.Keyed"/> で比べる列。指定が無ければキー以外のすべて。</summary>
    internal static IReadOnlyList<string> ValueColumnsOf(MergeTableRule rule, IReadOnlyList<string> columns)
        => rule.ValueColumns
           ?? columns.Where(c => !rule.KeyColumns.Contains(c, StringComparer.Ordinal)).ToList();

    /// <summary>
    /// 行のハッシュの多重集合を、順序に依らない一つのハッシュにする。1 行なら
    /// そのハッシュのまま (重複の無いテーブルの要約を変えないため)。
    /// </summary>
    private static string CombineUnordered(List<string> hashes)
    {
        if (hashes.Count == 1) return hashes[0];
        hashes.Sort(StringComparer.Ordinal);
        return Hash(string.Concat(hashes));
    }

    private static TableSummary SummarizeWatched(
        SqliteConnection connection, string schema, string table, MergeTableRule rule, IReadOnlyList<string> columns)
    {
        var ignored = rule.IgnoredColumns ?? Array.Empty<string>();
        var selected = columns.Where(c => !ignored.Contains(c, StringComparer.Ordinal)).ToList();
        if (selected.Count == 0)
        {
            return new TableSummary { Kind = MergeTableKind.Watched, Digest = Hash(string.Empty) };
        }

        var (count, digest) = DigestRows(connection, schema, table, selected, string.Join(", ", selected.Select(Quote)));
        return new TableSummary
        {
            Kind = MergeTableKind.Watched,
            RowCount = count,
            Digest = digest,
        };
    }

    /// <summary>指定した列の内容を、<paramref name="orderBy"/> の順に読んでハッシュにする。</summary>
    private static (long Count, string Digest) DigestRows(
        SqliteConnection connection, string schema, string table, IReadOnlyList<string> columns, string orderBy)
        => DigestQuery(connection, columns.Count,
            $"SELECT {string.Join(", ", columns.Select(Quote))} FROM {Quote(schema)}.{Quote(table)} ORDER BY {orderBy}");

    /// <summary>
    /// rowid が <paramref name="rowId"/> の行のキーのハッシュを返す。行が無ければ null。
    /// 要約の <see cref="TableSummary.LastRowHash"/> と同じ方法で求める。id で参照先を指す
    /// キーの列は、キー付きと同じく参照先の自然キーで取る (<see cref="KeyExpressions"/>)。
    /// </summary>
    internal static string? HashRowKey(
        SqliteConnection connection, string schema, string table, MergeTableRule rule, long rowId)
    {
        var keys = KeyExpressions(rule, schema, "t");
        var (count, digest) = DigestQuery(connection, keys.Count,
            $"SELECT {string.Join(", ", keys)} FROM {Quote(schema)}.{Quote(table)} AS t " +
            $"WHERE t.rowid = {rowId.ToString(CultureInfo.InvariantCulture)}");
        return count == 0 ? null : digest;
    }

    private static (long Count, string Digest) DigestQuery(SqliteConnection connection, int columnCount, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long count = 0;
        var row = new StringBuilder();
        while (reader.Read())
        {
            row.Clear();
            for (var i = 0; i < columnCount; i++) AppendValue(row, reader, i);
            row.Append('\n');
            sha.AppendData(Encoding.UTF8.GetBytes(row.ToString()));
            count++;
        }
        return (count, Convert.ToHexString(sha.GetHashAndReset()));
    }

    /// <summary>
    /// 値を型ごとに区別して書き出す。型を付けないと、数値の 1 と文字列の "1" や、
    /// NULL と空文字列が同じ要約になる。
    /// </summary>
    private static void AppendValue(StringBuilder builder, SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            builder.Append("n;");
            return;
        }
        switch (reader.GetValue(ordinal))
        {
            case long l:
                builder.Append('i').Append(l.ToString(CultureInfo.InvariantCulture)).Append(';');
                break;
            case double d:
                builder.Append('r').Append(d.ToString("R", CultureInfo.InvariantCulture)).Append(';');
                break;
            case byte[] bytes:
                builder.Append('b').Append(Convert.ToBase64String(bytes)).Append(';');
                break;
            case var other:
                var text = Convert.ToString(other, CultureInfo.InvariantCulture) ?? string.Empty;
                // 区切りと取り違えないよう、長さを前に付ける。
                builder.Append('t').Append(text.Length.ToString(CultureInfo.InvariantCulture))
                    .Append(':').Append(text).Append(';');
                break;
        }
    }

    private static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];

    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    internal static string Literal(string text) => "'" + text.Replace("'", "''") + "'";
}

/// <summary>テーブル 1 つぶんの要約。どの項目を埋めるかは <see cref="Kind"/> で決まる。</summary>
public sealed class TableSummary
{
    public MergeTableKind Kind { get; set; }

    public long RowCount { get; set; }

    /// <summary><see cref="MergeTableKind.AppendOnly"/>: rowid の最大値。</summary>
    public long MaxRowId { get; set; }

    /// <summary><see cref="MergeTableKind.Keyed"/>: キーから内容のハッシュへ。</summary>
    public Dictionary<string, string>? Rows { get; set; }

    /// <summary>
    /// <see cref="MergeTableKind.Keyed"/>: キーから、<see cref="MergeTableRule.CounterColumns"/>
    /// の値へ。3-way のマージで双方の増分を求めるのに使う。内容の比較には使わない
    /// (<see cref="Rows"/> のハッシュに含まれている)。
    /// </summary>
    public Dictionary<string, Dictionary<string, long>>? Counters { get; set; }

    /// <summary>
    /// <see cref="MergeTableKind.Watched"/>: 内容全体のハッシュ。
    /// <see cref="MergeTableKind.AppendOnly"/>: 後から書き換わる列があるときだけ、キーとその列のハッシュ。
    /// </summary>
    public string? Digest { get; set; }

    /// <summary>
    /// 要約を取ったときの列の並び。マージでは、今の列と違えば祖先に使わない。
    /// 列が増えるとキー付きの内容ハッシュがすべて変わり、変えていない行まで
    /// 編集されたように見えるため。
    /// </summary>
    public List<string>? Columns { get; set; }

    /// <summary><see cref="MergeTableKind.AppendOnly"/>: rowid が最大の行のキーのハッシュ。</summary>
    public string? LastRowHash { get; set; }

    /// <summary>
    /// <see cref="MergeTableKind.AppendOnly"/> で、マージの振り直しで <see cref="MaxRowId"/> より
    /// 後ろへ移った祖先の行。キーのハッシュから、後から書き換わる列の内容のハッシュへ。
    /// rowid では手元で足した行と見分けられないので、キーで見分ける。
    /// </summary>
    public Dictionary<string, string>? TailRows { get; set; }

    public bool HasSameContent(TableSummary other)
    {
        if (Kind != other.Kind || RowCount != other.RowCount) return false;
        // 列が増えたテーブルは、内容が変わったものとして扱う。追加専用の要約や列を絞った
        // キー付きの要約は既知の列しか見ないので、増えた列だけを書き換えても気付けない。
        // 列を残していない古い要約と列を持つ要約は、列が同じか確かめられないので違うものとして
        // 扱う。同じとみなすと、古い要約の後で増えた列だけを手元で書き換えた場合に
        // 気付けない。双方が古い要約なら、従来どおり内容だけを比べる。
        if ((Columns is null) != (other.Columns is null)) return false;
        if (Columns is not null && !Columns.SequenceEqual(other.Columns!, StringComparer.Ordinal))
        {
            return false;
        }
        return Kind switch
        {
            MergeTableKind.AppendOnly => MaxRowId == other.MaxRowId
                                         && string.Equals(LastRowHash, other.LastRowHash, StringComparison.Ordinal)
                                         && string.Equals(Digest, other.Digest, StringComparison.Ordinal),
            MergeTableKind.Keyed => SameRows(Rows, other.Rows),
            MergeTableKind.Watched => string.Equals(Digest, other.Digest, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static bool SameRows(Dictionary<string, string>? a, Dictionary<string, string>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count) return false;
        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var other) || !string.Equals(value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }
}
