using Microsoft.Data.Sqlite;
using VRCToolsDataSync.Core.Domain;
using VRCToolsDataSync.Core.Infra;
using VRCToolsDataSync.Core.UseCase;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// Friend Connect の同期対象の扱いを固定する。現行版の本体 DB は db_1.1.sqlite で、
/// 旧版の db.sqlite は古くから使っている PC にしか無い。写真のサムネイルは DB と
/// 対応しているので、DB と一緒に運ぶ。
/// </summary>
public sealed class FriendConnectSyncServiceTests : IDisposable
{
    private const string DbKey = "friend-connect/db/db.sqlite";
    private const string DbV11Key = "friend-connect/db/db_1.1.sqlite";
    private const string ThumbnailKey = "friend-connect/thumbnails/a.webp";

    private readonly string _root;

    public FriendConnectSyncServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vrctds-fc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // 接続プールが SQLite のファイルを掴んだままだと消せない。
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private FriendConnectPaths PathsOf(string machine) => new(Path.Combine(_root, machine, "data"));

    private FriendConnectSyncService ServiceOf(string machine)
        => new(PathsOf(machine), new LocalBackup(Path.Combine(_root, machine, "backup")));

    private static void CreateDb(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS t (v TEXT); INSERT INTO t VALUES ($v);";
        command.Parameters.AddWithValue("$v", value);
        command.ExecuteNonQuery();
    }

    private static string ReadDb(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT v FROM t";
        return (string)command.ExecuteScalar()!;
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static SyncResult Push(ISyncService service, ISyncStorage storage, long? lastPulled = null)
        => service.Push(new PushOptions
        {
            Storage = storage,
            MachineName = "test",
            LastPulledVersion = lastPulled,
        });

    private static SyncResult Pull(ISyncService service, ISyncStorage storage, long? lastPulled = null, bool skipIfNotNewer = false)
        => service.Pull(new PullOptions
        {
            Storage = storage,
            LastPulledVersion = lastPulled,
            SkipIfNotNewer = skipIfNotNewer,
        });

    private static IReadOnlyList<string> RemotePaths(ISyncStorage storage)
        => new ManifestStore(storage).Load().Tools[FriendConnectSyncService.Key].Files
            .Select(f => f.RelativePath)
            .ToList();

    [Fact(DisplayName = "旧版の db.sqlite が無くても、現行の本体 DB だけで Push できる")]
    public void PushesWithoutLegacyDb()
    {
        var storage = new FakeSyncStorage();
        CreateDb(PathsOf("a").DbV11File, "v11");

        var result = Push(ServiceOf("a"), storage);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.Contains(DbV11Key, RemotePaths(storage));
        Assert.DoesNotContain(DbKey, RemotePaths(storage));
    }

    [Fact(DisplayName = "現行の本体 DB が無ければ、旧版の db.sqlite があっても Push しない")]
    public void RefusesPushWithoutCurrentDb()
    {
        var storage = new FakeSyncStorage();
        CreateDb(PathsOf("a").DbFile, "legacy");

        var result = Push(ServiceOf("a"), storage);

        Assert.Equal(SyncOutcome.SourceMissing, result.Outcome);
        Assert.False(storage.Has(ManifestKeys.Manifest));
    }

    [Fact(DisplayName = "旧版の db.sqlite は、ある PC からは本体 DB と一緒に送られる")]
    public void PushesLegacyDbWhenPresent()
    {
        var storage = new FakeSyncStorage();
        CreateDb(PathsOf("a").DbV11File, "v11");
        CreateDb(PathsOf("a").DbFile, "legacy");

        Push(ServiceOf("a"), storage);

        Assert.Contains(DbV11Key, RemotePaths(storage));
        Assert.Contains(DbKey, RemotePaths(storage));
    }

    [Fact(DisplayName = "サムネイルは別の PC へ届き、消えたものは Pull で消える")]
    public void SyncsThumbnails()
    {
        var storage = new FakeSyncStorage();
        var a = PathsOf("a");
        CreateDb(a.DbV11File, "v11");
        WriteFile(Path.Combine(a.ThumbnailsDirectory, "a.webp"), "thumb-a");

        var first = Push(ServiceOf("a"), storage);
        Assert.Contains(ThumbnailKey, RemotePaths(storage));

        var b = PathsOf("b");
        WriteFile(Path.Combine(b.ThumbnailsDirectory, "stale.webp"), "stale");
        var pulled = Pull(ServiceOf("b"), storage);

        Assert.Equal(SyncOutcome.Success, pulled.Outcome);
        Assert.Equal("thumb-a", File.ReadAllText(Path.Combine(b.ThumbnailsDirectory, "a.webp")));
        Assert.False(File.Exists(Path.Combine(b.ThumbnailsDirectory, "stale.webp")));

        // A でサムネイルが消えたら、B からも消える。
        File.Delete(Path.Combine(a.ThumbnailsDirectory, "a.webp"));
        Push(ServiceOf("a"), storage, lastPulled: first.RemoteVersion);
        Pull(ServiceOf("b"), storage);

        Assert.False(File.Exists(Path.Combine(b.ThumbnailsDirectory, "a.webp")));
    }

    [Fact(DisplayName = "以前の版が書いた db.sqlite だけの manifest も Pull できる")]
    public void PullsManifestWithOnlyLegacyDb()
    {
        var storage = new FakeSyncStorage();
        var source = Path.Combine(_root, "legacy-source.sqlite");
        CreateDb(source, "legacy");
        SqliteConnection.ClearAllPools();
        var bytes = File.ReadAllBytes(source);
        var sha = FileHasher.Sha256(source);
        storage.Seed(BlobKeys.FromSha256(sha), bytes);
        storage.SeedManifest(new SyncManifest
        {
            Tools =
            {
                [FriendConnectSyncService.Key] = new ToolManifestEntry
                {
                    Version = 1,
                    Files =
                    {
                        new ManifestFile
                        {
                            RelativePath = DbKey,
                            Size = bytes.Length,
                            Sha256 = sha,
                            BlobKey = BlobKeys.FromSha256(sha),
                        },
                    },
                },
            },
        });

        var result = Pull(ServiceOf("b"), storage);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.Equal("legacy", ReadDb(PathsOf("b").DbFile));
    }

    [Fact(DisplayName = "同期先に旧版の db.sqlite が無ければ、ローカルの db.sqlite はバックアップへ退避して消す")]
    public void RemovesLocalLegacyDbMissingOnRemote()
    {
        var storage = new FakeSyncStorage();
        CreateDb(PathsOf("a").DbV11File, "v11");
        Push(ServiceOf("a"), storage);

        var b = PathsOf("b");
        CreateDb(b.DbV11File, "old");
        CreateDb(b.DbFile, "legacy");
        SqliteConnection.ClearAllPools();

        var result = Pull(ServiceOf("b"), storage);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.Equal("v11", ReadDb(b.DbV11File));
        Assert.False(File.Exists(b.DbFile));
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(Path.Combine(result.BackupPath!, "db.sqlite")));
    }

    [Fact(DisplayName = "起動時 Pull は、手元で本体 DB が消えていれば version が同じでも取り直す")]
    public void StartupPullRestoresMissingCurrentDb()
    {
        var storage = new FakeSyncStorage();
        CreateDb(PathsOf("a").DbV11File, "v11");
        var pushed = Push(ServiceOf("a"), storage);

        var b = PathsOf("b");
        Directory.CreateDirectory(b.RootDirectory);
        var result = Pull(ServiceOf("b"), storage, lastPulled: pushed.RemoteVersion, skipIfNotNewer: true);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.Equal("v11", ReadDb(b.DbV11File));
    }
}
