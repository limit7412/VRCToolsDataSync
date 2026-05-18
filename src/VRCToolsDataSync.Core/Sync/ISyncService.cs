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
