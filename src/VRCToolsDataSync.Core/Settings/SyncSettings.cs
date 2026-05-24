namespace VRCToolsDataSync.Core.Settings;

public sealed class SyncSettings
{
    public string CloudFolderPath { get; set; } = string.Empty;
    public string MachineName { get; set; } = Environment.MachineName;
    public bool SyncVrcx { get; set; } = true;
    public bool SyncFriendConnect { get; set; } = true;

    public bool AutoSyncEnabled { get; set; } = false;

    public Dictionary<string, ToolSyncState> ToolState { get; set; } = new();

    // Issue #6: ツールごとの自動起動設定。キーは ISyncService.ToolKey と一致させる
    // ("vrcx", "friend-connect")。
    public Dictionary<string, ToolLaunchConfig> Launch { get; set; } = new();
}

public sealed class ToolSyncState
{
    public long LastPulledVersion { get; set; }
    public DateTimeOffset? LastPulledAt { get; set; }
    public long LastPushedVersion { get; set; }
    public DateTimeOffset? LastPushedAt { get; set; }
}

public sealed class ToolLaunchConfig
{
    // 実行ファイルの絶対パス。null/空なら自動検出 (ToolDataPaths.TryFindExecutable) に委ねる。
    public string? ExecutablePath { get; set; }
    public string? Arguments { get; set; }
    // VRCToolsDataSync 起動時の Pull 完了後に自動起動するかどうか。
    public bool LaunchOnAppStart { get; set; }
}
