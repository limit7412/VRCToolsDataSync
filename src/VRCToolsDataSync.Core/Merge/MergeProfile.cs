using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace VRCToolsDataSync.Core.Merge;

/// <summary>テーブルの性質。マージでの扱いと、前回同期時点の要約の取り方が決まる。</summary>
public enum MergeTableKind
{
    /// <summary>
    /// 追加専用の履歴。マージでは和集合を取る。要約には行数と rowid の最大値を残し、
    /// 既存行で後から書き換わる列 (<see cref="MergeTableRule.GrowingColumns"/>) が
    /// あれば、その列の内容ハッシュも残す。
    /// </summary>
    AppendOnly,

    /// <summary>
    /// キーで行が決まり、ユーザーが編集・削除しうるもの。マージでは前回同期時点との
    /// 3-way で編集と削除を伝える。要約にはキーごとの内容ハッシュを残す。
    /// </summary>
    Keyed,

    /// <summary>
    /// 変化だけを見るもの。マージはしない (行を書き換えない) が、手元で変わったことは
    /// 検知したいテーブルに使う。要約には内容全体のハッシュを残す。
    /// </summary>
    Watched,
}

/// <summary>
/// テーブル 1 種類ぶんの規則。<see cref="NamePattern"/> は表全体に一致する正規表現で、
/// VRCX のようにユーザー id を接頭辞に持つテーブルをまとめて指せる。
/// </summary>
/// <param name="NamePattern">対象テーブル名に完全一致させる正規表現。</param>
/// <param name="Kind">テーブルの性質。</param>
/// <param name="KeyColumns">行を決めるキー。<see cref="MergeTableKind.AppendOnly"/> では重複除去に使う。</param>
/// <param name="ValueColumns">
/// <see cref="MergeTableKind.Keyed"/> で比べる列。null ならキー以外のすべての列。
/// </param>
/// <param name="EditedAtColumn">両側で編集されていた場合に新しい方を選ぶための時刻列。</param>
/// <param name="Filter">対象行を絞る WHERE 句 (例: 空のメモを除く)。</param>
/// <param name="IgnoredColumns">
/// <see cref="MergeTableKind.Watched"/> で比べない列。アプリが自動で書き換える
/// 時刻列や、作り直しで変わる id を外す。
/// </param>
/// <param name="OrderColumn">
/// <see cref="MergeTableKind.AppendOnly"/> で、出来事の順序を表す列。行を足した後、
/// id の順がこの列の順と合うように振り直す。VRCX は id の大きい行を「最新」と
/// して扱うため、古い出来事を後から足すと最新として表示されてしまう。
/// </param>
/// <param name="GrowingColumns">
/// <see cref="MergeTableKind.AppendOnly"/> で、既存行に後から書き込まれる列。VRCX は
/// ワールドを出たときに gamelog_location の滞在時間 (time) を書き込み、通知を期限切れに
/// したときに expired を 1 にする。どちらも値が増える方向にしか変わらない。
/// <see cref="MergeTableKind.Keyed"/> では、両方が同じキーを書き換えていたときに、
/// どちらの行を残してもこの列は大きい方の値にする。既読の印のように、編集時刻を
/// 更新せずに書き換わる列を、もう一方の編集で巻き戻さないため。
/// </param>
/// <param name="ShrinkingColumns">
/// <see cref="MergeTableKind.Keyed"/> で、両方が同じキーを書き換えていたときに小さい方の
/// 値を残す列。NULL は値が無いものとして扱う。VRCX は通知を期限切れにするとき、
/// 期限 (expires_at) をその時刻に早める。
/// </param>
/// <param name="CounterColumns">
/// <see cref="MergeTableKind.Keyed"/> で、各 PC が自分の使った分を足していく累計の列。
/// 両方が同じキーを書き換えていたら、祖先の値に双方の増分を足した値にする。大きい方を
/// 残すと、少ない側の PC で足した分が失われる。祖先の値が要約に無ければ大きい方を残す。
/// </param>
public sealed record MergeTableRule(
    string NamePattern,
    MergeTableKind Kind,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<string>? ValueColumns = null,
    string? EditedAtColumn = null,
    string? Filter = null,
    IReadOnlyList<string>? IgnoredColumns = null,
    string? OrderColumn = null,
    IReadOnlyList<string>? GrowingColumns = null,
    IReadOnlyList<string>? ShrinkingColumns = null,
    IReadOnlyList<string>? CounterColumns = null)
{
    private readonly Regex _pattern = new("^(?:" + NamePattern + ")$", RegexOptions.CultureInvariant);

    public bool Matches(string tableName) => _pattern.IsMatch(tableName);
}

/// <summary>
/// ツールごとのマージの規則一式。前回同期時点の要約 (<see cref="DatabaseSummary"/>) と、
/// 行を合成するマージ (<see cref="SqliteMerger"/>) の両方がこれに従う。
/// </summary>
public sealed class MergeProfile
{
    public required string ToolKey { get; init; }

    public required IReadOnlyList<MergeTableRule> Rules { get; init; }

    private string? _rulesDigest;

    /// <summary>
    /// 規則一式を表すハッシュ。要約に残し、要約を取ったときと今とで規則が同じかを見分ける。
    /// 規則に無いテーブルは要約に入らないので、規則を増やした後の最初のマージでは、
    /// 前回の要約にそのテーブルの祖先が無い。
    /// </summary>
    public string RulesDigest => _rulesDigest ??= ComputeRulesDigest();

    private string ComputeRulesDigest()
    {
        static string Columns(IReadOnlyList<string>? columns) => columns is null ? "-" : string.Join(",", columns);
        var text = string.Join("\n", Rules.Select(r => string.Join("|",
            r.NamePattern, r.Kind.ToString(), Columns(r.KeyColumns), Columns(r.ValueColumns),
            r.EditedAtColumn ?? "-", r.Filter ?? "-", Columns(r.IgnoredColumns), r.OrderColumn ?? "-",
            Columns(r.GrowingColumns), Columns(r.ShrinkingColumns), Columns(r.CounterColumns))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    /// <summary>
    /// 行を書き込むマージを許すか。false のツールは要約による検知だけを行う。
    /// </summary>
    public bool SupportsMerge { get; init; }

    /// <summary>
    /// DB のスキーマが本ツールの対応している版かを確かめる。対応していれば
    /// Version にその版を表す文字列 (版の概念が無ければ空文字列) を入れ、
    /// 対応していなければ Version を null にして Reason に理由を入れる。
    /// </summary>
    public Func<SqliteConnection, (string? Version, string? Reason)> CheckSchema { get; init; }
        = _ => (string.Empty, null);
}

/// <summary>
/// 前回同期時点の要約を取る DB の場所と、その規則の組。<paramref name="RemoteKey"/> は、その DB の
/// 写しが manifest に載る位置。送った写しの記録が反映されたかを、manifest の写しのハッシュと
/// 照らして確かめるのに使う。
/// </summary>
public sealed record TrackedDatabase(string Path, MergeProfile Profile, string? RemoteKey = null);
