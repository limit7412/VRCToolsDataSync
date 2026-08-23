using VRCToolsDataSync.Core.Storage;
using VRCToolsDataSync.Core.Sync;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// manifest の読み書きの性質を固定する。ここは同期先で唯一の正なので、
/// 「扱えない形式には触らない」と「書いた形式を正しく宣言する」を確かめる。
/// </summary>
public sealed class ManifestStoreTests
{
    private static ToolManifestEntry EntryWith(long version) => new()
    {
        Version = version,
        MachineName = "test",
        UpdatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
        Files = { new ManifestFile { RelativePath = "vrcx/a", Sha256 = "abc", BlobKey = "blobs/abc", Size = 1 } },
    };

    [Fact(DisplayName = "扱えない schemaVersion を読んだら投げる")]
    public void LoadRejectsNewerSchemaVersion()
    {
        var storage = new FakeSyncStorage();
        var manifest = new SyncManifest { SchemaVersion = SyncManifest.CurrentSchemaVersion + 1 };
        storage.SeedManifest(manifest);

        Assert.Throws<SyncStorageException>(() => new ManifestStore(storage).Load());
    }

    [Fact(DisplayName = "扱えない schemaVersion には書き込まない")]
    public void UpdateRejectsNewerSchemaVersion()
    {
        // デシリアライズは知らないフィールドを黙って捨てる。読んで書き戻すだけで
        // 新しい版が書いた情報が落ちるため、触らせない。
        var storage = new FakeSyncStorage();
        storage.SeedManifest(new SyncManifest { SchemaVersion = SyncManifest.CurrentSchemaVersion + 1 });

        Assert.Throws<SyncStorageException>(
            () => new ManifestStore(storage).UpdateToolEntry("vrcx", 0, EntryWith));
        Assert.DoesNotContain("TrySaveManifest", storage.Calls);
    }

    [Fact(DisplayName = "古い schemaVersion の manifest は読める")]
    public void LoadAcceptsOlderSchemaVersion()
    {
        var storage = new FakeSyncStorage();
        storage.SeedManifest(new SyncManifest { SchemaVersion = 1 });

        var manifest = new ManifestStore(storage).Load();

        Assert.Equal(1, manifest.SchemaVersion);
    }

    [Fact(DisplayName = "保存すると schemaVersion が現行値へ上がる")]
    public void SaveRaisesSchemaVersionToCurrent()
    {
        // #36 で見つかった不具合の回帰。旧形式を読み込むと初期値が JSON の値で
        // 上書きされ、BlobKey を含む内容を 1 と宣言したまま書き出していた。
        var storage = new FakeSyncStorage();
        storage.SeedManifest(new SyncManifest { SchemaVersion = 1 });

        new ManifestStore(storage).UpdateToolEntry("vrcx", 0, EntryWith);

        Assert.Equal(SyncManifest.CurrentSchemaVersion, new ManifestStore(storage).Load().SchemaVersion);
    }

    [Fact(DisplayName = "version を採番して返す")]
    public void AssignsTheNextVersion()
    {
        var storage = new FakeSyncStorage();

        var first = new ManifestStore(storage).UpdateToolEntry("vrcx", 0, EntryWith);
        var second = new ManifestStore(storage).UpdateToolEntry("vrcx", first, EntryWith);

        Assert.Equal(1, first);
        Assert.Equal(2, second);
    }

    [Fact(DisplayName = "見ていた version と違えばコンフリクトとして投げる")]
    public void ThrowsWhenTheEntryChangedUnderneath()
    {
        // 送信の可否をその version 基準で判断した前提が崩れている。押し切ると
        // 「同じ内容だから送らない」と判断した実体を他 PC が置き換えている場合に、
        // manifest の記録と実データがずれる。
        var storage = new FakeSyncStorage();
        new ManifestStore(storage).UpdateToolEntry("vrcx", 0, EntryWith);

        var ex = Assert.Throws<ToolEntryChangedException>(
            () => new ManifestStore(storage).UpdateToolEntry("vrcx", 0, EntryWith));
        Assert.Equal("vrcx", ex.ToolKey);
        Assert.Equal(0, ex.ExpectedVersion);
        Assert.Equal(1, ex.ActualVersion);
    }

    [Fact(DisplayName = "別 tool のエントリは保ったまま更新する")]
    public void KeepsOtherToolEntries()
    {
        var storage = new FakeSyncStorage();
        new ManifestStore(storage).UpdateToolEntry("vrcx", 0, EntryWith);
        new ManifestStore(storage).UpdateToolEntry("friendconnect", 0, EntryWith);

        var manifest = new ManifestStore(storage).Load();

        Assert.True(manifest.Tools.ContainsKey("vrcx"));
        Assert.True(manifest.Tools.ContainsKey("friendconnect"));
    }

    [Fact(DisplayName = "存在しない manifest は空として読む")]
    public void MissingManifestReadsAsEmpty()
    {
        var manifest = new ManifestStore(new FakeSyncStorage()).Load();

        Assert.Empty(manifest.Tools);
    }

    [Fact(DisplayName = "読み直しと保存の間に割り込まれたら、やり直して両方を残す")]
    public void RetriesWhenAnotherWriterSlipsInBetweenLoadAndSave()
    {
        // S3 互換モードでは ETag による条件付き更新で割り込みを検出し、読み直して
        // やり直す。押し切ると、割り込んだ側の更新が黙って消える。
        var storage = new FakeSyncStorage();
        new ManifestStore(storage).UpdateToolEntry("friendconnect", 0, EntryWith);

        // 最初の保存の直前に、他の PC が別 tool を Push したことにする。
        var interrupted = false;
        storage.OnBeforeSaveManifest = () =>
        {
            if (interrupted) return;
            interrupted = true;
            storage.OnBeforeSaveManifest = null;
            new ManifestStore(storage).UpdateToolEntry("friendconnect", 1, EntryWith);
        };

        var version = new ManifestStore(storage).UpdateToolEntry("vrcx", 0, EntryWith);

        // 1 度は弾かれ、読み直して成功していること。
        Assert.Contains("PreconditionFailed", storage.Calls);
        Assert.Equal(1, version);

        // 割り込んだ側の更新が残っていること。
        var manifest = new ManifestStore(storage).Load();
        Assert.Equal(2, manifest.Tools["friendconnect"].Version);
        Assert.Equal(1, manifest.Tools["vrcx"].Version);
    }

    [Fact(DisplayName = "旧形式の記録は RelativePath をキーとして解決する")]
    public void OldSchemaFileResolvesToRelativePath()
    {
        var old = new ManifestFile { RelativePath = "vrcx/latest.sqlite3", Sha256 = "abc" };
        var current = new ManifestFile { RelativePath = "vrcx/latest.sqlite3", Sha256 = "abc", BlobKey = "blobs/abc" };

        Assert.Equal("vrcx/latest.sqlite3", ManifestFileKeys.StorageKeyOf(old));
        Assert.Equal("blobs/abc", ManifestFileKeys.StorageKeyOf(current));
    }
}
