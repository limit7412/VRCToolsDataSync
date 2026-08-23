using VRCToolsDataSync.Core.Sync;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// 送信の性質を固定する。置き場所を内容から決めている以上、
/// 「キーが表す内容と実際に置かれる内容が一致する」が最も守るべき性質になる。
/// </summary>
public sealed class SyncTransferTests : IDisposable
{
    private readonly string _workDirectory;

    public SyncTransferTests()
    {
        _workDirectory = Path.Combine(Path.GetTempPath(), "vrctds-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDirectory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_workDirectory, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact(DisplayName = "送った内容のハッシュが、記録したキーと一致する")]
    public void SentContentMatchesTheRecordedKey()
    {
        var storage = new FakeSyncStorage();
        var source = WriteFile("config.json", "original");

        var (file, sent) = SyncTransfer.Send(storage, Array.Empty<ManifestFile>(), source, "fc/config.json");

        Assert.True(sent);
        Assert.Equal(BlobKeys.FromSha256(file.Sha256), file.BlobKey);
        Assert.Equal("original", storage.TextOf(file.BlobKey!));
    }

    [Fact(DisplayName = "ハッシュを取った後に元ファイルが変わっても、キーと内容はずれない")]
    public void KeyAndContentStayConsistentWhenTheSourceChangesMidPush()
    {
        // #36 で見つかった不具合の回帰。元ファイルを直接ハッシュして直接送っていた
        // ため、その間に書き換わると blobs/<変更前のハッシュ> に変更後の内容が入った。
        // 同じキーには同じ中身しか入らない、という前提が崩れる。
        var storage = new FakeSyncStorage();
        var source = WriteFile("config.json", "before");

        // 書き出し先を確保した直後 (= ハッシュを取った後) に元ファイルが変わる。
        storage.OnBeginUpload = () => File.WriteAllText(source, "after the hash was taken");

        var (file, sent) = SyncTransfer.Send(storage, Array.Empty<ManifestFile>(), source, "fc/config.json");

        Assert.True(sent);
        // 記録したハッシュと、実際に置かれた内容のハッシュが一致していること。
        // どちらの内容が送られたかは問わない。ずれないことだけが要る。
        var storedPath = Path.Combine(_workDirectory, "stored");
        File.WriteAllBytes(storedPath, storage.ContentOf(file.BlobKey!));
        Assert.Equal(file.Sha256, FileHasher.Sha256(storedPath));
        Assert.Equal(BlobKeys.FromSha256(file.Sha256), file.BlobKey);
    }

    [Fact(DisplayName = "同じ内容が既にあるなら送らない")]
    public void SkipsWhenTheSameContentIsAlreadyRecordedAndPresent()
    {
        var storage = new FakeSyncStorage();
        var source = WriteFile("config.json", "same");
        var sha = FileHasher.Sha256(source);
        var blobKey = BlobKeys.FromSha256(sha);
        storage.Seed(blobKey, "same");

        var remote = new[]
        {
            new ManifestFile { RelativePath = "fc/config.json", Sha256 = sha, BlobKey = blobKey, Size = 4 },
        };

        var (_, sent) = SyncTransfer.Send(storage, remote, source, "fc/config.json");

        Assert.False(sent);
        // 省ける場合は写しも作らない。同期フォルダモードでは、この写しが
        // そのまま同期クライアントの差分になる。
        Assert.DoesNotContain("BeginUpload", storage.Calls);
    }

    [Fact(DisplayName = "manifest に記録があっても実体が無ければ送り直す")]
    public void ResendsWhenTheRecordedBlobIsMissing()
    {
        // 中断した Push で実体が欠けている状態。記録だけを見て省くと、
        // 他の PC の Pull が失敗し続ける。
        var storage = new FakeSyncStorage();
        var source = WriteFile("config.json", "same");
        var sha = FileHasher.Sha256(source);

        var remote = new[]
        {
            new ManifestFile
            {
                RelativePath = "fc/config.json",
                Sha256 = sha,
                BlobKey = BlobKeys.FromSha256(sha),
                Size = 4,
            },
        };

        var (file, sent) = SyncTransfer.Send(storage, remote, source, "fc/config.json");

        Assert.True(sent);
        Assert.True(storage.Has(file.BlobKey!));
    }

    [Fact(DisplayName = "旧形式の記録は未変更と見なさない")]
    public void OldSchemaEntryCountsAsChanged()
    {
        // #36 で見つかった不具合の回帰。schemaVersion 1 の manifest は
        // RelativePath をそのままキーにしている。内容が同じでも置き場所が違うので、
        // 未変更と見なすと manifest が旧キーを指したまま残り、送り直した実体が
        // 孤児になる。しかも次の Push でも同じ判定になり、送り直しを繰り返す。
        var previous = new ToolManifestEntry
        {
            Version = 1,
            Files =
            {
                // 旧形式には BlobKey が無い。
                new ManifestFile { RelativePath = "vrcx/latest.sqlite3", Sha256 = "abc", Size = 1 },
            },
        };
        var current = new[]
        {
            new ManifestFile
            {
                RelativePath = "vrcx/latest.sqlite3",
                Sha256 = "abc",
                BlobKey = BlobKeys.FromSha256("abc"),
                Size = 1,
            },
        };

        Assert.False(SyncTransfer.IsUnchangedSet(previous, current));
    }

    [Fact(DisplayName = "内容も置き場所も同じなら未変更と見なす")]
    public void SameContentAndSamePlacementCountsAsUnchanged()
    {
        var file = new ManifestFile
        {
            RelativePath = "vrcx/latest.sqlite3",
            Sha256 = "abc",
            BlobKey = BlobKeys.FromSha256("abc"),
            Size = 1,
        };
        var previous = new ToolManifestEntry { Version = 1, Files = { file } };

        Assert.True(SyncTransfer.IsUnchangedSet(previous, new[] { file }));
    }

    [Fact(DisplayName = "ファイルが増えていれば未変更と見なさない")]
    public void AddedFileCountsAsChanged()
    {
        var previous = new ToolManifestEntry
        {
            Version = 1,
            Files = { new ManifestFile { RelativePath = "a", Sha256 = "abc", BlobKey = "blobs/abc" } },
        };
        var current = new[]
        {
            new ManifestFile { RelativePath = "a", Sha256 = "abc", BlobKey = "blobs/abc" },
            new ManifestFile { RelativePath = "b", Sha256 = "def", BlobKey = "blobs/def" },
        };

        Assert.False(SyncTransfer.IsUnchangedSet(previous, current));
    }

    [Fact(DisplayName = "前回の記録が無ければ未変更と見なさない")]
    public void NoPreviousEntryCountsAsChanged()
        => Assert.False(SyncTransfer.IsUnchangedSet(null, Array.Empty<ManifestFile>()));

    [Fact(DisplayName = "任意のバイト列でもキーと内容がずれない")]
    public void BinaryContentKeepsTheKeyAndContentConsistent()
    {
        // 同期の対象は SQLite のスナップショットが主で、テキストではない。
        // 文字列に通す経路があると不正な UTF-8 が置換され、ハッシュの元と
        // 保存した内容がずれる。
        var storage = new FakeSyncStorage();
        var bytes = new byte[256];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)i;
        var source = Path.Combine(_workDirectory, "snapshot.sqlite3");
        File.WriteAllBytes(source, bytes);

        var (file, sent) = SyncTransfer.Send(storage, Array.Empty<ManifestFile>(), source, "vrcx/latest.sqlite3");

        Assert.True(sent);
        Assert.Equal(bytes, storage.ContentOf(file.BlobKey!));
        Assert.Equal(BlobKeys.FromSha256(file.Sha256), file.BlobKey);
    }
}
