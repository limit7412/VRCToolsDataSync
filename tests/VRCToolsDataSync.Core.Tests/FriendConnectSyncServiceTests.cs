using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using VRCToolsDataSync.Core.Domain;
using VRCToolsDataSync.Core.Infra;
using VRCToolsDataSync.Core.Merge;
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

    [Fact(DisplayName = "Pull の取得中にサムネイルだけが増えても、消す前に中止する")]
    public void PullAbortsWhenThumbnailAddedDuringFetch()
    {
        var storage = new FakeSyncStorage();
        var a = PathsOf("a");
        CreateDb(a.DbV11File, "v11");
        WriteFile(Path.Combine(a.ThumbnailsDirectory, "a.webp"), "thumb-a");
        Assert.Equal(SyncOutcome.Success, Push(ServiceOf("a"), storage).Outcome);

        var b = PathsOf("b");
        CreateDb(b.DbV11File, "old");
        // Windows では、プールに残った接続が本体を掴んだままだと Pull が開けない。
        SqliteConnection.ClearAllPools();
        var added = Path.Combine(b.ThumbnailsDirectory, "added.webp");
        storage.OnDownload = _ => WriteFile(added, "added");

        var result = Pull(ServiceOf("b"), storage);

        // manifest に無いファイルとして消されずに残り、DB も差し替えていない。
        Assert.Equal(SyncOutcome.Aborted, result.Outcome);
        Assert.Contains("書き換えられた", result.Message);
        Assert.True(File.Exists(added));
        Assert.Equal("old", ReadDb(b.DbV11File));
    }

    [Fact]
    public void FirstPullFetchesDirectoryThatDoesNotExistLocally()
    {
        var storage = new FakeSyncStorage();
        var a = PathsOf("a");
        CreateDb(a.DbV11File, "v11");
        WriteFile(Path.Combine(a.ThumbnailsDirectory, "a.webp"), "thumb-a");
        Assert.Equal(SyncOutcome.Success, Push(ServiceOf("a"), storage).Outcome);

        // b には thumbnails がまだ無い。取り出しがフォルダを作っても
        // 「取得の間に書き換えられた」と見ない。
        var b = PathsOf("b");
        CreateDb(b.DbV11File, "old");
        SqliteConnection.ClearAllPools();
        Assert.False(Directory.Exists(b.ThumbnailsDirectory));

        var result = Pull(ServiceOf("b"), storage);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.Equal("thumb-a", File.ReadAllText(Path.Combine(b.ThumbnailsDirectory, "a.webp")));
        Assert.Equal("v11", ReadDb(b.DbV11File));
    }

    [Fact(DisplayName = "以前の版が manifest に残した notes\\ の項目は取り出さず、次の Push で一覧から落とす")]
    public void LegacyNotesEntriesAreIgnoredAndDropped()
    {
        var storage = new FakeSyncStorage();
        var a = PathsOf("a");
        CreateDb(a.DbV11File, "v11");
        WriteFile(Path.Combine(a.ThumbnailsDirectory, "a.webp"), "thumb-a");
        Assert.Equal(SyncOutcome.Success, Push(ServiceOf("a"), storage).Outcome);

        // notes\ を送っていた版が書いた項目を足す。Friend Connect にこのフォルダは無い。
        const string legacyNotesKey = "friend-connect/notes/note.md";
        var manifestStore = new ManifestStore(storage);
        var manifest = manifestStore.Load();
        var entry = manifest.Tools[FriendConnectSyncService.Key];
        var note = Encoding.UTF8.GetBytes("note-a");
        var sha = Convert.ToHexString(SHA256.HashData(note)).ToLowerInvariant();
        storage.Seed(BlobKeys.FromSha256(sha), note);
        entry.Files.Add(new ManifestFile
        {
            RelativePath = legacyNotesKey,
            Size = note.Length,
            Sha256 = sha,
            BlobKey = BlobKeys.FromSha256(sha),
        });
        storage.SeedManifest(manifest);

        var b = PathsOf("b");
        CreateDb(b.DbV11File, "old");
        SqliteConnection.ClearAllPools();

        var pulled = Pull(ServiceOf("b"), storage);

        Assert.Equal(SyncOutcome.Success, pulled.Outcome);
        Assert.False(Directory.Exists(Path.Combine(b.RootDirectory, "notes")));
        Assert.Equal("thumb-a", File.ReadAllText(Path.Combine(b.ThumbnailsDirectory, "a.webp")));
        Assert.Equal("v11", ReadDb(b.DbV11File));

        SqliteConnection.ClearAllPools();
        Assert.Equal(SyncOutcome.Success, Push(ServiceOf("b"), storage, lastPulled: pulled.RemoteVersion).Outcome);
        Assert.DoesNotContain(legacyNotesKey, RemotePaths(storage));
        Assert.Contains(ThumbnailKey, RemotePaths(storage));
    }

    [Fact]
    public void PullResultCarriesHashOfPulledSnapshot()
    {
        var storage = new FakeSyncStorage();
        var a = PathsOf("a");
        CreateDb(a.DbV11File, "v11");
        Assert.Equal(SyncOutcome.Success, Push(ServiceOf("a"), storage).Outcome);
        var b = PathsOf("b");
        CreateDb(b.DbV11File, "old");
        SqliteConnection.ClearAllPools();

        var result = Pull(ServiceOf("b"), storage);

        // 要約を保存できなかったとき、取り出した写しの記録を残すのに要る。
        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.Equal(FileHasher.Sha256(b.DbV11File), result.SyncedSha256);
    }

    [Fact(DisplayName = "Push は本体の写しを送る直前と manifest に反映した直後に記録の呼び出しをする")]
    public void PushRecordsSentSnapshotAroundManifestUpdate()
    {
        var storage = new FakeSyncStorage();
        var a = PathsOf("a");
        CreateDb(a.DbV11File, "v11");
        SqliteConnection.ClearAllPools();
        var before = new List<(string Sha256, long? Planned)>();
        var after = new List<(string Sha256, long Version)>();
        var options = new PushOptions
        {
            Storage = storage,
            MachineName = "test",
            BeforeCommit = (sha256, _, planned) => before.Add((sha256, planned)),
            AfterCommit = (sha256, version) => after.Add((sha256, version)),
        };

        var result = ServiceOf("a").Push(options);

        // 送った写しの記録が無いと、送った後に要約を保存できなかったときに復旧できない。
        Assert.Equal(SyncOutcome.Success, result.Outcome);
        var sent = new ManifestStore(storage).Load().Tools[FriendConnectSyncService.Key].Files
            .Single(f => f.RelativePath == DbV11Key);
        Assert.Equal(new[] { (sent.Sha256, (long?)1) }, before);
        Assert.Equal(new[] { (sent.Sha256, 1L) }, after);

        // 変更が無い Push も、同期先の版が送ろうとした写しと同じ内容だと伝える。
        Assert.Equal(SyncOutcome.Success, ServiceOf("a").Push(new PushOptions
        {
            Storage = storage,
            MachineName = "test",
            LastPulledVersion = 1,
            BeforeCommit = options.BeforeCommit,
            AfterCommit = options.AfterCommit,
        }).Outcome);
        Assert.Equal(2, before.Count);
        Assert.Equal(new[] { (sent.Sha256, 1L), (sent.Sha256, 1L) }, after);
    }

    [Fact(DisplayName = "送る写しの記録を控えられなければ、Push は送る前に中止する")]
    public void PushAbortsWhenSentSnapshotCannotBeRecorded()
    {
        var storage = new FakeSyncStorage();
        var a = PathsOf("a");
        CreateDb(a.DbV11File, "v11");
        SqliteConnection.ClearAllPools();
        var notCommitted = 0;

        var result = ServiceOf("a").Push(new PushOptions
        {
            Storage = storage,
            MachineName = "test",
            BeforeCommit = (_, _, _) => throw new IOException("記録できません (テスト)"),
            NotCommitted = () => notCommitted++,
        });

        Assert.Equal(SyncOutcome.Aborted, result.Outcome);
        Assert.Contains("記録を控えられない", result.Message);
        Assert.False(new ManifestStore(storage).Load().Tools.ContainsKey(FriendConnectSyncService.Key));
        Assert.Equal(0, notCommitted);
    }

    [Fact(DisplayName = "記録を控えた後に送信で失敗したら、反映していないと伝える")]
    public void PushReportsNotCommittedWhenUploadFailsAfterRecording()
    {
        var storage = new FakeSyncStorage();
        var a = PathsOf("a");
        CreateDb(a.DbV11File, "v11");
        SqliteConnection.ClearAllPools();
        var recorded = 0;
        var notCommitted = 0;
        storage.FailNextCommit = true;

        Assert.Throws<SyncStorageException>(() => ServiceOf("a").Push(new PushOptions
        {
            Storage = storage,
            MachineName = "test",
            BeforeCommit = (_, _, _) => recorded++,
            NotCommitted = () => notCommitted++,
        }));

        Assert.Equal(1, recorded);
        Assert.Equal(1, notCommitted);
    }

    [Fact(DisplayName = "Pull は本体を差し替える前に前後のハッシュを渡し、残りで止まったら本体の版を伝える")]
    public void PullRecordsReplaceAndReportsDatabaseWhenRestFails()
    {
        var storage = new FakeSyncStorage();
        var a = PathsOf("a");
        CreateDb(a.DbV11File, "v11");
        WriteFile(a.ConfigJsonFile, "{\"a\":1}");
        Assert.Equal(SyncOutcome.Success, Push(ServiceOf("a"), storage).Outcome);
        var remoteSha256 = new ManifestStore(storage).Load().Tools[FriendConnectSyncService.Key].Files
            .Single(f => f.RelativePath == DbV11Key).Sha256;

        var b = PathsOf("b");
        CreateDb(b.DbV11File, "old");
        SqliteConnection.ClearAllPools();
        var localSha256 = FileHasher.Sha256(b.DbV11File);
        // 設定の場所にフォルダを置き、本体を差し替えた後の設定の差し替えで失敗させる。
        Directory.CreateDirectory(b.ConfigJsonFile);
        MergePendingState? pending = null;
        (long Version, string Sha256)? replaced = null;

        Assert.Throws<IOException>(() => ServiceOf("b").Pull(new PullOptions
        {
            Storage = storage,
            SkipBackup = true,
            BeforeReplaceLocal = state => pending = state,
            AfterDatabaseReplaced = (version, _, sha256) => replaced = (version, sha256),
        }));

        // 本体は取り出した版。次回に差し替わったかを見分けられるよう前後のハッシュを渡し、
        // 残りで止まったら本体の版を伝える。伝えないと、次の起動時 Pull が本体の違いを
        // 手元の変更と見て見送り続ける。
        Assert.Equal("v11", ReadDb(b.DbV11File));
        Assert.NotNull(pending);
        Assert.Equal(1, pending!.RemoteVersion);
        Assert.Equal(localSha256, pending.LocalSha256);
        Assert.Equal(remoteSha256, pending.MergedSha256);
        Assert.Equal((1L, remoteSha256), replaced);
    }

    [Fact(DisplayName = "本体が同期先と同じで設定だけの Pull が残りで止まっても、記録を残して本体の版を伝える")]
    public void PullRecordsReplaceWhenDatabaseUnchanged()
    {
        var storage = new FakeSyncStorage();
        var a = PathsOf("a");
        CreateDb(a.DbV11File, "v11");
        WriteFile(a.ConfigJsonFile, "{\"a\":1}");
        Assert.Equal(SyncOutcome.Success, Push(ServiceOf("a"), storage).Outcome);
        var b = PathsOf("b");
        CreateDb(b.DbV11File, "old");
        SqliteConnection.ClearAllPools();
        Assert.Equal(SyncOutcome.Success, Pull(ServiceOf("b"), storage).Outcome);
        // 同期先では設定だけが変わる。本体は同じなので差し替えない。
        WriteFile(a.ConfigJsonFile, "{\"a\":2}");
        Assert.Equal(SyncOutcome.Success, Push(ServiceOf("a"), storage, lastPulled: 1).Outcome);
        var remoteSha256 = new ManifestStore(storage).Load().Tools[FriendConnectSyncService.Key].Files
            .Single(f => f.RelativePath == DbV11Key).Sha256;
        File.Delete(b.ConfigJsonFile);
        Directory.CreateDirectory(b.ConfigJsonFile);
        MergePendingState? pending = null;
        (long Version, string Sha256)? replaced = null;

        Assert.Throws<IOException>(() => ServiceOf("b").Pull(new PullOptions
        {
            Storage = storage,
            SkipBackup = true,
            LastPulledVersion = 1,
            BeforeReplaceLocal = state => pending = state,
            AfterDatabaseReplaced = (version, _, sha256) => replaced = (version, sha256),
        }));

        // 本体は元から同期先の写しと同じなので、前後のハッシュは同じ値。残りで止まったら
        // 本体の版を伝える。記録が無いと、次の Push が前の版の設定を送る。
        Assert.NotNull(pending);
        Assert.Equal(2, pending!.RemoteVersion);
        Assert.Equal(remoteSha256, pending.LocalSha256);
        Assert.Equal(remoteSha256, pending.MergedSha256);
        Assert.True(pending.FromPull);
        Assert.Equal((2L, remoteSha256), replaced);
    }

    [Fact(DisplayName = "記録を残している間に本体が書き換えられたら、差し替える前に中止する")]
    public void PullAbortsWhenDatabaseChangesWhileRecordingReplace()
    {
        var storage = new FakeSyncStorage();
        var a = PathsOf("a");
        CreateDb(a.DbV11File, "v11");
        Assert.Equal(SyncOutcome.Success, Push(ServiceOf("a"), storage).Outcome);
        var b = PathsOf("b");
        CreateDb(b.DbV11File, "old");
        SqliteConnection.ClearAllPools();

        var result = ServiceOf("b").Pull(new PullOptions
        {
            Storage = storage,
            SkipBackup = true,
            // ハッシュを取って記録を残す間に Friend Connect が起動して書き込んだ状況。
            BeforeReplaceLocal = _ =>
            {
                CreateDb(b.DbV11File, new string('x', 16 * 1024));
                SqliteConnection.ClearAllPools();
            },
        });

        Assert.Equal(SyncOutcome.Aborted, result.Outcome);
        Assert.Contains("書き換えられた", result.Message);
        Assert.Equal("old", ReadDb(b.DbV11File));
    }

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
