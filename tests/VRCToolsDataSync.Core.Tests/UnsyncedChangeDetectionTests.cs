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

    private static long Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
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

    /// <summary>
    /// B がマージ結果でローカルを置き換えた直後に止まった状態を作る。B の祖先と同期履歴は
    /// 置き換える前の版のまま、印には置き換え後の要約と本体のハッシュが残っている。
    /// その後 A が、B が取り込んだ行を消して Push する。
    /// </summary>
    private (Machine A, Machine B, BaseSummaryStore Store, string Key) ArrangeInterruptedAfterReplace()
    {
        var (a, b) = ArrangeRemoteNewer();
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var before = store.Load(key)!;
        var pulledBefore = b.Settings.ToolState[key].LastPulledVersion;

        Assert.Equal(SyncOutcome.Success, b.Runner.Merge(b.Service, b.Settings, _storage).Outcome);
        var after = store.Load(key)!;
        store.SavePendingReplace(key, new MergePendingState(after, after.RemoteVersion, "0", FileHasher.Sha256(b.Paths.SqliteFile)));
        store.Save(key, before);
        b.Settings.ToolState[key].LastPulledVersion = pulledBefore;

        // B のマージは Push まで行うので、A は一度取り込んでから消して Push する。
        Assert.Equal(SyncOutcome.Success, a.Runner.Pull(a.Service, a.Settings, _storage, skipBackup: false).Outcome);
        Exec(a.Paths.SqliteFile, "DELETE FROM gamelog_location WHERE created_at = '2026-10-02T00:00:00Z'");
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        return (a, b, store, key);
    }

    [Fact(DisplayName = "置き換えた後に止まったマージの祖先を復旧し、取り込んだ行を手元の追加と見ない")]
    public void MergeRecoversBaselineAfterInterruptedReplace()
    {
        var (_, b, store, key) = ArrangeInterruptedAfterReplace();

        var result = b.Runner.Merge(b.Service, b.Settings, _storage);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        // 復旧した祖先で比べれば、A が消した行は手元の追加ではないので戻らない。
        Assert.Equal(1, CountLocations(b.Paths.SqliteFile));
        Assert.False(store.IsMergePending(key));
    }

    [Fact(DisplayName = "置き換える前に止まったマージの記録は、祖先を変えずに消す")]
    public void MergeClearsRecordWhenReplaceDidNotHappen()
    {
        var (_, b) = ArrangeRemoteNewer();
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        AddLocation(b, "2026-10-03T00:00:00Z");
        store.SavePendingReplace(key, new MergePendingState(
            new DatabaseSummary { RemoteVersion = 999 }, 999, FileHasher.Sha256(b.Paths.SqliteFile), "0"));

        var result = b.Runner.Merge(b.Service, b.Settings, _storage);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.Equal(3, CountLocations(b.Paths.SqliteFile));
        Assert.False(store.IsMergePending(key));
        Assert.NotEqual(999, store.Load(key)!.RemoteVersion);
    }

    /// <summary>
    /// B の本体に書き戻されていない行 (2026-10-03) を持つ WAL を、マージが退けた名前に置き、
    /// 置き換える前に止まった記録を残す。<paramref name="pendingSummary"/> が null なら
    /// 保存済みの要約と同じ版への記録にする。退けた名前の末尾を返す。
    /// </summary>
    private string ArrangeAsideWal(Machine b, BaseSummaryStore store, string key, DatabaseSummary? pendingSummary, bool mergedSameAsLocal = false)
    {
        var db = b.Paths.SqliteFile;
        // WAL を読んでいる別の接続を開いたままにしておくと、書いた接続を閉じても書き戻されない。
        var frozen = Path.Combine(_root, "frozen.sqlite3");
        var asideWal = Path.Combine(_root, "aside-wal");
        Exec(db, "PRAGMA journal_mode=WAL");
        using (var holder = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString()))
        {
            holder.Open();
            using (var read = holder.CreateCommand())
            {
                read.CommandText = "SELECT count(*) FROM gamelog_location";
                read.ExecuteScalar();
            }
            File.Copy(db, frozen, overwrite: true);
            Exec(db, "INSERT INTO gamelog_location (created_at, location) VALUES ('2026-10-03T00:00:00Z', 'wrld_x:1')");
            File.Copy(db + "-wal", asideWal, overwrite: true);
        }
        File.Copy(frozen, db, overwrite: true);
        const string suffix = ".vrctds-aside-test";
        File.Move(asideWal, db + "-wal" + suffix, overwrite: true);
        var summary = pendingSummary ?? store.Load(key)!;
        var localSha256 = FileHasher.Sha256(db);
        store.SavePendingReplace(key, new MergePendingState(
            summary, summary.RemoteVersion, localSha256, mergedSameAsLocal ? localSha256 : "0", suffix));
        return suffix;
    }

    [Fact(DisplayName = "VRCX を閉じた後の WAL 方式の DB (WAL ファイル無し) でもマージできる")]
    public void MergesWalModeDatabaseWithoutWalFile()
    {
        var (_, b) = ArrangeRemoteNewer();
        var db = b.Paths.SqliteFile;
        // 接続を閉じると WAL は本体へ書き戻されて消え、本体に WAL 方式の印だけが残る。
        Exec(db, "PRAGMA journal_mode=WAL");
        Assert.False(File.Exists(db + "-wal"));

        var result = b.Runner.Merge(b.Service, b.Settings, _storage);

        // 写しを取る読み取りで空の WAL ができるが、それを VRCX の書き込みとは見ない。
        Assert.True(result.Outcome == SyncOutcome.Success, result.Message);
        Assert.Equal(2, CountLocations(db));
    }

    [Fact(DisplayName = "置き換えの前後で本体が同じ内容なら、退けた WAL を戻さずに置き換え後として済ませる")]
    public void MergeDoesNotRestoreAsideWalWhenMergedEqualsLocal()
    {
        var (_, b) = ArrangeRemoteNewer();
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var db = b.Paths.SqliteFile;
        var suffix = ArrangeAsideWal(b, store, key, pendingSummary: null, mergedSameAsLocal: true);

        var result = b.Runner.Merge(b.Service, b.Settings, _storage);

        Assert.True(result.Outcome == SyncOutcome.Success, result.Message);
        // 退けた WAL の行は戻らず、同期先の 2 行だけになる。
        Assert.Equal(2, CountLocations(db));
        Assert.False(File.Exists(db + "-wal" + suffix));
        Assert.False(store.IsMergePending(key));
    }

    [Fact(DisplayName = "WAL を退けた後に置き換える前で止まっていれば、退けた WAL を戻してからマージする")]
    public void MergeRestoresAsideWalWhenReplaceDidNotHappen()
    {
        var (_, b) = ArrangeRemoteNewer();
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var db = b.Paths.SqliteFile;
        var suffix = ArrangeAsideWal(b, store, key, new DatabaseSummary { RemoteVersion = 999 });

        var result = b.Runner.Merge(b.Service, b.Settings, _storage);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        // 戻した WAL の行が取り込まれ、同期先の 2 行と合わせて 3 行になる。
        Assert.Equal(3, CountLocations(db));
        Assert.False(File.Exists(db + "-wal" + suffix));
        Assert.False(store.IsMergePending(key));
    }

    [Fact(DisplayName = "保存済みと同じ版へのマージが置き換える前で止まっていても、退けた WAL を戻す")]
    public void MergeRestoresAsideWalEvenWhenVersionAlreadySaved()
    {
        var (_, b) = ArrangeRemoteNewer();
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var db = b.Paths.SqliteFile;
        var suffix = ArrangeAsideWal(b, store, key, pendingSummary: null);

        var result = b.Runner.Merge(b.Service, b.Settings, _storage);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.Equal(3, CountLocations(db));
        Assert.False(File.Exists(db + "-wal" + suffix));
        Assert.False(store.IsMergePending(key));
    }

    [Fact(DisplayName = "退けた WAL を戻す先に別の WAL があれば、印を残して中止する")]
    public void MergeAbortsWhenAsideWalCannotBeRestored()
    {
        var (_, b) = ArrangeRemoteNewer();
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var db = b.Paths.SqliteFile;
        var suffix = ArrangeAsideWal(b, store, key, new DatabaseSummary { RemoteVersion = 999 });
        File.WriteAllBytes(db + "-wal", Array.Empty<byte>());

        var result = b.Runner.Merge(b.Service, b.Settings, _storage);

        Assert.Equal(SyncOutcome.Aborted, result.Outcome);
        Assert.Contains("退けた WAL", result.Message);
        Assert.True(File.Exists(db + "-wal" + suffix));
        Assert.True(store.IsMergePending(key));
    }

    [Fact(DisplayName = "記録の無い印は置き換える前の印なので、置き換える前に中止するマージでも消す")]
    public void MergeClearsBareMarkWhenAbortingBeforeReplace()
    {
        var (_, b) = ArrangeRemoteNewer();
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        store.MarkMergePending(key);
        // 手元で履歴を消しているので、マージは置き換える前に中止する。
        Exec(b.Paths.SqliteFile, "DELETE FROM gamelog_location WHERE created_at = '2026-10-01T00:00:00Z'");

        var result = b.Runner.Merge(b.Service, b.Settings, _storage);

        Assert.Equal(SyncOutcome.Aborted, result.Outcome);
        Assert.False(store.IsMergePending(key));
    }

    [Fact(DisplayName = "Push の後に同期履歴を保存する前に止まっても、送った内容との再マージで累計を二重に足さない")]
    public void RemergeAfterInterruptedPushDoesNotDoubleCount()
    {
        var a = CreateMachine("a");
        a.Settings.MergeVrcx = true;
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            CREATE TABLE usr0123abcd_avatar_history (avatar_id TEXT PRIMARY KEY, created_at TEXT, time INTEGER);
            INSERT INTO usr0123abcd_avatar_history VALUES ('avtr_a', '2026-10-01T00:00:00Z', 100);
            """);
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "a", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var before = store.Load(key)!;
        var pulledBefore = a.Settings.ToolState[key].LastPulledVersion;
        Exec(a.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 150");
        var pushed = Push(a);
        Assert.Equal(SyncOutcome.Success, pushed.Outcome);
        RewindToInterruptedPush(a, store, key, before, pulledBefore, pushed.RemoteVersion!.Value);

        var result = Push(a);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.Equal(150, Scalar(a.Paths.SqliteFile, "SELECT time FROM usr0123abcd_avatar_history"));
    }

    [Fact(DisplayName = "送った後に止まり、別の PC がその上に版を進めても、送った変更を二重に足さない")]
    public void RemergeAfterInterruptedPushUsesPushedSummaryForLaterVersion()
    {
        var a = CreateMachine("a");
        a.Settings.MergeVrcx = true;
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            CREATE TABLE usr0123abcd_avatar_history (avatar_id TEXT PRIMARY KEY, created_at TEXT, time INTEGER);
            INSERT INTO usr0123abcd_avatar_history VALUES ('avtr_a', '2026-10-01T00:00:00Z', 100);
            """);
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "a", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var before = store.Load(key)!;
        var pulledBefore = a.Settings.ToolState[key].LastPulledVersion;
        Exec(a.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 150");
        var pushed = Push(a);
        Assert.Equal(SyncOutcome.Success, pushed.Outcome);
        RewindToInterruptedPush(a, store, key, before, pulledBefore, pushed.RemoteVersion!.Value);

        // 別の PC が A の送った内容を取り込み、着用時間を 20 足して次の版を送る。
        var b = CreateMachine("b");
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        Exec(b.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 170");
        Assert.Equal(SyncOutcome.Success, Push(b).Outcome);

        var result = Push(a);

        Assert.True(result.Outcome == SyncOutcome.Success, result.Message);
        // 祖先を送る前の 100 にすると、A の +50 と B の +20 を別々の増分と見て 220 になる。
        Assert.Equal(170, Scalar(a.Paths.SqliteFile, "SELECT time FROM usr0123abcd_avatar_history"));
    }

    [Fact(DisplayName = "送った版の後に別の PC が強制 Push で上書きしていれば、送った写しを祖先にせず中止する")]
    public void MergeAbortsWhenLaterVersionWasForcedAfterOwnPush()
    {
        var a = CreateMachine("a");
        a.Settings.MergeVrcx = true;
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            CREATE TABLE usr0123abcd_avatar_history (avatar_id TEXT PRIMARY KEY, created_at TEXT, time INTEGER);
            INSERT INTO usr0123abcd_avatar_history VALUES ('avtr_a', '2026-10-01T00:00:00Z', 100);
            """);
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var b = CreateMachine("b");
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "a", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var before = store.Load(key)!;
        var pulledBefore = a.Settings.ToolState[key].LastPulledVersion;
        Exec(a.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 150");
        var pushed = Push(a);
        Assert.Equal(SyncOutcome.Success, pushed.Outcome);
        RewindToInterruptedPush(a, store, key, before, pulledBefore, pushed.RemoteVersion!.Value);

        // B は A の送った内容を取り込まずに、祖先由来の 120 を強制 Push で上書きする。
        Exec(b.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 120");
        Assert.Equal(SyncOutcome.Success, b.Runner.Push(b.Service, b.Settings, _storage, force: true).Outcome);

        var result = Push(a);

        Assert.Equal(SyncOutcome.ConflictDetected, result.Outcome);
        Assert.Contains("強制 Push", result.Message);
        Assert.Equal(150, Scalar(a.Paths.SqliteFile, "SELECT time FROM usr0123abcd_avatar_history"));
    }

    [Fact(DisplayName = "Pull の取得中に VRCX が DB を書き換えていれば、差し替える前に中止する")]
    public void PullAbortsWhenDatabaseChangedDuringFetch()
    {
        var (_, b) = ArrangeRemoteNewer();
        var db = b.Paths.SqliteFile;
        _storage.OnDownload = _ => AddLocation(b, "2026-10-03T00:00:00Z");

        var result = b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false);

        Assert.Equal(SyncOutcome.Aborted, result.Outcome);
        Assert.Contains("書き換えられた", result.Message);
        // 取得中に足した行は残り、同期先の 2 行目は取り込まれていない。
        Assert.Equal(1, Scalar(db, "SELECT count(*) FROM gamelog_location WHERE created_at = '2026-10-03T00:00:00Z'"));
        Assert.Equal(0, Scalar(db, "SELECT count(*) FROM gamelog_location WHERE created_at = '2026-10-02T00:00:00Z'"));
        Assert.Empty(Directory.GetFiles(b.Paths.RootDirectory, "*" + PullStaging.IncomingMarker + "*").Select(Path.GetFileName));
    }

    [Fact(DisplayName = "反映されたか分からない送信の後に同期先が予定の版を越えていれば、マージを中止する")]
    public void MergeAbortsWhenUncommittedPushMayBeIncludedInLaterVersion()
    {
        var a = CreateMachine("a");
        a.Settings.MergeVrcx = true;
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            CREATE TABLE usr0123abcd_avatar_history (avatar_id TEXT PRIMARY KEY, created_at TEXT, time INTEGER);
            INSERT INTO usr0123abcd_avatar_history VALUES ('avtr_a', '2026-10-01T00:00:00Z', 100);
            """);
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "a", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var before = store.Load(key)!;
        var pulledBefore = a.Settings.ToolState[key].LastPulledVersion;
        Exec(a.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 150");
        var pushed = Push(a);
        Assert.Equal(SyncOutcome.Success, pushed.Outcome);
        RewindToInterruptedPush(a, store, key, before, pulledBefore, pushed.RemoteVersion!.Value, committed: false);

        var b = CreateMachine("b");
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        Exec(b.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 170");
        Assert.Equal(SyncOutcome.Success, Push(b).Outcome);

        var result = Push(a);

        Assert.Equal(SyncOutcome.ConflictDetected, result.Outcome);
        Assert.Contains("反映されたか分からない", result.Message);
        Assert.Equal(150, Scalar(a.Paths.SqliteFile, "SELECT time FROM usr0123abcd_avatar_history"));
    }

    [Fact(DisplayName = "取る予定だった版を別の PC が取っていれば、送信の記録を捨てて古い要約でマージする")]
    public void MergeDiscardsUncommittedPushWhenPlannedVersionWasTakenByOthers()
    {
        var a = CreateMachine("a");
        a.Settings.MergeVrcx = true;
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            CREATE TABLE usr0123abcd_avatar_history (avatar_id TEXT PRIMARY KEY, created_at TEXT, time INTEGER);
            INSERT INTO usr0123abcd_avatar_history VALUES ('avtr_a', '2026-10-01T00:00:00Z', 100);
            """);
        var first = Push(a);
        Assert.Equal(SyncOutcome.Success, first.Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "a", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        Exec(a.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 150");
        // 送ろうとして反映できないまま止まった記録。写しのハッシュは同期先のどれとも違う。
        store.SavePushed(key, new PushedRecord("0", store.Load(key), PlannedVersion: first.RemoteVersion!.Value + 1));

        var b = CreateMachine("b");
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        Exec(b.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 120");
        Assert.Equal(SyncOutcome.Success, Push(b).Outcome);

        var result = Push(a);

        Assert.True(result.Outcome == SyncOutcome.Success, result.Message);
        // 祖先は送る前の 100 のまま。A の +50 と B の +20 を足して 170 になる。
        Assert.Equal(170, Scalar(a.Paths.SqliteFile, "SELECT time FROM usr0123abcd_avatar_history"));
        Assert.Null(store.LoadPushed(key));
    }

    [Fact(DisplayName = "送信そのものが失敗すれば、反映していない送信の記録を残さない")]
    public void PushDiscardsPushedRecordWhenCommitFails()
    {
        var (a, _) = ArrangeRemoteNewer();
        var store = new BaseSummaryStore(Path.Combine(_root, "a", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        AddLocation(a, "2026-10-02T12:00:00Z");
        _storage.FailNextCommit = true;

        Assert.Throws<SyncStorageException>(() => Push(a));

        // 記録が残ると、同期先が進んだときに反映されたか分からない記録として扱われ、マージが止まる。
        Assert.Null(store.LoadPushed(key));
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
    }

    [Fact(DisplayName = "非常用の強制 Push も取る予定の版を控える")]
    public void ForcedPushRecordsPlannedVersion()
    {
        var (a, _) = ArrangeRemoteNewer();
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var remoteVersion = _storage.LoadManifest().Manifest.Tools[VrcxSyncService.Key].Version;
        long? planned = null;

        var result = a.Service.Push(new PushOptions
        {
            Storage = _storage,
            MachineName = a.Settings.MachineName,
            ForceOverwriteOnConflict = true,
            ReuploadAll = true,
            LastPulledVersion = a.Settings.ToolState[key].LastPulledVersion,
            BeforeCommit = (_, _, version) => planned = version,
        });

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.Equal(remoteVersion + 1, planned);
        Assert.Equal(remoteVersion + 1, result.RemoteVersion);
    }

    [Fact(DisplayName = "予定の版を控えていない送信の記録の後に同期先が進んでいれば、マージを中止する")]
    public void MergeAbortsWhenUncommittedPushLacksPlannedVersion()
    {
        var a = CreateMachine("a");
        a.Settings.MergeVrcx = true;
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            CREATE TABLE usr0123abcd_avatar_history (avatar_id TEXT PRIMARY KEY, created_at TEXT, time INTEGER);
            INSERT INTO usr0123abcd_avatar_history VALUES ('avtr_a', '2026-10-01T00:00:00Z', 100);
            """);
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "a", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        Exec(a.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 150");
        // 予定の版を控えない版のツールが、送ろうとして止まった記録。反映されたかを版で見分けられない。
        store.SavePushed(key, new PushedRecord("0", store.Load(key)));

        var b = CreateMachine("b");
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        Exec(b.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 120");
        Assert.Equal(SyncOutcome.Success, Push(b).Outcome);

        var result = Push(a);

        Assert.Equal(SyncOutcome.ConflictDetected, result.Outcome);
        Assert.Contains("反映されたか分からない", result.Message);
        Assert.Equal(150, Scalar(a.Paths.SqliteFile, "SELECT time FROM usr0123abcd_avatar_history"));
    }

    [Fact(DisplayName = "後続の版を古いツールが書いていれば、強制 Push の有無を確かめられないので送った写しを祖先にせず中止する")]
    public void MergeAbortsWhenLaterVersionLacksForcedVersion()
    {
        var a = CreateMachine("a");
        a.Settings.MergeVrcx = true;
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            CREATE TABLE usr0123abcd_avatar_history (avatar_id TEXT PRIMARY KEY, created_at TEXT, time INTEGER);
            INSERT INTO usr0123abcd_avatar_history VALUES ('avtr_a', '2026-10-01T00:00:00Z', 100);
            """);
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "a", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var before = store.Load(key)!;
        var pulledBefore = a.Settings.ToolState[key].LastPulledVersion;
        Exec(a.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 150");
        var pushed = Push(a);
        Assert.Equal(SyncOutcome.Success, pushed.Outcome);
        RewindToInterruptedPush(a, store, key, before, pulledBefore, pushed.RemoteVersion!.Value);

        var b = CreateMachine("b");
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        Exec(b.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 170");
        Assert.Equal(SyncOutcome.Success, Push(b).Outcome);
        // B のツールが古く、強制 Push の版を書かなかったことにする。
        var manifest = _storage.LoadManifest().Manifest;
        manifest.Tools[VrcxSyncService.Key].LastForcedVersion = null;
        _storage.SeedManifest(manifest);

        var result = Push(a);

        Assert.Equal(SyncOutcome.ConflictDetected, result.Outcome);
        Assert.Contains("古く", result.Message);
        Assert.Equal(150, Scalar(a.Paths.SqliteFile, "SELECT time FROM usr0123abcd_avatar_history"));
    }

    [Fact(DisplayName = "Pull の取得中に VRCX の設定だけが書き換えられても、差し替える前に中止する")]
    public void PullAbortsWhenSettingsChangedDuringFetch()
    {
        var (_, b) = ArrangeRemoteNewer();
        var settings = b.Paths.SettingsJsonFile;
        _storage.OnDownload = _ => File.WriteAllText(settings, "{\"changedWhileFetching\":true}");

        var result = b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false);

        Assert.Equal(SyncOutcome.Aborted, result.Outcome);
        Assert.Contains("書き換えられた", result.Message);
        Assert.Equal("{\"changedWhileFetching\":true}", File.ReadAllText(settings));
        Assert.Equal(0, Scalar(b.Paths.SqliteFile, "SELECT count(*) FROM gamelog_location WHERE created_at = '2026-10-02T00:00:00Z'"));
    }

    [Fact(DisplayName = "dry-run のマージは、別の PC が取った予定の版の記録も消さない")]
    public void DryRunMergeKeepsUncommittedPushRecord()
    {
        var a = CreateMachine("a");
        a.Settings.MergeVrcx = true;
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            CREATE TABLE usr0123abcd_avatar_history (avatar_id TEXT PRIMARY KEY, created_at TEXT, time INTEGER);
            INSERT INTO usr0123abcd_avatar_history VALUES ('avtr_a', '2026-10-01T00:00:00Z', 100);
            """);
        var first = Push(a);
        Assert.Equal(SyncOutcome.Success, first.Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "a", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        Exec(a.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 150");
        store.SavePushed(key, new PushedRecord("0", store.Load(key), PlannedVersion: first.RemoteVersion!.Value + 1));

        var b = CreateMachine("b");
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        Exec(b.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 120");
        Assert.Equal(SyncOutcome.Success, Push(b).Outcome);

        var dryRun = a.Runner.Merge(a.Service, a.Settings, _storage, dryRun: true);

        Assert.True(dryRun.Outcome == SyncOutcome.Success, dryRun.Message);
        Assert.NotNull(store.LoadPushed(key));
        Assert.Equal(150, Scalar(a.Paths.SqliteFile, "SELECT time FROM usr0123abcd_avatar_history"));

        // 本番のマージでは消す。
        Assert.True(Push(a).Outcome == SyncOutcome.Success);
        Assert.Null(store.LoadPushed(key));
    }

    [Fact(DisplayName = "送った写しの記録を控えられなければ、送る前に Push を中止する")]
    public void PushAbortsWhenPushedRecordCannotBeSaved()
    {
        var (a, _) = ArrangeRemoteNewer();
        var store = new BaseSummaryStore(Path.Combine(_root, "a", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var versionBefore = _storage.LoadManifest().Manifest.Tools[VrcxSyncService.Key].Version;
        AddLocation(a, "2026-10-02T12:00:00Z");
        // 記録のファイルの場所にフォルダを置き、書けなくする。
        store.SavePushed(key, new PushedRecord("0"));
        var pushedPath = Directory.GetFiles(store.RootDirectory, "*.pushed").Single();
        File.Delete(pushedPath);
        Directory.CreateDirectory(pushedPath);

        var result = Push(a);

        Assert.Equal(SyncOutcome.Aborted, result.Outcome);
        Assert.Contains("記録を控えられない", result.Message);
        Assert.Equal(versionBefore, _storage.LoadManifest().Manifest.Tools[VrcxSyncService.Key].Version);
    }

    [Fact(DisplayName = "ユーザー別の notes テーブルの変更も検知し、マージで残す")]
    public void StartupPullKeepsUnsyncedUserNotes()
    {
        var a = CreateMachine("a");
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            CREATE TABLE usr0123abcd_notes (user_id TEXT PRIMARY KEY, display_name TEXT, note TEXT, created_at TEXT);
            """);
        AddLocation(a, "2026-10-01T00:00:00Z");
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var b = CreateMachine("b");
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        AddLocation(a, "2026-10-02T00:00:00Z");
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        Exec(b.Paths.SqliteFile, "INSERT INTO usr0123abcd_notes VALUES ('usr_x', 'X', 'local note', '2026-10-03T00:00:00Z')");

        var startup = StartupPull(b);

        Assert.Equal(SyncOutcome.LocalChangesPending, startup.Outcome);
        Assert.Contains("usr0123abcd_notes", startup.Message);

        b.Settings.MergeVrcx = true;
        var merged = b.Runner.Merge(b.Service, b.Settings, _storage);

        Assert.True(merged.Outcome == SyncOutcome.Success, merged.Message);
        Assert.Equal(2, CountLocations(b.Paths.SqliteFile));
        Assert.Equal(1, Scalar(b.Paths.SqliteFile, "SELECT count(*) FROM usr0123abcd_notes WHERE note = 'local note'"));
    }

    [Fact(DisplayName = "古いツールが書いた版 (強制 Push の版が無い) の上に通常の Push をしても、無いままにする")]
    public void NormalPushKeepsUnknownForcedVersion()
    {
        var (a, _) = ArrangeRemoteNewer();
        var manifest = _storage.LoadManifest().Manifest;
        manifest.Tools[VrcxSyncService.Key].LastForcedVersion = null;
        _storage.SeedManifest(manifest);
        AddLocation(a, "2026-10-03T00:00:00Z");

        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);

        // 0 にすると、古いツールの強制 Push があったかを後から確かめられなくなる。
        Assert.Null(_storage.LoadManifest().Manifest.Tools[VrcxSyncService.Key].LastForcedVersion);
    }

    [Fact(DisplayName = "退けた WAL が残っていれば、dry-run のマージは戻せないので中止する")]
    public void DryRunMergeAbortsWhenAsideWalRemains()
    {
        var (_, b) = ArrangeRemoteNewer();
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var db = b.Paths.SqliteFile;
        var suffix = ArrangeAsideWal(b, store, key, new DatabaseSummary { RemoteVersion = 999 });

        var result = b.Runner.Merge(b.Service, b.Settings, _storage, dryRun: true);

        Assert.Equal(SyncOutcome.Aborted, result.Outcome);
        Assert.Contains("退けた WAL", result.Message);
        Assert.True(File.Exists(db + "-wal" + suffix));
        Assert.True(store.IsMergePending(key));
        Assert.Equal(1, CountLocations(db));
    }

    [Fact(DisplayName = "変更なしの Push でも、同期先と同じ写しの記録は要約の保存が済むまで残す")]
    public void UnchangedPushKeepsCommittedRecordUntilSummaryIsSaved()
    {
        var a = CreateMachine("a");
        a.Settings.MergeVrcx = true;
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            CREATE TABLE usr0123abcd_avatar_history (avatar_id TEXT PRIMARY KEY, created_at TEXT, time INTEGER);
            INSERT INTO usr0123abcd_avatar_history VALUES ('avtr_a', '2026-10-01T00:00:00Z', 100);
            """);
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "a", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var before = store.Load(key)!;
        var pulledBefore = a.Settings.ToolState[key].LastPulledVersion;
        Exec(a.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 150");
        var pushed = Push(a);
        Assert.Equal(SyncOutcome.Success, pushed.Outcome);
        RewindToInterruptedPush(a, store, key, before, pulledBefore, pushed.RemoteVersion!.Value);

        // 要約を保存できない状態 (要約のファイルの場所にフォルダがある) で、同じ内容を強制 Push する。
        var summaryPath = Directory.GetFiles(store.RootDirectory, "*.json").Single();
        File.Delete(summaryPath);
        Directory.CreateDirectory(summaryPath);
        var unchanged = a.Runner.Push(a.Service, a.Settings, _storage, force: true);
        Assert.Equal(SyncOutcome.Success, unchanged.Outcome);
        Assert.Contains("要約を保存できなかった", unchanged.Message);

        // 反映済みの記録が残っている。
        var record = store.LoadPushed(key);
        Assert.NotNull(record);
        Assert.True(record!.Committed);
        Assert.Equal(pushed.RemoteVersion, record.Version);

        // 要約を保存できるようになれば、残した記録から送った写しを祖先にできる。
        Directory.Delete(summaryPath);
        store.Save(key, before);
        store.SavePushed(key, record);
        Assert.Equal(pulledBefore, a.Settings.ToolState[key].LastPulledVersion);
        var result = Push(a);

        Assert.True(result.Outcome == SyncOutcome.Success, result.Message);
        // 祖先を送る前の 100 にすると、手元の +50 と同期先の +50 を別々に見て 200 になる。
        Assert.Equal(150, Scalar(a.Paths.SqliteFile, "SELECT time FROM usr0123abcd_avatar_history"));
    }

    /// <summary>
    /// 送った後、要約と同期履歴を保存する前に止まった状態に戻す。送った写しの記録は、
    /// 要約の保存で消えているので、送った写しの内容から作り直す。<paramref name="committed"/> が
    /// 偽なら、manifest へ反映した直後に止まって反映済みの記録を書けなかった状態にする。
    /// </summary>
    private void RewindToInterruptedPush(
        Machine a, BaseSummaryStore store, string key, DatabaseSummary before, long pulledBefore, long pushedVersion,
        bool committed = true)
    {
        var pushedSummary = store.Load(key)!;
        var snapshot = _storage.LoadManifest().Manifest.Tools[VrcxSyncService.Key].Files
            .Single(f => f.RelativePath == "vrcx/latest.sqlite3");
        store.Save(key, before);
        store.SavePushed(key, committed
            ? new PushedRecord(snapshot.Sha256, pushedSummary, pushedVersion, Committed: true, PlannedVersion: pushedVersion)
            : new PushedRecord(snapshot.Sha256, pushedSummary, PlannedVersion: pushedVersion));
        a.Settings.ToolState[key].LastPulledVersion = pulledBefore;
    }

    [Fact(DisplayName = "Push の後に手元の DB が書き換わっても、基準の要約は送った写しのもの")]
    public void PushRecordsSummaryOfSentSnapshot()
    {
        var (a, _) = ArrangeRemoteNewer();
        var store = new BaseSummaryStore(Path.Combine(_root, "a", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        AddLocation(a, "2026-10-02T12:00:00Z");
        // 送った直後 (同期履歴を保存する前) に VRCX が行を足した状態を、送る写しを取った
        // 後、manifest を保存する前に手元へ書き込んで作る。
        _storage.OnBeforeSaveManifest = () => AddLocation(a, "2026-10-03T00:00:00Z");
        try
        {
            Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        }
        finally
        {
            _storage.OnBeforeSaveManifest = null;
        }

        // 基準は送った写し (3 行) のまま。手元の 4 行目は未送信の変更として見つかる。
        var baseline = store.Load(key)!;
        var current = DatabaseSummary.Compute(a.Paths.SqliteFile, MergeProfiles.Vrcx, out _)!;
        Assert.Contains("gamelog_location", current.FindChangedTables(baseline));
    }

    [Fact(DisplayName = "通常の Push の前にも、退けた WAL を戻してから送る")]
    public void PushRestoresAsideWalBeforeSending()
    {
        var (a, b) = ArrangeRemoteNewer();
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var db = b.Paths.SqliteFile;
        var suffix = ArrangeAsideWal(b, store, key, new DatabaseSummary { RemoteVersion = 999 });

        var result = Push(b);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.False(File.Exists(db + "-wal" + suffix));
        Assert.False(store.IsMergePending(key));
        // 戻した WAL の行も送られている。
        Assert.Equal(SyncOutcome.Success, a.Runner.Pull(a.Service, a.Settings, _storage, skipBackup: false).Outcome);
        Assert.Equal(3, CountLocations(a.Paths.SqliteFile));
    }

    [Fact(DisplayName = "通常の Push でも、退けた WAL を戻せなければ印を残して送らない")]
    public void PushAbortsWhenAsideWalCannotBeRestored()
    {
        var (_, b) = ArrangeRemoteNewer();
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var db = b.Paths.SqliteFile;
        var suffix = ArrangeAsideWal(b, store, key, new DatabaseSummary { RemoteVersion = 999 });
        File.WriteAllBytes(db + "-wal", Array.Empty<byte>());

        var result = Push(b);

        Assert.Equal(SyncOutcome.Aborted, result.Outcome);
        Assert.True(File.Exists(db + "-wal" + suffix));
        Assert.True(store.IsMergePending(key));
    }

    [Fact(DisplayName = "Pull の基準の要約は、取り出した写しから取る")]
    public void PullRecordsSummaryOfFetchedSnapshot()
    {
        var (a, b) = ArrangeRemoteNewer();

        var result = b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.NotNull(result.SyncedSummary);
        // 取り出した写しは A が送ったものなので、A の DB の要約と同じ内容になる。
        var remote = DatabaseSummary.Compute(a.Paths.SqliteFile, MergeProfiles.Vrcx, out _)!;
        Assert.Empty(result.SyncedSummary!.FindChangedTables(remote));
    }

    [Fact(DisplayName = "本体が同期先と同じで取得を省いても、要約は WAL に残る手元の変更を含めない")]
    public void PullSummarizesRemoteCopyWhenFetchIsSkipped()
    {
        var (_, b) = ArrangeRemoteNewer();
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        var db = b.Paths.SqliteFile;
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);

        // 同期先の本体を WAL 方式の印が付いたものに差し替え、B へ取り込ませる。VACUUM INTO で
        // 作る写しはロールバック方式なので、そのままでは WAL を伴う本体と同じ内容にならない。
        var walMode = File.ReadAllBytes(db);
        walMode[18] = 2;
        walMode[19] = 2;
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(walMode)).ToLowerInvariant();
        var blobKey = BlobKeys.FromSha256(sha);
        _storage.Seed(blobKey, walMode);
        var manifest = _storage.LoadManifest().Manifest;
        var entry = manifest.Tools[VrcxSyncService.Key];
        var snapshot = entry.Files.Single(f => f.RelativePath == "vrcx/latest.sqlite3");
        snapshot.Sha256 = sha;
        snapshot.BlobKey = blobKey;
        snapshot.Size = walMode.Length;
        entry.Version++;
        _storage.SeedManifest(manifest);
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        Assert.Equal(sha, FileHasher.Sha256(db));
        var remote = DatabaseSummary.Compute(db, MergeProfiles.Vrcx, out _)!;

        // 同期先では本体はそのままで版だけが進み、B では本体へ書き戻していない行が WAL に残る。
        entry.Version++;
        _storage.SeedManifest(manifest);
        // 行が WAL にあることは、ここでは数えない。接続を閉じると WAL が本体へ書き戻され、
        // 本体が同期先と違う内容になる。
        AddWalOnlyLocation(db, "2026-10-03T00:00:00Z");
        Assert.Equal(sha, FileHasher.Sha256(db));
        Assert.True(File.Exists(db + "-wal"));

        var result = b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.DoesNotContain(db, result.AffectedFiles);
        Assert.NotNull(result.SyncedSummary);
        Assert.Empty(result.SyncedSummary!.FindChangedTables(remote));
        Assert.Equal(2, result.SyncedSummary.Tables["gamelog_location"].RowCount);
        var saved = new BaseSummaryStore(Path.Combine(_root, "b", "base")).Load(key)!;
        Assert.Equal(2, saved.Tables["gamelog_location"].RowCount);
        Assert.Empty(Directory.GetFiles(b.Paths.RootDirectory, "*" + PullStaging.IncomingMarker + "*").Select(Path.GetFileName));
    }

    /// <summary>
    /// 本体を変えずに、行の追加を WAL だけに残す。本体は WAL 方式の印が付いている必要がある。
    /// </summary>
    private void AddWalOnlyLocation(string db, string createdAt)
    {
        var frozen = Path.Combine(_root, "frozen-main.sqlite3");
        var wal = Path.Combine(_root, "frozen-wal");
        using (var holder = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString()))
        {
            holder.Open();
            using (var read = holder.CreateCommand())
            {
                read.CommandText = "SELECT count(*) FROM gamelog_location";
                read.ExecuteScalar();
            }
            File.Copy(db, frozen, overwrite: true);
            Exec(db, $"INSERT INTO gamelog_location (created_at, location) VALUES ('{createdAt}', 'wrld_x:1')");
            File.Copy(db + "-wal", wal, overwrite: true);
        }
        File.Copy(frozen, db, overwrite: true);
        File.Move(wal, db + "-wal", overwrite: true);
        File.Delete(frozen);
    }

    [Fact(DisplayName = "要約を保存できなければ、同期履歴を進めずに古い基準を残す")]
    public void PushKeepsStateWhenSummaryCannotBeSaved()
    {
        var (a, _) = ArrangeRemoteNewer();
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var baseDir = Path.Combine(_root, "a", "base");
        var pulledBefore = a.Settings.ToolState[key].LastPulledVersion;
        // 要約のファイルの場所にフォルダを置き、保存を失敗させる。
        var summaryPath = Directory.GetFiles(baseDir, "*.json").Single();
        File.Delete(summaryPath);
        Directory.CreateDirectory(summaryPath);
        AddLocation(a, "2026-10-03T00:00:00Z");

        var result = Push(a);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.Contains("要約を保存できなかった", result.Message);
        Assert.Equal(pulledBefore, a.Settings.ToolState[key].LastPulledVersion);
    }

    [Fact(DisplayName = "保存まで済んで印だけが残っていれば、本体が変わっていても印を消して進む")]
    public void MergeClearsStaleRecordWhenAlreadySaved()
    {
        var (_, b) = ArrangeRemoteNewer();
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var saved = store.Load(key)!;
        store.SavePendingReplace(key, new MergePendingState(saved, saved.RemoteVersion, "0", "1"));
        AddLocation(b, "2026-10-03T00:00:00Z");

        var result = b.Runner.Merge(b.Service, b.Settings, _storage);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.False(store.IsMergePending(key));
    }

    [Fact(DisplayName = "止まったマージの記録とローカルがどちらも合わなければ、マージを中止する")]
    public void MergeAbortsWhenInterruptedStateIsUnknown()
    {
        var (_, b) = ArrangeRemoteNewer();
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        store.SavePendingReplace(key, new MergePendingState(new DatabaseSummary { RemoteVersion = 999 }, 999, "0", "1"));

        var result = b.Runner.Merge(b.Service, b.Settings, _storage);

        Assert.Equal(SyncOutcome.Aborted, result.Outcome);
        Assert.Contains("置き換え", result.Message);
        Assert.True(store.IsMergePending(key));
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
