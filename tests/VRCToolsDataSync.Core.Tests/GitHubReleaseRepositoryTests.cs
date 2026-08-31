using VRCToolsDataSync.Core.Infra;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// GitHub Releases API の応答の読み取りを固定する (issue #45)。
/// 通信そのものは行わず、応答の JSON を直接渡して確かめる。
/// </summary>
public sealed class GitHubReleaseRepositoryTests
{
    private const string AssetName = "VRCToolsDataSync-win-x64.zip";

    private static string ReleaseJson(
        string tag,
        bool prerelease = false,
        bool draft = false,
        string? assetName = null,
        string? digest = null,
        long size = 100,
        string state = "uploaded")
    {
        var assets = assetName is null
            ? "[]"
            : $$"""
              [{
                "name": "{{assetName}}",
                "browser_download_url": "https://example.com/{{assetName}}",
                "digest": {{(digest is null ? "null" : $"\"{digest}\"")}},
                "size": {{size}},
                "state": "{{state}}"
              }]
              """;
        return $$"""
            {
              "tag_name": "{{tag}}",
              "html_url": "https://github.com/limit7412/VRCToolsDataSync/releases/tag/{{tag}}",
              "prerelease": {{(prerelease ? "true" : "false")}},
              "draft": {{(draft ? "true" : "false")}},
              "assets": {{assets}}
            }
            """;
    }

    private static readonly string ValidDigest = "sha256:" + new string('a', 64);

    [Fact(DisplayName = "下書きと読めないタグは黙って捨てる")]
    public void SkipsDraftsAndUnparsableTags()
    {
        var body = $"[{ReleaseJson("0.0.9")},{ReleaseJson("0.0.10", draft: true)},{ReleaseJson("nightly-build")}]";

        var releases = GitHubReleaseRepository.Parse(body, AssetName);

        var release = Assert.Single(releases);
        Assert.Equal("0.0.9", release.Tag);
    }

    [Fact(DisplayName = "プレリリースの印とタグの綴りの両方を安定版の判定に使う")]
    public void StableRequiresBothTagAndFlag()
    {
        var body = "[" + string.Join(",",
            ReleaseJson("1.0.0"),
            // 手動で作ったリリースにプレリリースの印だけが付いたケース。
            ReleaseJson("1.0.1", prerelease: true),
            ReleaseJson("1.0.2-test1")) + "]";

        var releases = GitHubReleaseRepository.Parse(body, AssetName);

        Assert.Equal(3, releases.Count);
        Assert.True(releases[0].IsStable);
        Assert.False(releases[1].IsStable);
        Assert.False(releases[2].IsStable);
    }

    [Fact(DisplayName = "digest を持つ同名の配布物だけを拾う")]
    public void PicksOnlyVerifiableAssetOfExpectedName()
    {
        var withAsset = GitHubReleaseRepository.Parse(
            $"[{ReleaseJson("1.0.0", assetName: AssetName, digest: ValidDigest)}]", AssetName);
        var wrongName = GitHubReleaseRepository.Parse(
            $"[{ReleaseJson("1.0.0", assetName: "VRCToolsDataSync-win-arm64.zip", digest: ValidDigest)}]", AssetName);
        var noDigest = GitHubReleaseRepository.Parse(
            $"[{ReleaseJson("1.0.0", assetName: AssetName, digest: null)}]", AssetName);
        var uploading = GitHubReleaseRepository.Parse(
            $"[{ReleaseJson("1.0.0", assetName: AssetName, digest: ValidDigest, state: "starter")}]", AssetName);

        var asset = Assert.Single(withAsset)!.Asset;
        Assert.NotNull(asset);
        // 接頭辞 sha256: は落とし、16 進の本体だけを持つ。
        Assert.Equal(new string('a', 64), asset!.DigestHex);
        Assert.Null(Assert.Single(wrongName).Asset);
        Assert.Null(Assert.Single(noDigest).Asset);
        Assert.Null(Assert.Single(uploading).Asset);
    }

    [Fact(DisplayName = "/releases/latest の 1 件も同じ規則で読む")]
    public void ParsesSingleLatestRelease()
    {
        var release = Assert.Single(GitHubReleaseRepository.ParseOne(ReleaseJson("1.2.3"), AssetName));
        Assert.Equal("1.2.3", release.Tag);

        // latest が下書きを返すことは無いはずだが、規則は一覧と揃えておく。
        Assert.Empty(GitHubReleaseRepository.ParseOne(ReleaseJson("1.2.3", draft: true), AssetName));
    }

    [Fact(DisplayName = "一覧と latest の重複はタグで除く")]
    public void MergeDeduplicatesByTag()
    {
        var listed = GitHubReleaseRepository.Parse(
            $"[{ReleaseJson("1.0.1-test1", prerelease: true)},{ReleaseJson("1.0.0")}]", AssetName);
        var latest = GitHubReleaseRepository.ParseOne(ReleaseJson("1.0.0"), AssetName);

        var merged = GitHubReleaseRepository.Merge(listed, latest);

        Assert.Equal(2, merged.Count);

        // 一覧から押し出された安定版は latest から拾える。
        var pushedOut = GitHubReleaseRepository.ParseOne(ReleaseJson("0.9.0"), AssetName);
        var mergedWithExtra = GitHubReleaseRepository.Merge(merged, pushedOut);
        Assert.Equal(3, mergedWithExtra.Count);
    }
}
