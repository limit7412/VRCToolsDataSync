using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCToolsDataSync.Core.Settings;
using VRCToolsDataSync.Core.Storage;

namespace VRCToolsDataSync.Core.Sync;

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

    public SyncRunner(SettingsStore? store = null, ILoggerFactory? loggerFactory = null)
    {
        _store = store ?? new SettingsStore();
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
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

    public SyncResult Push(
        ISyncService service,
        SyncSettings settings,
        ISyncStorage storage,
        bool force)
    {
        lock (PushLock)
        {
            var state = ResolveState(settings, storage, service.ToolKey, out var stateKey) ?? new ToolSyncState();
            var result = service.Push(new PushOptions
            {
                Storage = storage,
                MachineName = settings.MachineName,
                ForceOverwriteOnConflict = force,
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
        });

        if (result.Outcome == SyncOutcome.Success && result.RemoteVersion.HasValue)
        {
            state ??= new ToolSyncState();
            state.LastPulledVersion = result.RemoteVersion.Value;
            state.LastPulledAt = DateTimeOffset.Now;
            settings.ToolState[stateKey] = state;
            // Push と同じ理由で SaveToolStateOnly を使う。
            _store.SaveToolStateOnly(settings);
        }
        return result;
    }

    /// <summary>
    /// 自動回収 (issue #55) の実行間隔。狙いは 1 日 1 回。丸 1 日にすると、
    /// 毎日ほぼ同じ時刻に Push するユーザで前回からの経過がわずかに届かず、
    /// 実行が 1 日おきになりがちなので、少し短く取る。
    /// </summary>
    public static readonly TimeSpan AutoGcInterval = TimeSpan.FromHours(20);

    // 自動回収の同時実行を避ける。回収そのものは並走しても安全 (猶予期間と
    // 削除直前の読み直しで守られる) だが、同じ対象を二重に走査するだけ無駄になる。
    // 待たずにスキップするのは、待った側が取る頃には実行済みになっているため。
    private static readonly object AutoGcLock = new();

    /// <summary>
    /// 前回の自動回収から <see cref="AutoGcInterval"/> が空いていれば、参照が
    /// 切れた実体の回収 (<see cref="BlobGarbageCollector"/>) を実行する。
    /// Push の後始末として呼ぶ。実行しなかった場合は null を返す。
    /// <para>
    /// 前回の実行時刻は settings.json のディスク上の値で判定する。GUI と CLI は
    /// 別プロセスで同じ保存先へ Push しうるので、手元の settings インスタンス
    /// では互いの実行を知れない。記録は実行の成否に関わらず先に書く。回収が
    /// 失敗し続ける状態 (権限不足など) で、Push のたびに走査をやり直さないため。
    /// </para>
    /// <para>
    /// 例外は投げない。回収は Push の成果に影響しない後始末で、これが原因で
    /// 成功した Push をエラー扱いにするわけにはいかないため。失敗はログに
    /// 残し、次の機会に回す。
    /// </para>
    /// </summary>
    public BlobGarbageCollectionResult? CollectGarbageIfDue(ISyncStorage storage)
    {
        if (!Monitor.TryEnter(AutoGcLock)) return null;
        try
        {
            var logger = _loggerFactory.CreateLogger<BlobGarbageCollector>();
            try
            {
                var current = _store.Load();
                var key = storage.StateKeyPrefix;
                if (current.LastGcAt.TryGetValue(key, out var last)
                    && DateTimeOffset.Now - last < AutoGcInterval)
                {
                    return null;
                }
                _store.SaveLastGcAt(key, DateTimeOffset.Now);

                logger.LogInformation("自動回収を開始します: {Target}", storage.DisplayName);
                return new BlobGarbageCollector(storage, logger).Collect();
            }
            catch (Exception ex)
            {
                // 同期先の不調 (SyncStorageException) だけでなく、settings.json の
                // 読み書きの失敗もここで止める。どれも Push の結果を汚す理由にならない。
                logger.LogWarning(ex, "自動回収を中止しました");
                return null;
            }
        }
        finally
        {
            Monitor.Exit(AutoGcLock);
        }
    }

    /// <summary>
    /// 間引きをせず、今すぐ回収する。GUI の手動実行用。
    /// <para>
    /// 実行時刻は自動回収と同じ記録に残し、直後の自動回収を省かせる。
    /// 失敗は自動回収と違って握りつぶさない。手動実行では結果を待っている
    /// 利用者がいるので、失敗を伝えて対処 (権限の見直しなど) につなげる。
    /// </para>
    /// </summary>
    public BlobGarbageCollectionResult CollectGarbageNow(ISyncStorage storage)
    {
        // 自動回収と同時に走った場合は待つ。飛ばすと「押したのに何も起きない」
        // ように見えるため、自動側と違って TryEnter にしない。
        lock (AutoGcLock)
        {
            _store.SaveLastGcAt(storage.StateKeyPrefix, DateTimeOffset.Now);
            var logger = _loggerFactory.CreateLogger<BlobGarbageCollector>();
            logger.LogInformation("手動回収を開始します: {Target}", storage.DisplayName);
            return new BlobGarbageCollector(storage, logger).Collect();
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
