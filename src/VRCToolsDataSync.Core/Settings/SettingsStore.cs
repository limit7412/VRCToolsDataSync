using System.Text.Json;
using System.Threading;

namespace VRCToolsDataSync.Core.Settings;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
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
        if (!File.Exists(FilePath))
        {
            return new SyncSettings();
        }
        using var stream = File.OpenRead(FilePath);
        return JsonSerializer.Deserialize<SyncSettings>(stream, JsonOptions) ?? new SyncSettings();
    }

    public void Save(SyncSettings settings) => SaveInternal(settings, mergeTopLevelFromDisk: false);

    /// <summary>
    /// ToolState の更新だけが目的の Save。Top-level の設定
    /// (CloudFolderPath / MachineName / SyncVrcx / SyncFriendConnect /
    /// AutoSyncEnabled) はディスク側の現行値を採用し、incoming は ToolState
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
                    settings.ToolState = merged.ToolState;
                    settings.Launch = merged.Launch;
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
    /// AutoSyncEnabled) は incoming を優先する。
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
