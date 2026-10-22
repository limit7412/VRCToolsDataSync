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

    [Fact(DisplayName = "明示的な Pull も、手元に変更があれば上書きせずに見送る")]
    public void ExplicitPullKeepsLocalChanges()
    {
        var (_, b) = ArrangeRemoteNewer();
        AddLocation(b, "2026-10-03T00:00:00Z");

        var result = b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false);

        Assert.Equal(SyncOutcome.LocalChangesPending, result.Outcome);
        Assert.Contains("強制 Pull", result.Message);
        Assert.Equal(2, CountLocations(b.Paths.SqliteFile));
        Assert.Null(result.BackupPath);
    }

    [Fact(DisplayName = "明示的な Pull は、マージが有効なら手元の変更を残したままマージして Push する")]
    public void ExplicitPullMergesWhenEnabled()
    {
        var (a, b) = ArrangeRemoteNewer();
        b.Settings.MergeVrcx = true;
        AddLocation(b, "2026-10-03T00:00:00Z");

        var result = b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.NotNull(result.MergeReport);
        // 同期先の 2 行 + 手元の 1 行。
        Assert.Equal(3, CountLocations(b.Paths.SqliteFile));
        // マージ結果は Push され、A の起動時 Pull に届く。
        Assert.Equal(SyncOutcome.Success, StartupPull(a).Outcome);
        Assert.Equal(3, CountLocations(a.Paths.SqliteFile));
    }

    [Fact(DisplayName = "強制 Pull は、手元に変更があってもバックアップを取って上書きする")]
    public void ForcedPullOverwrites()
    {
        var (_, b) = ArrangeRemoteNewer();
        AddLocation(b, "2026-10-03T00:00:00Z");

        var result = b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false, force: true);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.Null(result.MergeReport);
        Assert.Equal(2, CountLocations(b.Paths.SqliteFile));
        Assert.NotNull(result.BackupPath);
        // 上書きした後は手元の変更は無く、次の起動時 Pull は何もしない。
        Assert.Equal(SyncOutcome.NothingToDo, StartupPull(b).Outcome);
    }

    [Fact(DisplayName = "強制 Pull は、マージが有効でもマージせずに上書きする")]
    public void ForcedPullIgnoresMergeSetting()
    {
        var (_, b) = ArrangeRemoteNewer();
        b.Settings.MergeVrcx = true;
        AddLocation(b, "2026-10-03T00:00:00Z");

        var result = b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false, force: true);

        Assert.Equal(SyncOutcome.Success, result.Outcome);
        Assert.Null(result.MergeReport);
        Assert.Equal(2, CountLocations(b.Paths.SqliteFile));
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

    [Fact(DisplayName = "WAL を退けた後に置き換える前で止まっていれば、強制 Push も退けた WAL を戻してから送る")]
    public void ForcePushRestoresAsideWalWhenReplaceDidNotHappen()
    {
        var (a, b) = ArrangeRemoteNewer();
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var db = b.Paths.SqliteFile;
        var suffix = ArrangeAsideWal(b, store, key, new DatabaseSummary { RemoteVersion = 999 });

        var result = b.Runner.Push(b.Service, b.Settings, _storage, force: true);

        Assert.True(result.Outcome == SyncOutcome.Success, result.Message);
        // 戻した WAL の行を含む 2 行が送られる。
        Assert.Equal(2, CountLocations(db));
        Assert.False(File.Exists(db + "-wal" + suffix));
        Assert.False(store.IsMergePending(key));
        Assert.Equal(SyncOutcome.Success, a.Runner.Pull(a.Service, a.Settings, _storage, skipBackup: false).Outcome);
        Assert.Equal(2, CountLocations(a.Paths.SqliteFile));
    }

    [Fact(DisplayName = "退けた WAL を戻せなければ、強制 Push も送らずに中止する")]
    public void ForcePushAbortsWhenAsideWalCannotBeRestored()
    {
        var (_, b) = ArrangeRemoteNewer();
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var db = b.Paths.SqliteFile;
        var suffix = ArrangeAsideWal(b, store, key, new DatabaseSummary { RemoteVersion = 999 });
        File.WriteAllBytes(db + "-wal", Array.Empty<byte>());
        var versionBefore = _storage.LoadManifest().Manifest.Tools[VrcxSyncService.Key].Version;

        var result = b.Runner.Push(b.Service, b.Settings, _storage, force: true);

        Assert.Equal(SyncOutcome.Aborted, result.Outcome);
        Assert.Contains("退けた WAL", result.Message);
        Assert.True(File.Exists(db + "-wal" + suffix));
        Assert.Equal(versionBefore, _storage.LoadManifest().Manifest.Tools[VrcxSyncService.Key].Version);
    }

    [Fact(DisplayName = "置き換えの前後を見分けられない状態でも、強制 Push は手元の内容を送って抜け出せる")]
    public void ForcePushProceedsWhenInterruptedStateIsUnknown()
    {
        var (_, b) = ArrangeRemoteNewer();
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var db = b.Paths.SqliteFile;
        ArrangeAsideWal(b, store, key, new DatabaseSummary { RemoteVersion = 999 });
        // 本体が書き換わり、置き換えの前後どちらの記録とも合わない。
        AddLocation(b, "2026-10-04T00:00:00Z");
        Assert.Equal(SyncOutcome.Aborted, b.Runner.Push(b.Service, b.Settings, _storage, force: false).Outcome);

        var result = b.Runner.Push(b.Service, b.Settings, _storage, force: true);

        Assert.True(result.Outcome == SyncOutcome.Success, result.Message);
        Assert.False(store.IsMergePending(key));
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
        Assert.Contains("強制 Push の版が無く", result.Message);
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

    [Fact(DisplayName = "反映済みの記録が残ったまま次の Push が失敗しても、前の記録を失わない")]
    public void FailedPushRestoresPreviousCommittedRecord()
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

        // 内容を変えて強制 Push するが、送信が失敗する。
        Exec(a.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 160");
        _storage.FailNextCommit = true;
        Assert.Throws<SyncStorageException>(() => a.Runner.Push(a.Service, a.Settings, _storage, force: true));

        var record = store.LoadPushed(key);
        Assert.NotNull(record);
        Assert.True(record!.Committed);
        Assert.Equal(pushed.RemoteVersion, record.Version);

        // 別の PC が送った版の上に版を進めても、戻した記録から送った写しを祖先にできる。
        var b = CreateMachine("b");
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        Exec(b.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 170");
        Assert.Equal(SyncOutcome.Success, Push(b).Outcome);
        var result = Push(a);

        Assert.True(result.Outcome == SyncOutcome.Success, result.Message);
        // 祖先は送った 150。手元の +10 と B の +20 を足して 180 になる。古い 100 を祖先にすると 230。
        Assert.Equal(180, Scalar(a.Paths.SqliteFile, "SELECT time FROM usr0123abcd_avatar_history"));
    }

    [Fact(DisplayName = "反映したか分からない記録が残ったまま次の Push が失敗しても、その記録を失わない")]
    public void FailedPushRestoresPreviousUncommittedRecord()
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
        // 実際には反映されたが、応答が失われて反映済みの記録を書けなかった状態。
        RewindToInterruptedPush(a, store, key, before, pulledBefore, pushed.RemoteVersion!.Value, committed: false);

        // 内容を変えて強制 Push するが、送信が失敗する。
        Exec(a.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 160");
        _storage.FailNextCommit = true;
        Assert.Throws<SyncStorageException>(() => a.Runner.Push(a.Service, a.Settings, _storage, force: true));

        // 送る前に同期先と照らし、予定の版の写しが送ったものなので反映済みになる。失敗した
        // 送信の記録を捨てるときに、その記録が戻る。
        var record = store.LoadPushed(key);
        Assert.NotNull(record);
        Assert.True(record!.Committed);
        Assert.Equal(pushed.RemoteVersion, record.Version);

        // 同期先は送った写しのままなので、戻した記録からそれを祖先にできる。
        var result = Push(a);

        Assert.True(result.Outcome == SyncOutcome.Success, result.Message);
        // 祖先は送った 150。手元の +10 で 160 になる。古い 100 を祖先にすると同期先の +50 も足して 210。
        Assert.Equal(160, Scalar(a.Paths.SqliteFile, "SELECT time FROM usr0123abcd_avatar_history"));
    }

    [Fact(DisplayName = "Pull の後に要約を保存できなくても、取り出した写しの記録を残して次のマージの祖先にする")]
    public void PullKeepsPulledSnapshotRecordWhenSummaryCannotBeSaved()
    {
        var a = CreateMachine("a");
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            CREATE TABLE usr0123abcd_avatar_history (avatar_id TEXT PRIMARY KEY, created_at TEXT, time INTEGER);
            INSERT INTO usr0123abcd_avatar_history VALUES ('avtr_a', '2026-10-01T00:00:00Z', 100);
            """);
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var b = CreateMachine("b");
        b.Settings.MergeVrcx = true;
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var before = store.Load(key)!;
        var pulledBefore = b.Settings.ToolState[key].LastPulledVersion;
        Exec(a.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 150");
        var pushed = Push(a);
        Assert.Equal(SyncOutcome.Success, pushed.Outcome);

        // 要約を保存できない状態 (要約のファイルの場所にフォルダがある) で起動時 Pull を通す。
        var summaryPath = Directory.GetFiles(store.RootDirectory, "*.json").Single();
        File.Delete(summaryPath);
        Directory.CreateDirectory(summaryPath);
        var pull = StartupPull(b);
        Assert.Equal(SyncOutcome.Success, pull.Outcome);
        Assert.Contains("要約を保存できなかった", pull.Message);
        Assert.Equal(150, Scalar(b.Paths.SqliteFile, "SELECT time FROM usr0123abcd_avatar_history"));
        Assert.Equal(pulledBefore, b.Settings.ToolState[key].LastPulledVersion);

        // 取り出した写しの記録が反映済みとして残る。記録があれば置き換えの記録は要らない。
        var record = store.LoadPushed(key);
        Assert.NotNull(record);
        Assert.True(record!.Committed);
        Assert.Equal(pushed.RemoteVersion, record.Version);
        Assert.NotNull(record.Summary);
        Assert.False(store.IsMergePending(key));

        // 古い要約と古い同期履歴のまま、手元で値を変えて Push する。起動時 Pull と同じく、
        // 古い要約との違いを手元の変更と見てマージに回る。
        Directory.Delete(summaryPath);
        store.Save(key, before);
        store.SavePushed(key, record);
        Exec(b.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 160");
        var result = Push(b);

        Assert.True(result.Outcome == SyncOutcome.Success, result.Message);
        // 祖先は取り出した 150。手元の +10 で 160 になる。古い 100 を祖先にすると取り込んだ +50 も足して 210。
        Assert.Equal(160, Scalar(b.Paths.SqliteFile, "SELECT time FROM usr0123abcd_avatar_history"));
    }

    [Fact(DisplayName = "Pull の後に要約も写しの記録も残せなければ、置き換えの記録を残して次の起動時 Pull で祖先と同期履歴をそろえる")]
    public void PullKeepsPendingReplaceRecordWhenNothingCanBeSaved()
    {
        var a = CreateMachine("a");
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            CREATE TABLE usr0123abcd_avatar_history (avatar_id TEXT PRIMARY KEY, created_at TEXT, time INTEGER);
            INSERT INTO usr0123abcd_avatar_history VALUES ('avtr_a', '2026-10-01T00:00:00Z', 100);
            """);
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var b = CreateMachine("b");
        b.Settings.MergeVrcx = true;
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var pulledBefore = b.Settings.ToolState[key].LastPulledVersion;
        Exec(a.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 150");
        var pushed = Push(a);
        Assert.Equal(SyncOutcome.Success, pushed.Outcome);

        // 要約と写しの記録のどちらの場所にもフォルダを置き、保存を失敗させる。
        var summaryPath = Directory.GetFiles(store.RootDirectory, "*.json").Single();
        var pushedPath = summaryPath + ".pushed";
        File.Delete(summaryPath);
        Directory.CreateDirectory(summaryPath);
        Directory.CreateDirectory(pushedPath);
        var pull = StartupPull(b);
        Assert.Equal(SyncOutcome.Success, pull.Outcome);
        Assert.Contains("要約を保存できなかった", pull.Message);
        Assert.Equal(pulledBefore, b.Settings.ToolState[key].LastPulledVersion);

        // 置き換えの記録だけが手掛かりなので残る。
        Assert.Null(store.LoadPushed(key));
        var pending = store.LoadPendingReplace(key);
        Assert.NotNull(pending);
        Assert.Equal(pushed.RemoteVersion, pending!.RemoteVersion);
        Assert.Equal(FileHasher.Sha256(b.Paths.SqliteFile), pending.MergedSha256);

        // 保存できるようになれば、次の起動時 Pull が記録から本体の差し替え後と見分けて祖先を
        // 本体の版にそろえ、取り込んだ本体を手元の変更と見ずに残りをそろえて同期履歴を進める。
        // Pull の記録からは残りをそろえるまで同期履歴を進めないので、マージは中止する。
        Directory.Delete(summaryPath);
        Directory.Delete(pushedPath);
        var merged = b.Runner.Merge(b.Service, b.Settings, _storage);
        Assert.Equal(SyncOutcome.Aborted, merged.Outcome);
        Assert.Equal(pulledBefore, b.Settings.ToolState[key].LastPulledVersion);
        var result = StartupPull(b);

        Assert.True(result.Outcome == SyncOutcome.Success, result.Message);
        Assert.Equal(pushed.RemoteVersion, b.Settings.ToolState[key].LastPulledVersion);
        Assert.Equal(pushed.RemoteVersion, store.Load(key)!.RemoteVersion);
        Assert.False(store.IsMergePending(key));
        Assert.Equal(150, Scalar(b.Paths.SqliteFile, "SELECT time FROM usr0123abcd_avatar_history"));
    }

    [Fact(DisplayName = "置き換えの記録を書いた後に置き換えずに中止したマージは、記録を消す")]
    public void MergeClearsPendingReplaceRecordWhenAbortingAfterRecording()
    {
        var (_, b) = ArrangeRemoteNewer();
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var aborting = new ThrowingReplaceService(b.Service, b.Paths.SqliteFile, abortInstead: true);

        var result = b.Runner.Merge(aborting, b.Settings, _storage);

        Assert.Equal(SyncOutcome.Aborted, result.Outcome);
        Assert.False(store.IsMergePending(key));
    }

    [Fact(DisplayName = "Pull が本体を差し替えた後に止まっても、次のマージは取り出した写しを祖先にする")]
    public void MergeAfterPartiallyAppliedPullUsesPulledSnapshotAsBaseline()
    {
        var a = CreateMachine("a");
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            CREATE TABLE usr0123abcd_avatar_history (avatar_id TEXT PRIMARY KEY, created_at TEXT, time INTEGER);
            INSERT INTO usr0123abcd_avatar_history VALUES ('avtr_a', '2026-10-01T00:00:00Z', 100);
            """);
        File.WriteAllText(a.Paths.SettingsJsonFile, "{\"v\":1}");
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var b = CreateMachine("b");
        b.Settings.MergeVrcx = true;
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var pulledBefore = b.Settings.ToolState[key].LastPulledVersion;
        Exec(a.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 150");
        File.WriteAllText(a.Paths.SettingsJsonFile, "{\"v\":2}");
        var pushed = Push(a);
        Assert.Equal(SyncOutcome.Success, pushed.Outcome);

        // 設定ファイルの場所にフォルダを置き、本体を差し替えた後の設定の差し替えで失敗させる。
        File.Delete(b.Paths.SettingsJsonFile);
        Directory.CreateDirectory(b.Paths.SettingsJsonFile);
        Assert.Throws<IOException>(() => StartupPull(b));
        Directory.Delete(b.Paths.SettingsJsonFile);

        // 本体は取り出した版なので、祖先の要約はその版にそろう。設定は残っているので、
        // 同期履歴は進めない。
        Assert.Equal(150, Scalar(b.Paths.SqliteFile, "SELECT time FROM usr0123abcd_avatar_history"));
        Assert.Equal(pulledBefore, b.Settings.ToolState[key].LastPulledVersion);
        Assert.Equal(pushed.RemoteVersion, store.Load(key)!.RemoteVersion);
        // 置き換えの記録は残りをそろえるまで残る。
        Assert.True(store.IsMergePending(key));

        // 次の起動時 Pull は、取り込んだ本体を手元の変更と見ずに残りの差し替えをやり直す。
        var retried = StartupPull(b);
        Assert.True(retried.Outcome == SyncOutcome.Success, retried.Message);
        Assert.Equal("{\"v\":2}", File.ReadAllText(b.Paths.SettingsJsonFile));
        Assert.Equal(pushed.RemoteVersion, b.Settings.ToolState[key].LastPulledVersion);
        Assert.False(store.IsMergePending(key));

        // 手元で値を変えて Push しても、取り込んだ +50 を手元の変更とは見ない。
        // 前の版を祖先にマージすると、手元の +10 に取り込んだ +50 も足して 210 になる。
        Exec(b.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 160");
        var result = Push(b);

        Assert.True(result.Outcome == SyncOutcome.Success, result.Message);
        Assert.Equal(160, Scalar(b.Paths.SqliteFile, "SELECT time FROM usr0123abcd_avatar_history"));
    }

    [Fact(DisplayName = "Pull は記録を残した後に書き換えを確かめ、書き換えられていれば差し替えない")]
    public void PullChecksForWritesAfterRecordingReplace()
    {
        var (_, b) = ArrangeRemoteNewer();
        var before = CountLocations(b.Paths.SqliteFile);

        var result = b.Service.Pull(new PullOptions
        {
            Storage = _storage,
            SkipBackup = true,
            // 前後のハッシュを取って記録を残す間に VRCX が起動して書き込んだ状況。DB が
            // 大きいほどこの間は長い。
            BeforeReplaceLocal = _ => AddLocation(b, "2026-10-03T00:00:00Z"),
        });

        // 記録を残す前に確かめるだけでは、この書き込みを見ずに WAL を消して差し替え、
        // 起動後の変更を失う。
        Assert.Equal(SyncOutcome.Aborted, result.Outcome);
        Assert.Contains("書き換えられた", result.Message);
        Assert.Equal(before + 1, CountLocations(b.Paths.SqliteFile));
    }

    [Fact(DisplayName = "記録を残した後に差し替える前で中止した Pull は、記録を残さない")]
    public void PullClearsReplaceRecordWhenAbortedBeforeReplacing()
    {
        var (_, b) = ArrangeRemoteNewer();
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        // 取り出しの間に VRCX が起動して書き込んだ状況。記録を残した後の確かめで中止する。
        _storage.OnDownload = _ => AddLocation(b, "2026-10-03T00:00:00Z");

        var result = b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: true);
        _storage.OnDownload = null;

        // ローカルは元のままなので記録は要らない。残すと、本体のハッシュがどちらの記録とも
        // 合わず、次のマージが置き換えの前後を見分けられないとして止まる。
        Assert.Equal(SyncOutcome.Aborted, result.Outcome);
        Assert.False(store.IsMergePending(key));
        Assert.Null(store.LoadPendingReplace(key));
    }

    [Fact(DisplayName = "設定が同期先から消えた版の Pull が設定の削除で止まっても、要約は本体の版にそろう")]
    public void PullCommitsDatabaseWhenSettingsDeletionFails()
    {
        var a = CreateMachine("a");
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            """);
        AddLocation(a, "2026-10-01T00:00:00Z");
        File.WriteAllText(a.Paths.SettingsJsonFile, "{\"v\":1}");
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var b = CreateMachine("b");
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var pulledBefore = b.Settings.ToolState[key].LastPulledVersion;
        // 同期先では設定が消え、本体は進んだ。
        File.Delete(a.Paths.SettingsJsonFile);
        AddLocation(a, "2026-10-02T00:00:00Z");
        var pushed = Push(a);
        Assert.Equal(SyncOutcome.Success, pushed.Outcome);

        // 本体を差し替えた後、設定の削除で失敗させる。
        using (var locked = LockAgainstDelete(b.Paths.SettingsJsonFile))
        {
            if (locked is null) return; // この環境では削除を失敗させられない。
            Assert.ThrowsAny<Exception>(() => StartupPull(b));
        }

        // 本体は取り出した版なので要約はその版にそろい、設定は残っているので同期履歴は進めない。
        // 進めると、次の起動時 Pull が取り込み済みと見て削除をやり直さず、残った設定を次の
        // Push で同期先へ送り直す。
        Assert.Equal(2, CountLocations(b.Paths.SqliteFile));
        Assert.True(File.Exists(b.Paths.SettingsJsonFile));
        Assert.Equal(pushed.RemoteVersion, store.Load(key)!.RemoteVersion);
        Assert.Equal(pulledBefore, b.Settings.ToolState[key].LastPulledVersion);
        Assert.True(store.IsMergePending(key));

        // 次の起動時 Pull は、取り込んだ本体を手元の変更と見ずに削除をやり直す。
        var retried = StartupPull(b);
        Assert.True(retried.Outcome == SyncOutcome.Success, retried.Message);
        Assert.False(File.Exists(b.Paths.SettingsJsonFile));
        Assert.Equal(pushed.RemoteVersion, b.Settings.ToolState[key].LastPulledVersion);
        Assert.False(store.IsMergePending(key));
    }

    [Fact(DisplayName = "本体を差し替えた後に設定で止まった起動時 Pull の後、マージが有効でも Push は自動マージへ進まない")]
    public void PushWithMergeEnabledDoesNotMergeAfterPartiallyAppliedStartupPull()
    {
        var a = CreateMachine("a");
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            CREATE TABLE usr0123abcd_avatar_history (avatar_id TEXT PRIMARY KEY, created_at TEXT, time INTEGER);
            INSERT INTO usr0123abcd_avatar_history VALUES ('avtr_a', '2026-10-01T00:00:00Z', 100);
            """);
        File.WriteAllText(a.Paths.SettingsJsonFile, "{\"v\":1}");
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var b = CreateMachine("b");
        b.Settings.MergeVrcx = true;
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        Exec(a.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 150");
        File.WriteAllText(a.Paths.SettingsJsonFile, "{\"v\":2}");
        var pushed = Push(a);
        Assert.Equal(SyncOutcome.Success, pushed.Outcome);

        // 本体を差し替えた後の設定の差し替えで失敗する。要約は本体の版にそろうが、設定は前の版。
        File.Delete(b.Paths.SettingsJsonFile);
        Directory.CreateDirectory(b.Paths.SettingsJsonFile);
        Assert.Throws<IOException>(() => StartupPull(b));
        Directory.Delete(b.Paths.SettingsJsonFile);
        File.WriteAllText(b.Paths.SettingsJsonFile, "{\"v\":1}");

        // 競合からの自動マージに進むと、DB をマージした後に前の版の設定を送り、同期先の新しい
        // 設定を上書きする。
        var push = Push(b);

        Assert.Equal(SyncOutcome.Aborted, push.Outcome);
        Assert.Equal(pushed.RemoteVersion, _storage.LoadManifest().Manifest.Tools[VrcxSyncService.Key].Version);

        var retried = StartupPull(b);
        Assert.True(retried.Outcome == SyncOutcome.Success, retried.Message);
        Assert.Equal("{\"v\":2}", File.ReadAllText(b.Paths.SettingsJsonFile));
        Assert.False(store.IsMergePending(key));
    }

    [Fact(DisplayName = "本体が同期先と同じで設定だけの Pull が止まっても、記録を残して Push は自動マージへ進まない")]
    public void PullRecordsReplaceWhenOnlySettingsChange()
    {
        var a = CreateMachine("a");
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            """);
        AddLocation(a, "2026-10-01T00:00:00Z");
        File.WriteAllText(a.Paths.SettingsJsonFile, "{\"v\":1}");
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var b = CreateMachine("b");
        b.Settings.MergeVrcx = true;
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var pulledBefore = b.Settings.ToolState[key].LastPulledVersion;
        // 同期先では設定だけが変わった。本体は同じなので差し替えない。
        File.WriteAllText(a.Paths.SettingsJsonFile, "{\"v\":2}");
        var pushed = Push(a);
        Assert.Equal(SyncOutcome.Success, pushed.Outcome);

        File.Delete(b.Paths.SettingsJsonFile);
        Directory.CreateDirectory(b.Paths.SettingsJsonFile);
        Assert.Throws<IOException>(() => StartupPull(b));
        Directory.Delete(b.Paths.SettingsJsonFile);
        File.WriteAllText(b.Paths.SettingsJsonFile, "{\"v\":1}");

        // 本体は差し替えていないが同期先と同じなので、要約はその版にそろい、記録が残る。
        Assert.Equal(pushed.RemoteVersion, store.Load(key)!.RemoteVersion);
        Assert.Equal(pulledBefore, b.Settings.ToolState[key].LastPulledVersion);
        Assert.True(store.IsMergePending(key));

        // 記録が無いと、競合からの自動マージに進んで前の版の設定を送る。
        var push = Push(b);
        Assert.Equal(SyncOutcome.Aborted, push.Outcome);
        Assert.Equal(pushed.RemoteVersion, _storage.LoadManifest().Manifest.Tools[VrcxSyncService.Key].Version);

        var retried = StartupPull(b);
        Assert.True(retried.Outcome == SyncOutcome.Success, retried.Message);
        Assert.Equal("{\"v\":2}", File.ReadAllText(b.Paths.SettingsJsonFile));
        Assert.Equal(pushed.RemoteVersion, b.Settings.ToolState[key].LastPulledVersion);
        Assert.False(store.IsMergePending(key));
    }

    [Fact(DisplayName = "強制 Push が予定と違う版を取った直後に止まっても、起動時 Pull は Push の値で反映済みにする")]
    public void StartupPullConfirmsUncommittedPushRecordByIdWhenVersionMoved()
    {
        var a = CreateMachine("a");
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            """);
        AddLocation(a, "2026-10-01T00:00:00Z");
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "a", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var before = store.Load(key)!;
        var pulledBefore = a.Settings.ToolState[key].LastPulledVersion;
        var b = CreateMachine("b");
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: true).Outcome);

        // a の非常用の強制 Push が manifest を保存する直前に b が版を進める。強制 Push は読み直して
        // その次の版を取るので、予定の版 (2) とは違う版 (3) になる。
        AddLocation(a, "2026-10-02T00:00:00Z");
        var interrupted = false;
        _storage.OnBeforeSaveManifest = () =>
        {
            if (interrupted) return;
            interrupted = true;
            AddLocation(b, "2026-10-02T12:00:00Z");
            Assert.Equal(SyncOutcome.Success, Push(b).Outcome);
        };
        SyncResult forced;
        try
        {
            forced = a.Runner.Push(a.Service, a.Settings, _storage, force: true, reuploadAll: true);
        }
        finally
        {
            _storage.OnBeforeSaveManifest = null;
        }
        Assert.Equal(SyncOutcome.Success, forced.Outcome);
        Assert.Equal(3, forced.RemoteVersion);
        // manifest を保存した直後、反映済みと記録する前に止まった状態。
        RewindToInterruptedPush(a, store, key, before, pulledBefore, 2, committed: false);

        // 同期先の項目はこの Push のものなので、版が予定と違っても実際の版で反映済みにする。
        var pull = StartupPull(a);

        Assert.True(pull.Outcome == SyncOutcome.Success, pull.Message);
        Assert.Equal(3, a.Settings.ToolState[key].LastPulledVersion);
        AddLocation(a, "2026-10-03T00:00:00Z");
        var result = Push(a);
        Assert.True(result.Outcome == SyncOutcome.Success, result.Message);
        Assert.Equal(4, result.RemoteVersion);
    }

    /// <summary>
    /// ファイルを消せない状態にする。Windows は読み取りだけ共有して開いたままにし (バックアップの
    /// 複製は通り、削除は共有違反になる)、Linux は変更不可の属性を付ける (root でないか、対応
    /// しないファイルシステムなら null)。
    /// </summary>
    private static IDisposable? LockAgainstDelete(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            if (RunChattr("+i", path) != 0) return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
        return new ImmutableFlag(path);
    }

    private static int RunChattr(string flag, string path)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("chattr", new[] { flag, path })
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        })!;
        process.WaitForExit();
        return process.ExitCode;
    }

    private sealed class ImmutableFlag(string path) : IDisposable
    {
        public void Dispose() => RunChattr("-i", path);
    }

    /// <summary>
    /// Pull が本体を差し替えた後、設定の差し替えの前にプロセスごと止まった状態を作る。
    /// 置き換えの直前の記録だけが残り、本体は取り出した版、設定と要約と同期履歴は前の版。
    /// </summary>
    private (Machine B, BaseSummaryStore Store, string Key, SyncResult Pushed, long PulledBefore) ArrangePullInterruptedAfterDatabase(bool mergeEnabled)
    {
        var a = CreateMachine("a");
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            CREATE TABLE usr0123abcd_avatar_history (avatar_id TEXT PRIMARY KEY, created_at TEXT, time INTEGER);
            INSERT INTO usr0123abcd_avatar_history VALUES ('avtr_a', '2026-10-01T00:00:00Z', 100);
            """);
        File.WriteAllText(a.Paths.SettingsJsonFile, "{\"v\":1}");
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var b = CreateMachine("b");
        b.Settings.MergeVrcx = mergeEnabled;
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var pulledBefore = b.Settings.ToolState[key].LastPulledVersion;
        Exec(a.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 150");
        File.WriteAllText(a.Paths.SettingsJsonFile, "{\"v\":2}");
        var pushed = Push(a);
        Assert.Equal(SyncOutcome.Success, pushed.Outcome);

        // 記録を残して本体を差し替えた後、設定の差し替えで止める。プロセスごと止まった状態
        // なので、本体の版を伝える経路 (AfterDatabaseReplaced) は走らない。
        File.Delete(b.Paths.SettingsJsonFile);
        Directory.CreateDirectory(b.Paths.SettingsJsonFile);
        Assert.Throws<IOException>(() => b.Service.Pull(new PullOptions
        {
            Storage = _storage,
            SkipBackup = true,
            BeforeReplaceLocal = pending => store.SavePendingReplace(key, pending),
        }));
        Directory.Delete(b.Paths.SettingsJsonFile);
        File.WriteAllText(b.Paths.SettingsJsonFile, "{\"v\":1}");
        Assert.Equal(150, Scalar(b.Paths.SqliteFile, "SELECT time FROM usr0123abcd_avatar_history"));
        Assert.True(store.IsMergePending(key));
        return (b, store, key, pushed, pulledBefore);
    }

    [Fact(DisplayName = "Pull が本体を差し替えた後に止まった記録からは、残りをそろえるまで同期履歴を進めない")]
    public void InterruptedPullRecordDoesNotAdvanceHistory()
    {
        var (b, store, key, pushed, pulledBefore) = ArrangePullInterruptedAfterDatabase(mergeEnabled: false);

        // 記録から本体の差し替えが済んだと分かっても、設定は前の版のまま。同期履歴を進めると
        // この Push が通り、前の版の設定で同期先の新しい設定を上書きする。
        var push = Push(b);

        Assert.Equal(SyncOutcome.Aborted, push.Outcome);
        Assert.Contains("Pull", push.Message);
        Assert.Equal(pushed.RemoteVersion, _storage.LoadManifest().Manifest.Tools[VrcxSyncService.Key].Version);
        Assert.Equal(pulledBefore, b.Settings.ToolState[key].LastPulledVersion);
        Assert.Equal(pushed.RemoteVersion, store.Load(key)!.RemoteVersion);
        // 記録は残りをそろえるまで残る。
        Assert.True(store.IsMergePending(key));

        // 次の起動時 Pull は、取り込んだ本体を手元の変更と見ずに残りをそろえる。
        var retried = StartupPull(b);
        Assert.True(retried.Outcome == SyncOutcome.Success, retried.Message);
        Assert.Equal("{\"v\":2}", File.ReadAllText(b.Paths.SettingsJsonFile));
        Assert.Equal(pushed.RemoteVersion, b.Settings.ToolState[key].LastPulledVersion);
        Assert.False(store.IsMergePending(key));

        // そろえた後は Push も通る。
        Exec(b.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 160");
        var result = Push(b);
        Assert.True(result.Outcome == SyncOutcome.Success, result.Message);
    }

    [Fact(DisplayName = "Pull が本体を差し替えた後に止まっていれば、マージが有効でも Push は自動マージへ進まない")]
    public void PushWithMergeEnabledDoesNotMergeOverInterruptedPull()
    {
        var (b, store, key, pushed, pulledBefore) = ArrangePullInterruptedAfterDatabase(mergeEnabled: true);

        // 競合からの自動マージに進むと、DB をマージした後に前の版の設定を送り、同期先の新しい
        // 設定を上書きする。
        var push = Push(b);

        Assert.Equal(SyncOutcome.Aborted, push.Outcome);
        Assert.Equal(pushed.RemoteVersion, _storage.LoadManifest().Manifest.Tools[VrcxSyncService.Key].Version);
        Assert.Equal(pulledBefore, b.Settings.ToolState[key].LastPulledVersion);
        Assert.True(store.IsMergePending(key));

        var retried = StartupPull(b);
        Assert.True(retried.Outcome == SyncOutcome.Success, retried.Message);
        Assert.Equal("{\"v\":2}", File.ReadAllText(b.Paths.SettingsJsonFile));
        Assert.False(store.IsMergePending(key));
    }

    [Fact(DisplayName = "初めての Pull が本体を差し替えた後に止まっても、次の起動時 Pull が残りをそろえる")]
    public void FirstStartupPullResolvesInterruptedPullRecord()
    {
        var a = CreateMachine("a");
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            """);
        AddLocation(a, "2026-10-01T00:00:00Z");
        File.WriteAllText(a.Paths.SettingsJsonFile, "{\"v\":1}");
        var pushed = Push(a);
        Assert.Equal(SyncOutcome.Success, pushed.Outcome);

        // b はまだ一度も同期していない (同期履歴が無い)。初めての Pull が本体を差し替えた後、
        // 設定の差し替えで止まる。
        var b = CreateMachine("b");
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        Directory.CreateDirectory(b.Paths.SettingsJsonFile);
        Assert.Throws<IOException>(() => b.Service.Pull(new PullOptions
        {
            Storage = _storage,
            SkipBackup = true,
            BeforeReplaceLocal = pending => store.SavePendingReplace(key, pending),
        }));
        Directory.Delete(b.Paths.SettingsJsonFile);
        Assert.True(store.IsMergePending(key));
        Assert.False(b.Settings.ToolState.ContainsKey(key));

        var result = StartupPull(b);

        Assert.True(result.Outcome == SyncOutcome.Success, result.Message);
        Assert.Equal("{\"v\":1}", File.ReadAllText(b.Paths.SettingsJsonFile));
        Assert.Equal(pushed.RemoteVersion, b.Settings.ToolState[key].LastPulledVersion);
        Assert.False(store.IsMergePending(key));
    }

    [Fact(DisplayName = "Pull が本体を差し替えた後に止まっていれば、マージは残りをそろえるまで中止する")]
    public void MergeAbortsWhenPullStoppedAfterReplacingDatabase()
    {
        var (b, store, key, pushed, pulledBefore) = ArrangePullInterruptedAfterDatabase(mergeEnabled: true);

        // マージは設定に触れず、続く Push が手元の設定を送る。進めると前の版の設定で上書きする。
        var merged = b.Runner.Merge(b.Service, b.Settings, _storage);

        Assert.Equal(SyncOutcome.Aborted, merged.Outcome);
        Assert.Contains("Pull", merged.Message);
        Assert.Equal(pushed.RemoteVersion, _storage.LoadManifest().Manifest.Tools[VrcxSyncService.Key].Version);
        Assert.Equal(pulledBefore, b.Settings.ToolState[key].LastPulledVersion);
        Assert.True(store.IsMergePending(key));

        var retried = StartupPull(b);
        Assert.True(retried.Outcome == SyncOutcome.Success, retried.Message);
        Assert.Equal("{\"v\":2}", File.ReadAllText(b.Paths.SettingsJsonFile));
        Assert.False(store.IsMergePending(key));
    }

    [Fact(DisplayName = "同じ DB で別の PC が予定の版を取っていても、自分の Push が反映されたとは見ない")]
    public void PushDoesNotTreatOtherMachinesVersionWithSameDatabaseAsOwn()
    {
        var a = CreateMachine("a");
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            """);
        AddLocation(a, "2026-10-01T00:00:00Z");
        File.WriteAllText(a.Paths.SettingsJsonFile, "{\"v\":\"a\"}");
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "a", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        // b は同じ DB のまま設定だけを変えて次の版を取った。
        var b = CreateMachine("b");
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: true).Outcome);
        File.WriteAllText(b.Paths.SettingsJsonFile, "{\"v\":\"b\"}");
        var pushedByB = Push(b);
        Assert.Equal(SyncOutcome.Success, pushedByB.Outcome);
        var entry = _storage.LoadManifest().Manifest.Tools[VrcxSyncService.Key];
        var snapshot = entry.Files.Single(f => f.RelativePath == "vrcx/latest.sqlite3");
        // a も同じ版を取る予定で送り、manifest を保存する前に止まっていた。DB の写しは b と同じ。
        store.SavePushed(key, new PushedRecord(snapshot.Sha256, store.Load(key), PlannedVersion: pushedByB.RemoteVersion, PushId: "a-push"));

        // 同期先の版を自分の Push と見ると、取り込み済みとして競合の検査を通り、b の設定を上書きする。
        var result = Push(a);

        Assert.Equal(SyncOutcome.ConflictDetected, result.Outcome);
        Assert.Null(store.LoadPushed(key));
        var settings = _storage.LoadManifest().Manifest.Tools[VrcxSyncService.Key].Files.Single(f => f.RelativePath == "vrcx/latest.json");
        Assert.Equal(entry.Files.Single(f => f.RelativePath == "vrcx/latest.json").Sha256, settings.Sha256);
    }

    [Fact(DisplayName = "反映の直後に止まった送信の記録は、起動時 Pull が同期先と照らして反映済みにする")]
    public void StartupPullConfirmsUncommittedPushRecordWithRemote()
    {
        var a = CreateMachine("a");
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            """);
        AddLocation(a, "2026-10-01T00:00:00Z");
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "a", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var before = store.Load(key)!;
        var pulledBefore = a.Settings.ToolState[key].LastPulledVersion;
        AddLocation(a, "2026-10-02T00:00:00Z");
        var pushed = Push(a);
        Assert.Equal(SyncOutcome.Success, pushed.Outcome);
        // manifest を保存した直後、反映済みと記録する前に止まった状態。
        RewindToInterruptedPush(a, store, key, before, pulledBefore, pushed.RemoteVersion!.Value, committed: false);

        // マージ無しの起動時 Pull は、送った DB を手元の変更と見ずに進む。
        var pull = StartupPull(a);

        Assert.True(pull.Outcome == SyncOutcome.Success, pull.Message);
        Assert.Equal(pushed.RemoteVersion, a.Settings.ToolState[key].LastPulledVersion);
        Assert.Equal(2, CountLocations(a.Paths.SqliteFile));

        // Push も自分が送った版と競合しない。
        AddLocation(a, "2026-10-03T00:00:00Z");
        var result = Push(a);
        Assert.True(result.Outcome == SyncOutcome.Success, result.Message);
        Assert.Equal(pushed.RemoteVersion + 1, result.RemoteVersion);
    }

    [Fact(DisplayName = "予定の版を別の PC が取っていれば、起動時 Pull は送信の記録を捨てる")]
    public void StartupPullDiscardsUncommittedPushRecordTakenByOtherMachine()
    {
        var (a, b) = ArrangeRemoteNewer();
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var remoteVersion = _storage.LoadManifest().Manifest.Tools[VrcxSyncService.Key].Version;
        // B が版 remoteVersion を取る予定で送ったが、反映されず A が同じ版を取った状態。
        store.SavePushed(key, new PushedRecord("0123456789abcdef", store.Load(key), PlannedVersion: remoteVersion));

        var result = StartupPull(b);

        Assert.True(result.Outcome == SyncOutcome.Success, result.Message);
        Assert.Null(store.LoadPushed(key));
        Assert.Equal(2, CountLocations(b.Paths.SqliteFile));
        _ = a;
    }

    [Fact(DisplayName = "Push の後に要約を保存できなくても、マージ無しの起動時 Pull と Push が送った写しの記録でそろえ直せる")]
    public void SyncRecoversAfterPushSummarySaveFailureWithoutMerge()
    {
        var a = CreateMachine("a");
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            """);
        AddLocation(a, "2026-10-01T00:00:00Z");
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "a", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var pulledBefore = a.Settings.ToolState[key].LastPulledVersion;

        // 要約を保存できない状態で Push する。反映は済み、同期履歴は進まない。
        var summaryPath = Directory.GetFiles(store.RootDirectory, "*.json").Single();
        File.Delete(summaryPath);
        Directory.CreateDirectory(summaryPath);
        AddLocation(a, "2026-10-02T00:00:00Z");
        var pushed = Push(a);
        Assert.Equal(SyncOutcome.Success, pushed.Outcome);
        Assert.Contains("要約を保存できなかった", pushed.Message);
        Assert.Equal(pulledBefore, a.Settings.ToolState[key].LastPulledVersion);
        Directory.Delete(summaryPath);

        // 手元に変更が無ければ、起動時 Pull は送った写しの記録と比べて進み、同期履歴をそろえる。
        var pull = StartupPull(a);
        Assert.True(pull.Outcome == SyncOutcome.Success, pull.Message);
        Assert.Equal(pushed.RemoteVersion, a.Settings.ToolState[key].LastPulledVersion);
        Assert.Equal(pushed.RemoteVersion, store.Load(key)!.RemoteVersion);
        Assert.Null(store.LoadPushed(key));
        Assert.Equal(2, CountLocations(a.Paths.SqliteFile));
    }

    [Fact(DisplayName = "Push の後に要約を保存できず手元に変更があっても、マージ無しの Push は自分の版と競合しない")]
    public void PushDoesNotConflictWithOwnSnapshotAfterSummarySaveFailure()
    {
        var a = CreateMachine("a");
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            """);
        AddLocation(a, "2026-10-01T00:00:00Z");
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "a", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var summaryPath = Directory.GetFiles(store.RootDirectory, "*.json").Single();
        File.Delete(summaryPath);
        Directory.CreateDirectory(summaryPath);
        AddLocation(a, "2026-10-02T00:00:00Z");
        var pushed = Push(a);
        Assert.Equal(SyncOutcome.Success, pushed.Outcome);
        Directory.Delete(summaryPath);

        // 手元で増えた履歴は、送った写しの記録と比べて見つかる。起動時 Pull は上書きしない。
        AddLocation(a, "2026-10-03T00:00:00Z");
        var pull = StartupPull(a);
        Assert.Equal(SyncOutcome.LocalChangesPending, pull.Outcome);
        Assert.Equal(3, CountLocations(a.Paths.SqliteFile));

        // 同期先は自分が送った版なので、通常の Push で送れる。
        var result = Push(a);
        Assert.True(result.Outcome == SyncOutcome.Success, result.Message);
        Assert.Equal(pushed.RemoteVersion + 1, result.RemoteVersion);
        Assert.Equal(result.RemoteVersion, a.Settings.ToolState[key].LastPulledVersion);
        Assert.Null(store.LoadPushed(key));
    }

    [Fact(DisplayName = "置き換えを始めた後にマージが例外で止まれば、置き換えの記録を残す")]
    public void MergeKeepsPendingReplaceRecordWhenReplaceThrows()
    {
        var (_, b) = ArrangeRemoteNewer();
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var throwing = new ThrowingReplaceService(b.Service, b.Paths.SqliteFile);

        Assert.Throws<IOException>(() => b.Runner.Merge(throwing, b.Settings, _storage));

        // 退けた WAL の場所を含む記録が残り、次回に置き換えの前後を見分けられる。
        var pending = store.LoadPendingReplace(key);
        Assert.NotNull(pending);
        Assert.Equal(".vrctds-aside-test", pending!.AsideSuffix);

        // 本体は元のままなので、次のマージは置き換える前に止まったと見て進む。
        var result = b.Runner.Merge(b.Service, b.Settings, _storage);
        Assert.True(result.Outcome == SyncOutcome.Success, result.Message);
        Assert.False(store.IsMergePending(key));
    }

    /// <summary>
    /// 置き換えの記録を書いた直後に、本体の差し替えで失敗するマージ。<paramref name="abortInstead"/> なら
    /// 置き換える前の確認で中止した結果を返す。
    /// </summary>
    private sealed class ThrowingReplaceService(IMergeableSyncService inner, string database, bool abortInstead = false) : IMergeableSyncService
    {
        public string ToolKey => inner.ToolKey;
        public TrackedDatabase? TrackedDatabase => inner.TrackedDatabase;
        public SyncResult Push(PushOptions options) => inner.Push(options);
        public SyncResult Pull(PullOptions options) => inner.Pull(options);

        public SyncResult PullMerge(PullMergeOptions options)
        {
            options.BeforeReplaceLocal?.Invoke(new MergePendingState(
                null, 999, FileHasher.Sha256(database), "0", ".vrctds-aside-test"));
            if (abortInstead) return new SyncResult { Outcome = SyncOutcome.Aborted, Message = "置き換える前に中止" };
            throw new IOException("本体を差し替えられません");
        }
    }

    [Fact(DisplayName = "予定の版を別の PC が取った記録を捨てるとき、抱えていた前の反映済みの記録で祖先を決め直す")]
    public void MergeFallsBackToPreviousRecordWhenPlannedVersionWasTakenByOthers()
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
        // 次の Push が反映されないまま止まり、前の反映済みの記録を抱えた記録が残っている。
        Exec(a.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 160");
        store.BeginPushed(key, new PushedRecord("0", store.Load(key), PlannedVersion: pushed.RemoteVersion + 1));
        Assert.NotNull(store.LoadPushed(key)!.Previous);

        // 別の PC が予定の版を取る。
        var b = CreateMachine("b");
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        Exec(b.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 170");
        Assert.Equal(SyncOutcome.Success, Push(b).Outcome);

        var result = Push(a);

        Assert.True(result.Outcome == SyncOutcome.Success, result.Message);
        Assert.Equal(180, Scalar(a.Paths.SqliteFile, "SELECT time FROM usr0123abcd_avatar_history"));
    }

    [Fact(DisplayName = "manifest の更新が毎回拒まれて止まった Push は、反映していない送信の記録を残さない")]
    public void PushDiscardsPushedRecordWhenManifestSaveIsRejected()
    {
        var (a, _) = ArrangeRemoteNewer();
        var store = new BaseSummaryStore(Path.Combine(_root, "a", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        AddLocation(a, "2026-10-02T12:00:00Z");
        // 保存の直前に毎回別の保存が割り込んで manifest のタグが変わり、条件付きの保存が拒まれ続ける。
        var interrupting = false;
        _storage.OnBeforeSaveManifest = () =>
        {
            if (interrupting) return;
            interrupting = true;
            try
            {
                var snapshot = _storage.LoadManifest();
                Assert.True(_storage.TrySaveManifest(snapshot.Manifest, snapshot.VersionTag));
            }
            finally
            {
                interrupting = false;
            }
        };
        try
        {
            Assert.Throws<SyncStorageConcurrencyException>(() => Push(a));
        }
        finally
        {
            _storage.OnBeforeSaveManifest = null;
        }

        Assert.Null(store.LoadPushed(key));
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
    }

    [Fact(DisplayName = "以前の版が残した写しのハッシュだけの記録と同期先が違えば、祖先を決められないので中止する")]
    public void MergeAbortsWhenLegacyShaOnlyRecordDoesNotMatchRemote()
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
        // 以前の版が書いた、写しのハッシュだけの記録。
        store.SavePushed(key, new PushedRecord("0"));
        File.WriteAllText(Directory.GetFiles(store.RootDirectory, "*.pushed").Single(), "0");

        var b = CreateMachine("b");
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        Exec(b.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 170");
        Assert.Equal(SyncOutcome.Success, Push(b).Outcome);

        var result = Push(a);

        Assert.Equal(SyncOutcome.ConflictDetected, result.Outcome);
        Assert.Contains("反映されたか分からない", result.Message);
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
        var entry = _storage.LoadManifest().Manifest.Tools[VrcxSyncService.Key];
        var snapshot = entry.Files.Single(f => f.RelativePath == "vrcx/latest.sqlite3");
        store.Save(key, before);
        store.SavePushed(key, committed
            ? new PushedRecord(snapshot.Sha256, pushedSummary, pushedVersion, Committed: true, PlannedVersion: pushedVersion, PushId: entry.PushId)
            : new PushedRecord(snapshot.Sha256, pushedSummary, PlannedVersion: pushedVersion, PushId: entry.PushId));
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

        // WAL の行は手元の変更なので、通常の Pull は見送る。上書きさせるために強制 Pull を使う。
        var result = b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false, force: true);

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

    [Fact(DisplayName = "記録を残した後、置き換える直前の確かめで VRCX が起動していたマージは、記録を残さない")]
    public void MergeClearsReplaceRecordWhenToolStartsBeforeReplacing()
    {
        var (_, b) = ArrangeRemoteNewer();
        AddLocation(b, "2026-10-03T00:00:00Z");
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        // 記録を残す間に VRCX が起動し、置き換える直前の確かめで見つかった状況。
        var service = new RunningAfterRecordService(b.Service);

        Assert.Throws<RunningProcessException>(() => b.Runner.Merge(service, b.Settings, _storage, skipBackup: true));

        // 本体も WAL もまだ退けていないので記録は要らない。残すと、VRCX が終了時に WAL を本体へ
        // 書き戻したとき本体のハッシュがどちらの記録とも合わず、次の Push とマージが止まり続ける。
        Assert.False(store.IsMergePending(key));
        Assert.Null(store.LoadPendingReplace(key));
        Assert.Equal(2, CountLocations(b.Paths.SqliteFile));
        Assert.Equal(SyncOutcome.Success, b.Runner.Merge(b.Service, b.Settings, _storage, skipBackup: true).Outcome);
    }

    /// <summary>置き換えの記録を残した直後に、VRCX の起動を検知したことにする。</summary>
    private sealed class RunningAfterRecordService : IMergeableSyncService
    {
        private readonly VrcxSyncService _inner;
        public RunningAfterRecordService(VrcxSyncService inner) => _inner = inner;
        public string ToolKey => _inner.ToolKey;
        public TrackedDatabase? TrackedDatabase => _inner.TrackedDatabase;
        public SyncResult Push(PushOptions options) => _inner.Push(options);
        public SyncResult Pull(PullOptions options) => _inner.Pull(options);
        public SyncResult PullMerge(PullMergeOptions options) => _inner.PullMerge(new PullMergeOptions
        {
            Storage = options.Storage,
            Baseline = options.Baseline,
            OwnPush = options.OwnPush,
            DiscardOwnPush = options.DiscardOwnPush,
            DryRun = options.DryRun,
            SkipBackup = options.SkipBackup,
            AfterReplaceLocal = options.AfterReplaceLocal,
            BeforeReplaceLocal = pending =>
            {
                options.BeforeReplaceLocal?.Invoke(pending);
                throw new RunningProcessException(ProcessGuard.VrcxProcessNames);
            },
        });
    }

    [Fact(DisplayName = "前回同期した版の後に別の PC が強制 Push で上書きしていれば、要約を祖先にせず中止する")]
    public void MergeAbortsWhenLaterVersionWasForcedAfterBaseline()
    {
        var a = CreateMachine("a");
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            CREATE TABLE usr0123abcd_avatar_history (avatar_id TEXT PRIMARY KEY, created_at TEXT, time INTEGER);
            INSERT INTO usr0123abcd_avatar_history VALUES ('avtr_a', '2026-10-01T00:00:00Z', 100);
            """);
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var b = CreateMachine("b");
        b.Settings.MergeVrcx = true;
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        // A は B の取り込んだ版とは別の祖先から、強制 Push で同期先を上書きした。
        Exec(a.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 120");
        Assert.Equal(SyncOutcome.Success, a.Runner.Push(a.Service, a.Settings, _storage, force: true).Outcome);
        Exec(b.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 150");

        var result = b.Runner.Merge(b.Service, b.Settings, _storage, skipBackup: true);

        // 送った写しの記録が無い通常の状態でも、要約の版より後の強制 Push を見て止める。
        // 要約を祖先にすると、祖先からの双方の増分を実際と違う祖先を基に足す。
        Assert.Equal(SyncOutcome.Aborted, result.Outcome);
        Assert.Contains("強制 Push", result.Message);
        Assert.Equal(150, Scalar(b.Paths.SqliteFile, "SELECT time FROM usr0123abcd_avatar_history"));
    }

    [Fact(DisplayName = "前回同期した版の後の版を古いツールが書いていれば、強制 Push の有無を確かめられないので要約を祖先にせず中止する")]
    public void MergeAbortsWhenVersionAfterBaselineLacksForcedVersion()
    {
        var a = CreateMachine("a");
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            CREATE TABLE usr0123abcd_avatar_history (avatar_id TEXT PRIMARY KEY, created_at TEXT, time INTEGER);
            INSERT INTO usr0123abcd_avatar_history VALUES ('avtr_a', '2026-10-01T00:00:00Z', 100);
            """);
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var b = CreateMachine("b");
        b.Settings.MergeVrcx = true;
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        Exec(a.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 120");
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        // A のツールが古く、強制 Push の版を書かなかったことにする。
        var manifest = _storage.LoadManifest().Manifest;
        manifest.Tools[VrcxSyncService.Key].LastForcedVersion = null;
        _storage.SeedManifest(manifest);
        Exec(b.Paths.SqliteFile, "UPDATE usr0123abcd_avatar_history SET time = 150");

        var result = b.Runner.Merge(b.Service, b.Settings, _storage, skipBackup: true);

        Assert.Equal(SyncOutcome.Aborted, result.Outcome);
        Assert.Contains("強制 Push の版が無く", result.Message);
        Assert.Equal(150, Scalar(b.Paths.SqliteFile, "SELECT time FROM usr0123abcd_avatar_history"));
    }

    [Fact(DisplayName = "Push の後に要約だけ保存して同期履歴を保存する前に止まっても、マージ無しの Push は自分の版と競合しない")]
    public void PushUsesSavedSummaryVersionWhenHistorySaveFailed()
    {
        var a = CreateMachine("a");
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            """);
        AddLocation(a, "2026-10-01T00:00:00Z");
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "a", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var pulledBefore = a.Settings.ToolState[key].LastPulledVersion;
        AddLocation(a, "2026-10-02T00:00:00Z");
        var pushed = Push(a);
        Assert.Equal(SyncOutcome.Success, pushed.Outcome);
        // 要約は送った版で保存され、送った写しの記録は消えた後、同期履歴の保存で止まった状態。
        Assert.Equal(pushed.RemoteVersion, store.Load(key)!.RemoteVersion);
        Assert.Null(store.LoadPushed(key));
        a.Settings.ToolState[key].LastPulledVersion = pulledBefore;
        AddLocation(a, "2026-10-03T00:00:00Z");

        var result = Push(a);

        // 同期履歴だけで見ると自分が送った版と競合し、先に Pull しない限り送れない。
        Assert.True(result.Outcome == SyncOutcome.Success, result.Message);
        Assert.Equal(pushed.RemoteVersion + 1, result.RemoteVersion);
        Assert.Equal(result.RemoteVersion, a.Settings.ToolState[key].LastPulledVersion);
    }

    [Fact(DisplayName = "manifest を保存する前に止まった Push の記録は、起動時 Pull が取り込みを見送っても捨てる")]
    public void StartupPullDiscardsUncommittedPushRecordWhenManifestWasNotSaved()
    {
        var a = CreateMachine("a");
        Exec(a.Paths.SqliteFile, """
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            """);
        AddLocation(a, "2026-10-01T00:00:00Z");
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        var b = CreateMachine("b");
        b.Settings.MergeVrcx = true;
        Assert.Equal(SyncOutcome.Success, b.Runner.Pull(b.Service, b.Settings, _storage, skipBackup: false).Outcome);
        var store = new BaseSummaryStore(Path.Combine(_root, "b", "base"));
        var key = SyncRunner.ToolStateKey(_storage, VrcxSyncService.Key);
        var remoteVersion = _storage.LoadManifest().Manifest.Tools[VrcxSyncService.Key].Version;
        // B が次の版を取る予定で送ったが、manifest を保存する前に止まった状態。同期先は前の版のまま。
        store.SavePushed(key, new PushedRecord("0123456789abcdef", store.Load(key), PlannedVersion: remoteVersion + 1, PushId: "b-push"));

        // 同期先が進んでいないので起動時 Pull は取り込みを見送るが、記録は反映されなかったと確定できる。
        var pull = StartupPull(b);
        Assert.NotEqual(SyncOutcome.Aborted, pull.Outcome);
        Assert.Null(store.LoadPushed(key));

        // その後に A が予定の版を越えても、B の Push は反映不明の記録に止められずにマージして送れる。
        AddLocation(a, "2026-10-02T00:00:00Z");
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        AddLocation(a, "2026-10-03T00:00:00Z");
        Assert.Equal(SyncOutcome.Success, Push(a).Outcome);
        AddLocation(b, "2026-10-04T00:00:00Z");
        var pushed = Push(b);
        Assert.True(pushed.Outcome == SyncOutcome.Success, pushed.Message);
        Assert.Equal(4, CountLocations(b.Paths.SqliteFile));
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
