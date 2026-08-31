using System.Security.Cryptography;
using VRCToolsDataSync.Core.Domain;
using VRCToolsDataSync.Core.Infra;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// 配布物の取得時の照合を固定する (issue #45 第 3 段階)。
/// 置き換えるのは実行ファイル一式なので、宣言と食い違うものは
/// ファイルとして残らないことまで確かめる。
/// </summary>
public sealed class UpdateDownloadStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "vrctoolsdatasync-tests-" + Guid.NewGuid().ToString("N"));

    public UpdateDownloadStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort */ }
    }

    private string PathFor(string name) => Path.Combine(_directory, name);

    private static ReleaseAsset AssetFor(byte[] content, long? declaredSize = null, string? digest = null)
        => new(
            "VRCToolsDataSync-win-x64.zip",
            "https://example.com/asset",
            digest ?? Convert.ToHexStringLower(SHA256.HashData(content)),
            declaredSize ?? content.Length);

    [Fact(DisplayName = "宣言どおりの内容だけがファイルとして残る")]
    public async Task StoresContentThatMatchesDeclaration()
    {
        var content = new byte[200_000];
        Random.Shared.NextBytes(content);
        var path = PathFor("staged.zip");

        await GitHubReleaseRepository.StoreAsync(new MemoryStream(content), AssetFor(content), path);

        Assert.Equal(content, await File.ReadAllBytesAsync(path));
    }

    [Fact(DisplayName = "宣言より大きいものは受け取りを打ち切り、残さない")]
    public async Task AbortsWhenContentExceedsDeclaredSize()
    {
        var content = new byte[1000];
        var path = PathFor("staged.zip");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GitHubReleaseRepository.StoreAsync(new MemoryStream(content), AssetFor(content, declaredSize: 100), path));

        Assert.False(File.Exists(path));
    }

    [Fact(DisplayName = "宣言より小さいものも受け入れず、残さない")]
    public async Task RejectsTruncatedContent()
    {
        var content = new byte[1000];
        var path = PathFor("staged.zip");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GitHubReleaseRepository.StoreAsync(new MemoryStream(content), AssetFor(content, declaredSize: 2000), path));

        Assert.False(File.Exists(path));
    }

    [Fact(DisplayName = "digest が合わないものは残さない")]
    public async Task RejectsDigestMismatch()
    {
        var content = new byte[1000];
        var path = PathFor("staged.zip");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GitHubReleaseRepository.StoreAsync(
                new MemoryStream(content), AssetFor(content, digest: new string('b', 64)), path));

        Assert.False(File.Exists(path));
    }
}
