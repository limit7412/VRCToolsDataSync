using Microsoft.Extensions.Logging;
using VRCToolsDataSync.Core.Domain;
using VRCToolsDataSync.Core.Infra;
using VRCToolsDataSync.Core.Merge;

namespace VRCToolsDataSync.Core.UseCase;

/// <summary>
/// SQLite の本体 1 つを持つツールの <see cref="IMergeableSyncService.PullMerge"/> の手順。
/// 同期先の最新のスナップショットを土台に、手元の DB にしか無い変更を足した DB を作り、
/// ローカルの DB と置き換える。同期先へは送らない。
/// <para>
/// VRCX と Friend Connect で違うのは、ファイルの場所と名前、規則、止めるべきプロセス、
/// バックアップに含めるファイルだけなので、それらを受け取ってここで共通に行う。
/// 祖先の決め方 (送った写しの記録と強制 Push の版の照合) は、どちらのツールでも同じ。
/// </para>
/// <para>
/// DB 以外のファイル (VRCX.json、Friend Connect の config.json やサムネイル) には触れない。
/// 続く Push で手元のものが送られる。行単位で合わせられないので、マージを始めた PC の
/// ものを残す。
/// </para>
/// </summary>
internal sealed class DatabaseMergeFlow
{
    /// <summary>manifest の項目の名前 (<see cref="ISyncService.ToolKey"/>)。</summary>
    public required string ToolKey { get; init; }

    /// <summary>ログと伝言に出すツールの名前。</summary>
    public required string DisplayName { get; init; }

    /// <summary>manifest の files[] での、DB のスナップショットの名前。</summary>
    public required string SnapshotKey { get; init; }

    /// <summary>手元の DB 本体の場所。</summary>
    public required string DatabasePath { get; init; }

    public required MergeProfile Profile { get; init; }

    /// <summary>マージの間に動いていてはいけないプロセス。</summary>
    public required IReadOnlyList<string> ProcessNames { get; init; }

    /// <summary>
    /// 置き換える前のローカルを退避する。退避先を返す。<see cref="PullMergeOptions.SkipBackup"/>
    /// なら呼ばない。
    /// </summary>
    public required Func<string?> CreateBackup { get; init; }

    public required ILogger Logger { get; init; }

    public SyncResult Run(PullMergeOptions options)
    {
        ProcessGuard.EnsureNotRunning(ProcessNames);

        if (!File.Exists(DatabasePath))
        {
            return new SyncResult
            {
                Outcome = SyncOutcome.SourceMissing,
                Message = $"{DisplayName} のデータが見つかりません: {DatabasePath}",
            };
        }

        var storage = options.Storage;
        var manifest = new ManifestStore(storage).Load();
        if (!manifest.Tools.TryGetValue(ToolKey, out var entry)
            || entry.Files.FirstOrDefault(f => f.RelativePath == SnapshotKey) is not { } remoteSnapshot)
        {
            return new SyncResult
            {
                Outcome = SyncOutcome.NothingToDo,
                Message = $"クラウド側に {DisplayName} のデータがありません",
            };
        }

        var work = Path.Combine(Path.GetTempPath(), "VRCToolsDataSync-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // (1) 土台にする同期先のスナップショットを取り出し、記録と合うか確かめる。
            var hostPath = Path.Combine(work, "remote.sqlite3");
            if (!storage.TryDownload(ManifestFileKeys.StorageKeyOf(remoteSnapshot), hostPath))
            {
                return new SyncResult
                {
                    Outcome = SyncOutcome.SourceMissing,
                    Message = $"クラウド側にスナップショットがありません: {SnapshotKey}",
                };
            }
            if (!string.IsNullOrEmpty(remoteSnapshot.Sha256)
                && !string.Equals(FileHasher.Sha256(hostPath), remoteSnapshot.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new SyncResult
                {
                    Outcome = SyncOutcome.Aborted,
                    Message = $"取得したファイルの内容が manifest の記録と一致しません: {SnapshotKey}。" +
                              "正しいデータを持つ PC から非常用の強制 Push を行うと解消します。",
                };
            }

            // (2) 手元の DB の写しを取る。WAL を統合するためと、マージ中に手元の
            //     ファイルを開いたままにしないため。
            // 写しを取っている間か取った後にツールが DB を書き換えたら、その更新は写しに
            // 入らず、置き換えで失われる。写しの前後でファイルの状態を控えて比べ、置き換えの
            // 直前にもう一度比べる。
            var localPath = Path.Combine(work, "local.sqlite3");
            var localFingerprint = DatabaseFingerprint.Of(DatabasePath);
            SqliteSnapshot.Create(DatabasePath, localPath);
            if (DatabaseFingerprint.Of(DatabasePath) != localFingerprint)
            {
                return new SyncResult
                {
                    Outcome = SyncOutcome.Aborted,
                    RemoteVersion = entry.Version,
                    Message = $"手元の DB の写しを取る間に {DisplayName} の DB が書き換えられたため、マージを中止しました。" +
                              $"{DisplayName} を終了してからやり直してください。",
                };
            }

            // (3) 写しどうしでマージする。手元の DB にはまだ触れない。
            //     マージ後の祖先にするため、土台の要約をマージの前に取っておく。
            var hostBaseline = DatabaseSummary.Compute(hostPath, Profile, out var hostReason);
            if (hostBaseline is not null) hostBaseline.RemoteVersion = entry.Version;
            else if (!options.DryRun)
            {
                // 要約が無いと、置き換えた後のローカルに祖先が無くなり、次の同期で手元の
                // 変更を見分けられない。置き換える前にやめる。
                return new SyncResult
                {
                    Outcome = SyncOutcome.Aborted,
                    RemoteVersion = entry.Version,
                    Message = $"同期先の DB を要約できないため、マージを中止しました: {hostReason}",
                };
            }
            var baseline = options.Baseline;
            // 送った写しの記録から祖先を決める。マージを中止するなら、その結果を返す。
            SyncResult? ResolveOwnPush(PushedRecord ownPush)
            {
                if (string.Equals(remoteSnapshot.Sha256, ownPush.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    // 同期先は自分が送った写しそのもの。手元との違いはすべて送った後の手元の変更
                    // なので、同期先を祖先にする。
                    Logger.LogInformation("同期先の DB は自分が送ったものなので、それを祖先にします");
                    baseline = hostBaseline;
                }
                else if (ownPush is { Committed: true, Version: long pushedVersion, Summary: not null }
                         && entry.Version > pushedVersion
                         && (baseline is null || baseline.RemoteVersion < pushedVersion))
                {
                    if (entry.LastForcedVersion is null)
                    {
                        // この項目を知らない古い版のツールが書いた版。その後に強制 Push があったかを
                        // 確かめられないので、送った写しを祖先にできない。
                        return UnknownForcedVersion(entry.Version, pushedVersion);
                    }
                    if (entry.LastForcedVersion is long forcedVersion && forcedVersion > pushedVersion)
                    {
                        // 送った版の後に別の PC が強制 Push で上書きした。その版は送った内容を
                        // 取り込んでいないので、送った写しを祖先にすると送った変更が消える。
                        // 古い要約を祖先にすると二重に足す。どちらも避けるため止める。
                        return new SyncResult
                        {
                            Outcome = SyncOutcome.Aborted,
                            RemoteVersion = entry.Version,
                            Message = $"前回の同期 (版 {pushedVersion}) の後に別の PC が強制 Push (版 {forcedVersion}) で同期先を上書きしています。" +
                                      "前回そろえた内容が同期先に残っているか分からないため、自動マージを中止しました。" +
                                      "同期先の内容を正とするなら Pull を、手元の内容を正とするなら非常用の強制 Push をしてください",
                        };
                    }
                    // 送った版の後に別の PC が版を進めた。その版は送った内容を取り込んでいるので、
                    // 送った写しの要約を祖先にする。それより前の要約を祖先にすると、送った変更を
                    // 同期先と手元の両方の変更と見て二重に足す。
                    Logger.LogInformation(
                        "同期先の版 {Remote} は前回そろえた版 {Pushed} より後なので、そのときの写しの要約を祖先にします",
                        entry.Version, pushedVersion);
                    baseline = ownPush.Summary;
                }
                else if (ownPush is { Committed: false, PlannedVersion: null })
                {
                    // 予定の版を控えない版のツールが送った後に止まった記録 (写しのハッシュだけの
                    // 記録も含む)。反映されたかを版で見分けられず、要約も無ければ祖先にもできない
                    // ので、同期先の写しが違えば止める。古い要約で続けると送った変更を二重に足す。
                    return new SyncResult
                    {
                        Outcome = SyncOutcome.Aborted,
                        RemoteVersion = entry.Version,
                        Message = "前回の Push が同期先に反映されたか分からないまま、同期先が進んでいます。" +
                                  "送った変更を二重に数えないよう、自動マージを中止しました。" +
                                  "同期先の内容を正とするなら Pull を、手元の内容を正とするなら非常用の強制 Push をしてください",
                    };
                }
                else if (ownPush is { Committed: false, PlannedVersion: long plannedVersion }
                         && (baseline is null || baseline.RemoteVersion < plannedVersion))
                {
                    if (entry.Version == plannedVersion)
                    {
                        // 取る予定だった版を別の PC が取った。送信は反映されなかったので、記録は要らない。
                        // dry-run は手元に何も書かないので、消す予定として知らせるだけにする。
                        Logger.LogInformation("前回の送信は反映されていませんでした (版 {Version} は別の PC のもの)", plannedVersion);
                        if (options.DryRun) Logger.LogInformation("dry-run のため、前回の送信の記録はまだ消しません");
                        else options.DiscardOwnPush?.Invoke();
                        // その前の Push の反映済みの記録を抱えていれば、同期先がその版の先へ
                        // 進んでいるので、その記録で祖先を決め直す。
                        if (ownPush.Previous is { } previous) return ResolveOwnPush(previous);
                    }
                    else if (entry.Version > plannedVersion)
                    {
                        // 反映の直後に止まって反映済みの記録を書けなかったか、反映されずに別の PC が
                        // 版を 2 つ以上進めたか、見分けられない。古い要約を祖先にすると、反映されていた
                        // 場合に送った変更を二重に足すので、止める。
                        return new SyncResult
                        {
                            Outcome = SyncOutcome.Aborted,
                            RemoteVersion = entry.Version,
                            Message = $"前回の Push (版 {plannedVersion}) が同期先に反映されたか分からないまま、同期先が版 {entry.Version} まで進んでいます。" +
                                      "送った変更を二重に数えないよう、自動マージを中止しました。" +
                                      "同期先の内容を正とするなら Pull を、手元の内容を正とするなら非常用の強制 Push をしてください",
                        };
                    }
                }
                return null;
            }
            if (options.OwnPush is { } ownPushRecord && ResolveOwnPush(ownPushRecord) is { } ownPushAbort)
            {
                return ownPushAbort;
            }
            if (baseline is not null && ReferenceEquals(baseline, options.Baseline) && entry.Version > baseline.RemoteVersion)
            {
                // 前回同期した版より後に別の PC が強制 Push で上書きしていれば、同期先はその版の
                // 内容を取り込んだ子孫とは限らない。その要約を祖先にすると、祖先からの双方の増分を
                // 足す合成 (着用時間の累計など) が実際と違う祖先を基に計算され、値が狂う。
                if (entry.LastForcedVersion is null)
                {
                    return UnknownForcedVersion(entry.Version, baseline.RemoteVersion);
                }
                if (entry.LastForcedVersion is long forcedAfterBaseline && forcedAfterBaseline > baseline.RemoteVersion)
                {
                    return new SyncResult
                    {
                        Outcome = SyncOutcome.Aborted,
                        RemoteVersion = entry.Version,
                        Message = $"前回の同期 (版 {baseline.RemoteVersion}) の後に別の PC が強制 Push (版 {forcedAfterBaseline}) で同期先を上書きしています。" +
                                  "前回そろえた内容が同期先に残っているか分からないため、自動マージを中止しました。" +
                                  "同期先の内容を正とするなら Pull を、手元の内容を正とするなら非常用の強制 Push をしてください",
                    };
                }
            }
            var report = SqliteMerger.Merge(hostPath, localPath, Profile, baseline, options.DryRun);
            if (hostBaseline is not null && report.Succeeded)
            {
                DatabaseSummary.TrimRenumbered(hostBaseline, hostPath, Profile, report);
            }
            report.HostBaseline = hostBaseline;
            Logger.LogInformation("{Tool} マージ: {Report}", DisplayName, report.Describe());
            if (!report.Succeeded)
            {
                return new SyncResult
                {
                    Outcome = SyncOutcome.Aborted,
                    RemoteVersion = entry.Version,
                    Message = report.Describe(),
                    MergeReport = report,
                };
            }
            if (options.DryRun)
            {
                return new SyncResult
                {
                    Outcome = SyncOutcome.Success,
                    RemoteVersion = entry.Version,
                    Message = "(dry-run) " + report.Describe(),
                    MergeReport = report,
                };
            }

            // (4) ここから先はローカルを書き換える。Pull と同じく WAL/SHM ごと退避してから消す。
            var backupPath = options.SkipBackup ? null : CreateBackup();
            // 置き換えた後に止まっても祖先を復旧できるよう、置き換える前後の本体のハッシュを
            // 呼び出し側に渡す。読むのに時間がかかるので、書き換えの確認より前に取る。
            // WAL/SHM を退ける名前も記録に残す。退けた後、本体を置き換える前に止まると、
            // 本体は元のままで WAL だけが無い。次回に戻せるよう、名前を先に決めておく。
            var asideSuffix = VrcxSyncService.AsideSuffix();
            var pending = options.BeforeReplaceLocal is null
                ? null
                : new MergePendingState(report.HostBaseline, entry.Version,
                    FileHasher.Sha256(DatabasePath), FileHasher.Sha256(hostPath), asideSuffix);
            // 記録の保存には時間がかかりうるので、書き換えの確認より前に済ませる。
            if (pending is not null) options.BeforeReplaceLocal!(pending);
            // マージには時間がかかることがあり、その間にツールが起動されうる (起動時の自動
            // Pull が別プロセスのマージを待ちきれずにツールを起動した場合など)。動いている
            // DB を置き換えないよう、置き換えの直前にもう一度確かめる。
            ProcessGuard.EnsureNotRunning(ProcessNames);
            if (DatabaseFingerprint.Of(DatabasePath) != localFingerprint)
            {
                // 写しを取った後にツールが起動して DB を書き換え、もう終了している。
                return new SyncResult
                {
                    Outcome = SyncOutcome.Aborted,
                    RemoteVersion = entry.Version,
                    BackupPath = backupPath,
                    Message = $"マージの間に {DisplayName} の DB が書き換えられたため、置き換えずに中止しました。もう一度実行してください",
                    MergeReport = report,
                };
            }
            VrcxSyncService.ReplaceDatabase(hostPath, DatabasePath, Logger, asideSuffix);
            options.AfterReplaceLocal?.Invoke(entry.Version, report.HostBaseline);

            return new SyncResult
            {
                Outcome = SyncOutcome.Success,
                RemoteVersion = entry.Version,
                BackupPath = backupPath,
                Message = report.Describe(),
                MergeReport = report,
                AffectedFiles = new[] { SnapshotKey },
            };
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// 同期先の項目に強制 Push の版が無い (この項目を知らない古い版のツールが書いた記録を、
    /// 通常の Push がそのまま引き継いでいる) ので、前回の同期の後に強制 Push があったかを
    /// 確かめられないときの中止。通常の Push ではこの項目は埋まらないので、ツールを更新しても
    /// 解消しない。内容を正とする PC から一度、非常用の強制 Push をすると項目が埋まる。
    /// </summary>
    private static SyncResult UnknownForcedVersion(long remoteVersion, long lastSyncedVersion) => new()
    {
        Outcome = SyncOutcome.Aborted,
        RemoteVersion = remoteVersion,
        Message = $"同期先の版 {remoteVersion} の記録に強制 Push の版が無く (古い版のツールが書いた記録を引き継いでいます)、" +
                  $"前回の同期 (版 {lastSyncedVersion}) の後に強制 Push があったかを確かめられません。" +
                  "前回そろえた内容を二重に数えたり失ったりしないよう、自動マージを中止しました。" +
                  "この状態は通常の Push では解消しません。同期先の内容を正とする PC から一度、非常用の強制 Push をしてください " +
                  "(以後の同期は自動マージできます)。手元の内容を正とするなら、この PC から非常用の強制 Push をしてください",
    };
}
