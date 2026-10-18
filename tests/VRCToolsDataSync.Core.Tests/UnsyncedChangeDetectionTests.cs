using Microsoft.Data.Sqlite;
using VRCToolsDataSync.Core.Domain;
using VRCToolsDataSync.Core.Infra;
using VRCToolsDataSync.Core.Merge;
using VRCToolsDataSync.Core.UseCase;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// 起動時の自動 Pull が、手元に未 Push の変更がある場合は上書きしないことを確かめる。
/// 2 台の PC を、別々のデータフォルダと設定ファイルで再現する。
/// </summary>
public sealed class UnsyncedChangeDetectionTests : IDisposable
{
    private readonly string _root;
    private readonly FakeSyncStorage _storage = new();

    public UnsyncedChangeDetectionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vrctds-detect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>1 台ぶんの環境。設定ファイル、要約の置き場所、VRCX のデータを持つ。</summary>
    private sealed class Machine
    {
        public required SyncRunner Runner { get; init; }
        public required SyncSettings Settings { get; init; }
        public required VrcxSyncService Service { get; init; }
        public required VrcxPaths Paths { get; init; }
    }

    private Machine CreateMachine(string name)
    {
        var dir = Path.Combine(_root, name);
        var store = new SettingsStore(Path.Combine(dir, "settings.json"));
        var paths = new VrcxPaths(Path.Combine(dir, "VRCX"));
        Directory.CreateDirectory(paths.RootDirectory);
        return new Machine
        {
            Runner = new SyncRunner(store),
            Settings = new SyncSettings { MachineName = name },
            Service = new VrcxSyncService(paths, new LocalBackup(Path.Combine(dir, "backup"))),
            Paths = paths,
        };
    }

    private static void Exec(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long CountLocations(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM gamelog_location";
        return (long)command.ExecuteScalar()!;
    }

    private static void AddLocation(Machine machine, string createdAt)
        => Exec(machine.Paths.SqliteFile,
            $"INSERT INTO gamelog_location (created_at, location) VALUES ('{createdAt}', 'wrld_x:1')");

    private SyncResult Push(Machine machine) => machine.Runner.Push(machine.Service, machine.Settings, _storage, force: false);

    private SyncResult StartupPull(Machine machine)
        => machine.Runner.Pull(machine.Service, machine.Settings, _storage, skipBackup: false, skipIfNotNewer: true);

    /// <summary>
    /// A が作ったデータを B が取り込み、その後 A がもう一度 Push した状態を作る。
    /// B の次の起動時 Pull は、リモートが新しいので本来なら上書きに進む。
    /// </summary>
    private (Machine A, Machine B) ArrangeRemoteNewer()
    {
        var a = CreateMachine("a");
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            """);
        AddLocation(a, "2026-10-01T00:00:00Z");
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);

        var b = CreateMachine("b");
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);

        AddLocation(a, "2026-10-02T00:00:00Z");
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        return (a, b);
    }

    [Fact(DisplayName = "手元に未 Push の履歴があれば、起動時 Pull は上書きを見送る")]
    public void StartupPullKeepsUnsyncedLocalChanges()
    {
        var (_, b) = ArrangeRemoteNewer();
        AddLocation(b, "2026-10-03T00:00:00Z");

        var result = StartupPull(b);

        Assert.Equal(SyncOutcome.LocalChangesPending, result.Outcome);
        Assert.Contains("gamelog_location", result.Message);
        // B の手元は 1 件目 (取り込んだもの) と 3 件目 (手元で増えたもの) のまま。
        Assert.Equal(2, CountLocations(b.Paths.SqliteFile));
    }

    [Fact(DisplayName = "手元に変更が無ければ、起動時 Pull は従来どおり取り込む")]
    public void StartupPullProceedsWithoutLocalChanges()
    {
        var (_, b) = ArrangeRemoteNewer();

        var result = StartupPull(b);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.Equal(2, CountLocations(b.Paths.SqliteFile));
    }

    [Fact(DisplayName = "前回の要約が無ければ比べられないので、従来どおり取り込む")]
    public void StartupPullProceedsWithoutBaseline()
    {
        var (_, b) = ArrangeRemoteNewer();
        AddLocation(b, "2026-10-03T00:00:00Z");
        new BaseSummaryStore(Path.Combine(_root, "b", "base"))
            .Delete(SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key));

        var result = StartupPull(b);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
    }

    [Fact(DisplayName = "要約が同期履歴と別の version のものなら、比べずに従来どおり取り込む")]
    public void StartupPullIgnoresBaselineOfOtherVersion()
    {
        var (_, b) = ArrangeRemoteNewer();
        AddLocation(b, "2026-10-03T00:00:00Z");
        // 同期履歴を書いた後、要約を書く前に止まった状態を作る。
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var stale = store.Load(key)!;
        stale.RemoteVersion -= 1;
        store.Save(key, stale);

        var result = StartupPull(b);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
    }

    [Fact(DisplayName = "マージの後、要約だけ保存して止まっていても、手元の変更を守る")]
    public void StartupPullComparesWithNewerBaseline()
    {
        var (_, b) = ArrangeRemoteNewer();
        AddLocation(b, "2026-10-03T00:00:00Z");
        // マージ結果で置き換え、要約を保存した後、同期履歴を保存する前に止まった状態を作る。
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var newer = store.Load(key)!;
        newer.RemoteVersion += 1;
        store.Save(key, newer);

        var result = StartupPull(b);

        Assert.Equal(SyncOutcome.LocalChangesPending, result.Outcome);
        Assert.Equal(2, CountLocations(b.Paths.SqliteFile));
    }

    [Fact(DisplayName = "明示的な Pull は、手元に変更があっても上書きする")]
    public void ExplicitPullStillOverwrites()
    {
        var (_, b) = ArrangeRemoteNewer();
        AddLocation(b, "2026-10-03T00:00:00Z");

        var result = b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.Equal(2, CountLocations(b.Paths.SqliteFile));
        Assert.NotNull(result.BackupPath);
    }

    [Fact(DisplayName = "Push の後は、その時点が次の比較の基準になる")]
    public void PushResetsBaseline()
    {
        var (a, _) = ArrangeRemoteNewer();

        // A は直前に Push しているので、手元に未 Push の変更は無い。
        var baseline = new BaseSummaryStore(Path.Combine(_root, "a", "base"))
            .Load(SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key));
        Assert.NotNull(baseline);
        var current = DatabaseSummary.Compute(a.Paths.SqliteFile, MergeProfiles.Vrcx, out _);
        Assert.Empty(current!.FindChangedTables(baseline!));
    }

    [Fact(DisplayName = "マージが有効なら、起動時 Pull は手元の変更を残したまま取り込み、Push まで済ませる")]
    public void StartupPullMergesWhenEnabled()
    {
        var (a, b) = ArrangeRemoteNewer();
        b.Settings.MergeVrcx = true;
        AddLocation(b, "2026-10-03T00:00:00Z");

        var result = StartupPull(b);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.NotNull(result.MergeReport);
        Assert.Equal(3, CountLocations(b.Paths.SqliteFile));

        // マージ結果は同期先に載っているので、A は普通の Pull で 3 件を受け取る。
        var pulled = a.Runner.Pull(a.Service, a.Settings, _storage, skipBackup: false, skipIfNotNewer: true);
        Assert.Equal(SyncOutcome.Success, pulled.Outcome);
        Assert.Equal(3, CountLocations(a.Paths.SqliteFile));
    }

    [Fact(DisplayName = "マージが有効なら、リモートの方が新しくて止まった Push はマージしてから送る")]
    public void ConflictingPushMergesWhenEnabled()
    {
        var (_, b) = ArrangeRemoteNewer();
        b.Settings.MergeVrcx = true;
        AddLocation(b, "2026-10-03T00:00:00Z");

        var result = Push(b);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.Equal(3, CountLocations(b.Paths.SqliteFile));
        // Push の後は要約が取り直され、手元に未 Push の変更は残っていない。
        Assert.Equal(SyncOutcome.NothingToDo, StartupPull(b).Outcome);
    }

    [Fact(DisplayName = "マージ後の Push が失敗しても、次のマージは土台にした版を祖先にする")]
    public void FailedPushAfterMergeKeepsMergedBaseAsAncestor()
    {
        var (a, b) = ArrangeRemoteNewer();
        // 祖先 (B が取り込んだ版) から、A がメモを書いて Push する。
        Exec(a.Paths.SqliteFile, "INSERT INTO memos VALUES ('usr_m', '2026-10-02T00:00:00Z', 'm1')");
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);

        // B は手元に履歴を足してマージする。マージ後の Push は通信の失敗で例外になる。
        b.Settings.MergeVrcx = true;
        AddLocation(b, "2026-10-03T00:00:00Z");
        _storage.OnBeginUpload = () => throw new IOException("network down");
        Assert.ThrowsAny<IOException>(() => b.Runner.Merge(b.Service, b.Settings, _storage));
        _storage.OnBeginUpload = null;

        // その間に A がメモを消して Push する。
        Exec(a.Paths.SqliteFile, "DELETE FROM memos WHERE user_id = 'usr_m'");
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);

        // 同期履歴の保存は設定ファイルの値で上書きする。テストでは設定ファイルに
        // 書いていないので、マージの有効化をもう一度行う。
        b.Settings.MergeVrcx = true;
        var result = StartupPull(b);

        // B のメモはマージで取り込んだ A の内容のままで、B が編集したものではない。
        // 祖先が古いままだと B の編集に見え、A の削除が取り消される。
        Assert.True(result.Outcome == SyncOutcome.Success, result.Message);
        Assert.Equal(0, CountRows(b.Paths.SqliteFile, "memos"));
        Assert.Equal(3, CountLocations(b.Paths.SqliteFile));
    }

    private static long CountRows(string path, string table)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table}";
        return (long)command.ExecuteScalar()!;
    }

    [Fact(DisplayName = "競合した Push をマージできなければ、元の競合として返す")]
    public void ConflictingPushReturnsConflictWhenMergeAborts()
    {
        var (_, b) = ArrangeRemoteNewer();
        b.Settings.MergeVrcx = true;
        // 手元だけ列が違うので、マージは中止になる。
        Exec(b.Paths.SqliteFile, "ALTER TABLE memos ADD COLUMN extra TEXT");
        AddLocation(b, "2026-10-03T00:00:00Z");

        var result = Push(b);

        Assert.Equal(SyncOutcome.ConflictDetected, result.Outcome);
        Assert.Contains("自動マージはできませんでした", result.Message);
        Assert.Equal(2, CountLocations(b.Paths.SqliteFile));
    }

    [Fact(DisplayName = "マージが無効なら、Push の競合はこれまでどおり利用者に返す")]
    public void ConflictingPushWithoutMerge()
    {
        var (_, b) = ArrangeRemoteNewer();
        AddLocation(b, "2026-10-03T00:00:00Z");

        Assert.Equal(SyncOutcome.ConflictDetected, Push(b).Outcome);
    }

    [Fact(DisplayName = "dry-run のマージは、手元にも同期先にも書かない")]
    public void DryRunMergeWritesNothing()
    {
        var (_, b) = ArrangeRemoteNewer();
        AddLocation(b, "2026-10-03T00:00:00Z");
        var remoteVersion = new ManifestStore(_storage).Load().Tools[VrcxSyncService.Key].Version;

        var result = b.Runner.Merge(b.Service, b.Settings, _storage, dryRun: true);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.Equal(1, result.MergeReport!.ChangedRows);
        Assert.Equal(2, CountLocations(b.Paths.SqliteFile));
        Assert.Equal(remoteVersion, new ManifestStore(_storage).Load().Tools[VrcxSyncService.Key].Version);
    }

    [Fact(DisplayName = "別のプロセスがマージ中なら、マージを見送って手元に書かない")]
    public void MergeWaitsForOtherProcess()
    {
        var (_, b) = ArrangeRemoteNewer();
        AddLocation(b, "2026-10-03T00:00:00Z");
        var root = Path.Combine(_root, "b", "base");
        Directory.CreateDirectory(root);
        var previous = SyncRunner.MergeLockTimeout;
        SyncRunner.MergeLockTimeout = TimeSpan.FromMilliseconds(100);
        try
        {
            using (new FileStream(Path.Combine(root, $"merge-{VrcxSyncService.Key}.lock"),
                       FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var result = b.Runner.Merge(b.Service, b.Settings, _storage);

                Assert.Equal(SyncOutcome.Aborted, result.Outcome);
                Assert.Equal(2, CountLocations(b.Paths.SqliteFile));
            }
        }
        finally
        {
            SyncRunner.MergeLockTimeout = previous;
        }
    }

    [Fact(DisplayName = "別のプロセスがマージ中なら、Pull も見送って手元を置き換えない")]
    public void PullWaitsForOtherProcess()
    {
        var (_, b) = ArrangeRemoteNewer();
        var root = Path.Combine(_root, "b", "base");
        Directory.CreateDirectory(root);
        var before = CountLocations(b.Paths.SqliteFile);
        var previous = SyncRunner.MergeLockTimeout;
        SyncRunner.MergeLockTimeout = TimeSpan.FromMilliseconds(100);
        try
        {
            using (new FileStream(Path.Combine(root, $"merge-{VrcxSyncService.Key}.lock"),
                       FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var result = b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: true);

                Assert.Equal(SyncOutcome.Aborted, result.Outcome);
                Assert.Equal(before, CountLocations(b.Paths.SqliteFile));
            }
        }
        finally
        {
            SyncRunner.MergeLockTimeout = previous;
        }
    }

    [Fact(DisplayName = "別のプロセスがマージ中なら、Push も見送る")]
    public void PushWaitsForOtherProcess()
    {
        var (_, b) = ArrangeRemoteNewer();
        var root = Path.Combine(_root, "b", "base");
        Directory.CreateDirectory(root);
        var remoteVersion = new ManifestStore(_storage).Load().Tools[VrcxSyncService.Key].Version;
        var previous = SyncRunner.MergeLockTimeout;
        SyncRunner.MergeLockTimeout = TimeSpan.FromMilliseconds(100);
        try
        {
            using (new FileStream(Path.Combine(root, $"merge-{VrcxSyncService.Key}.lock"),
                       FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var result = Push(b);

                Assert.Equal(SyncOutcome.Aborted, result.Outcome);
                Assert.Equal(remoteVersion, new ManifestStore(_storage).Load().Tools[VrcxSyncService.Key].Version);
            }
        }
        finally
        {
            SyncRunner.MergeLockTimeout = previous;
        }
    }

    [Fact(DisplayName = "マージの印が残っていれば、要約が無くても起動時 Pull は上書きを見送る")]
    public void StartupPullKeepsLocalWhenMergePending()
    {
        var (_, b) = ArrangeRemoteNewer();
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        store.Delete(key);
        store.MarkMergePending(key);

        var result = StartupPull(b);

        Assert.Equal(SyncOutcome.LocalChangesPending, result.Outcome);
    }

    [Fact(DisplayName = "マージが済めば、マージの印は残らない")]
    public void MergeClearsPendingMark()
    {
        var (_, b) = ArrangeRemoteNewer();
        AddLocation(b, "2026-10-03T00:00:00Z");

        var result = b.Runner.Merge(b.Service, b.Settings, _storage);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        Assert.False(store.IsMergePending(SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key)));
    }
}
