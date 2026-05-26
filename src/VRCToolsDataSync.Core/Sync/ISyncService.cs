namespace VRCToolsDataSync.Core.Sync;

public interface ISyncService
{
    string ToolKey { get; }

    SyncResult Push(PushOptions options);
    SyncResult Pull(PullOptions options);
}

public sealed class PushOptions
{
    public required string CloudFolderPath { get; init; }
    public required string MachineName { get; init; }
    public bool ForceOverwriteOnConflict { get; init; }
    public long? LastPulledVersion { get; init; }
}

public sealed class PullOptions
{
    public required string CloudFolderPath { get; init; }
    public bool SkipBackup { get; init; }

    // Issue #19: 起動時自動 Pull の暴走防止。
    // SkipIfNotNewer=true かつ LastPulledVersion>=リモート Version の場合は Pull を行わず
    // NothingToDo を返す。手動 Pull / コンフリクト解消 Pull は SkipIfNotNewer=false のままにして、
    // 「ユーザが意図的に呼んだ Pull は従来通り上書きする」セマンティクスを維持する。
    public long? LastPulledVersion { get; init; }
    public bool SkipIfNotNewer { get; init; }
}

public sealed class SyncResult
{
    public required SyncOutcome Outcome { get; init; }
    public string? Message { get; init; }
    public long? RemoteVersion { get; init; }
    public long? LastPulledVersion { get; init; }
    public string? BackupPath { get; init; }
    public IReadOnlyList<string> AffectedFiles { get; init; } = Array.Empty<string>();
}

public enum SyncOutcome
{
    Success,
    NothingToDo,
    ConflictDetected,
    SourceMissing,
    Aborted,
}
