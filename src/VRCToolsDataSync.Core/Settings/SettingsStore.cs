using System.Text.Json;

namespace VRCToolsDataSync.Core.Settings;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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
        var tmp = FilePath + ".tmp";
        using (var stream = File.Create(tmp))
        {
            JsonSerializer.Serialize(stream, settings, JsonOptions);
        }
        if (File.Exists(FilePath))
        {
            File.Replace(tmp, FilePath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tmp, FilePath);
        }
    }
}
