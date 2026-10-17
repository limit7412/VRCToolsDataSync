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

            summary.Tables[table] = rule.Kind switch
            {
                MergeTableKind.AppendOnly => SummarizeAppendOnly(connection, "main", table, rule),
                MergeTableKind.Keyed => SummarizeKeyed(connection, table, rule, columns),
                MergeTableKind.Watched => SummarizeWatched(connection, table, rule, columns),
                _ => throw new InvalidOperationException($"未知の種類です: {rule.Kind}"),
            };
        }

        reason = null;
        return summary;
    }

    /// <summary>
    /// 規則が前提にしている列のうち、テーブルに無いものを返す。編集時刻の列は要約には
    /// 使わないが、3-way のマージで使うので、ここで欠けていないことを確かめる。
    /// </summary>
    internal static List<string> FindMissingColumns(MergeTableRule rule, IReadOnlyList<string> columns)
        => rule.KeyColumns
            .Concat(rule.ValueColumns ?? Array.Empty<string>())
            .Concat(rule.EditedAtColumn is null ? Array.Empty<string>() : new[] { rule.EditedAtColumn })
            .Concat(rule.OrderColumn is null ? Array.Empty<string>() : new[] { rule.OrderColumn })
            .Concat(rule.GrowingColumns ?? Array.Empty<string>())
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
            : DigestRows(connection, table, rule.KeyColumns, "rowid DESC LIMIT 1").Digest;
        if (rule.GrowingColumns is { Count: > 0 } growing)
        {
            // 行数と rowid だけでは、既存行の書き換え (滞在時間の書き込みなど) を拾えない。
            summary.Digest = DigestRows(connection, table, rule.KeyColumns.Concat(growing).ToList(), "rowid").Digest;
        }
        return summary;
    }

    private static TableSummary SummarizeKeyed(
        SqliteConnection connection, string table, MergeTableRule rule, IReadOnlyList<string> columns)
    {
        var rows = ReadKeyedRows(connection, "main", table, rule, columns);
        return new TableSummary
        {
            Kind = MergeTableKind.Keyed,
            RowCount = rows.Count,
            Rows = rows.ToDictionary(r => r.Key, r => r.Value.Hash, StringComparer.Ordinal),
        };
    }

    /// <summary>キー付きのテーブルの 1 キーぶん。キーの値そのものと、内容のハッシュを持つ。</summary>
    internal sealed record KeyedRow(object?[] KeyValues, string Hash);

    /// <summary>
    /// キー付きのテーブルを読み、キー (型付きで文字列にしたもの) から行へ写す。
    /// 要約とマージで同じハッシュを使うため、両方がここを通る。
    /// </summary>
    internal static Dictionary<string, KeyedRow> ReadKeyedRows(
        SqliteConnection connection, string schema, string table, MergeTableRule rule, IReadOnlyList<string> columns)
    {
        var valueColumns = ValueColumnsOf(rule, columns);
        var selected = rule.KeyColumns.Concat(valueColumns).ToList();

        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {string.Join(", ", selected.Select(Quote))} FROM {Quote(schema)}.{Quote(table)}" +
            (rule.Filter is null ? string.Empty : $" WHERE {rule.Filter}");
        using var reader = command.ExecuteReader();

        var rows = new Dictionary<string, KeyedRow>(StringComparer.Ordinal);
        var keyCount = rule.KeyColumns.Count;
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
            // キーが重複するテーブル (UNIQUE 制約の無い favorite_* など) では、
            // 同じキーの行をまとめて一つの値として扱う。行の順序に左右されないよう、
            // ハッシュを並べ替えてから束ねる。
            var hash = Hash(value.ToString());
            rows[key.ToString()] = rows.TryGetValue(key.ToString(), out var existing)
                ? existing with { Hash = Combine(existing.Hash, hash) }
                : new KeyedRow(keyValues, hash);
        }
        return rows;
    }

    /// <summary><see cref="MergeTableKind.Keyed"/> で比べる列。指定が無ければキー以外のすべて。</summary>
    internal static IReadOnlyList<string> ValueColumnsOf(MergeTableRule rule, IReadOnlyList<string> columns)
        => rule.ValueColumns
           ?? columns.Where(c => !rule.KeyColumns.Contains(c, StringComparer.Ordinal)).ToList();

    private static string Combine(string a, string b)
        => string.CompareOrdinal(a, b) <= 0 ? Hash(a + b) : Hash(b + a);

    private static TableSummary SummarizeWatched(
        SqliteConnection connection, string table, MergeTableRule rule, IReadOnlyList<string> columns)
    {
        var ignored = rule.IgnoredColumns ?? Array.Empty<string>();
        var selected = columns.Where(c => !ignored.Contains(c, StringComparer.Ordinal)).ToList();
        if (selected.Count == 0)
        {
            return new TableSummary { Kind = MergeTableKind.Watched, Digest = Hash(string.Empty) };
        }

        var (count, digest) = DigestRows(connection, table, selected, string.Join(", ", selected.Select(Quote)));
        return new TableSummary
        {
            Kind = MergeTableKind.Watched,
            RowCount = count,
            Digest = digest,
        };
    }

    /// <summary>指定した列の内容を、<paramref name="orderBy"/> の順に読んでハッシュにする。</summary>
    private static (long Count, string Digest) DigestRows(
        SqliteConnection connection, string table, IReadOnlyList<string> columns, string orderBy)
        => DigestQuery(connection, columns.Count,
            $"SELECT {string.Join(", ", columns.Select(Quote))} FROM {Quote(table)} ORDER BY {orderBy}");

    /// <summary>
    /// rowid が <paramref name="rowId"/> の行のキーのハッシュを返す。行が無ければ null。
    /// 要約の <see cref="TableSummary.LastRowHash"/> と同じ方法で求める。
    /// </summary>
    internal static string? HashRowKey(
        SqliteConnection connection, string schema, string table, IReadOnlyList<string> keyColumns, long rowId)
    {
        var (count, digest) = DigestQuery(connection, keyColumns.Count,
            $"SELECT {string.Join(", ", keyColumns.Select(Quote))} FROM {Quote(schema)}.{Quote(table)} " +
            $"WHERE rowid = {rowId.ToString(CultureInfo.InvariantCulture)}");
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
    /// <see cref="MergeTableKind.Watched"/>: 内容全体のハッシュ。
    /// <see cref="MergeTableKind.AppendOnly"/>: 後から書き換わる列があるときだけ、キーとその列のハッシュ。
    /// </summary>
    public string? Digest { get; set; }

    /// <summary><see cref="MergeTableKind.AppendOnly"/>: rowid が最大の行のキーのハッシュ。</summary>
    public string? LastRowHash { get; set; }

    public bool HasSameContent(TableSummary other)
    {
        if (Kind != other.Kind || RowCount != other.RowCount) return false;
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
