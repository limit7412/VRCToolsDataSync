using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCToolsDataSync.Core.Domain;
using VRCToolsDataSync.Core.Infra;
using VRCToolsDataSync.Core.Merge;

namespace VRCToolsDataSync.Core.UseCase;

public sealed class SyncRunner
{
    // 同一プロセス内の Push を直列化する。manifest は read-modify-write なので、
    // 自動 Push・手動 Push・終了時 Push が重なると互いの更新を潰しうる。
    // 特に終了時 Push は期限切れで切り離されることがあり、その間に
    // AutoSyncCoordinator が再開して自動 Push を始める経路がある。
    // (PC をまたぐ競合は manifest の条件付き更新と version 検査で扱う)
    private static readonly object PushLock = new();

    private readonly SettingsStore _store;
    private readonly ILoggerFactory _loggerFactory;
    private readonly BaseSummaryStore _baseSummaries;
    private readonly ILogger<SyncRunner> _logger;

    public SyncRunner(
        SettingsStore? store = null,
        ILoggerFactory? loggerFactory = null,
        BaseSummaryStore? baseSummaries = null)
    {
        _store = store ?? new SettingsStore();
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _baseSummaries = baseSummaries ?? BaseSummaryStore.NextTo(_store.FilePath);
        _logger = _loggerFactory.CreateLogger<SyncRunner>();
    }

    public SyncSettings LoadSettings() => _store.Load();
    public void SaveSettings(SyncSettings settings) => _store.Save(settings);

    /// <summary>
    /// 通知済みの版の記録だけを書く (issue #45)。他の設定には触れない。
    /// </summary>
    public void SaveNotifiedVersion(string tag) => _store.SaveNotifiedVersion(tag);

    /// <summary>
    /// 設定から同期先を組み立てる。設定が足りない場合は
    /// <see cref="SyncStorageConfigurationException"/> を投げる。
    /// </summary>
    public ISyncStorage CreateStorage(SyncSettings settings, string? localFolderOverride = null)
        => SyncStorageFactory.Create(settings, localFolderOverride, _loggerFactory);

    /// <param name="reuploadAll">
    /// 非常用の強制 Push (<see cref="PushOptions.ReuploadAll"/>)。true なら
    /// <paramref name="force"/> の値によらずコンフリクトを無視する。
    /// </param>
    public SyncResult Push(
        ISyncService service,
        SyncSettings settings,
        ISyncStorage storage,
        bool force,
        bool reuploadAll = false)
    {
        // 競合からのマージと送り直しが終わるまで、他の Push を待たせる。マージは
        // ローカルの DB と同期履歴を書き換えるので、並んで走ると互いの途中の結果を
        // 土台にしてしまう。lock は同じスレッドからなら入り直せる。
        lock (PushLock)
        {
            // Push の間に別プロセスがローカルを置き換えると、送った内容と記録する要約が
            // 食い違う。マージや Pull と同じ錠前で、スナップショットから要約の保存までを守る。
            using var localLock = AcquireLocalLock(service.ToolKey);
            if (!localLock.IsHeld) return LocalLockBusy();
            var result = PushOnce(service, settings, storage, force, reuploadAll);

            // リモートの方が新しくて止まった Push は、マージが有効ならマージしてから送り直す。
            // 強制 Push は利用者が上書きを選んだ操作なので、マージに回さない。
            if (result.Outcome == SyncOutcome.ConflictDetected
                && !force
                && !reuploadAll
                && settings.IsMergeEnabled(service.ToolKey)
                && service is IMergeableSyncService mergeable)
            {
                _logger.LogInformation("Push が競合したのでマージします ({Tool})", service.ToolKey);
                var (merged, localReplaced) = MergeCore(mergeable, settings, storage, skipBackup: false, dryRun: false);
                if (!localReplaced)
                {
                    // マージできなかった (列の食い違いなど)。ローカルは元のままなので、
                    // 利用者が Pull か強制 Push を選べるよう、元の競合として返す。
                    return new SyncResult
                    {
                        Outcome = SyncOutcome.ConflictDetected,
                        RemoteVersion = result.RemoteVersion,
                        AffectedFiles = result.AffectedFiles,
                        Message = $"{result.Message} (自動マージはできませんでした: {merged.Message})",
                    };
                }
                return merged;
            }
            return result;
        }
    }

    private SyncResult PushOnce(
        ISyncService service,
        SyncSettings settings,
        ISyncStorage storage,
        bool force,
        bool reuploadAll)
    {
        lock (PushLock)
        {
            var state = ResolveState(settings, storage, service.ToolKey, out var stateKey) ?? new ToolSyncState();
            // 前回のマージが置き換えの前後で止まっていれば、送る前にそろえる。置き換える前に
            // 退けた WAL が残っていれば戻してから送る。戻さずに送ると、WAL にしか無い手元の
            // 変更を含まない本体が送られ、送った後に記録が消えて戻せなくなる。戻せなければ
            // 送らない。置き換えの前後を見分けられない状態からは、強制 Push で抜け出せる。
            var recovered = ResolveInterruptedReplace(service, settings, stateKey, state, dryRun: false, force: force || reuploadAll, caller: RecoveryCaller.Push);
            if (recovered.Abort is not null) return recovered.Abort;
            ConfirmPushedRecord(service, storage, stateKey);
            // 前回の Push が反映された後に要約を保存できていないと、同期履歴は送る前の版のまま。
            // 反映済みの記録の版は自分が送った写しなので、その版までは取り込んだものとして扱う。
            // そうしないと、要約の保存に一度失敗しただけで、以後の Push が同期先の自分の版と
            // 競合し続ける。
            var pulledVersion = state.LastPulledVersion;
            if (_baseSummaries.LoadPushed(stateKey) is { Committed: true, Version: long syncedVersion } && syncedVersion > pulledVersion)
            {
                pulledVersion = syncedVersion;
            }
            // この Push を見分ける値。manifest の項目と送信の記録の両方に残し、反映されたか
            // 分からないまま止まった後に、同期先の項目がこの Push のものかを確かめる。
            var pushId = Guid.NewGuid().ToString("N");
            var result = service.Push(new PushOptions
            {
                Storage = storage,
                MachineName = settings.MachineName,
                ForceOverwriteOnConflict = force || reuploadAll,
                ReuploadAll = reuploadAll,
                LastPulledVersion = pulledVersion == 0 ? null : pulledVersion,
                PushId = pushId,
                // 控えられなければ送らない (サービス側が中止する)。送った後に要約の保存にも
                // 失敗すると、次のマージが送った内容を見分けられない。
                BeforeCommit = (sha256, summary, planned) =>
                    _baseSummaries.BeginPushed(stateKey, new PushedRecord(sha256, summary, PlannedVersion: planned, PushId: pushId)),
                AfterCommit = (sha256, version) => TryMarkPushedCommitted(stateKey, sha256, version),
                // 反映しなかった送信の記録は捨てる。残すと、同期先が進んだときに反映されたか
                // 分からない記録として扱われ、マージが止まる。
                NotCommitted = () => _baseSummaries.DiscardUncommittedPushed(stateKey),
            });

            if (result.Outcome == SyncOutcome.Success && result.RemoteVersion.HasValue)
            {
                // Push が済み、ローカルと同期先がそろった。中断したマージの印は要らない。
                TryClearMergePending(stateKey);
                // 要約を先に保存する。同期履歴だけが進んで要約が無いと、次の起動時 Pull が
                // 手元の変更を確かめられずに上書きする。要約が先なら、同期履歴の保存前に
                // 止まっても、要約の版が同期履歴より新しいことから中断と分かる。
                if (!RecordBaseSummary(service, stateKey, result.RemoteVersion.Value, result.SyncedSummary))
                {
                    return WithMessage(result, "送信は済みましたが、同期時点の要約を保存できなかったため、同期履歴は進めていません。次の同期でそろいます");
                }
                state.LastPushedVersion = result.RemoteVersion.Value;
                state.LastPushedAt = DateTimeOffset.Now;
                state.LastPulledVersion = result.RemoteVersion.Value;
                settings.ToolState[stateKey] = state;
                // Push 経由の Save は ToolState の更新だけが目的。Top-level 設定は
                // disk 値を優先しないと、古いインスタンスを持った別経路 (CLI / 別
                // SyncRunner) からの Push が、ユーザが GUI で変更した AutoSyncEnabled
                // 等を巻き戻してしまう。
                _store.SaveToolStateOnly(settings);
            }
            return result;
        }
    }

    public SyncResult Pull(
        ISyncService service,
        SyncSettings settings,
        ISyncStorage storage,
        bool skipBackup,
        bool skipIfNotNewer = false)
    {
        // Issue #19: skipIfNotNewer=true の経路 (StartupSyncOrchestrator) では、
        // ローカルが既に最新と分かっているリモート (LastPulledVersion>=Version) の
        // Pull を抑止する。手動 Pull / コンフリクト解消 Pull は呼び出し側でデフォルトの
        // false を使い、従来通り上書き Pull を行う。
        SyncResult result;
        lock (PushLock)
        {
            // Pull もローカルの DB を置き換えるので、別プロセスのマージと重ならないようにする。
            using var localLock = AcquireLocalLock(service.ToolKey);
            if (!localLock.IsHeld) return LocalLockBusy();
            result = PullLocked(service, settings, storage, skipBackup, skipIfNotNewer);
        }

        // 起動時の自動 Pull が手元の変更を守って見送った場合、マージが有効なら
        // 上書きの代わりにマージする。
        if (result.Outcome == SyncOutcome.LocalChangesPending
            && settings.IsMergeEnabled(service.ToolKey)
            && service is IMergeableSyncService mergeable)
        {
            _logger.LogInformation("手元に未 Push の変更があるのでマージします ({Tool})", service.ToolKey);
            return Merge(mergeable, settings, storage, skipBackup);
        }
        return result;
    }

    private SyncResult PullLocked(
        ISyncService service,
        SyncSettings settings,
        ISyncStorage storage,
        bool skipBackup,
        bool skipIfNotNewer)
    {
        var state = ResolveState(settings, storage, service.ToolKey, out var stateKey);
        // 置き換えの記録を残した後は真。サービスは記録を残してから、差し替える直前にツールの
        // 起動や書き換えを確かめる。そこで中止したらローカルは元のままなので、記録を消す。
        // 残すと、次回に本体のハッシュがどちらの記録とも合わず、置き換えの前後を見分けられない
        // としてマージが止まる。
        var replaceRecorded = false;
        if (skipIfNotNewer)
        {
            // 前回のマージか Pull が置き換えの前後で止まっていれば、先にそろえる。Pull が本体を
            // 差し替えた後に止まっていた場合は、祖先だけが本体の版になり、この Pull が残りを
            // そろえる。見分けられなければ、手元の変更ありとして見送るのではなく、理由を添えて止める。
            // 初めての Pull が止まった場合は同期履歴がまだ無いので、空の同期履歴で解決する。
            if (_baseSummaries.LoadPendingReplace(stateKey) is not null)
            {
                var recovered = ResolveInterruptedReplace(service, settings, stateKey, state ?? new ToolSyncState(), dryRun: false, caller: RecoveryCaller.Pull);
                if (recovered.Abort is not null) return recovered.Abort;
                state ??= settings.ToolState.GetValueOrDefault(stateKey);
            }
            // 起動時の自動 Pull は、送った写しの記録と比べて手元の変更を見つける。反映の直後に
            // 止まって反映済みと記録できていなければ、同期先と照らして先に確かめる。
            ConfirmPushedRecord(service, storage, stateKey);
        }
        SyncResult result;
        try
        {
            result = service.Pull(new PullOptions
            {
                Storage = storage,
                SkipBackup = skipBackup,
                SkipIfNotNewer = skipIfNotNewer,
                LastPulledVersion = state is null || state.LastPulledVersion == 0
                    ? null
                    : state.LastPulledVersion,
                FindUnsyncedLocalChanges = skipIfNotNewer
                    ? () => FindUnsyncedLocalChanges(service, stateKey, state?.LastPulledVersion ?? 0)
                    : null,
                // 本体を差し替えた直後に止まっても、次回に差し替わったかを見分けて祖先をそろえられるよう、
                // マージと同じ記録を残す。要約を保存できれば RecordBaseSummary が消す。
                BeforeReplaceLocal = pending =>
                {
                    _baseSummaries.SavePendingReplace(stateKey, pending);
                    replaceRecorded = true;
                },
                // 本体を差し替えた後に残りの差し替えで止まったら、祖先の要約だけを本体の版へそろえる。
                // 同期履歴は進めない。進めると、次の起動時 Pull が取り込み済みと見て残りの差し替えを
                // やり直さず、古い設定が残ったまま次の Push で同期先の新しい設定を上書きする。
                AfterDatabaseReplaced = (version, summary, sha256) =>
                    CommitPulled(service, settings, stateKey, state, version, summary, sha256, advanceHistory: false),
            });
        }
        catch (RunningProcessException) when (replaceRecorded)
        {
            // 記録を残した後、差し替える直前の確かめでツールが起動していた。差し替えていない。
            TryClearMergePending(stateKey);
            throw;
        }
        if (replaceRecorded && result.Outcome == SyncOutcome.Aborted)
        {
            // 記録を残した後、差し替える直前の確かめで書き換えが見つかった。差し替えていない。
            TryClearMergePending(stateKey);
        }
        if (result.Outcome == SyncOutcome.Success && result.RemoteVersion.HasValue
            && !CommitPulled(service, settings, stateKey, state, result.RemoteVersion.Value, result.SyncedSummary, result.SyncedSha256))
        {
            return WithMessage(result, "取り込みは済みましたが、同期時点の要約を保存できなかったため、同期履歴は進めていません。次の同期でそろいます");
        }
        return result;
    }

    /// <summary>
    /// 取り出した版へ要約と同期履歴をそろえる。Push と同じく、要約を先に保存する。
    /// 要約を保存できなければ同期履歴は進めず、取り出した写しの記録を残して偽を返す。
    /// <para>
    /// 本体を差し替える前に残した置き換えの記録は、要約を保存し終えるまで消さない。先に消すと、
    /// 消してから保存するまでの間に止まったとき、本体は取り出した版なのに復旧の手掛かりが何も
    /// 残らない。要約を保存できず写しの記録も残せなければ、置き換えの記録だけが手掛かりなので残す。
    /// </para>
    /// <para>
    /// <paramref name="advanceHistory"/> が偽なら要約だけを保存し、同期履歴は進めない。本体だけを
    /// 差し替えて残りで止まったときに使う。要約の版が同期履歴より新しい状態になり、起動時 Pull は
    /// その要約と比べて手元の変更を見つけ、無ければ残りの差し替えをやり直す。
    /// </para>
    /// </summary>
    private bool CommitPulled(
        ISyncService service, SyncSettings settings, string stateKey, ToolSyncState? state,
        long version, DatabaseSummary? summary, string? sha256, bool advanceHistory = true)
    {
        if (!RecordBaseSummary(service, stateKey, version, summary))
        {
            if (KeepPulledSnapshotRecord(stateKey, version, summary, sha256)) TryClearMergePending(stateKey);
            return false;
        }
        TryClearMergePending(stateKey);
        if (!advanceHistory) return true;
        state ??= new ToolSyncState();
        state.LastPulledVersion = version;
        state.LastPulledAt = DateTimeOffset.Now;
        settings.ToolState[stateKey] = state;
        // Push と同じ理由で SaveToolStateOnly を使う。
        _store.SaveToolStateOnly(settings);
        return true;
    }

    /// <summary>Push を競合で取り直す回数の上限。</summary>
    private const int MaxMergeAttempts = 3;

    /// <summary>別プロセスのマージや Pull が終わるのを待つ上限。テストで短くする。</summary>
    internal static TimeSpan MergeLockTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// ローカルの DB を置き換える操作 (マージと Pull) を、プロセスをまたいで直列にする錠前を取る。
    /// PushLock はこのプロセスの中でしか効かないので、常駐の GUI と CLI が同じ DB を同時に
    /// 置き換えないよう、要約の置き場所にツールごとの錠前ファイルを置く。
    /// </summary>
    /// <remarks>
    /// Push からマージへ、マージから Push へと入れ子で取るので、このプロセスの中では
    /// 入り直せるようにする。呼び出しは必ず PushLock の中なので、表は PushLock で守られる。
    /// </remarks>
    private LocalLock AcquireLocalLock(string toolKey)
    {
        var path = Path.Combine(_baseSummaries.RootDirectory, $"merge-{toolKey}.lock");
        if (HeldLocalLocks.TryGetValue(path, out var held))
        {
            HeldLocalLocks[path] = (held.Lock, held.Count + 1);
            return new LocalLock(path, isHeld: true);
        }
        Directory.CreateDirectory(_baseSummaries.RootDirectory);
        var acquired = CrossSessionFileLock.Acquire(path, MergeLockTimeout);
        if (!acquired.IsHeld) return new LocalLock(path, isHeld: false);
        HeldLocalLocks[path] = (acquired, 1);
        return new LocalLock(path, isHeld: true);
    }

    private static readonly Dictionary<string, (CrossSessionFileLock Lock, int Count)> HeldLocalLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class LocalLock(string path, bool isHeld) : IDisposable
    {
        public bool IsHeld { get; } = isHeld;

        public void Dispose()
        {
            if (!IsHeld || !HeldLocalLocks.TryGetValue(path, out var held)) return;
            if (held.Count > 1)
            {
                HeldLocalLocks[path] = (held.Lock, held.Count - 1);
                return;
            }
            HeldLocalLocks.Remove(path);
            held.Lock.Dispose();
        }
    }

    private static SyncResult LocalLockBusy() => new()
    {
        Outcome = SyncOutcome.Aborted,
        Message = "別のプロセスがマージか Pull をしているため、見送りました",
    };

    /// <summary>
    /// 同期先の最新に手元の変更を足してローカルに置き、続けて Push する。
    /// <para>
    /// マージの直後に必ず Push する。マージ結果を同期先に載せないと、他の PC が
    /// 同じ食い違いに出会い、同じマージを繰り返すことになる。Push が他の PC の Push に
    /// 負けた場合は、新しい同期先を土台にマージからやり直す。
    /// </para>
    /// <para>
    /// ローカルを置き換える直前に、<see cref="ToolSyncState.LastPulledVersion"/> を土台に
    /// した版まで進め、祖先の要約もその版の要約に置き換える。ローカルはその版に手元の
    /// 変更を足したものになるので、この組が正しい基準になる。置き換えに失敗したら
    /// 両方を元に戻す。Push が失敗しても (例外を含む)、次の Push はそのまま手元の変更を
    /// 送れる。他の PC が先に Push していれば、やり直しや次の起動時の Pull は、この版を
    /// 祖先にして手元の変更だけを足し直す。
    /// </para>
    /// </summary>
    /// <param name="dryRun">true なら結果を数えるだけで、ローカルにも同期先にも書かない。</param>
    public SyncResult Merge(
        IMergeableSyncService service,
        SyncSettings settings,
        ISyncStorage storage,
        bool skipBackup = false,
        bool dryRun = false)
        => MergeCore(service, settings, storage, skipBackup, dryRun).Result;

    /// <returns>結果と、ローカルをマージ結果で置き換えたか。</returns>
    private (SyncResult Result, bool LocalReplaced) MergeCore(
        IMergeableSyncService service,
        SyncSettings settings,
        ISyncStorage storage,
        bool skipBackup,
        bool dryRun)
    {
        lock (PushLock)
        {
            // 取れなければ、相手の途中の DB を土台にしないようマージを見送る。
            using var localLock = AcquireLocalLock(service.ToolKey);
            if (!localLock.IsHeld) return (LocalLockBusy(), false);

            SyncResult? last = null;
            string? firstBackupPath = null;
            for (var attempt = 1; attempt <= MaxMergeAttempts; attempt++)
            {
                var state = ResolveState(settings, storage, service.ToolKey, out var stateKey) ?? new ToolSyncState();
                // 前回のマージが置き換えの前後で止まっていれば、先に祖先をそろえる。
                var recovered = ResolveInterruptedReplace(service, settings, stateKey, state, dryRun, caller: RecoveryCaller.Merge);
                if (recovered.Abort is not null) return (WithBackupPath(recovered.Abort, firstBackupPath), attempt > 1);
                var previousBaseline = recovered.Baseline ?? _baseSummaries.Load(stateKey);
                var previousPulledVersion = recovered.PulledVersion ?? state.LastPulledVersion;
                var previousPulledAt = state.LastPulledAt;
                // 同期履歴より古い要約は祖先に使えない。祖先なしの 2-way にする。新しい要約は、
                // 前回のマージで置き換えた後、同期履歴を保存する前に止まったときのもので、
                // 今のローカルの祖先として正しい。
                var baseline = previousBaseline is not null && previousBaseline.RemoteVersion >= previousPulledVersion
                    ? previousBaseline
                    : null;

                var committed = false;
                // 置き換えから要約と同期履歴の保存までの間に止まっても分かるよう、先に印を付ける。
                // 前の中断の印が残っていれば、それは消さない。
                var wasPending = _baseSummaries.IsMergePending(stateKey);
                if (!dryRun) _baseSummaries.MarkMergePending(stateKey);
                // 置き換えの記録を書いた後は、置き換えが始まっている。
                var replaceStarted = false;
                SyncResult merged;
                try
                {
                    merged = service.PullMerge(new PullMergeOptions
                    {
                        Storage = storage,
                        Baseline = baseline,
                        OwnPush = _baseSummaries.LoadPushed(stateKey),
                        // dry-run は手元に何も書かない。記録も消さない。捨てる記録が前の反映済みの
                        // 記録を抱えていれば、それが戻る。
                        DiscardOwnPush = dryRun ? null : () => _baseSummaries.DiscardUncommittedPushed(stateKey),
                        DryRun = dryRun,
                        // 2 周目以降のローカルは前の周のマージ結果なので、退避するのは最初だけ。
                        SkipBackup = skipBackup || attempt > 1,
                        BeforeReplaceLocal = pending =>
                        {
                            replaceStarted = true;
                            _baseSummaries.SavePendingReplace(stateKey, pending);
                        },
                        AfterReplaceLocal = (remoteVersion, hostBaseline) =>
                        {
                            // 置き換えた後のローカルは、土台にした版に手元の変更を足したもの
                            // になる。祖先の要約と同期履歴をその版へそろえる。
                            committed = true;
                            if (hostBaseline is null) _baseSummaries.Delete(stateKey);
                            else _baseSummaries.Save(stateKey, hostBaseline);
                            state.LastPulledVersion = remoteVersion;
                            state.LastPulledAt = DateTimeOffset.Now;
                            settings.ToolState[stateKey] = state;
                            _store.SaveToolStateOnly(settings);
                            // ここまで保存できれば、印が残っても手元の変更があるものとして
                            // 扱われるだけなので、消せなくても失敗にしない。
                            TryClearMergePending(stateKey);
                        },
                    });
                }
                catch
                {
                    // 置き換えを始める前に失敗したなら、ローカルは元のままなので印は要らない。
                    // 置き換えを始めた後なら、本体の差し替えに失敗して退けた WAL を戻せなかった
                    // こともあるので、記録を残す。次回に本体のハッシュで置き換えの前後を見分け、
                    // 退けた WAL を戻すか、祖先と同期履歴をそろえる。
                    if (!committed && !replaceStarted && !wasPending && !dryRun) TryClearMergePending(stateKey);
                    // そろえている途中で失敗した。要約と同期履歴が食い違わないよう元へ戻す。
                    // ローカルはマージ結果のままだが、手元に未 Push の変更があるものとして
                    // 扱われるので、次の同期で同期先の変更を上書きすることはない。
                    if (committed)
                    {
                        RestoreBaseline(stateKey, previousBaseline);
                        state.LastPulledVersion = previousPulledVersion;
                        state.LastPulledAt = previousPulledAt;
                        settings.ToolState[stateKey] = state;
                        _store.SaveToolStateOnly(settings);
                    }
                    throw;
                }
                if (!committed && !wasPending && !dryRun) TryClearMergePending(stateKey);
                if (dryRun || merged.Outcome != SyncOutcome.Success || !merged.RemoteVersion.HasValue)
                {
                    // 2 周目以降はバックアップを取らないが、ローカルは 1 周目で置き換えて
                    // いる。操作前の状態を戻せるよう、最初のバックアップを結果に残す。
                    return (WithBackupPath(merged, firstBackupPath), attempt > 1);
                }
                firstBackupPath ??= merged.BackupPath;

                var pushed = PushOnce(service, settings, storage, force: false, reuploadAll: false);
                if (pushed.Outcome != SyncOutcome.ConflictDetected)
                {
                    return (new SyncResult
                    {
                        Outcome = pushed.Outcome,
                        RemoteVersion = pushed.RemoteVersion,
                        BackupPath = firstBackupPath,
                        MergeReport = merged.MergeReport,
                        AffectedFiles = pushed.AffectedFiles,
                        Message = pushed.Outcome == SyncOutcome.Success
                            ? merged.Message
                            : $"{merged.Message}。ただし Push に失敗しました: {pushed.Message}",
                    }, true);
                }
                _logger.LogInformation("マージ後の Push が競合しました。やり直します ({Attempt}/{Max})", attempt, MaxMergeAttempts);
                last = pushed;
            }
            // 送り直しが上限まで競合した。ローカルは既にマージ結果で置き換えているので、
            // 操作前の状態を戻せるよう、最初のバックアップを結果に残す。
            return (WithBackupPath(last!, firstBackupPath), true);
        }
    }

    /// <summary>
    /// 前回のマージがローカルの置き換えの前後で止まっていれば、祖先と同期履歴を今の
    /// ローカルに合わせる。置き換えが済んだかは、今の本体の SHA-256 が記録のどちらと
    /// 同じかで見分ける。どちらとも違えば、その後に DB が書き換えられていて判断できない
    /// ので、中止の結果を返す。
    /// </summary>
    /// <returns>
    /// 中止の結果か、dry-run で保存せずに使う祖先と同期履歴の版。記録が無いか、保存まで
    /// 済ませたなら、すべて null。
    /// </returns>
    /// <param name="force">
    /// 強制 Push から呼ぶなら真。置き換えの前後を見分けられない状態は、利用者が手元の内容を
    /// 正として送ると選んだのだから中止しない。退けた WAL を戻せない状態は、WAL にしか無い
    /// 変更を失うので強制 Push でも中止する。
    /// </param>
    /// <param name="caller">
    /// どの操作から呼ぶか。Pull が本体を差し替えた後、残りの差し替えの前に止まっていた場合の扱いが
    /// 違う。Pull はそのまま残りをそろえる。マージは設定などに触れず、続く Push が手元の設定を
    /// 送るので中止する。Push も、前の版の設定などで同期先を上書きしないよう中止する (強制 Push は
    /// 利用者が手元を正として送ると選んだので送る)。
    /// </param>
    private (SyncResult? Abort, DatabaseSummary? Baseline, long? PulledVersion) ResolveInterruptedReplace(
        ISyncService service, SyncSettings settings, string stateKey, ToolSyncState state, bool dryRun, bool force = false, RecoveryCaller caller = RecoveryCaller.Push)
    {
        var pending = _baseSummaries.LoadPendingReplace(stateKey);
        if (pending is null)
        {
            if (!dryRun && _baseSummaries.IsMergePending(stateKey))
            {
                // 時刻だけの印は、置き換えの直前の記録を書く前に止まった印。置き換えは
                // 始まっていないので、ローカルは元のまま。残すと、置き換える前に中止する
                // マージが続く限り消えず、起動時 Pull が手元の変更ありと見続ける。
                _logger.LogInformation("前回のマージは置き換えの記録を書く前に止まっていました ({Tool})", service.ToolKey);
                TryClearMergePending(stateKey);
            }
            return (null, null, null);
        }
        if (service.TrackedDatabase is not { } tracked || !File.Exists(tracked.Path)) return (null, null, null);
        var saved = _baseSummaries.Load(stateKey);
        var aside = pending.AsideSuffix is null ? Array.Empty<string>() : VrcxSyncService.FindAsideFiles(tracked.Path, pending.AsideSuffix);
        var current = FileHasher.Sha256(tracked.Path);
        // 置き換えた後と見る判定を先にする。マージ結果が元の本体と同じ内容になる (同期先の本体が
        // 手元と同じで、手元の WAL にマージの対象外の変更しか無い) と、2 つの記録は同じ値になる。
        // そのとき退けた WAL を戻すと、置き換えが済んでいた場合に、マージで同期先の値に
        // 揃えたはずの対象外のテーブルへ手元の変更が戻り、次の Push で送り直される。
        // 置き換えが済んでいなくても本体はマージ結果と同じなので、済んだものとして進めてよい。
        if (string.Equals(current, pending.MergedSha256, StringComparison.OrdinalIgnoreCase))
        {
            if (pending.FromPull)
            {
                // Pull が本体を差し替えた後、残りの差し替えの前に止まった。本体は取り出した版なので
                // 要約だけをその版へそろえ、同期履歴は進めない。進めると、次の起動時 Pull が
                // 取り込み済みと見て残りをやり直さず、前の版の設定などを次の Push で同期先へ送る。
                // 記録は残りをそろえる Pull が済むまで消さない。消すと、競合した Push から続く
                // 自動マージが止まらず、マージした DB と前の版の設定を送る。起動時 Pull は記録の
                // 要約と比べて手元の変更を見つけ、無ければ残りをやり直す。
                _logger.LogInformation("前回の Pull は本体を差し替えた後に止まっていました。祖先を本体の版にそろえ、残りは次の Pull で差し替えます ({Tool})", service.ToolKey);
                if (pending.Summary is not null) pending.Summary.RemoteVersion = pending.RemoteVersion;
                if (!dryRun)
                {
                    if (pending.Summary is null) _baseSummaries.Delete(stateKey);
                    else _baseSummaries.Save(stateKey, pending.Summary);
                }
                if (caller == RecoveryCaller.Pull) return (null, null, null);
                if (force)
                {
                    // 利用者が手元の内容を正として送ると選んだ。送った後に記録は消える。
                    _logger.LogWarning("前回の Pull が本体を差し替えた後に止まっていますが、強制 Push なので手元の内容をそのまま送ります ({Tool})", service.ToolKey);
                    return (null, null, null);
                }
                return (new SyncResult
                {
                    Outcome = SyncOutcome.Aborted,
                    Message = $"前回の Pull (版 {pending.RemoteVersion}) が DB の本体を差し替えた後、設定などの残りを差し替える前に止まっています。" +
                              (caller == RecoveryCaller.Merge
                                  ? "マージは設定に触れず、続く Push が手元の設定を送るので、同期先の設定を前の版のもので上書きしないよう中止しました。"
                                  : "そのまま送ると前の版の設定などで同期先を上書きするので、Push を中止しました。") +
                              "先に Pull (起動時の自動 Pull でも可) で残りをそろえてください。手元の内容を正として送るなら、非常用の強制 Push をしてください",
                }, null, null);
            }
            // 置き換えた後、要約と同期履歴を保存する前に止まった。その保存をここで終える。
            _logger.LogInformation("前回のマージは置き換えた後に止まっていました。祖先と同期履歴をそろえます ({Tool})", service.ToolKey);
            if (pending.Summary is not null) pending.Summary.RemoteVersion = pending.RemoteVersion;
            if (dryRun) return (null, pending.Summary, pending.RemoteVersion);
            if (pending.Summary is null) _baseSummaries.Delete(stateKey);
            else _baseSummaries.Save(stateKey, pending.Summary);
            state.LastPulledVersion = pending.RemoteVersion;
            state.LastPulledAt = DateTimeOffset.Now;
            settings.ToolState[stateKey] = state;
            _store.SaveToolStateOnly(settings);
            if (pending.AsideSuffix is not null) VrcxSyncService.DeleteAsideFiles(tracked.Path, pending.AsideSuffix);
            TryClearMergePending(stateKey);
            return (null, null, null);
        }
        if (string.Equals(current, pending.LocalSha256, StringComparison.OrdinalIgnoreCase))
        {
            // 置き換える前に止まった。ローカルは元のままなので、祖先も同期履歴もそのままでよい。
            // ただし WAL/SHM を脇へ退けた後なら、本体に書き戻されていない変更がそこに残って
            // いるので、戻してから進む。戻せなければ、その変更を見失わないよう印を残して止める。
            // 既に保存まで済んでいた場合も、本体が変わっていなければ同じ扱いでよい。
            _logger.LogInformation("前回のマージは置き換える前に止まっていました ({Tool})", service.ToolKey);
            if (dryRun)
            {
                // dry-run は手元に書き込まないので、退けた WAL を戻せない。戻さずに進むと、
                // WAL にしか無い手元の変更を含まない本体から写しを取り、変更の件数を少なく報告する。
                if (aside.Length == 0) return (null, null, null);
                return (new SyncResult
                {
                    Outcome = SyncOutcome.Aborted,
                    Message = "前回のマージが退けた WAL (" + string.Join(", ", aside) + ") が残っています。" +
                              "dry-run は手元に書き込まないので戻せず、戻さずに比べると手元の変更を少なく数えます。" +
                              "dry-run を付けずにマージするか、Push か Pull をして戻してから、もう一度お試しください",
                }, null, null);
            }
            if (pending.AsideSuffix is not null && !VrcxSyncService.RestoreAsideFiles(tracked.Path, pending.AsideSuffix, _logger))
            {
                return (new SyncResult
                {
                    Outcome = SyncOutcome.Aborted,
                    Message = "前回のマージが退けた WAL を戻せません (" + string.Join(", ", aside) + ")。" +
                              "戻す先に別のファイルがあります。VRCX を終了してからもう一度実行してください。" +
                              "それでも進まなければ、退けたファイルの中身が要らないことを確かめて消してください",
                }, null, null);
            }
            TryClearMergePending(stateKey);
            return (null, null, null);
        }
        if (aside.Length == 0
            && state.LastPulledVersion == pending.RemoteVersion
            && (pending.Summary is null ? saved is null : saved?.RemoteVersion == pending.RemoteVersion))
        {
            // 要約と同期履歴の保存まで済んでいて、印を消すところだけ失敗していた。
            // 本体のハッシュは、その後の VRCX の書き込みで変わっていてもよい。退けた WAL が
            // 残っていれば、同じ版へのマージが置き換える前に止まった可能性もあるので、この
            // 判定はしない。
            _logger.LogInformation("前回のマージの印だけが残っていました ({Tool})", service.ToolKey);
            if (!dryRun) TryClearMergePending(stateKey);
            return (null, null, null);
        }
        if (force)
        {
            // 利用者が手元の内容を正として送ると選んだ。送った後に記録は消える。
            _logger.LogWarning("前回のマージの置き換えの前後を見分けられませんが、強制 Push なので手元の内容をそのまま送ります ({Tool})", service.ToolKey);
            return (null, null, null);
        }
        var message = "前回のマージがローカルの置き換えの前後で止まり、その後に DB が書き換えられています。" +
                      "置き換えが済んだかを見分けられないため、自動マージを中止しました。";
        if (aside.Length > 0)
        {
            message += "退けた WAL (" + string.Join(", ", aside) + ") も残っていますが、本体が変わっているので戻せません。";
        }
        return (new SyncResult
        {
            Outcome = SyncOutcome.Aborted,
            Message = message + "バックアップを確かめたうえで、Pull で同期先に合わせるか、強制 Push で手元を送ってください",
        }, null, null);
    }

    /// <summary>結果のバックアップの場所を、取ってあれば最初のバックアップに差し替える。</summary>
    private static SyncResult WithBackupPath(SyncResult result, string? backupPath)
        => backupPath is null
            ? result
            : new SyncResult
            {
                Outcome = result.Outcome,
                Message = result.Message,
                RemoteVersion = result.RemoteVersion,
                LastPulledVersion = result.LastPulledVersion,
                BackupPath = backupPath,
                AffectedFiles = result.AffectedFiles,
                MergeReport = result.MergeReport,
            };

    /// <summary>
    /// 反映されたか分からないまま残った送信の記録を、同期先の manifest と照らして確かめる。
    /// manifest を保存した直後、反映済みと記録する前に止まると、記録は予定の版を持ったまま
    /// 未反映として残る。同期先が予定の版で、その項目が記録と同じ Push のものなら反映済みに
    /// する。予定の版を別の PC が取っていれば、反映されなかったので捨てる。同期先が予定の版を
    /// 越えていれば見分けられないので、そのまま残す (マージが止めて知らせる)。
    /// <para>
    /// マージは同じ照合を自分で行うが、マージ無しの起動時 Pull と Push は反映済みの記録しか
    /// 見ないので、ここで確かめておかないと、送った DB を手元の変更と見て見送り続け、Push も
    /// 送る前の版の同期履歴で競合し続ける。
    /// </para>
    /// </summary>
    private void ConfirmPushedRecord(ISyncService service, ISyncStorage storage, string stateKey)
    {
        // Push を見分ける値の無い記録は、古い版のツールが書いたもので確かめられない。
        if (_baseSummaries.LoadPushed(stateKey) is not { Committed: false, PlannedVersion: long planned, PushId: { } pushId } record) return;
        try
        {
            if (!new ManifestStore(storage).Load().Tools.TryGetValue(service.ToolKey, out var entry)) return;
            if (entry.Version != planned) return;
            // 同じ版を別の PC が同じ DB で取ると、DB の写しのハッシュでは見分けられず、設定や
            // メモが違う版を自分の Push と見て、次の Push がそれらを上書きする。Push を見分ける
            // 値で照らす。
            var sent = string.Equals(entry.PushId, pushId, StringComparison.Ordinal);
            if (sent)
            {
                _logger.LogInformation("前回の Push (版 {Version}) は同期先に反映されていました ({Tool})", planned, service.ToolKey);
                _baseSummaries.MarkPushedCommitted(stateKey, record.Sha256, planned);
            }
            else
            {
                _logger.LogInformation("前回の Push は反映されていませんでした (版 {Version} は別の PC のもの) ({Tool})", planned, service.ToolKey);
                _baseSummaries.DiscardUncommittedPushed(stateKey);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "送った写しの記録を同期先と照らして確かめられませんでした ({Tool})", service.ToolKey);
        }
    }

    /// <summary><see cref="ResolveInterruptedReplace"/> を呼ぶ操作。</summary>
    private enum RecoveryCaller
    {
        Push,
        Merge,
        Pull,
    }

    private void TryMarkPushedCommitted(string stateKey, string sha256, long version)
    {
        try
        {
            _baseSummaries.MarkPushedCommitted(stateKey, sha256, version);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "送った写しの反映を記録できませんでした");
        }
    }

    private void TryClearMergePending(string stateKey)
    {
        try
        {
            _baseSummaries.ClearMergePending(stateKey);
        }
        catch (Exception ex)
        {
            // 残っても、次の同期で手元の変更があるものとして扱われるだけで済む。
            _logger.LogWarning(ex, "マージの印を消せませんでした");
        }
    }

    private void RestoreBaseline(string stateKey, DatabaseSummary? previous)
    {
        try
        {
            if (previous is null) _baseSummaries.Delete(stateKey);
            else _baseSummaries.Save(stateKey, previous);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "祖先の要約を元に戻せませんでした。次の同期で取り直します");
            try { _baseSummaries.Delete(stateKey); } catch { /* 次の同期で取り直す */ }
        }
    }

    /// <summary>
    /// 同期を終えた時点の DB の要約を、次に比べる基準として残す。
    /// <para>
    /// 失敗しても例外は投げない。要約は同期の結果に影響しない付加情報で、これが
    /// 原因で成功した同期をエラー扱いにするわけにはいかないため。要約を取れなかった
    /// (DB が無い、読めない) ときは古い要約を消す。残すと、今回の同期より前の状態を
    /// 基準に比べることになり、変わっていないのに「手元に変更がある」と判定し続ける。
    /// 保存に失敗したときは古い要約を残し、偽を返す。呼び出し側は同期履歴を進めない
    /// ので、古い要約と古い同期履歴の組で手元の変更を見つけられる。
    /// </para>
    /// </summary>
    /// <param name="synced">
    /// 送った写しか取り出した写しから取った要約。あればそれを保存する。同期した後の
    /// 手元の DB から取ると、その間にツールが書き込んだ未送信の変更が基準に混ざるため。
    /// </param>
    /// <returns>要約を保存したか、要約が要らなくて消したら真。保存に失敗したら偽。</returns>
    private bool RecordBaseSummary(ISyncService service, string stateKey, long remoteVersion, DatabaseSummary? synced)
    {
        if (service.TrackedDatabase is not { } tracked) return true;
        try
        {
            string? reason = "DB がありません";
            var summary = synced
                ?? (File.Exists(tracked.Path) ? DatabaseSummary.Compute(tracked.Path, tracked.Profile, out reason) : null);
            if (summary is null)
            {
                _baseSummaries.Delete(stateKey);
                _logger.LogInformation("同期時点の要約を取れませんでした ({Tool}): {Reason}", service.ToolKey, reason);
                return true;
            }
            summary.RemoteVersion = remoteVersion;
            _baseSummaries.Save(stateKey, summary);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "同期時点の要約の保存に失敗しました ({Tool})", service.ToolKey);
            return false;
        }
    }

    /// <summary>
    /// Pull で差し替えた後に要約を保存できなかったとき、取り出した写しの記録を残す。
    /// <para>
    /// 古い要約と古い同期履歴はそのまま残る。手元は取り出した版なので、次の起動時 Pull は古い
    /// 要約との違いを手元の変更と見てマージに回す。そのマージが古い要約を祖先にすると、取り込んだ
    /// 内容を手元と同期先の両方の変更と見て二重に足す (足し合わせる列なら値が増える)。
    /// 取り出した写しの記録があれば、マージは送った写しと同じ扱いでその要約を祖先にする。
    /// 記録も残せなければ古い要約を消し、祖先なしでマージさせる。
    /// </para>
    /// </summary>
    /// <returns>写しの記録を残せたら真。</returns>
    private bool KeepPulledSnapshotRecord(string stateKey, long version, DatabaseSummary? summary, string? sha256)
    {
        try
        {
            if (summary is not null && !string.IsNullOrEmpty(sha256))
            {
                summary.RemoteVersion = version;
                _baseSummaries.SavePushed(stateKey, new PushedRecord(sha256, summary, version, Committed: true, PlannedVersion: version));
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "取り出した写しの記録を残せませんでした");
        }
        try
        {
            _baseSummaries.Delete(stateKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "古い要約を消せませんでした。次のマージが古い祖先を使わないよう、Pull をやり直してください");
        }
        return false;
    }

    private static SyncResult WithMessage(SyncResult result, string note)
        => new()
        {
            Outcome = result.Outcome,
            Message = result.Message is null ? note : $"{result.Message}。{note}",
            RemoteVersion = result.RemoteVersion,
            LastPulledVersion = result.LastPulledVersion,
            BackupPath = result.BackupPath,
            AffectedFiles = result.AffectedFiles,
            MergeReport = result.MergeReport,
            SyncedSummary = result.SyncedSummary,
            SyncedSha256 = result.SyncedSha256,
        };

    /// <summary>
    /// 前回の同期の後に手元の DB で変わったテーブルを返す。比べる基準が無い場合や
    /// 読めなかった場合は空を返す (従来どおり上書きさせる)。
    /// </summary>
    private IReadOnlyList<string> FindUnsyncedLocalChanges(ISyncService service, string stateKey, long lastPulledVersion)
    {
        if (service.TrackedDatabase is not { } tracked) return Array.Empty<string>();
        try
        {
            if (!File.Exists(tracked.Path)) return Array.Empty<string>();
            DatabaseSummary? baseline;
            if (_baseSummaries.IsMergePending(stateKey))
            {
                if (_baseSummaries.LoadPendingReplace(stateKey) is { FromPull: true } pending
                    && string.Equals(FileHasher.Sha256(tracked.Path), pending.MergedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    // Pull が本体を差し替えた後、残りの差し替えの前に止まった。本体は取り出した
                    // 写しそのものなので、その要約と比べれば手元の変更を見つけられる。記録は残りを
                    // そろえる Pull が済むまで残すので、ここで手元の変更ありと見ると進めない。
                    _logger.LogInformation("前回の Pull が本体を差し替えた後に止まっているので、取り出した写しの要約 (版 {Version}) と比べます ({Tool})", pending.RemoteVersion, service.ToolKey);
                    if (pending.Summary is null) return Array.Empty<string>();
                    pending.Summary.RemoteVersion = pending.RemoteVersion;
                    baseline = pending.Summary;
                }
                else
                {
                    // マージ結果で置き換えた後、要約か同期履歴を保存し終える前に止まった。
                    // 要約が当てにならないので、比べずに手元の変更があるものとして扱う。
                    return new[] { "前回のマージか Pull が途中で止まりました" };
                }
            }
            else
            {
                baseline = _baseSummaries.Load(stateKey);
            }
            // 送った写し (か、要約を保存できなかった Pull の写し) の記録が反映済みで、要約より
            // 新しければ、ローカルはその写しに手元の変更を足したもの。要約の保存に失敗した後も
            // 起動時 Pull が進めるよう、その写しの要約と比べる。
            if (_baseSummaries.LoadPushed(stateKey) is { Committed: true, Version: long syncedVersion, Summary: { } syncedSummary }
                && syncedVersion > lastPulledVersion
                && (baseline is null || baseline.RemoteVersion < syncedVersion))
            {
                _logger.LogInformation("同期先とそろえた写しの記録 (版 {Version}) と比べます ({Tool})", syncedVersion, service.ToolKey);
                syncedSummary.RemoteVersion = syncedVersion;
                baseline = syncedSummary;
            }
            // 要約の方が同期履歴より新しいのは、マージ結果で置き換えた後、要約を保存して
            // 同期履歴を保存する前に止まった場合か、Pull が本体を差し替えた後に残りの差し替えで
            // 止まった場合である。ローカルはその要約の版に手元の変更を足したものなので、
            // 要約と比べれば手元の変更を見つけられる。
            var interruptedMerge = baseline is not null && baseline.RemoteVersion > lastPulledVersion;
            if (baseline is null || (baseline.RemoteVersion != lastPulledVersion && !interruptedMerge))
            {
                _logger.LogInformation("前回の同期時点の要約が無いか、同期履歴と合わないため、手元の変更を確かめられません ({Tool})", service.ToolKey);
                return Array.Empty<string>();
            }
            var current = DatabaseSummary.Compute(tracked.Path, tracked.Profile, out var reason);
            if (current is null)
            {
                _logger.LogInformation("手元の DB の要約を取れません ({Tool}): {Reason}", service.ToolKey, reason);
                return Array.Empty<string>();
            }
            return current.FindChangedTables(baseline);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "手元の変更の確認に失敗しました ({Tool})", service.ToolKey);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 自動回収 (issue #55) の実行間隔。狙いは 1 日 1 回。丸 1 日にすると、
    /// 毎日ほぼ同じ時刻に Push するユーザで前回からの経過がわずかに届かず、
    /// 実行が 1 日おきになりがちなので、少し短く取る。
    /// </summary>
    public static readonly TimeSpan AutoGcInterval = TimeSpan.FromHours(20);

    /// <summary>
    /// 回収の排他に使う Mutex を作る。
    /// <para>
    /// プロセス内のロックではなく名前付き Mutex にするのは、GUI と CLI が
    /// 別プロセスで同じ保存先へ Push しうるため。プロセス内の排他では期限の判定と
    /// 時刻の記録が不可分にならず、双方が同じ古い記録を読んで、課金対象の走査
    /// (S3 の List) を二重に行う。
    /// </para>
    /// <para>
    /// 名前は保存先ごとに分ける。全体で 1 つにすると、ある保存先の回収が進行して
    /// いる間に別の保存先への回収が弾かれ、実行時刻も記録されないまま取り残される。
    /// <see cref="ISyncStorage.StateKeyPrefix"/> はパスなど Mutex 名に使えない文字を
    /// 含むため、ハッシュにして使う。Global\ は付けずユーザセッション内のみの排他に
    /// する (settings.json と保存先の認証情報はユーザ毎のため)。
    /// </para>
    /// </summary>
    private static Mutex CreateGcMutex(ISyncStorage storage)
    {
        var name = "VRCToolsDataSync.BlobGc." + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(storage.StateKeyPrefix)));
        return new Mutex(initiallyOwned: false, name);
    }

    private static bool TryAcquire(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // 保持したまま死んだプロセスから所有権が渡ってきた。取れてはいるので続行。
            return true;
        }
    }

    /// <summary>
    /// 前回の自動回収から <see cref="AutoGcInterval"/> が空いていれば、参照が
    /// 切れた実体の回収 (<see cref="BlobGarbageCollector"/>) を実行する。
    /// Push の後始末として呼ぶ。実行しなかった場合は null を返す。
    /// <para>
    /// 前回の実行時刻は settings.json のディスク上の値で判定する。GUI と CLI は
    /// 別プロセスで同じ保存先へ Push しうるので、手元の settings インスタンス
    /// では互いの実行を知れない。判定から記録までは保存先ごとの Mutex の中で行い、
    /// 並走した相手が同じ古い記録で期限到来と判定することを防ぐ。
    /// 記録は実行の成否に関わらず先に書く。回収が失敗し続ける状態 (権限不足など)
    /// で、Push のたびに走査をやり直さないため。
    /// </para>
    /// <para>
    /// 例外は投げない。回収は Push の成果に影響しない後始末で、これが原因で
    /// 成功した Push をエラー扱いにするわけにはいかないため。失敗はログに
    /// 残し、次の機会に回す。
    /// </para>
    /// </summary>
    public BlobGarbageCollectionResult? CollectGarbageIfDue(ISyncStorage storage)
    {
        var logger = _loggerFactory.CreateLogger<BlobGarbageCollector>();
        try
        {
            using var mutex = CreateGcMutex(storage);
            if (!TryAcquire(mutex, TimeSpan.Zero))
            {
                // 同じ保存先の回収が実行中。取れる頃には実行済みになっているので、
                // 待たずに次の機会へ回す。回収そのものは並走しても安全 (猶予期間と
                // 削除直前の読み直しで守られる) で、避けたいのは走査の重複だけ。
                return null;
            }
            try
            {
                var current = _store.Load();
                var key = storage.StateKeyPrefix;
                var now = DateTimeOffset.Now;
                // 未来を指す記録は無かったものとして扱う (期限到来)。時計が進んだ
                // 状態で書かれた記録をそのまま信じると、時計を直した後、その未来
                // 時刻から間隔が経つまで回収が止まる。保存側のマージも同じ基準で
                // 未来の記録を捨てるので、ここで書く記録が置き換わる。
                if (current.LastGcAt.TryGetValue(key, out var last)
                    && last <= now + SettingsStore.LastGcAtFutureTolerance
                    && now - last < AutoGcInterval)
                {
                    return null;
                }
                _store.SaveLastGcAt(key, now);

                logger.LogInformation("自動回収を開始します: {Target}", storage.DisplayName);
                return new BlobGarbageCollector(storage, logger).Collect();
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception ex)
        {
            // 同期先の不調 (SyncStorageException) だけでなく、settings.json の
            // 読み書きの失敗もここで止める。どれも Push の結果を汚す理由にならない。
            logger.LogWarning(ex, "自動回収を中止しました");
            return null;
        }
    }

    /// <summary>
    /// 間引きをせず、今すぐ回収する。GUI と CLI の手動実行用。
    /// <para>
    /// 実行時刻は自動回収と同じ記録に残し、直後の自動回収を省かせる。
    /// dry-run では残さない。何も消していないので、続く自動回収を省かせる
    /// 理由が無い。排他は dry-run でも共有する (走査の重複は同じため)。
    /// </para>
    /// <para>
    /// 失敗は自動回収と違って握りつぶさない。手動実行では結果を待っている
    /// 利用者がいるので、失敗を伝えて対処 (権限の見直しなど) につなげる。
    /// </para>
    /// </summary>
    public BlobGarbageCollectionResult CollectGarbageNow(
        ISyncStorage storage,
        TimeSpan? gracePeriod = null,
        bool dryRun = false)
    {
        using var mutex = CreateGcMutex(storage);
        // 同じ保存先の回収と重なった場合はしばらく待ち、それでも取れなければ
        // 続行せずに中止する。排他を無視して進めると、回収に時間がかかるケース
        // (オブジェクトが多い = 走査が高くつくケース) ほど課金対象の走査を
        // 重複させ、排他を入れた意味が無くなる。中止の理由は例外で呼び出し元へ
        // 伝え、「押したのに何も起きない」ようには見せない。
        if (!TryAcquire(mutex, TimeSpan.FromSeconds(30)))
        {
            throw new SyncStorageException(
                "同じ保存先に対する回収が既に実行中です。終わってからやり直してください。");
        }
        try
        {
            if (!dryRun)
            {
                _store.SaveLastGcAt(storage.StateKeyPrefix, DateTimeOffset.Now);
            }
            var logger = _loggerFactory.CreateLogger<BlobGarbageCollector>();
            logger.LogInformation("手動回収を開始します: {Target}", storage.DisplayName);
            return new BlobGarbageCollector(storage, logger).Collect(gracePeriod, dryRun);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// <see cref="CollectGarbageIfDue"/> をバックグラウンドで実行する。
    /// <para>
    /// 常駐アプリの経路 (自動 Push・GUI の Push) から使う。回収は初回や
    /// 溜まっている場合に時間がかかりうるので、Push の完了報告や次の自動 Push を
    /// その完了で待たせない。プロセスの終了で途中打ち切りになっても、消し残しは
    /// 次回の実行が回収するだけなので害はない。
    /// </para>
    /// </summary>
    public void CollectGarbageInBackground(ISyncStorage storage)
        // CollectGarbageIfDue は例外を投げないので、切り離した Task を
        // 観測しなくても未処理例外にはならない。
        => _ = Task.Run(() => CollectGarbageIfDue(storage));

    /// <summary>
    /// <see cref="SyncSettings.ToolState"/> のキー。同期先ごとに分けることで、
    /// 保存先を切り替えても互いの同期履歴を壊さない。
    /// </summary>
    public static string ToolStateKey(ISyncStorage storage, string toolKey)
        => storage.StateKeyPrefix + toolKey;

    /// <summary>
    /// 同期先とツールに対応する同期履歴を取り出す。
    /// 表示や通知の判定など、更新を伴わない用途からも使う。
    /// </summary>
    public static ToolSyncState? FindToolState(SyncSettings settings, ISyncStorage storage, string toolKey)
        => ResolveState(settings, storage, toolKey, out _);

    /// <summary>
    /// 同期先とツールに対応する同期履歴を取り出す。
    /// 旧形式のキーからの移行は <see cref="SettingsStore.Load"/> が読み込み時に
    /// 済ませているので、ここでは現行のキーを引くだけでよい。
    /// </summary>
    private static ToolSyncState? ResolveState(
        SyncSettings settings,
        ISyncStorage storage,
        string toolKey,
        out string stateKey)
    {
        // JSON デシリアライズで ToolState が明示的に null になる可能性に備え、
        // SettingsStore.MergeForSave と同様に null ガードを入れる。
        settings.ToolState ??= new Dictionary<string, ToolSyncState>();
        stateKey = ToolStateKey(storage, toolKey);
        return settings.ToolState.GetValueOrDefault(stateKey);
    }

    public ILogger<T> CreateLogger<T>() => _loggerFactory.CreateLogger<T>();
}
