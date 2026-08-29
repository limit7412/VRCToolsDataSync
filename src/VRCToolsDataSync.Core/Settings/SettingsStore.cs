using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using VRCToolsDataSync.Core.Storage;
using VRCToolsDataSync.Core.Update;

namespace VRCToolsDataSync.Core.Settings;

public sealed class SettingsStore
{
    /// <summary>現在の <see cref="SyncSettings.ToolStateSchema"/>。</summary>
    private const int CurrentToolStateSchema = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // StorageMode を数値ではなく "localFolder" / "s3" として読み書きする。
        // 数値表記の既存ファイルもこのコンバータで読める。
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly object _saveLock = new();

    // クロスプロセス排他用の Named Mutex 名。
    // GUI (App) と CLI が同じ settings.json に対して並走で
    // read-modify-write すると、プロセス内 _saveLock だけではアトミック性が
    // 担保できず、片方の更新が他方に潰される。Global\ は付けずユーザセッション
    // 内のみ排他にする (settings は %AppData% 配下なのでユーザ毎にしか共有されない)。
    private const string CrossProcessMutexName = "VRCToolsDataSync.SettingsStore.Save";
    // Mutex 取得のタイムアウト。普通の Save は数十 ms で終わるため、
    // これだけ待っても取れない場合は別プロセスがハング相当なので、
    // 取得を諦めてプロセス内ロックだけで救済し best-effort で書く。
    private static readonly TimeSpan CrossProcessMutexTimeout = TimeSpan.FromSeconds(10);

    public string FilePath { get; }

    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? DefaultFilePath();
    }

    public static string DefaultFilePath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VRCToolsDataSync");
        return Path.Combine(dir, "settings.json");
    }

    public SyncSettings Load()
    {
        SyncSettings settings;
        if (File.Exists(FilePath))
        {
            using var stream = File.OpenRead(FilePath);
            settings = JsonSerializer.Deserialize<SyncSettings>(stream, JsonOptions) ?? new SyncSettings();
        }
        else
        {
            settings = new SyncSettings();
        }
        MigrateToolStateKeys(settings);
        return settings;
    }

    /// <summary>
    /// 保存先ごとの接頭辞を持たない旧形式の同期履歴を、同期フォルダのキーへ移す。
    /// <para>
    /// 旧形式には「どの保存先に対する履歴か」が記録されていないため、判断材料は
    /// <see cref="SyncSettings.CloudFolderPath"/> しかない。読み込みの直後に一度だけ
    /// 行うことで、利用者が画面や CLI で保存先を変更する前の値を使える。
    /// 遅延して判断すると、変更後の保存先へ別の保存先の履歴を持ち込むことになる。
    /// </para>
    /// <para>
    /// 同期フォルダが未設定の場合、引き継ぎ先を決められないので旧エントリは捨てる。
    /// 次の同期で新しい履歴が作られる。
    /// </para>
    /// </summary>
    private static void MigrateToolStateKeys(SyncSettings settings)
    {
        if (settings.ToolStateSchema >= CurrentToolStateSchema) return;
        settings.ToolStateSchema = CurrentToolStateSchema;

        settings.ToolState ??= new Dictionary<string, ToolSyncState>();
        var legacyKeys = settings.ToolState.Keys.Where(k => !k.Contains('|')).ToList();
        if (legacyKeys.Count == 0) return;

        string? prefix = null;
        var folder = settings.CloudFolderPath?.Trim();
        if (!string.IsNullOrEmpty(folder))
        {
            try
            {
                prefix = new LocalFolderSyncStorage(folder).StateKeyPrefix;
            }
            catch (Exception ex) when (ex is SyncStorageException
                                        or ArgumentException
                                        or NotSupportedException
                                        or PathTooLongException)
            {
                // 設定に壊れたパスが入っている。引き継がない。
            }
        }

        foreach (var key in legacyKeys)
        {
            var state = settings.ToolState[key];
            settings.ToolState.Remove(key);
            if (prefix is null) continue;
            var migrated = prefix + key;
            if (!settings.ToolState.ContainsKey(migrated))
            {
                settings.ToolState[migrated] = state;
            }
        }
    }

    public void Save(SyncSettings settings) => SaveInternal(settings, mergeTopLevelFromDisk: false);

    /// <summary>
    /// ToolState の更新だけが目的の Save。Top-level の設定
    /// (CloudFolderPath / MachineName / SyncVrcx / SyncFriendConnect /
    /// AutoSyncEnabled / StorageMode / S3) はディスク側の現行値を採用し、incoming は ToolState
    /// のみを差し込む形でマージする。
    ///
    /// 通常の Save (= GUI の「設定を保存」ボタン) と違い、Push/Pull のような
    /// 「Top-level 設定をユーザが触っていない経路」から呼ばれることを想定。
    /// これがないと、GUI で AutoSyncEnabled=ON にした直後に CLI 等の別プロセス
    /// が古い settings (AutoSyncEnabled=false) で Push して store.Save を呼ぶと、
    /// その古い値で上書きされて ON 設定が消えてしまう。
    /// </summary>
    public void SaveToolStateOnly(SyncSettings settings) => SaveInternal(settings, mergeTopLevelFromDisk: true);

    private void SaveInternal(SyncSettings settings, bool mergeTopLevelFromDisk)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // クロスプロセス排他: GUI と CLI が並走したケースで read-modify-write を
        // アトミックに完結させる。プロセス内 _saveLock は同一インスタンス内の
        // 並行 Save 直列化用で、別プロセスからの同時 Save は守れない。
        // initiallyOwned=false でハンドルだけ作り、WaitOne でブロック取得する。
        using var crossProcessMutex = new Mutex(initiallyOwned: false, name: CrossProcessMutexName);
        bool mutexAcquired = false;
        try
        {
            try
            {
                mutexAcquired = crossProcessMutex.WaitOne(CrossProcessMutexTimeout);
            }
            catch (AbandonedMutexException)
            {
                // 他プロセスが Mutex を保持したまま死んだ場合、所有権はこちらに
                // 渡ってくる。Mutex 自体は取れているので続行する。
                mutexAcquired = true;
            }

            // タイムアウトで取れなかった場合はプロセス内ロックだけで best-effort 保存。
            // 取得を諦めるよりは書き込んだ方がマシ (待つほど呼び出し元が長時間ハングする)。

            // 一時ファイル名にも GUID を付けて、Mutex 取得失敗時の best-effort 書き込みや
            // 他プロセスからの同時書き込みでも tmp 衝突しないようにする。
            lock (_saveLock)
            {
                // 保存直前にディスクの現行 settings を再読込し、ToolState を
                // tool キー単位でマージする。これにより、別プロセス/別 SyncRunner
                // が同じ settings.json に対して別 tool の状態更新を入れた直後でも、
                // 自分の Save がそれを消し飛ばさない。
                var merged = MergeForSave(settings, mergeTopLevelFromDisk);

                var tmp = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (var stream = File.Create(tmp))
                    {
                        JsonSerializer.Serialize(stream, merged, JsonOptions);
                    }
                    if (File.Exists(FilePath))
                    {
                        File.Replace(tmp, FilePath, destinationBackupFileName: null);
                    }
                    else
                    {
                        File.Move(tmp, FilePath);
                    }

                    // 呼び出し元のインスタンスにも反映しておく。これがないと、
                    // 呼び出し元 settings の ToolState / Launch が古いままで、続けて
                    // 別の経路 (例: GUI ボタンの Push) が走った時に旧情報を
                    // 書き戻してしまう。
                    // Top-level 設定も同期する: SaveToolStateOnly 後に同じ settings
                    // インスタンスで通常 Save が走ると、ディスクから採用した最新の
                    // top-level が in-memory の古い値で上書きされて消えるため。
                    settings.CloudFolderPath = merged.CloudFolderPath;
                    settings.MachineName = merged.MachineName;
                    settings.SyncVrcx = merged.SyncVrcx;
                    settings.SyncFriendConnect = merged.SyncFriendConnect;
                    settings.AutoSyncEnabled = merged.AutoSyncEnabled;
                    settings.StorageMode = merged.StorageMode;
                    settings.S3 = merged.S3;
                    settings.ToolStateSchema = merged.ToolStateSchema;
                    settings.ToolState = merged.ToolState;
                    settings.Launch = merged.Launch;
                    settings.Update = merged.Update;
                }
                finally
                {
                    if (File.Exists(tmp))
                    {
                        try { File.Delete(tmp); } catch { /* best-effort */ }
                    }
                }
            }
        }
        finally
        {
            if (mutexAcquired)
            {
                try { crossProcessMutex.ReleaseMutex(); } catch { /* best-effort */ }
            }
        }
    }

    /// <summary>
    /// 呼び出し元の <paramref name="incoming"/> とディスクの現行 settings を
    /// マージした結果を返す。
    /// <para>
    /// <paramref name="mergeTopLevelFromDisk"/> が false (通常の Save) の場合、
    /// Top-level 設定 (CloudFolderPath, MachineName, SyncVrcx, SyncFriendConnect,
    /// AutoSyncEnabled, StorageMode, S3) は incoming を優先する。
    /// </para>
    /// <para>
    /// true (SaveToolStateOnly) の場合、Top-level 設定はディスク側を採用する。
    /// Push/Pull の付随 Save がユーザの Top-level 設定変更を巻き戻すのを防ぐ。
    /// ディスクに settings.json が無い場合 (初回) は incoming を採用する。
    /// </para>
    /// ToolState は tool キーごとに、より新しいタイムスタンプを持つ側を採用する。
    /// </summary>
    private SyncSettings MergeForSave(SyncSettings incoming, bool mergeTopLevelFromDisk)
    {
        SyncSettings disk;
        bool diskAvailable;
        try
        {
            diskAvailable = File.Exists(FilePath);
            disk = Load();
        }
        catch
        {
            // 読み込めない場合 (初回 / ファイル破損) はマージ不要、incoming をそのまま使う。
            disk = new SyncSettings();
            diskAvailable = false;
        }

        // Top-level の採用元を決める。
        // - 通常 Save: incoming (ユーザが触った最新値)
        // - ToolState 専用 Save: ディスク (Push/Pull は Top-level を変えない)
        //   ただしディスクに既存ファイルが無い場合は incoming にフォールバック
        //   しないと、初回 Push でユーザ設定がデフォルト値に潰れる。
        var topLevelSource = (mergeTopLevelFromDisk && diskAvailable) ? disk : incoming;
        var result = new SyncSettings
        {
            CloudFolderPath = topLevelSource.CloudFolderPath,
            MachineName = topLevelSource.MachineName,
            SyncVrcx = topLevelSource.SyncVrcx,
            SyncFriendConnect = topLevelSource.SyncFriendConnect,
            AutoSyncEnabled = topLevelSource.AutoSyncEnabled,
            // 保存先の種類と S3 接続設定も Top-level と同じ採用元から取る。
            // Push/Pull 経由の Save がユーザの保存先変更を巻き戻さないようにする。
            StorageMode = topLevelSource.StorageMode,
            S3 = topLevelSource.S3?.Clone(),
            // 更新確認の設定も Top-level と同じ扱い。Push/Pull の付随 Save が
            // チャンネルや通知済みの記録を巻き戻さないようにする。
            Update = (topLevelSource.Update ?? new UpdateSettings()).Clone(),
            // 読み込み時に移行済みなので、ディスク側も incoming 側も現行の版になっている。
            ToolStateSchema = CurrentToolStateSchema,
            ToolState = new Dictionary<string, ToolSyncState>(),
            Launch = new Dictionary<string, ToolLaunchConfig>(),
        };

        // 両方に存在する tool キーは新しい方を採用、片方だけにあるものはそのまま追加。
        // JSON デシリアライズで明示的に null が入る可能性があるため、null セーフに扱う。
        var diskToolState = disk.ToolState ?? new Dictionary<string, ToolSyncState>();
        var incomingToolState = incoming.ToolState ?? new Dictionary<string, ToolSyncState>();
        var allKeys = new HashSet<string>(diskToolState.Keys, StringComparer.Ordinal);
        foreach (var k in incomingToolState.Keys) allKeys.Add(k);
        foreach (var key in allKeys)
        {
            var inc = incomingToolState.GetValueOrDefault(key);
            var dsk = diskToolState.GetValueOrDefault(key);
            if (inc is null) { result.ToolState[key] = dsk!; continue; }
            if (dsk is null) { result.ToolState[key] = inc; continue; }
            result.ToolState[key] = PickNewer(inc, dsk);
        }

        // 通知済みの版だけは、採用元に関わらずディスクと incoming の新しいほうを
        // 採用する。通知の記録は常に前へ進む (UpdateChecker.MarkNotified が古い版で
        // 上書きしない) ので、新旧は版の比較で決められる。これが無いと、起動時に
        // 読んだ古い設定のまま保存する経路 (接続確認に時間のかかる CLI の storage
        // など) が別プロセスの記録した版を巻き戻し、同じ版を知らせ直す。
        var diskNotified = ReleaseVersion.Parse(disk.Update?.NotifiedVersion);
        var incomingNotified = ReleaseVersion.Parse(incoming.Update?.NotifiedVersion);
        if (diskNotified is not null && (incomingNotified is null || diskNotified > incomingNotified))
        {
            result.Update.NotifiedVersion = disk.Update!.NotifiedVersion;
        }
        else if (incomingNotified is not null)
        {
            result.Update.NotifiedVersion = incoming.Update!.NotifiedVersion;
        }

        // Launch は Top-level と同じ採用元から取る。理由:
        // - 通常 Save (= GUI で設定変更) は incoming を採用したい
        // - SaveToolStateOnly (= Push/Pull) は Launch を触らないので disk を残したい
        // Launch には ToolSyncState のようなタイムスタンプが無いため、PickNewer は不要。
        var launchSource = (mergeTopLevelFromDisk && diskAvailable) ? disk : incoming;
        var launchDict = launchSource.Launch ?? new Dictionary<string, ToolLaunchConfig>();
        foreach (var kv in launchDict)
        {
            result.Launch[kv.Key] = kv.Value;
        }

        return result;
    }

    private static ToolSyncState PickNewer(ToolSyncState a, ToolSyncState b)
    {
        // LastPushedAt と LastPulledAt のうち、最新タイムスタンプを比較。
        var aLatest = LatestTimestamp(a);
        var bLatest = LatestTimestamp(b);
        return aLatest >= bLatest ? a : b;
    }

    private static DateTimeOffset LatestTimestamp(ToolSyncState s)
    {
        var p = s.LastPushedAt ?? DateTimeOffset.MinValue;
        var u = s.LastPulledAt ?? DateTimeOffset.MinValue;
        return p > u ? p : u;
    }
}
