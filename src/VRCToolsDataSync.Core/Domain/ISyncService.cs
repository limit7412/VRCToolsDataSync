
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

    /// <summary>true なら結果を数えるだけで、ローカルには何も書かない。</summary>
    public bool DryRun { get; init; }

    public bool SkipBackup { get; init; }
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
