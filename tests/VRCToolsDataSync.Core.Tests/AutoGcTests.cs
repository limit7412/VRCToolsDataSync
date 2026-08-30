using VRCToolsDataSync.Core.Settings;
using VRCToolsDataSync.Core.Storage;
using VRCToolsDataSync.Core.Sync;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// Push の後始末として走る自動回収 (issue #55) の性質を固定する。
/// 回収そのものの正しさは <see cref="BlobGarbageCollectorTests"/> が持つので、
/// ここでは「いつ走り、いつ走らないか」と、その記録の持ち方だけを確かめる。
/// </summary>
public sealed class AutoGcTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "vrctoolsdatasync-tests-" + Guid.NewGuid().ToString("N"));

    private SettingsStore CreateStore()
        => new(Path.Combine(_directory, "settings.json"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>猶予期間 (既定 7 日) を確実に過ぎている時刻。</summary>
    private static DateTimeOffset LongAgo => DateTimeOffset.UtcNow - TimeSpan.FromDays(30);

    private static SyncManifest ManifestReferencing(params string[] blobKeys)
    {
        var manifest = new SyncManifest();
        manifest.Tools["vrcx"] = new ToolManifestEntry
        {
            Version = 1,
            MachineName = "test",
            UpdatedAt = DateTimeOffset.UtcNow,
            Files = blobKeys
                .Select((key, i) => new ManifestFile
                {
                    RelativePath = $"vrcx/file{i}",
                    Size = 1,
                    Sha256 = key.Replace(BlobKeys.Prefix, string.Empty, StringComparison.Ordinal),
                    BlobKey = key,
                })
                .ToList(),
        };
        return manifest;
    }

    private static FakeSyncStorage StorageWithOrphan()
    {
        var storage = new FakeSyncStorage { Now = LongAgo };
        storage.Seed(BlobKeys.Prefix + "aaa", "live", LongAgo);
        storage.Seed(BlobKeys.Prefix + "bbb", "orphan", LongAgo);
        storage.SeedManifest(ManifestReferencing(BlobKeys.Prefix + "aaa"));
        return storage;
    }

    [Fact(DisplayName = "初回は回収を実行し、実行時刻を保存先ごとのキーで記録する")]
    public void RunsOnFirstCallAndRecordsTime()
    {
        var store = CreateStore();
        var storage = StorageWithOrphan();

        var result = new SyncRunner(store).CollectGarbageIfDue(storage);

        Assert.NotNull(result);
        Assert.Equal(1, result!.Deleted);
        Assert.False(storage.Has(BlobKeys.Prefix + "bbb"));
        Assert.True(storage.Has(BlobKeys.Prefix + "aaa"));
        Assert.True(CreateStore().Load().LastGcAt.ContainsKey(storage.StateKeyPrefix));
    }

    [Fact(DisplayName = "間隔内の再実行は走査ごと省く")]
    public void SkipsWithinInterval()
    {
        var store = CreateStore();
        var storage = StorageWithOrphan();
        var runner = new SyncRunner(store);

        Assert.NotNull(runner.CollectGarbageIfDue(storage));
        var second = runner.CollectGarbageIfDue(storage);

        Assert.Null(second);
        // S3 互換モードでは走査 (List) がそのまま操作数の課金になるので、
        // スキップ時は判定より先の操作を一切しないことまで確かめる。
        Assert.Equal(1, storage.Calls.Count(c => c == "List:" + BlobKeys.Prefix));
    }

    [Fact(DisplayName = "記録が間隔より古ければ再び実行する")]
    public void RunsAgainAfterInterval()
    {
        var store = CreateStore();
        var storage = StorageWithOrphan();
        var settings = store.Load();
        settings.LastGcAt[storage.StateKeyPrefix] =
            DateTimeOffset.Now - SyncRunner.AutoGcInterval - TimeSpan.FromMinutes(1);
        store.Save(settings);

        var result = new SyncRunner(CreateStore()).CollectGarbageIfDue(storage);

        Assert.NotNull(result);
    }

    [Fact(DisplayName = "回収が中止になっても例外を出さず、間隔内は再試行しない")]
    public void FailureIsSwallowedAndNotRetriedWithinInterval()
    {
        // manifest がまだ無い保存先では、回収は安全側の中止 (例外) になる。
        // 後始末の失敗で Push の成功を汚さないこと、失敗を理由に Push のたびに
        // 走査をやり直さないことを確かめる。
        var store = CreateStore();
        var storage = new FakeSyncStorage { Now = LongAgo };
        storage.Seed(BlobKeys.Prefix + "bbb", "orphan", LongAgo);
        var runner = new SyncRunner(store);

        Assert.Null(runner.CollectGarbageIfDue(storage));
        Assert.Null(runner.CollectGarbageIfDue(storage));

        Assert.True(storage.Has(BlobKeys.Prefix + "bbb"));
        Assert.Equal(1, storage.Calls.Count(c => c == "LoadManifest"));
        Assert.True(CreateStore().Load().LastGcAt.ContainsKey(storage.StateKeyPrefix));
    }

    [Fact(DisplayName = "手動実行は間隔に関わらず回収し、直後の自動実行を省かせる")]
    public void ManualRunIgnoresInterval()
    {
        var store = CreateStore();
        var storage = StorageWithOrphan();
        var runner = new SyncRunner(store);
        Assert.NotNull(runner.CollectGarbageIfDue(storage));

        // 自動実行の直後でも、手動はそのまま走る。
        storage.Seed(BlobKeys.Prefix + "ccc", "orphan2", LongAgo);
        var manual = runner.CollectGarbageNow(storage);

        Assert.Equal(1, manual.Deleted);
        Assert.False(storage.Has(BlobKeys.Prefix + "ccc"));
        // 手動実行も記録を更新するので、続く自動実行は間引かれる。
        Assert.Null(runner.CollectGarbageIfDue(storage));
    }

    [Fact(DisplayName = "手動実行の失敗は呼び出し側へ伝える")]
    public void ManualRunPropagatesFailure()
    {
        // manifest がまだ無い保存先では、回収は安全側の中止 (例外) になる。
        // 手動実行では結果を待っている利用者がいるので、自動実行と違って
        // 握りつぶさず伝える。
        var storage = new FakeSyncStorage { Now = LongAgo };
        storage.Seed(BlobKeys.Prefix + "bbb", "orphan", LongAgo);

        Assert.Throws<SyncStorageException>(
            () => new SyncRunner(CreateStore()).CollectGarbageNow(storage));
        Assert.True(storage.Has(BlobKeys.Prefix + "bbb"));
    }

    [Fact(DisplayName = "実行時刻の記録は、古い設定の保存で巻き戻らない")]
    public void LastGcAtNeverMovesBackwardsOnStaleSave()
    {
        // 別プロセス (常駐 GUI など) が先に設定を読んでおく。
        var store = CreateStore();
        var stale = store.Load();

        // その間に別の経路が回収を実行し、時刻を記録する。
        var recorded = DateTimeOffset.Now;
        CreateStore().SaveLastGcAt("fake|", recorded);

        // 古い設定 (記録なし) のまま通常の Save をしても、記録は消えない。
        stale.MachineName = "PC-1";
        store.Save(stale);

        var loaded = CreateStore().Load();
        Assert.Equal("PC-1", loaded.MachineName);
        Assert.Equal(recorded, loaded.LastGcAt["fake|"]);
    }

    [Fact(DisplayName = "実行時刻の保存は、他の設定に触れない")]
    public void SaveLastGcAtKeepsEverythingElseOnDisk()
    {
        var store = CreateStore();
        var settings = store.Load();
        settings.CloudFolderPath = @"C:\sync";
        settings.AutoSyncEnabled = true;
        store.Save(settings);

        CreateStore().SaveLastGcAt("fake|", DateTimeOffset.Now);

        var loaded = CreateStore().Load();
        Assert.Equal(@"C:\sync", loaded.CloudFolderPath);
        Assert.True(loaded.AutoSyncEnabled);
    }
}
