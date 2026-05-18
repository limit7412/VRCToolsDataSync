using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRCToolsDataSync.Core.Sync;

public sealed class SyncManifest
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, ToolManifestEntry> Tools { get; set; } = new();
}

public sealed class ToolManifestEntry
{
    public long Version { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ManifestFile> Files { get; set; } = new();
}

public sealed class ManifestFile
{
    public string RelativePath { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class ManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath { get; }

    public ManifestStore(string cloudFolderPath)
    {
        FilePath = Path.Combine(cloudFolderPath, "manifest.json");
    }

    public SyncManifest Load()
    {
        if (!File.Exists(FilePath))
        {
            return new SyncManifest();
        }
        using var stream = File.OpenRead(FilePath);
        return JsonSerializer.Deserialize<SyncManifest>(stream, JsonOptions) ?? new SyncManifest();
    }

    public void Save(SyncManifest manifest)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        using (var stream = File.Create(tmp))
        {
            JsonSerializer.Serialize(stream, manifest, JsonOptions);
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
