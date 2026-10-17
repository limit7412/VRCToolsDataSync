using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace VRCToolsDataSync.Core.Merge;

/// <summary>テーブルの性質。マージでの扱いと、前回同期時点の要約の取り方が決まる。</summary>
public enum MergeTableKind
{
    /// <summary>
    /// 追加専用の履歴。行の削除も書き換えも起きない前提で、マージでは和集合を取る。
    /// 要約には行数と rowid の最大値だけを残す。
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
public sealed record MergeTableRule(
    string NamePattern,
    MergeTableKind Kind,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<string>? ValueColumns = null,
    string? EditedAtColumn = null,
    string? Filter = null,
    IReadOnlyList<string>? IgnoredColumns = null)
{
    private readonly Regex _pattern = new("^(?:" + NamePattern + ")$", RegexOptions.CultureInvariant);

    public bool Matches(string tableName) => _pattern.IsMatch(tableName);
}

/// <summary>
/// ツールごとのマージの規則一式。今は前回同期時点の要約 (<see cref="DatabaseSummary"/>)
/// を取るためだけに使い、行の書き込みはまだ行わない。
/// </summary>
public sealed class MergeProfile
{
    public required string ToolKey { get; init; }

    public required IReadOnlyList<MergeTableRule> Rules { get; init; }

    /// <summary>
    /// DB のスキーマが本ツールの対応している版かを確かめる。対応していれば
    /// Version にその版を表す文字列 (版の概念が無ければ空文字列) を入れ、
    /// 対応していなければ Version を null にして Reason に理由を入れる。
    /// </summary>
    public Func<SqliteConnection, (string? Version, string? Reason)> CheckSchema { get; init; }
        = _ => (string.Empty, null);
}

/// <summary>前回同期時点の要約を取る DB の場所と、その規則の組。</summary>
public sealed record TrackedDatabase(string Path, MergeProfile Profile);
