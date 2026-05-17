namespace VRCToolsDataSync.Core.Settings;

public sealed class SyncSettings
{
    public string CloudFolderPath { get; set; } = string.Empty;
    public string MachineName { get; set; } = Environment.MachineName;
    public bool SyncVrcx { get; set; } = true;
    public bool SyncFriendConnect { get; set; } = true;

    public Dictionary<string, ToolSyncState> ToolState { get; set; } = new();
}

public sealed class ToolSyncState
{
    public long LastPulledVersion { get; set; }
    public DateTimeOffset? LastPulledAt { get; set; }
    public long LastPushedVersion { get; set; }
    public DateTimeOffset? LastPushedAt { get; set; }
}
