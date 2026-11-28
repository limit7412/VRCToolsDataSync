using Microsoft.Data.Sqlite;
using VRCToolsDataSync.Core.Domain;
using VRCToolsDataSync.Core.Infra;
using VRCToolsDataSync.Core.Merge;
using VRCToolsDataSync.Core.UseCase;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// Pull が本体 DB を差し替えるときの WAL/SHM の扱いを固定する。差し替える前に WAL/SHM を
/// 消すと、差し替えに失敗したときに本体は古いまま WAL にしか無い変更だけが失われる。
/// そこで脇へ退けておき、本体を差し替えられなければ戻し、差し替えが済めば消す。
/// </summary>
public sealed class PullSidecarsTests : IDisposable
{
    private const string Wal = "wal-bytes";
    private const string Shm = "shm-bytes";

    private readonly string _root;

    public PullSidecarsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vrctds-sidecars-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // 接続プールが SQLite のファイルを掴んだままだと消せない。
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private VrcxPaths VrcxPathsOf(string machine) => new(Path.Combine(_root, machine, "vrcx"));

    private VrcxSyncService VrcxOf(string machine)
        => new(VrcxPathsOf(machine), new LocalBackup(Path.Combine(_root, machine, "backup")));

    private FriendConnectPaths FriendConnectPathsOf(string machine) => new(Path.Combine(_root, machine, "fc"));

    private FriendConnectSyncService FriendConnectOf(string machine)
        => new(FriendConnectPathsOf(machine), new LocalBackup(Path.Combine(_root, machine, "backup")));

    /// <summary>A が Push した VRCX のデータと、それと違う DB に WAL/SHM が付いた B を用意する。</summary>
    private (VrcxPaths B, FakeSyncStorage Storage) ArrangeVrcx()
    {
        var storage = new FakeSyncStorage();
        var a = VrcxPathsOf("a");
        CreateDb(a.SqliteFile, "remote");
        File.WriteAllText(a.SettingsJsonFile, "{\"a\":1}");
        Assert.Equal(SyncOutcome.Success, Push(VrcxOf("a"), storage).Outcome);

        var b = VrcxPathsOf("b");
        CreateDb(b.SqliteFile, "local");
        File.WriteAllText(b.SettingsJsonFile, "{\"b\":1}");
        SqliteConnection.ClearAllPools();
        // 中身は本物の WAL でなくてよい。Pull は手元の DB を SQLite として開かないので、
        // バイト列がそのまま残るか、消えるかだけを見る。
        File.WriteAllText(b.SqliteFile + "-wal", Wal);
        File.WriteAllText(b.SqliteFile + "-shm", Shm);
        return (b, storage);
    }

    [Fact(DisplayName = "VRCX の Pull が本体の差し替えに失敗したら、WAL と SHM を元に戻す")]
    public void VrcxPullRestoresSidecarsWhenDatabaseReplaceFails()
    {
        var (b, storage) = ArrangeVrcx();
        var before = File.ReadAllBytes(b.SqliteFile);

        using (var locked = LockAgainstReplace(b.SqliteFile))
        {
            if (locked is null) return; // この環境では差し替えを失敗させられない。
            Assert.ThrowsAny<IOException>(() => Pull(VrcxOf("b"), storage));
        }

        // 本体は元のままなので、WAL にしか無い変更も元のまま残る。
        Assert.Equal(before, File.ReadAllBytes(b.SqliteFile));
        Assert.Equal(Wal, File.ReadAllText(b.SqliteFile + "-wal"));
        Assert.Equal(Shm, File.ReadAllText(b.SqliteFile + "-shm"));
        Assert.Empty(AsideFilesIn(b.RootDirectory));
    }

    [Fact(DisplayName = "VRCX の Pull が本体を差し替えた後に止まったら、古い WAL は戻さない")]
    public void VrcxPullDiscardsSidecarsWhenFailingAfterDatabaseReplaced()
    {
        var (b, storage) = ArrangeVrcx();
        // 設定の場所にフォルダを置き、本体を差し替えた後の設定の差し替えで失敗させる。
        File.Delete(b.SettingsJsonFile);
        Directory.CreateDirectory(b.SettingsJsonFile);

        Assert.ThrowsAny<IOException>(() => Pull(VrcxOf("b"), storage));

        // 本体は取り出した版。古い WAL を戻すと、別内容の本体に対して再生されて壊れる。
        Assert.Equal("remote", ReadDb(b.SqliteFile));
        Assert.False(File.Exists(b.SqliteFile + "-wal"));
        Assert.False(File.Exists(b.SqliteFile + "-shm"));
        Assert.Empty(AsideFilesIn(b.RootDirectory));
    }

    [Fact(DisplayName = "VRCX の Pull が済めば WAL と SHM は消え、退けたファイルも残らない")]
    public void VrcxPullRemovesSidecarsOnSuccess()
    {
        var (b, storage) = ArrangeVrcx();
        MergePendingState? pending = null;

        var result = VrcxOf("b").Pull(new PullOptions
        {
            Storage = storage,
            SkipBackup = true,
            BeforeReplaceLocal = state => pending = state,
        });

        Assert.True(result.Outcome == SyncOutcome.Success, result.Message);
        Assert.False(File.Exists(b.SqliteFile + "-wal"));
        Assert.False(File.Exists(b.SqliteFile + "-shm"));
        Assert.Empty(AsideFilesIn(b.RootDirectory));
        Assert.Equal("remote", ReadDb(b.SqliteFile));
        // 退けた後にプロセスごと止まっても次回に探せるよう、退ける名前を記録に残す。
        Assert.NotNull(pending?.AsideSuffix);
    }

    [Fact(DisplayName = "Friend Connect の Pull が本体の差し替えに失敗したら、WAL と SHM を元に戻す")]
    public void FriendConnectPullRestoresSidecarsWhenDatabaseReplaceFails()
    {
        var storage = new FakeSyncStorage();
        var a = FriendConnectPathsOf("a");
        CreateDb(a.DbV11File, "remote");
        Assert.Equal(SyncOutcome.Success, Push(FriendConnectOf("a"), storage).Outcome);

        var b = FriendConnectPathsOf("b");
        CreateDb(b.DbV11File, "local");
        SqliteConnection.ClearAllPools();
        File.WriteAllText(b.DbV11File + "-wal", Wal);
        File.WriteAllText(b.DbV11File + "-shm", Shm);
        var before = File.ReadAllBytes(b.DbV11File);

        using (var locked = LockAgainstReplace(b.DbV11File))
        {
            if (locked is null) return; // この環境では差し替えを失敗させられない。
            Assert.ThrowsAny<IOException>(() => Pull(FriendConnectOf("b"), storage));
        }

        Assert.Equal(before, File.ReadAllBytes(b.DbV11File));
        Assert.Equal(Wal, File.ReadAllText(b.DbV11File + "-wal"));
        Assert.Equal(Shm, File.ReadAllText(b.DbV11File + "-shm"));
        Assert.Empty(AsideFilesIn(b.DbDirectory));
    }

    private static string[] AsideFilesIn(string directory)
        => Directory.GetFiles(directory, "*.vrctds-aside-*");

    /// <summary>
    /// ファイルを差し替えられない状態にする。Windows は読み取りだけ共有して開いたままにする
    /// (ハッシュを取る読み取りは通り、File.Replace は共有違反になる)。ほかの環境では null。
    /// </summary>
    private static IDisposable? LockAgainstReplace(string path)
        => OperatingSystem.IsWindows()
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            : null;

    private static void CreateDb(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
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
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT v FROM t";
        return (string)command.ExecuteScalar()!;
    }

    private static SyncResult Push(ISyncService service, ISyncStorage storage)
        => service.Push(new PushOptions
        {
            Storage = storage,
            MachineName = "test",
        });

    private static SyncResult Pull(ISyncService service, ISyncStorage storage)
        => service.Pull(new PullOptions
        {
            Storage = storage,
            SkipBackup = true,
        });
}
