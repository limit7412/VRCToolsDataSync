using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace VRCToolsDataSync.Core.Infra;

/// <summary>
/// AWS Signature Version 4 でリクエストに署名する。
/// <para>
/// S3 互換ストレージへのアクセスに必要なのは署名だけで、SDK が提供する機能の
/// ほとんどは本ツールでは使わない。自前で署名することで、配布物のサイズを増やさず、
/// SDK 側の既定チェックサム付与のようなプロバイダ間の互換問題も避けられる。
/// </para>
/// </summary>
internal static class AwsV4Signer
{
    /// <summary>
    /// 本文のハッシュを署名に含めないことを示す値。HTTPS で送るため改竄検知は
    /// トランスポート側に任せ、数百 MB のファイルを署名前にもう一度読み直す
    /// コストを避ける。
    /// </summary>
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";

    /// <summary>空の本文の SHA-256。本文を持たない GET / DELETE などで使う。</summary>
    public const string EmptyPayloadHash =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private const string Algorithm = "AWS4-HMAC-SHA256";
    private const string ServiceName = "s3";

    /// <summary>
    /// x-amz-* 以外で署名対象に含めるヘッダ。Content-Length は HttpClient が
    /// 署名後に設定することがあるため含めない。
    /// </summary>
    private static readonly HashSet<string> AdditionalSignedHeaders = new(StringComparer.Ordinal)
    {
        "content-type",
        "content-md5",
        "if-match",
        "if-none-match",
    };

    /// <summary>
    /// <paramref name="request"/> に x-amz-date / x-amz-content-sha256 /
    /// Authorization を付ける。
    /// <paramref name="canonicalPath"/> と <paramref name="canonicalQuery"/> には
    /// URI を組み立てたときと同じエンコード済みの文字列を渡す。
    /// <see cref="Uri"/> から読み直すとエスケープの正規化で署名がずれるため、
    /// 呼び出し側が持っている値をそのまま使う。
    /// </summary>
    public static void Sign(
        HttpRequestMessage request,
        string canonicalPath,
        string canonicalQuery,
        string payloadHash,
        string accessKeyId,
        string secretAccessKey,
        string region,
        DateTimeOffset timestamp)
    {
        var uri = request.RequestUri
            ?? throw new InvalidOperationException("RequestUri が設定されていません");

        var utc = timestamp.UtcDateTime;
        var amzDate = utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);

        var signedHeaders = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = uri.IsDefaultPort
                ? uri.Host
                : $"{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}",
        };
        Collect(request.Headers, signedHeaders);
        if (request.Content is not null)
        {
            Collect(request.Content.Headers, signedHeaders);
        }

        var signedHeaderNames = string.Join(";", signedHeaders.Keys);

        var canonicalHeaders = new StringBuilder();
        foreach (var (name, value) in signedHeaders)
        {
            canonicalHeaders.Append(name).Append(':').Append(value).Append('\n');
        }

        var canonicalRequest = new StringBuilder()
            .Append(request.Method.Method).Append('\n')
            .Append(canonicalPath).Append('\n')
            .Append(canonicalQuery).Append('\n')
            .Append(canonicalHeaders).Append('\n')
            .Append(signedHeaderNames).Append('\n')
            .Append(payloadHash)
            .ToString();

        var credentialScope = $"{dateStamp}/{region}/{ServiceName}/aws4_request";
        var stringToSign = new StringBuilder()
            .Append(Algorithm).Append('\n')
            .Append(amzDate).Append('\n')
            .Append(credentialScope).Append('\n')
            .Append(ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))))
            .ToString();

        var signingKey = DeriveSigningKey(secretAccessKey, dateStamp, region);
        var signature = ToHex(HmacSha256(signingKey, stringToSign));

        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"{Algorithm} Credential={accessKeyId}/{credentialScope}, " +
            $"SignedHeaders={signedHeaderNames}, Signature={signature}");
    }

    /// <summary>
    /// RFC 3986 の非予約文字以外をパーセントエンコードする。
    /// <paramref name="preserveSlash"/> が true のときはパスの区切りとして '/' を残す。
    /// </summary>
    public static string UriEncode(string value, bool preserveSlash)
    {
        var builder = new StringBuilder(value.Length * 2);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            var unreserved =
                (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c is '-' or '.' or '_' or '~';
            if (unreserved || (preserveSlash && c == '/'))
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(((int)b).ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return builder.ToString();
    }

    /// <summary>クエリ文字列を署名の規則 (名前順、値も含めてエンコード済み) で組み立てる。</summary>
    public static string BuildCanonicalQuery(IReadOnlyList<KeyValuePair<string, string>>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
        {
            return string.Empty;
        }
        var encoded = parameters
            .Select(p => (
                Name: UriEncode(p.Key, preserveSlash: false),
                Value: UriEncode(p.Value ?? string.Empty, preserveSlash: false)))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => $"{p.Name}={p.Value}");
        return string.Join("&", encoded);
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data) => ToHex(SHA256.HashData(data));

    private static void Collect(HttpHeaders headers, SortedDictionary<string, string> into)
    {
        foreach (var header in headers)
        {
            var name = header.Key.ToLowerInvariant();
            if (name == "host") continue;
            if (!name.StartsWith("x-amz-", StringComparison.Ordinal)
                && !AdditionalSignedHeaders.Contains(name))
            {
                continue;
            }
            into[name] = string.Join(",", header.Value.Select(CollapseWhitespace));
        }
    }

    /// <summary>署名の規則に合わせて前後の空白を削り、連続する空白を 1 つにまとめる。</summary>
    private static string CollapseWhitespace(string value)
    {
        var trimmed = value.Trim();
        var builder = new StringBuilder(trimmed.Length);
        var previousWasSpace = false;
        foreach (var c in trimmed)
        {
            var isSpace = c is ' ' or '\t';
            if (isSpace)
            {
                if (!previousWasSpace) builder.Append(' ');
            }
            else
            {
                builder.Append(c);
            }
            previousWasSpace = isSpace;
        }
        return builder.ToString();
    }

    private static byte[] DeriveSigningKey(string secretAccessKey, string dateStamp, string region)
    {
        var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), dateStamp);
        var kRegion = HmacSha256(kDate, region);
        var kService = HmacSha256(kRegion, ServiceName);
        return HmacSha256(kService, "aws4_request");
    }

    private static byte[] HmacSha256(byte[] key, string data)
        => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
