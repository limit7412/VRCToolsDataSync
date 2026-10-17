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
            var result = service.Push(new PushOptions
            {
                Storage = storage,
                MachineName = settings.MachineName,
                ForceOverwriteOnConflict = force || reuploadAll,
                ReuploadAll = reuploadAll,
                LastPulledVersion = state.LastPulledVersion == 0 ? null : state.LastPulledVersion,
            });

            if (result.Outcome == SyncOutcome.Success && result.RemoteVersion.HasValue)
            {
                state.LastPushedVersion = result.RemoteVersion.Value;
                state.LastPushedAt = DateTimeOffset.Now;
                state.LastPulledVersion = result.RemoteVersion.Value;
                settings.ToolState[stateKey] = state;
                // Push 経由の Save は ToolState の更新だけが目的。Top-level 設定は
                // disk 値を優先しないと、古いインスタンスを持った別経路 (CLI / 別
                // SyncRunner) からの Push が、ユーザが GUI で変更した AutoSyncEnabled
                // 等を巻き戻してしまう。
                _store.SaveToolStateOnly(settings);
                RecordBaseSummary(service, stateKey, result.RemoteVersion.Value);
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
        var state = ResolveState(settings, storage, service.ToolKey, out var stateKey);
        var result = service.Pull(new PullOptions
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
        });

        if (result.Outcome == SyncOutcome.Success && result.RemoteVersion.HasValue)
        {
            state ??= new ToolSyncState();
            state.LastPulledVersion = result.RemoteVersion.Value;
            state.LastPulledAt = DateTimeOffset.Now;
            settings.ToolState[stateKey] = state;
            // Push と同じ理由で SaveToolStateOnly を使う。
            _store.SaveToolStateOnly(settings);
            RecordBaseSummary(service, stateKey, result.RemoteVersion.Value);
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

    /// <summary>Push を競合で取り直す回数の上限。</summary>
    private const int MaxMergeAttempts = 3;

    /// <summary>別プロセスのマージが終わるのを待つ上限。テストで短くする。</summary>
    internal static TimeSpan MergeLockTimeout { get; set; } = TimeSpan.FromMinutes(5);

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
            // PushLock はこのプロセスの中でしか効かない。常駐の GUI と CLI の merge が
            // 同じローカル DB を同時に置き換えないよう、プロセスをまたぐ錠前も取る。
            // 取れなければ、相手の途中の DB を土台にしないようマージを見送る。
            Directory.CreateDirectory(_baseSummaries.RootDirectory);
            using var crossProcessLock = CrossSessionFileLock.Acquire(
                Path.Combine(_baseSummaries.RootDirectory, $"merge-{service.ToolKey}.lock"), MergeLockTimeout);
            if (!crossProcessLock.IsHeld)
            {
                return (new SyncResult
                {
                    Outcome = SyncOutcome.Aborted,
                    Message = "別のプロセスがマージ中のため、マージを見送りました",
                }, false);
            }

            SyncResult? last = null;
            string? firstBackupPath = null;
            for (var attempt = 1; attempt <= MaxMergeAttempts; attempt++)
            {
                var state = ResolveState(settings, storage, service.ToolKey, out var stateKey) ?? new ToolSyncState();
                var previousBaseline = _baseSummaries.Load(stateKey);
                var previousPulledVersion = state.LastPulledVersion;
                var previousPulledAt = state.LastPulledAt;
                // 同期履歴と別の時点の要約は祖先に使えない。祖先なしの 2-way にする。
                var baseline = previousBaseline?.RemoteVersion == previousPulledVersion ? previousBaseline : null;

                var committed = false;
                SyncResult merged;
                try
                {
                    merged = service.PullMerge(new PullMergeOptions
                    {
                        Storage = storage,
                        Baseline = baseline,
                        DryRun = dryRun,
                        // 2 周目以降のローカルは前の周のマージ結果なので、退避するのは最初だけ。
                        SkipBackup = skipBackup || attempt > 1,
                        BeforeReplaceLocal = (remoteVersion, hostBaseline) =>
                        {
                            // 置き換えた後のローカルは、土台にした版に手元の変更を足したもの
                            // になる。祖先の要約と同期履歴を、置き換えの前にその版へそろえる。
                            committed = true;
                            if (hostBaseline is null) _baseSummaries.Delete(stateKey);
                            else _baseSummaries.Save(stateKey, hostBaseline);
                            state.LastPulledVersion = remoteVersion;
                            state.LastPulledAt = DateTimeOffset.Now;
                            settings.ToolState[stateKey] = state;
                            _store.SaveToolStateOnly(settings);
                        },
                    });
                }
                catch
                {
                    // そろえた後でローカルを置き換えられなかった。ローカルは元のままなので、
                    // 祖先の要約と同期履歴も元へ戻す。
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
                if (dryRun || merged.Outcome != SyncOutcome.Success || !merged.RemoteVersion.HasValue)
                {
                    return (merged, attempt > 1);
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
            return (new SyncResult
            {
                Outcome = last!.Outcome,
                RemoteVersion = last.RemoteVersion,
                BackupPath = firstBackupPath,
                AffectedFiles = last.AffectedFiles,
                Message = last.Message,
            }, true);
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
    /// 原因で成功した同期をエラー扱いにするわけにはいかないため。ただし古い要約は
    /// 消す。残すと、今回の同期より前の状態を基準に比べることになり、変わって
    /// いないのに「手元に変更がある」と判定し続ける。
    /// </para>
    /// </summary>
    private void RecordBaseSummary(ISyncService service, string stateKey, long remoteVersion)
    {
        if (service.TrackedDatabase is not { } tracked) return;
        try
        {
            string? reason = "DB がありません";
            var summary = File.Exists(tracked.Path)
                ? DatabaseSummary.Compute(tracked.Path, tracked.Profile, out reason)
                : null;
            if (summary is null)
            {
                _baseSummaries.Delete(stateKey);
                _logger.LogInformation("同期時点の要約を取れませんでした ({Tool}): {Reason}", service.ToolKey, reason);
                return;
            }
            summary.RemoteVersion = remoteVersion;
            _baseSummaries.Save(stateKey, summary);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "同期時点の要約の保存に失敗しました ({Tool})", service.ToolKey);
            try { _baseSummaries.Delete(stateKey); } catch { /* 次の同期で取り直す */ }
        }
    }

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
            var baseline = _baseSummaries.Load(stateKey);
            if (baseline is null || baseline.RemoteVersion != lastPulledVersion)
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
