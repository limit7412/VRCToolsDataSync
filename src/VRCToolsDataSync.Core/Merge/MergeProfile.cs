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
/// 祖先より減っていた側は、アプリが行を消して 0 から数え直したとみなし、その値を増分にする。
/// 値は整数でも実数でもよい (Friend Connect の同席の秒数は小数を持つ)。
/// </param>
/// <param name="References">
/// キーの列のうち別のテーブルの行を整数 id で指すもの。Friend Connect の id は PC ごとに
/// 別の値を取るので、id のままでは突き合わせられない。
/// 要約とマージでは、その列の値の代わりに指している行の自然キーを使い、手元の行を
/// 同期先へ写すときは同期先での id に付け替える。指している行が同期先に無ければ、
/// その行も手元から写す。<see cref="MergeTableKind.AppendOnly"/> の突き合わせでは、
/// 指す列が NULL の行は同期先に同じ行が無いものとして扱う。
/// </param>
/// <param name="UpdateInPlace">
/// <see cref="MergeTableKind.Keyed"/> で、同期先にあるキーの行を消して入れ直すのではなく、
/// 比べる列だけを書き換える。他のテーブルから id で参照されるテーブル (Friend Connect の
/// User や Tag) に使う。入れ直すと id が変わり、ON DELETE CASCADE で参照している行まで消える。
/// <see cref="ValueColumns"/> を指定したテーブルだけに使える。
/// </param>
/// <param name="ClearOnDelete">
/// <see cref="MergeTableKind.Keyed"/> で、手元からキーが消えていたときに同期先の行を消す代わりに
/// 比べる列を NULL にする。<see cref="Filter"/> で絞ったキーは、行が残っていても列を消すと
/// キーから外れるので、その「消えた」は行の削除ではなく列の消去を表す。Friend Connect の
/// User.note は、メモを消しても User の行は残る。
/// </param>
/// <param name="ClearedColumns">
/// 手元の行を同期先へ写すときに NULL にする列。外部キーの制約が無いまま別の行の id を
/// 持つ列で、同期先では別の行を指してしまうもの。
/// </param>
/// <param name="RuntimeGate">
/// <see cref="MergeTableKind.AppendOnly"/> で、2 台の PC で同時にアプリを動かしていた期間の行を
/// 足さないための絞り込み。同じ出来事を両方の PC が記録していて、記録した時刻がポーリングの
/// 分だけずれるので、キーの一致では重複を除けない。相手が動いていなかった時間の記録だけを
/// 足せば、曖昧な突き合わせ無しで重複を避けられる。詳細は <see cref="Merge.RuntimeGate"/>。
/// </param>
/// <param name="UnionWithoutAncestor">
/// 前回の同期時点の要約にこのテーブルの祖先が無くても (規則に加える前の版のツールが要約を
/// 取った場合)、中止せずに合わせる。<see cref="MergeTableKind.AppendOnly"/> では全行を突き合わせて
/// 和集合を取る。祖先が無いと手元で消した行を見分けられないので、アプリが行を消す操作を持たない
/// テーブル (Friend Connect の稼働区間や履歴) だけに使う。<see cref="MergeTableKind.Keyed"/> では
/// 祖先の無い 2-way になり、手元の削除は伝わらず、両方にあって内容が違うキーは同期先の内容を残す。
/// 手元にしか無いキーを足せれば十分なテーブル (Friend Connect の写真の一覧) に使う。
/// </param>
/// <param name="UnionByKey">
/// <see cref="MergeTableKind.AppendOnly"/> で、前回の同期の後に増えた行を rowid の範囲で絞らず、
/// 手元の全行をキーで突き合わせて同期先に無い行を足す。id をアプリが決めていて新しい行の id が
/// 小さいこともあるテーブル (Friend Connect の LocationMaster) では、rowid の最大値で増えた行を
/// 取れないため。キーに rowid の別名の列 (id) が含まれていれば、その値も変えずに写す。
/// </param>
/// <param name="IdentityColumns">
/// <see cref="MergeTableKind.AppendOnly"/> で、キーが同じ行なら両方の PC で同じ値を持つはずの列。
/// キーの一致する行でこの列が違えば、キーが別の実体を指していて (アプリが決める id が PC ごとに
/// 違う値になるなど) 突き合わせの前提が崩れているので、マージを中止する。
/// </param>
/// <param name="KeepHostOnLocalDelete">
/// <see cref="MergeTableKind.Keyed"/> で、手元で消えたキーの同期先の行を消さずに残す。同期先の値を
/// 正として足りない行だけを足すテーブル (Friend Connect の分析結果) に使う。手元の追加と、
/// 手元だけが変えたキーの内容は伝える。
/// </param>
/// <param name="DerivedColumns">
/// <see cref="MergeTableKind.Keyed"/> で、同じ行の他の列から決まる列と、その求め方。両方が同じキーを
/// 書き換えていて累計の列などを合成したときに、合成した値からこの列を求め直す。合成しなかった行
/// (片方の内容をそのまま残す行) はアプリが書いた値のままなので触らない。比べる列には含めない。
/// Friend Connect のワールド訪問の平均滞在時間 (stayAvg) は、滞在の回数と秒数から決まる。
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
    IReadOnlyList<string>? CounterColumns = null,
    IReadOnlyList<ColumnReference>? References = null,
    bool UpdateInPlace = false,
    bool ClearOnDelete = false,
    IReadOnlyList<string>? ClearedColumns = null,
    RuntimeGate? RuntimeGate = null,
    bool UnionWithoutAncestor = false,
    bool UnionByKey = false,
    IReadOnlyList<string>? IdentityColumns = null,
    IReadOnlyList<DerivedColumn>? DerivedColumns = null,
    bool KeepHostOnLocalDelete = false)
{
    private readonly Regex _pattern = new("^(?:" + NamePattern + ")$", RegexOptions.CultureInvariant);

    public bool Matches(string tableName) => _pattern.IsMatch(tableName);

    /// <summary><paramref name="column"/> が指す別のテーブルの行の対応。無ければ null。</summary>
    public ColumnReference? ReferenceOf(string column)
        => References?.FirstOrDefault(r => string.Equals(r.Column, column, StringComparison.Ordinal));
}

/// <summary>
/// 同じ行の他の列から決まる列 (<see cref="MergeTableRule.DerivedColumns"/>) と、その求め方。
/// </summary>
/// <param name="Column">求め直す列。</param>
/// <param name="Expression">
/// 値を求める SQL の式。同じ行の列を名前で参照できる (別名は付けない)。整数の列なら、式の結果も
/// 整数になるようにする (SQLite の整数どうしの除算は切り捨てで、実数が混ざれば CAST で切り捨てる)。
/// </param>
public sealed record DerivedColumn(string Column, string Expression);

/// <summary>
/// 別のテーブルの行を整数 id で指す列と、その行を PC をまたいで見分ける自然キーの対応。
/// </summary>
/// <param name="Column">このテーブルの、参照している列。</param>
/// <param name="Table">参照先のテーブル。</param>
/// <param name="KeyColumns">参照先の行を見分ける列。複数なら組で見分ける。</param>
/// <param name="IdColumn">参照先の、<see cref="Column"/> の値が指す列。</param>
/// <param name="KeyReferences">
/// <see cref="KeyColumns"/> のうち、それ自体がさらに別のテーブルの行を id で指す列の対応。
/// Friend Connect の滞在 (Location) はユーザーの id と開始時刻で見分けるが、ユーザーの id も
/// PC ごとに違うので、その列は VRChat のユーザー ID に置き換えて比べる。
/// </param>
/// <param name="NullIfMissing">
/// 手元の行を同期先へ写すとき、指す先の行が同期先に無ければ、その行を写さずにこの列を NULL に
/// する。NULL を許す列で、対応が無くても行自体に意味があるもの (写真と滞在の対応) に使う。
/// 指す先の行が相手の PC の稼働中の記録 (<see cref="MergeTableRule.RuntimeGate"/> で足さない行)
/// だと、写すとその行を足すことになるため。
/// </param>
/// <param name="Fallback">
/// 自然キーの一致する行が同期先に無いとき、代わりに指す行の探し方。<see cref="KeyColumns"/> から
/// <see cref="IntervalMatch.StartColumn"/> を除いた列が一致し、このテーブルの行の時刻
/// (<see cref="IntervalMatch.TimeColumn"/>) を区間に含む行を指す。相手の PC の稼働中に手元が記録した
/// 滞在は足さないので、その間の動画や写真は、相手が記録した同じユーザーの滞在 (開始時刻が
/// ポーリングの分だけ違う) に結び付ける。複数あれば開始時刻の最も遅い行。
/// </param>
public sealed record ColumnReference(
    string Column, string Table, IReadOnlyList<string> KeyColumns, string IdColumn = "id",
    IReadOnlyList<ColumnReference>? KeyReferences = null, bool NullIfMissing = false, IntervalMatch? Fallback = null)
{
    /// <summary><paramref name="column"/> (参照先の列) が指す別のテーブルの行の対応。無ければ null。</summary>
    public ColumnReference? KeyReferenceOf(string column)
        => KeyReferences?.FirstOrDefault(r => string.Equals(r.Column, column, StringComparison.Ordinal));

    /// <summary>
    /// 参照先の行を id そのもので見分けるか。id が PC 間で同じ値を取る (アプリが入力から決める)
    /// テーブルへの参照で、その行を同期先へ写すときは id も変えずに写す。
    /// </summary>
    public bool KeepsId => KeyColumns.Count == 1 && string.Equals(KeyColumns[0], IdColumn, StringComparison.Ordinal);
}

/// <summary>
/// 自然キーの一致する行が無いときに、時刻を区間に含む行を代わりに指す対応 (<see cref="ColumnReference.Fallback"/>)。
/// </summary>
/// <param name="TimeColumn">参照する側のテーブルの、出来事の時刻の列。</param>
/// <param name="StartColumn">参照先の、区間の開始時刻の列。自然キーに含まれる列で、一致を求めない。</param>
/// <param name="EndColumn">参照先の、区間の終了時刻の列。</param>
public sealed record IntervalMatch(string TimeColumn, string StartColumn, string EndColumn);

/// <summary>
/// 追加専用の履歴で、相手の PC のアプリが動いていた期間に手元が記録した行を足さないための対応。
/// <para>
/// 同期先の稼働区間のテーブル (<see cref="Table"/>) の行のうち、手元の同じ区間 (開始時刻が同じ行)
/// が知らない部分を「相手が動いていた期間」とみなす。手元に無い区間は全体、手元にもあるが
/// 同期先の終了時刻の方が遅い区間は手元の終了時刻より後の部分である。手元の履歴行のうち
/// <see cref="TimeColumn"/> がその期間に入るものは、相手の PC も同じ出来事を記録しているので
/// 足さない。同期先の稼働区間は、マージが同期先の表を書き換える前の内容で取る。
/// </para>
/// <para>
/// 同期先の表には、前回までの同期で手元から渡った手元自身の区間も入っているが、それは手元にも
/// 同じ開始時刻の行があり、手元の終了時刻の方が遅いか等しいので、期間に数えない。
/// </para>
/// <para>
/// <see cref="EndColumn"/> を指定すると、出来事が相手の停止後まで続いた行 (終了時刻が期間の外)
/// は足す。相手の行は停止時点で止まっているので、除くとその後の記録が失われる。期間の中で
/// 重なる 2 行が残るのは、相手の起動をまたいで始まった出来事と同じ扱いである。
/// </para>
/// </summary>
/// <param name="TimeColumn">このテーブルの、出来事の開始時刻の列。</param>
/// <param name="Table">稼働区間のテーブル。</param>
/// <param name="StartColumn">稼働区間の開始時刻の列。区間を見分けるキーでもある。</param>
/// <param name="EndColumn">稼働区間の終了時刻の列。</param>
/// <param name="TimeEndColumn">このテーブルの、出来事の終了時刻の列。null なら開始時刻だけで判定する。</param>
public sealed record RuntimeGate(string TimeColumn, string Table, string StartColumn, string EndColumn, string? TimeEndColumn = null);

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
        // 参照の後から足した項目 (入れ子の参照、無ければ NULL、区間での代替) も、付いている参照だけに書く。
        static string Reference(ColumnReference r)
            => $"{r.Column}>{r.Table}.{r.IdColumn}({Columns(r.KeyColumns)})"
               + (r.KeyReferences is { Count: > 0 } nested ? $"[{string.Join(",", nested.Select(Reference))}]" : string.Empty)
               + (r.NullIfMissing ? "?" : string.Empty)
               + (r.Fallback is { } f ? $"~{f.TimeColumn}:{f.StartColumn}-{f.EndColumn}" : string.Empty);
        static string References(IReadOnlyList<ColumnReference>? references) => references is null
            ? "-"
            : string.Join(",", references.Select(Reference));
        // 後から足した印は、付いている規則だけに書く。既定値でも書くと、その印を使わない
        // プロファイル (VRCX) のダイジェストまで版が変わり、更新後の最初のマージで、前回の
        // 要約の後に両方の PC で作られた同名のテーブルを「以前は対象外だった」と見誤る。
        var text = string.Join("\n", Rules.Select(r => string.Join("|",
            r.NamePattern, r.Kind.ToString(), Columns(r.KeyColumns), Columns(r.ValueColumns),
            r.EditedAtColumn ?? "-", r.Filter ?? "-", Columns(r.IgnoredColumns), r.OrderColumn ?? "-",
            Columns(r.GrowingColumns), Columns(r.ShrinkingColumns), Columns(r.CounterColumns),
            References(r.References), r.UpdateInPlace ? "u" : "-", r.ClearOnDelete ? "c" : "-", Columns(r.ClearedColumns))
            + (r.RuntimeGate is { } g ? $"|g{g.TimeColumn}{(g.TimeEndColumn is null ? "" : "~" + g.TimeEndColumn)}@{g.Table}.{g.StartColumn}-{g.EndColumn}" : string.Empty)
            + (r.UnionWithoutAncestor ? "|a" : string.Empty)
            + (r.UnionByKey ? "|k" : string.Empty)
            + (r.IdentityColumns is { Count: > 0 } ? $"|i{Columns(r.IdentityColumns)}" : string.Empty)
            + (r.DerivedColumns is { Count: > 0 } ? $"|d{string.Join(",", r.DerivedColumns.Select(d => $"{d.Column}={d.Expression}"))}" : string.Empty)
            + (r.KeepHostOnLocalDelete ? "|h" : string.Empty)));
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

/// <summary>前回同期時点の要約を取る DB の場所と、その規則の組。</summary>
public sealed record TrackedDatabase(string Path, MergeProfile Profile);
