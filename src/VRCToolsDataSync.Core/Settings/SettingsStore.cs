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

    public void Save(SyncSettings settings)
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
            var merged = MergeForSave(settings);

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
    /// マージした結果を返す。Top-level の設定 (CloudFolderPath, MachineName,
    /// SyncVrcx, SyncFriendConnect, AutoSyncEnabled) は incoming を優先する。
    /// ToolState は tool キーごとに、より新しいタイムスタンプを持つ側を採用する。
    /// </summary>
    private SyncSettings MergeForSave(SyncSettings incoming)
    {
        SyncSettings disk;
        try
        {
            disk = Load();
        }
        catch
        {
            // 読み込めない場合 (初回 / ファイル破損) はマージ不要、incoming をそのまま使う。
            disk = new SyncSettings();
        }

        var result = new SyncSettings
        {
            CloudFolderPath = incoming.CloudFolderPath,
            MachineName = incoming.MachineName,
            SyncVrcx = incoming.SyncVrcx,
            SyncFriendConnect = incoming.SyncFriendConnect,
            AutoSyncEnabled = incoming.AutoSyncEnabled,
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
