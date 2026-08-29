using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VRCToolsDataSync.Core.Update;

/// <summary>
/// GitHub Releases API からリリースの候補を集める実装 (issue #45)。
/// <para>
/// 一覧と /releases/latest の両方を見て、重複を除いて返す。
/// 一覧だけでは足りない。作成の新しい順に返るため、安定版を出した後に
/// プレリリースが積まれ続けると、その安定版はいずれ取得の範囲から落ちる。
/// /releases/latest だけでも足りない。これが返すのは GitHub が latest とした 1 件、
/// すなわち下書きとプレリリースを除いて最も新しく作られたものであり、
/// 版番号が最大のものとは限らない。
/// </para>
/// </summary>
public sealed class GitHubReleaseRepository : IReleaseRepository, IDisposable
{
    private const string DefaultBaseUrl = "https://api.github.com/repos/limit7412/VRCToolsDataSync";

    /// <summary>一覧の 1 ページあたりの件数。100 が API の上限である。</summary>
    private const int PerPage = 100;

    /// <summary>
    /// 一覧をたどるページ数の上限。
    /// <para>
    /// 上限を置くのは、たどる回数を積まれた数に比例させないためである。
    /// 未認証の上限は IP あたり 60 回/時であり、確認のたびに際限なくたどると
    /// そこへ近づくほど確認そのものが失敗しやすくなる。
    /// 上限に達したら Complete=false で返す。黙って切ると「全部見た」と読めてしまう。
    /// </para>
    /// </summary>
    private const int MaxPages = 3;

    // 常駐アプリの片手間の確認であり、待たされてまで通す価値は無い。
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _assetName;
    private readonly ILogger _logger;

    /// <param name="assetName">
    /// 置き換えに使う配布物の名前 (<see cref="ReleaseAsset.NameForCurrentArchitecture"/>)。
    /// null なら配布物を拾わず、確認だけを行う。
    /// </param>
    /// <param name="baseUrl">テストから差し替えるための API の起点。</param>
    public GitHubReleaseRepository(
        string? assetName,
        string? baseUrl = null,
        HttpMessageHandler? handler = null,
        ILogger? logger = null)
    {
        _assetName = assetName ?? string.Empty;
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _logger = logger ?? NullLogger.Instance;
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.Timeout = RequestTimeout;
        // GitHub API は User-Agent の無い要求を拒む。
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("VRCToolsDataSync", "1"));
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public void Dispose() => _httpClient.Dispose();

    public async Task<ReleaseCatalog> FetchReleasesAsync(CancellationToken cancellationToken = default)
    {
        var (listed, complete) = await FetchAllAsync(cancellationToken).ConfigureAwait(false);

        // 一覧を最後までたどれたなら、そこに全てのリリースが入っている。
        // latest はその部分集合であり、確かめに行く必要が無い。
        if (complete) return new ReleaseCatalog(listed, true);

        // 一覧が上限で切れた場合だけ latest を見る。押し出された安定版を拾うためである。
        // ここでの失敗は握る。一覧は取れており、集めきれていないことは
        // complete=false が伝える。残りのレート枠が尽きただけで、
        // 手元にある候補ごと「届かなかった」ことにするのは行き過ぎである。
        IReadOnlyList<ReleaseInfo> latest;
        try
        {
            latest = await FetchLatestStableAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "最新の安定版を取れなかった。一覧の範囲だけで判断する");
            latest = Array.Empty<ReleaseInfo>();
        }

        return new ReleaseCatalog(Merge(listed, latest), false);
    }

    /// <summary>
    /// 集めた候補と、最後まで取れたかを返す。
    /// <para>
    /// 2 ページ目以降で失敗しても、そこまでに取れた候補は返す。
    /// 手元には既に候補があり、その中に新しい版がいるかもしれない。
    /// 集めきれていないことは Complete=false が伝えるので、
    /// 「最新である」と言い切ることにはならない。
    /// 1 ページも取れなかった場合だけ投げる。候補が空なのは、
    /// 「新しい版が無い」ではなく「届かなかった」である。
    /// </para>
    /// </summary>
    private async Task<(List<ReleaseInfo> Releases, bool Complete)> FetchAllAsync(CancellationToken cancellationToken)
    {
        var releases = new List<ReleaseInfo>();
        for (var page = 1; page <= MaxPages; page++)
        {
            List<ReleasePayload> payloads;
            try
            {
                var body = await GetStringAsync(
                    $"{_baseUrl}/releases?per_page={PerPage}&page={page}", cancellationToken).ConfigureAwait(false);
                payloads = JsonSerializer.Deserialize<List<ReleasePayload>>(body)
                    ?? throw new HttpRequestException("GitHub API の応答を読めなかった");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // 1 ページ目を取れているかで分ける。取れた候補の数では分けない。
            // 1 ページ分すべてが読めないタグ (下書きなど) の場合に、届いている
            // のに「届かなかった」と扱うことになる。
            catch (Exception ex) when (page > 1)
            {
                _logger.LogWarning(ex, "リリースの一覧を {Page} ページ目で取れなかった。そこまでの範囲で判断する", page);
                return (releases, false);
            }

            releases.AddRange(BuildAll(payloads, _assetName));

            // 上限に満たない件数で返ってきたら最後のページである。
            if (payloads.Count < PerPage) return (releases, true);
        }
        return (releases, false);
    }

    /// <summary>
    /// 安定版が 1 つも無いリポジトリでは 404 が返る。
    /// 取れなかったのではなく「無い」ので、空として扱う。
    /// </summary>
    private async Task<IReadOnlyList<ReleaseInfo>> FetchLatestStableAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            $"{_baseUrl}/releases/latest", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return Array.Empty<ReleaseInfo>();
        EnsureSuccess(response);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseOne(body, _assetName);
    }

    private async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub API が {(int)response.StatusCode} を返した");
        }
    }

    /// <summary>
    /// タグで重複を除く。一覧と latest は同じリリースを返しうる。
    /// 通信から切り離してテストで確かめられるようにしてある。
    /// </summary>
    internal static List<ReleaseInfo> Merge(List<ReleaseInfo> releases, IReadOnlyList<ReleaseInfo> extra)
    {
        var seen = new HashSet<string>(releases.Select(r => r.Tag), StringComparer.Ordinal);
        releases.AddRange(extra.Where(r => !seen.Contains(r.Tag)));
        return releases;
    }

    /// <summary>
    /// 版として読めないタグと下書きは黙って捨てる。
    /// 確認の目的は新しい版を見つけることであり、運用しているタグの綴りから
    /// 外れたものを見つけられなくても実害が無い。
    /// 応答の読み取りだけをテストで確かめられるよう、通信から切り離してある。
    /// </summary>
    internal static List<ReleaseInfo> Parse(string body, string assetName)
    {
        var payloads = JsonSerializer.Deserialize<List<ReleasePayload>>(body) ?? new List<ReleasePayload>();
        return BuildAll(payloads, assetName);
    }

    /// <summary>/releases/latest は配列ではなく 1 件を返す。</summary>
    internal static IReadOnlyList<ReleaseInfo> ParseOne(string body, string assetName)
    {
        var payload = JsonSerializer.Deserialize<ReleasePayload>(body);
        var release = payload is null ? null : Build(payload, assetName);
        return release is null ? Array.Empty<ReleaseInfo>() : new[] { release };
    }

    private static List<ReleaseInfo> BuildAll(List<ReleasePayload> payloads, string assetName)
        => payloads.Select(p => Build(p, assetName)).Where(r => r is not null).Select(r => r!).ToList();

    private static ReleaseInfo? Build(ReleasePayload payload, string assetName)
    {
        if (payload.Draft) return null;
        var version = ReleaseVersion.Parse(payload.TagName);
        if (version is null || payload.TagName is null || payload.HtmlUrl is null) return null;

        return new ReleaseInfo(version, payload.TagName, payload.HtmlUrl, payload.Prerelease, AssetOf(payload, assetName));
    }

    /// <summary>
    /// 置き換えに使える配布物を 1 つ選ぶ。
    /// 名前が合い、アップロードが済み、digest を持つものだけが対象になる。
    /// </summary>
    private static ReleaseAsset? AssetOf(ReleasePayload payload, string assetName)
    {
        if (assetName.Length == 0) return null;
        foreach (var attached in payload.Assets)
        {
            // アップロードの途中のものを掴まない。
            if (!string.Equals(attached.State, "uploaded", StringComparison.Ordinal)) continue;

            var asset = ReleaseAsset.TryCreate(
                attached.Name, attached.BrowserDownloadUrl, attached.Digest, attached.Size, assetName);
            if (asset is not null) return asset;
        }
        return null;
    }

    /// <summary>応答のうち、確認に使う項目だけを読む。</summary>
    internal sealed class ReleasePayload
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("assets")]
        public List<AssetPayload> Assets { get; set; } = new();
    }

    /// <summary>添付のうち、置き換えに使う項目だけを読む。</summary>
    internal sealed class AssetPayload
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        /// <summary>GitHub が計算した digest。付かないリリースもあるため任意とする。</summary>
        [JsonPropertyName("digest")]
        public string? Digest { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        /// <summary>アップロードの途中のものを掴まないために見る。</summary>
        [JsonPropertyName("state")]
        public string? State { get; set; } = "uploaded";
    }
}
