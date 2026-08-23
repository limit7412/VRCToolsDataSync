using VRCToolsDataSync.Core.Storage;
using VRCToolsDataSync.Core.Sync;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// 回収の性質を固定する。ここは唯一データを消す経路なので、
/// 「消してはいけないものを消さない」側を厚く確かめる。
/// </summary>
public sealed class BlobGarbageCollectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromDays(7);

    /// <summary>猶予期間を確実に過ぎている時刻。</summary>
    private static DateTimeOffset LongAgo => Now - TimeSpan.FromDays(30);

    private static SyncManifest ManifestReferencing(params string[] blobKeys)
    {
        var manifest = new SyncManifest();
        manifest.Tools["vrcx"] = new ToolManifestEntry
        {
            Version = 1,
            MachineName = "test",
            UpdatedAt = Now,
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

    private static FakeSyncStorage StorageAt(DateTimeOffset now)
    {
        // 実装は DateTimeOffset.UtcNow を基準に猶予期間を測る。テストからは
        // その現在時刻を動かせないので、同期先側の時刻を過去に置くことで
        // 「古い実体」「新しい実体」を作る。
        var storage = new FakeSyncStorage { Now = now };
        return storage;
    }

    [Fact(DisplayName = "参照されている実体は猶予期間を過ぎていても消さない")]
    public void LiveBlobSurvivesEvenWhenOlderThanGrace()
    {
        var storage = StorageAt(LongAgo);
        storage.Seed(BlobKeys.Prefix + "aaa", "live", LongAgo);
        storage.SeedManifest(ManifestReferencing(BlobKeys.Prefix + "aaa"));

        var result = new BlobGarbageCollector(storage).Collect(Grace);

        Assert.True(storage.Has(BlobKeys.Prefix + "aaa"));
        Assert.Equal(1, result.Live);
        Assert.Equal(0, result.Deleted);
    }

    [Fact(DisplayName = "参照が切れて猶予期間を過ぎた実体は消す")]
    public void OrphanOlderThanGraceIsDeleted()
    {
        var storage = StorageAt(LongAgo);
        storage.Seed(BlobKeys.Prefix + "aaa", "live", LongAgo);
        storage.Seed(BlobKeys.Prefix + "bbb", "orphan", LongAgo);
        storage.SeedManifest(ManifestReferencing(BlobKeys.Prefix + "aaa"));

        var result = new BlobGarbageCollector(storage).Collect(Grace);

        Assert.True(storage.Has(BlobKeys.Prefix + "aaa"));
        Assert.False(storage.Has(BlobKeys.Prefix + "bbb"));
        Assert.Equal(1, result.Deleted);
    }

    [Fact(DisplayName = "参照が切れていても猶予期間内の実体は残す")]
    public void OrphanWithinGraceIsKept()
    {
        // 他の PC が今まさに送っている最中の実体を巻き込まないための性質。
        var storage = StorageAt(DateTimeOffset.UtcNow);
        storage.Seed(BlobKeys.Prefix + "aaa", "live", DateTimeOffset.UtcNow);
        storage.Seed(BlobKeys.Prefix + "fresh", "in flight", DateTimeOffset.UtcNow);
        storage.SeedManifest(ManifestReferencing(BlobKeys.Prefix + "aaa"));

        var result = new BlobGarbageCollector(storage).Collect(Grace);

        Assert.True(storage.Has(BlobKeys.Prefix + "fresh"));
        Assert.Equal(1, result.Young);
        Assert.Equal(0, result.Deleted);
    }

    [Fact(DisplayName = "参照が一件も無い場合は何も消さずに中止する")]
    public void AbortsWhenNoLiveReferenceIsFound()
    {
        // manifest を読めない一瞬に当たると空の manifest が返る。そのまま走らせると
        // 生きている実体まで全部消すため、区別が付かない以上どちらでも走らせない。
        var storage = StorageAt(LongAgo);
        storage.Seed(BlobKeys.Prefix + "aaa", "would be deleted", LongAgo);
        storage.ClearManifest();

        Assert.Throws<SyncStorageException>(() => new BlobGarbageCollector(storage).Collect(Grace));
        Assert.True(storage.Has(BlobKeys.Prefix + "aaa"));
    }

    [Fact(DisplayName = "扱えない形式の manifest では何も消さずに中止する")]
    public void AbortsOnUnsupportedSchemaVersion()
    {
        // 知らない形式が持つ参照は数え切れない。既知のエントリが 1 つでもあれば
        // 上のゼロ件の検査は素通りするので、別の条件として要る。
        var storage = StorageAt(LongAgo);
        storage.Seed(BlobKeys.Prefix + "aaa", "live", LongAgo);
        storage.Seed(BlobKeys.Prefix + "unseen", "referenced by a newer format", LongAgo);
        var manifest = ManifestReferencing(BlobKeys.Prefix + "aaa");
        manifest.SchemaVersion = SyncManifest.CurrentSchemaVersion + 1;
        storage.SeedManifest(manifest);

        Assert.Throws<SyncStorageException>(() => new BlobGarbageCollector(storage).Collect(Grace));
        Assert.True(storage.Has(BlobKeys.Prefix + "unseen"));
    }

    [Fact(DisplayName = "列挙してから削除するまでに書き直された実体は消さない")]
    public void BlobRewrittenAfterListingIsKept()
    {
        // 列挙は取った時点の写しでしかない。別の PC の Push が同じ内容を再利用して
        // 置き直すと、その実体はこれから公開される manifest に参照される。
        var storage = StorageAt(LongAgo);
        storage.Seed(BlobKeys.Prefix + "aaa", "live", LongAgo);
        storage.Seed(BlobKeys.Prefix + "reused", "orphan for now", LongAgo);
        storage.SeedManifest(ManifestReferencing(BlobKeys.Prefix + "aaa"));

        // 列挙が終わった直後に、別の PC が置き直したことにする。
        storage.OnListed = key =>
        {
            if (key != BlobKeys.Prefix + "reused") return;
            storage.Seed(BlobKeys.Prefix + "reused", "orphan for now", DateTimeOffset.UtcNow);
        };

        var result = new BlobGarbageCollector(storage).Collect(Grace);

        Assert.True(storage.Has(BlobKeys.Prefix + "reused"));
        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.Young);
    }

    [Fact(DisplayName = "読み直した後に書き直された実体は消さない")]
    public void BlobRewrittenAfterStatIsKept()
    {
        // 列挙後の読み直しでも捕まらない、さらに内側の隙間。読み直してから削除するまでに
        // 別の PC が置き直すと、その実体はこれから公開される manifest に参照される。
        // 削除を「読み直したときのまま」を条件にすることで、ここを取り違えない。
        var storage = StorageAt(LongAgo);
        storage.Seed(BlobKeys.Prefix + "aaa", "live", LongAgo);
        storage.Seed(BlobKeys.Prefix + "reused", "orphan for now", LongAgo);
        storage.SeedManifest(ManifestReferencing(BlobKeys.Prefix + "aaa"));

        // Stat を返した直後に、別の PC が同じ日時のまま置き直したことにする。
        // 日時での判定はすり抜けるので、印 (ETag) でしか捕まらない。
        storage.OnStat = key =>
        {
            if (key != BlobKeys.Prefix + "reused") return;
            storage.OnStat = null;
            storage.Seed(BlobKeys.Prefix + "reused", "orphan for now", LongAgo);
        };

        var result = new BlobGarbageCollector(storage).Collect(Grace);

        Assert.True(storage.Has(BlobKeys.Prefix + "reused"));
        Assert.Contains("PreconditionFailed:" + BlobKeys.Prefix + "reused", storage.Calls);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.Young);
    }

    [Fact(DisplayName = "一件の削除失敗で全体を止めない")]
    public void SingleDeleteFailureDoesNotStopTheRun()
    {
        var storage = StorageAt(LongAgo);
        storage.Seed(BlobKeys.Prefix + "aaa", "live", LongAgo);
        storage.Seed(BlobKeys.Prefix + "bad", "cannot delete", LongAgo);
        storage.Seed(BlobKeys.Prefix + "good", "can delete", LongAgo);
        storage.SeedManifest(ManifestReferencing(BlobKeys.Prefix + "aaa"));
        storage.DeleteFailures.Add(BlobKeys.Prefix + "bad");

        var result = new BlobGarbageCollector(storage).Collect(Grace);

        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Deleted);
        Assert.True(storage.Has(BlobKeys.Prefix + "bad"));
        Assert.False(storage.Has(BlobKeys.Prefix + "good"));
    }

    [Fact(DisplayName = "dry_run では数えるだけで消さない")]
    public void DryRunCountsWithoutDeleting()
    {
        var storage = StorageAt(LongAgo);
        storage.Seed(BlobKeys.Prefix + "aaa", "live", LongAgo);
        storage.Seed(BlobKeys.Prefix + "bbb", "orphan", LongAgo);
        storage.SeedManifest(ManifestReferencing(BlobKeys.Prefix + "aaa"));

        var result = new BlobGarbageCollector(storage).Collect(Grace, dryRun: true);

        Assert.Equal(1, result.Deleted);
        Assert.True(storage.Has(BlobKeys.Prefix + "bbb"));
        Assert.DoesNotContain(storage.Calls, c => c.StartsWith("TryDelete:", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "manifest を読んでから列挙する")]
    public void ReadsManifestBeforeListing()
    {
        // 逆順にすると、列挙してから manifest を読むまでの間に公開された
        // 実体を取りこぼす。
        var storage = StorageAt(LongAgo);
        storage.Seed(BlobKeys.Prefix + "aaa", "live", LongAgo);
        storage.SeedManifest(ManifestReferencing(BlobKeys.Prefix + "aaa"));

        new BlobGarbageCollector(storage).Collect(Grace);

        var load = storage.Calls.IndexOf("LoadManifest");
        var list = storage.Calls.FindIndex(c => c.StartsWith("List:", StringComparison.Ordinal));
        Assert.True(load >= 0 && list >= 0);
        Assert.True(load < list, $"LoadManifest ({load}) は List ({list}) より先に呼ばれること");
    }

    [Fact(DisplayName = "blobs 以外のキーは走査しない")]
    public void ScansOnlyTheBlobPrefix()
    {
        // 旧形式で置かれた実データや manifest 自身を巻き込まない。
        var storage = StorageAt(LongAgo);
        storage.Seed(BlobKeys.Prefix + "aaa", "live", LongAgo);
        storage.Seed("vrcx/latest.sqlite3", "old layout", LongAgo);
        storage.Seed(ManifestStore.ManifestKey, "{}", LongAgo);
        storage.SeedManifest(ManifestReferencing(BlobKeys.Prefix + "aaa"));

        var result = new BlobGarbageCollector(storage).Collect(Grace);

        Assert.True(storage.Has("vrcx/latest.sqlite3"));
        Assert.True(storage.Has(ManifestStore.ManifestKey));
        Assert.Equal(1, result.Scanned);
    }

    [Fact(DisplayName = "猶予期間に負の値は指定できない")]
    public void RejectsNegativeGracePeriod()
    {
        var storage = StorageAt(LongAgo);
        storage.SeedManifest(ManifestReferencing(BlobKeys.Prefix + "aaa"));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BlobGarbageCollector(storage).Collect(TimeSpan.FromSeconds(-1)));
    }
}
