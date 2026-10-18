
using VRCToolsDataSync.Core.Merge;

namespace VRCToolsDataSync.Core.Domain;

public interface ISyncService
{
    string ToolKey { get; }

    /// <summary>
    /// 前回同期時点の要約を取る DB と、その規則。要約を取らないツールは null。
    /// </summary>
    TrackedDatabase? TrackedDatabase { get; }

    SyncResult Push(PushOptions options);
    SyncResult Pull(PullOptions options);
}

/// <summary>
/// 行を合成するマージに対応したツール。マージは「同期先の最新を土台にし、手元にしか
/// 無い変更を足し戻す」操作で、結果はローカルに置くだけである。同期先へ反映する
/// Push は呼び出し側 (<c>SyncRunner</c>) が続けて行う。
/// </summary>
public interface IMergeableSyncService : ISyncService
{
    SyncResult PullMerge(PullMergeOptions options);
}

public sealed class PullMergeOptions
{
    public required ISyncStorage Storage { get; init; }

    /// <summary>前回同期した時点の要約。無ければ 2-way のマージになる。</summary>
    public DatabaseSummary? Baseline { get; init; }

    /// <summary>
    /// 自分が最後に送った DB の写しの SHA-256。同期先の写しがこれと同じなら、同期先は
    /// 自分の送った内容そのものなので、<see cref="Baseline"/> の代わりに同期先の要約を
    /// 祖先にする。送った後に同期履歴を保存する前に止まった場合に、同じ変更を同期先と
    /// 手元の両方の変更と見て二重に足さないため。
    /// </summary>
    public string? OwnPushSha256 { get; init; }

    /// <summary>true なら結果を数えるだけで、ローカルには何も書かない。</summary>
    public bool DryRun { get; init; }

    public bool SkipBackup { get; init; }

    /// <summary>
    /// ローカルをマージ結果で置き換えた直後に呼ぶ。引数は土台にした同期先の version と、
    /// その版の要約 (取れなければ null)。呼び出し側は、置き換えた後のローカルに合う
    /// 同期履歴と祖先の要約をここで保存する。
    /// <para>
    /// 置き換えより後に呼ぶのは、間で落ちたときに同期履歴だけが進まないようにするため。
    /// 同期履歴が進んで DB が古いままだと、次の Push が同期先の変更を古い DB で上書きする。
    /// 逆に DB だけが新しいなら、手元に未 Push の変更があるものとして扱われ、上書きされない。
    /// </para>
    /// </summary>
    public Action<long, DatabaseSummary?>? AfterReplaceLocal { get; init; }

    /// <summary>
    /// ローカルをマージ結果で置き換える直前に呼ぶ。呼び出し側は引数を保留として残し、
    /// <see cref="AfterReplaceLocal"/> の保存が終わる前に止まっても、次回に置き換えが
    /// 済んだかを見分けて祖先を復旧できるようにする。例外を投げれば置き換えない。
    /// </summary>
    public Action<MergePendingState>? BeforeReplaceLocal { get; init; }
}

public sealed class PushOptions
{
    /// <summary>同期先。ローカル同期フォルダか S3 互換ストレージのどちらか。</summary>
    public required ISyncStorage Storage { get; init; }

    public required string MachineName { get; init; }
    public bool ForceOverwriteOnConflict { get; init; }

    /// <summary>
    /// 非常用の強制 Push。同期先の記録を一切信用せず、手元の全ファイルを送り直して
    /// 同期先を置き換える。<see cref="ForceOverwriteOnConflict"/> を含む。
    /// <para>
    /// 通常の Push は manifest の記録と実体の有無を見て送信を省き、何も変わらなければ
    /// manifest にも触れない。同期先の実体の中身が壊れている (ハッシュが記録と合わない)
    /// 場合は、この省略のせいで Push し直しても直らず、他の PC の Pull が失敗し続ける。
    /// その状態から抜け出すための手段で、次の違いがある。
    /// </para>
    /// <list type="bullet">
    /// <item>送信を省かず、同期先に同じキーの実体があっても置き換える。</item>
    /// <item>内容が前回と同じでも manifest の version を進め、他の PC に取り直させる。</item>
    /// <item>Push の途中で他の PC が同期先を更新しても、競合として止めずに上書きする。</item>
    /// </list>
    /// </summary>
    public bool ReuploadAll { get; init; }

    public long? LastPulledVersion { get; init; }

    /// <summary>
    /// 送る DB の写しの SHA-256 が決まったら、同期先へ反映する前に呼ばれる。送った後、
    /// 同期履歴を保存する前に止まっても、次の同期で同期先の内容が自分の送ったものだと
    /// 見分けられるよう、呼び出し側が控える。
    /// </summary>
    public Action<string>? BeforeCommit { get; init; }
}

public sealed class PullOptions
{
    /// <summary>同期先。ローカル同期フォルダか S3 互換ストレージのどちらか。</summary>
    public required ISyncStorage Storage { get; init; }

    public bool SkipBackup { get; init; }

    // Issue #19: 起動時自動 Pull の暴走防止。
    // SkipIfNotNewer=true かつ LastPulledVersion>=リモート Version の場合は Pull を行わず
    // NothingToDo を返す。手動 Pull / コンフリクト解消 Pull は呼び出し側でデフォルトの
    // false のままにして、「ユーザが意図的に呼んだ Pull は従来通り上書きする」セマンティクスを維持する。
    public long? LastPulledVersion { get; init; }
    public bool SkipIfNotNewer { get; init; }

    /// <summary>
    /// 前回の同期の後に手元で変わったテーブルを返す (無ければ空)。起動時の自動 Pull
    /// (<see cref="SkipIfNotNewer"/>) で、リモートが新しいと分かった後に呼ぶ。
    /// 変わったテーブルがあれば上書きせず <see cref="SyncOutcome.LocalChangesPending"/>
    /// を返す。比べられない場合 (前回の要約が無いなど) は空を返し、従来どおり上書きする。
    /// </summary>
    public Func<IReadOnlyList<string>>? FindUnsyncedLocalChanges { get; init; }
}

public sealed class SyncResult
{
    public required SyncOutcome Outcome { get; init; }
    public string? Message { get; init; }
    public long? RemoteVersion { get; init; }
    public long? LastPulledVersion { get; init; }
    public string? BackupPath { get; init; }
    public IReadOnlyList<string> AffectedFiles { get; init; } = Array.Empty<string>();

    /// <summary>マージを行った場合の内訳。</summary>
    public MergeReport? MergeReport { get; init; }

    /// <summary>
    /// Push で実際に送った DB の写し、または Pull で取り出して手元に置いた写しの要約。
    /// 次に比べる基準はこれから保存する。同期した後に手元の DB から取ると、その間に
    /// ツールが書き込んだ未送信の変更が基準に混ざり、次の起動時 Pull がその変更を
    /// 見落として上書きする。
    /// </summary>
    public DatabaseSummary? SyncedSummary { get; init; }
}

public enum SyncOutcome
{
    Success,
    NothingToDo,
    ConflictDetected,
    SourceMissing,
    Aborted,

    /// <summary>
    /// 起動時の自動 Pull で、手元に未 Push の変更があったので上書きを見送った。
    /// </summary>
    LocalChangesPending,
}
