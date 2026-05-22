using System.Text.Json;

namespace VRCToolsDataSync.Core.Settings;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _saveLock = new();

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

        // 同一インスタンスからの並行 Save を直列化し、
        // 一時ファイル名にも GUID を付けて他プロセス/別 SyncRunner からの
        // 同時書き込みでも tmp 衝突しないようにする。
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
                // 呼び出し元 settings の ToolState が古いままで、続けて
                // 別の経路 (例: GUI ボタンの Push) が走った時に旧情報を
                // 書き戻してしまう。
                settings.ToolState = merged.ToolState;
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
        };

        // 両方に存在する tool キーは新しい方を採用、片方だけにあるものはそのまま追加。
        var allKeys = new HashSet<string>(disk.ToolState.Keys, StringComparer.Ordinal);
        foreach (var k in incoming.ToolState.Keys) allKeys.Add(k);
        foreach (var key in allKeys)
        {
            var inc = incoming.ToolState.GetValueOrDefault(key);
            var dsk = disk.ToolState.GetValueOrDefault(key);
            if (inc is null) { result.ToolState[key] = dsk!; continue; }
            if (dsk is null) { result.ToolState[key] = inc; continue; }
            result.ToolState[key] = PickNewer(inc, dsk);
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
