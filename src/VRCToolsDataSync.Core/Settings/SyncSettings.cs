namespace VRCToolsDataSync.Core.Settings;

public sealed class SyncSettings
{
    public string CloudFolderPath { get; set; } = string.Empty;
    public string MachineName { get; set; } = Environment.MachineName;
    public bool SyncVrcx { get; set; } = true;
    public bool SyncFriendConnect { get; set; } = true;
}
