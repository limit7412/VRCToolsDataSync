using Microsoft.Extensions.Logging.Abstractions;
using VRCToolsDataSync.Core.UseCase;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

public sealed class ReplaceDatabaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vrctds-replace-" + Guid.NewGuid().ToString("N"));

    public ReplaceDatabaseTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact(DisplayName = "置き換えに成功したら WAL と SHM を消す")]
    public void RemovesSidecarsOnSuccess()
    {
        var source = Write("merged.sqlite3", "merged");
        var db = Write("VRCX.sqlite3", "old");
        Write("VRCX.sqlite3-wal", "wal");
        Write("VRCX.sqlite3-shm", "shm");

        VrcxSyncService.ReplaceDatabase(source, db, NullLogger.Instance);

        Assert.Equal("merged", File.ReadAllText(db));
        Assert.Equal(new[] { "VRCX.sqlite3", "merged.sqlite3" },
            Directory.GetFiles(_root).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact(DisplayName = "置き換えに失敗したら WAL と SHM を元に戻す")]
    public void RestoresSidecarsOnFailure()
    {
        var db = Write("VRCX.sqlite3", "old");
        Write("VRCX.sqlite3-wal", "wal");
        Write("VRCX.sqlite3-shm", "shm");

        Assert.ThrowsAny<IOException>(() =>
            VrcxSyncService.ReplaceDatabase(Path.Combine(_root, "missing.sqlite3"), db, NullLogger.Instance));

        Assert.Equal("old", File.ReadAllText(db));
        Assert.Equal("wal", File.ReadAllText(db + "-wal"));
        Assert.Equal("shm", File.ReadAllText(db + "-shm"));
        Assert.Equal(3, Directory.GetFiles(_root).Length);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }
}
