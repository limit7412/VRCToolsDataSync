using System.Net;
using System.Net.Http;
using VRCToolsDataSync.Core.Update;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// 候補を集める経路の組み立てを固定する (issue #45)。
/// 応答は偽のハンドラで作り、どの要求を出すか、失敗をどう扱うかを見る。
/// </summary>
public sealed class GitHubReleaseFetchTests
{
    /// <summary>要求ごとに応答を差し替えられるハンドラ。要求した URL も残す。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _respond;

        public List<string> RequestedPaths { get; } = new();

        public StubHandler(Func<string, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            RequestedPaths.Add(url);
            return Task.FromResult(_respond(url));
        }
    }

    private const string BaseUrl = "https://api.example.com/repos/limit7412/VRCToolsDataSync";

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static string ReleaseJson(string tag, bool prerelease = false)
        => $$"""
            {
              "tag_name": "{{tag}}",
              "html_url": "https://example.com/{{tag}}",
              "prerelease": {{(prerelease ? "true" : "false")}},
              "draft": false,
              "assets": []
            }
            """;

    /// <summary>1 ページ分に満たない件数を返す一覧。これで「最後まで取れた」になる。</summary>
    private static string ShortListJson(params string[] tags)
        => "[" + string.Join(",", tags.Select(t => ReleaseJson(t))) + "]";

    [Fact(DisplayName = "一覧を最後まで取れたら latest は見に行かない")]
    public async Task SkipsLatestWhenListingIsComplete()
    {
        var handler = new StubHandler(_ => Json(ShortListJson("0.0.9", "0.0.10")));
        using var repository = new GitHubReleaseRepository("asset.zip", BaseUrl, handler);

        var catalog = await repository.FetchReleasesAsync();

        Assert.True(catalog.Complete);
        Assert.Equal(2, catalog.Releases.Count);
        // 一覧に全てが入っているので、latest への要求で枠を使わない。
        Assert.DoesNotContain(handler.RequestedPaths, p => p.EndsWith("/releases/latest", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "一覧が上限で切れたときだけ latest を見る")]
    public async Task FetchesLatestOnlyWhenListingIsTruncated()
    {
        // 3 ページとも満杯で返し、上限に達した状態を作る。
        var full = "[" + string.Join(",", Enumerable.Range(1, 100).Select(i => ReleaseJson($"1.0.{i}"))) + "]";
        var handler = new StubHandler(url =>
            url.EndsWith("/releases/latest", StringComparison.Ordinal)
                ? Json(ReleaseJson("0.9.0"))
                : Json(full));
        using var repository = new GitHubReleaseRepository("asset.zip", BaseUrl, handler);

        var catalog = await repository.FetchReleasesAsync();

        Assert.False(catalog.Complete);
        Assert.Contains(handler.RequestedPaths, p => p.EndsWith("/releases/latest", StringComparison.Ordinal));
        // 一覧から押し出された安定版を latest から拾えている。
        Assert.Contains(catalog.Releases, r => r.Tag == "0.9.0");
    }

    [Fact(DisplayName = "latest を取れなくても、一覧の範囲だけで判断する")]
    public async Task KeepsListedCandidatesWhenLatestFails()
    {
        var full = "[" + string.Join(",", Enumerable.Range(1, 100).Select(i => ReleaseJson($"1.0.{i}"))) + "]";
        var handler = new StubHandler(url =>
            url.EndsWith("/releases/latest", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : Json(full));
        using var repository = new GitHubReleaseRepository("asset.zip", BaseUrl, handler);

        var catalog = await repository.FetchReleasesAsync();

        // 残りのレート枠が尽きただけで、手元にある候補ごと捨てるわけにはいかない。
        Assert.False(catalog.Complete);
        Assert.Equal(300, catalog.Releases.Count);
    }

    [Fact(DisplayName = "一覧の 1 ページ目を取れなければ例外を投げる")]
    public async Task ThrowsWhenFirstPageFails()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var repository = new GitHubReleaseRepository("asset.zip", BaseUrl, handler);

        // 候補が 1 つも無い状態は「最新である」とは違う。呼び出し側が
        // Unreachable として扱えるよう、ここでは握らない。
        await Assert.ThrowsAsync<HttpRequestException>(() => repository.FetchReleasesAsync());
    }

    [Fact(DisplayName = "2 ページ目以降で切れても、取れた候補は返す")]
    public async Task KeepsFirstPageWhenLaterPageFails()
    {
        var full = "[" + string.Join(",", Enumerable.Range(1, 100).Select(i => ReleaseJson($"1.0.{i}"))) + "]";
        // 一覧の URL は per_page も持つため、末尾で見る (Contains だと
        // "per_page=100" が "page=1" を含んでしまい、全ページが成功扱いになる)。
        var handler = new StubHandler(url =>
            url.EndsWith("&page=1", StringComparison.Ordinal)
                ? Json(full)
                : new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var repository = new GitHubReleaseRepository("asset.zip", BaseUrl, handler);

        var catalog = await repository.FetchReleasesAsync();

        // 残りのレート枠が尽きただけで、1 ページ目に入っている新しい版まで
        // 見落とすわけにはいかない。
        Assert.Equal(100, catalog.Releases.Count);
        Assert.False(catalog.Complete);
    }

    [Fact(DisplayName = "安定版がまだ無いリポジトリでも失敗にしない")]
    public async Task TreatsMissingLatestAsEmpty()
    {
        var full = "[" + string.Join(",", Enumerable.Range(1, 100).Select(i => ReleaseJson($"1.0.{i}-test1", prerelease: true))) + "]";
        var handler = new StubHandler(url =>
            url.EndsWith("/releases/latest", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Json(full));
        using var repository = new GitHubReleaseRepository("asset.zip", BaseUrl, handler);

        var catalog = await repository.FetchReleasesAsync();

        Assert.Equal(300, catalog.Releases.Count);
    }
}
