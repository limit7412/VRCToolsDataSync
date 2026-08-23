using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace VRCToolsDataSync.Core.Storage;

internal enum S3PutOutcome
{
    /// <summary>書き込みに成功した。</summary>
    Success,

    /// <summary>条件付き書き込みが弾かれた。読み直してやり直す。</summary>
    PreconditionFailed,

    /// <summary>相手が条件付き書き込みに対応していない。</summary>
    ConditionalWriteUnsupported,
}

internal sealed record S3ObjectBody(byte[] Content, string? ETag);

/// <summary>
/// S3 互換ストレージへの最小限の HTTP クライアント。
/// 本ツールが使う操作 (GET / PUT / DELETE / ListObjectsV2 / マルチパートアップロード)
/// だけを実装する。
/// </summary>
internal sealed class S3Client
{
    /// <summary>この大きさを超えるファイルはマルチパートアップロードに切り替える。</summary>
    private const long MultipartThresholdBytes = 64L * 1024 * 1024;

    /// <summary>マルチパートアップロードの 1 パートの大きさ。</summary>
    private const long MultipartPartSizeBytes = 32L * 1024 * 1024;

    /// <summary>一時的な失敗に対する試行回数の上限 (初回を含む)。</summary>
    private const int MaxAttempts = 4;

    private const int MaxErrorBodyChars = 2048;

    // 接続プールを共有するため静的に持つ。タイムアウトは操作ごとに
    // CancellationToken で与えるので、HttpClient 側では無制限にしておく
    // (既定の 100 秒では数百 MB のアップロードが途中で切れる)。
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly S3StorageOptions _options;
    private readonly Uri _endpoint;
    private readonly string _basePath;

    public S3Client(S3StorageOptions options)
    {
        options.Validate();
        _options = options;
        _endpoint = new Uri(options.ServiceUrl.Trim(), UriKind.Absolute);
        // MinIO などをサブパスに置いている構成に備え、エンドポイント側のパスを前置きする。
        _basePath = _endpoint.AbsolutePath.TrimEnd('/');
    }

    public string Host => _endpoint.Host;

    /// <summary>オブジェクトを丸ごと取得する。存在しなければ null。</summary>
    public S3ObjectBody? GetBytes(string key)
    {
        using var cts = new CancellationTokenSource(_options.Timeout);
        using var response = Send(
            HttpMethod.Get, key, query: null, contentFactory: null,
            AwsV4Signer.EmptyPayloadHash, headers: null,
            HttpCompletionOption.ResponseContentRead, cts.Token);

        if (response.StatusCode == HttpStatusCode.NotFound
            && IsMissingObject(response, $"オブジェクトの取得 ({key})", cts.Token))
        {
            return null;
        }
        EnsureSuccess(response, $"オブジェクトの取得 ({key})", cts.Token);

        using var stream = response.Content.ReadAsStream(cts.Token);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return new S3ObjectBody(buffer.ToArray(), ReadETag(response));
    }

    /// <summary>
    /// オブジェクトを <paramref name="localPath"/> へ取り出す。存在しなければ false。
    /// 途中で失敗しても元のファイルが壊れないよう、一時ファイルへ書いてから差し替える。
    /// </summary>
    public bool GetToFile(string key, string localPath)
    {
        using var cts = new CancellationTokenSource(_options.Timeout);
        using var response = Send(
            HttpMethod.Get, key, query: null, contentFactory: null,
            AwsV4Signer.EmptyPayloadHash, headers: null,
            HttpCompletionOption.ResponseHeadersRead, cts.Token);

        if (response.StatusCode == HttpStatusCode.NotFound
            && IsMissingObject(response, $"オブジェクトの取得 ({key})", cts.Token))
        {
            return false;
        }
        EnsureSuccess(response, $"オブジェクトの取得 ({key})", cts.Token);

        var directory = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tmp = localPath + ".downloading-" + Guid.NewGuid().ToString("N");
        try
        {
            // ResponseHeadersRead ではヘッダー受信で Send が返るため、本文の同期読み取りに
            // タイムアウトが効かない。相手がヘッダーだけ返して黙り込むと止まったままになる。
            // 期限が来たら応答を破棄して、止まっている読み取りを失敗させる。
            using (cts.Token.Register(static state => ((HttpResponseMessage)state!).Dispose(), response))
            {
                try
                {
                    using var source = response.Content.ReadAsStream(cts.Token);
                    using var destination = File.Create(tmp);
                    source.CopyTo(destination);
                }
                catch (Exception ex) when (cts.Token.IsCancellationRequested)
                {
                    throw new SyncStorageException(
                        $"オブジェクトの取得がタイムアウトしました ({key})", ex);
                }
            }
            if (File.Exists(localPath))
            {
                File.Replace(tmp, localPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, localPath);
            }
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
            }
        }
        return true;
    }

    /// <summary>
    /// メモリ上のデータを書き込む。<paramref name="conditionHeaders"/> に
    /// If-Match / If-None-Match を渡すと条件付き書き込みになる。
    /// </summary>
    public S3PutOutcome PutBytes(
        string key,
        byte[] body,
        string contentType,
        IReadOnlyList<KeyValuePair<string, string>>? conditionHeaders)
    {
        using var cts = new CancellationTokenSource(_options.Timeout);
        var payloadHash = AwsV4Signer.Sha256Hex(body);

        using var response = Send(
            HttpMethod.Put, key, query: null,
            contentFactory: () =>
            {
                var content = new ByteArrayContent(body);
                content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                return content;
            },
            payloadHash, conditionHeaders,
            HttpCompletionOption.ResponseContentRead, cts.Token);

        if (response.IsSuccessStatusCode)
        {
            return S3PutOutcome.Success;
        }
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return S3PutOutcome.PreconditionFailed;
        }

        // 応答本文は一度しか読めないので、ここで読み切ってから判定と例外に回す。
        var errorBody = ReadBody(response, cts.Token);
        if (conditionHeaders is { Count: > 0 })
        {
            // S3 は条件付き書き込みの衝突を 412 ではなく 409 で返すことがある。
            // 汎用の失敗として扱うと ManifestStore の再試行へ戻れず、
            // 読み直せば解消する競合で Push が落ちる。
            var (code, _) = ParseError(errorBody);
            if (response.StatusCode == HttpStatusCode.Conflict
                && code is "ConditionalRequestConflict" or "OperationAborted")
            {
                return S3PutOutcome.PreconditionFailed;
            }
            if (IsConditionalWriteUnsupported(response.StatusCode, errorBody))
            {
                return S3PutOutcome.ConditionalWriteUnsupported;
            }
        }
        throw BuildFailure(response.StatusCode, errorBody, $"オブジェクトの書き込み ({key})");
    }

    /// <summary>ローカルファイルを書き込む。大きい場合はマルチパートアップロードを使う。</summary>
    public void PutFile(string key, string localPath)
    {
        var length = new FileInfo(localPath).Length;
        if (length > MultipartThresholdBytes)
        {
            PutFileMultipart(key, localPath, length);
            return;
        }

        using var cts = new CancellationTokenSource(_options.Timeout);
        using var response = Send(
            HttpMethod.Put, key, query: null,
            contentFactory: () => new FileRangeContent(localPath, 0, length),
            // 本文のハッシュは署名に含めない。含めると数百 MB を署名前にもう一度
            // 読む必要があり、その間にファイルが変わると署名が合わなくなる。
            AwsV4Signer.UnsignedPayload, headers: null,
            HttpCompletionOption.ResponseContentRead, cts.Token);
        EnsureSuccess(response, $"オブジェクトの書き込み ({key})");
    }

    /// <summary>オブジェクトの有無だけを確かめる。本文は受け取らない。</summary>
    public bool Exists(string key)
    {
        using var cts = new CancellationTokenSource(_options.Timeout);
        using var response = Send(
            HttpMethod.Head, key, query: null, contentFactory: null,
            AwsV4Signer.EmptyPayloadHash, headers: null,
            HttpCompletionOption.ResponseHeadersRead, cts.Token);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // HEAD は本文を返さないのでエラーコードで区別できない。
            // バケット名の誤りは接続テスト (VerifyAccess) と取得側で弾く。
            return false;
        }
        EnsureSuccess(response, $"オブジェクトの確認 ({key})", cts.Token);
        return true;
    }

    public void DeleteObject(string key)
    {
        using var cts = new CancellationTokenSource(_options.Timeout);
        using var response = Send(
            HttpMethod.Delete, key, query: null, contentFactory: null,
            AwsV4Signer.EmptyPayloadHash, headers: null,
            HttpCompletionOption.ResponseContentRead, cts.Token);

        // 存在しないキーの削除は成功扱い。S3 互換 API は 204 を返すのが普通だが、
        // 404 を返す実装もあるので受け入れる。
        // ただしバケット自体が無い場合は設定の誤りなのでエラーにする。取得側と違い
        // 「NoSuchKey 以外は失敗」にはしない。実装によって欠落時のコードが異なり、
        // 任意ファイルの削除は毎回の Push で通るため、狭く判定すると常時失敗する。
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var body = ReadBody(response, cts.Token);
            var (code, _) = ParseError(body);
            if (!string.Equals(code, "NoSuchBucket", StringComparison.Ordinal))
            {
                return;
            }
            throw BuildFailure(response.StatusCode, body, $"オブジェクトの削除 ({key})");
        }
        EnsureSuccess(response, $"オブジェクトの削除 ({key})", cts.Token);
    }

    /// <summary>
    /// 接頭辞に一致するオブジェクトを ListObjectsV2 で列挙する。
    /// 1 回の応答は最大 1000 件なので、継続トークンを辿って読み切る。
    /// </summary>
    public IEnumerable<(string Key, DateTimeOffset LastModified, long Size)> ListObjects(string keyPrefix)
    {
        string? continuationToken = null;
        do
        {
            var query = new List<KeyValuePair<string, string>>
            {
                new("list-type", "2"),
                new("prefix", keyPrefix),
            };
            if (continuationToken is not null)
            {
                query.Add(new KeyValuePair<string, string>("continuation-token", continuationToken));
            }

            string body;
            using (var cts = new CancellationTokenSource(_options.Timeout))
            {
                using var response = Send(
                    HttpMethod.Get, key: string.Empty, query, contentFactory: null,
                    AwsV4Signer.EmptyPayloadHash, headers: null,
                    HttpCompletionOption.ResponseContentRead, cts.Token);
                EnsureSuccess(response, $"オブジェクトの一覧 ({keyPrefix})", cts.Token);
                body = ReadBody(response, cts.Token);
            }

            XDocument document;
            try
            {
                document = XDocument.Parse(body);
            }
            catch (Exception ex)
            {
                throw new SyncStorageException($"オブジェクトの一覧を解釈できませんでした ({keyPrefix})", ex);
            }

            var root = document.Root;
            if (root is null) yield break;

            foreach (var element in ChildElements(root, "Contents"))
            {
                var key = ChildElements(element, "Key").FirstOrDefault()?.Value;
                if (string.IsNullOrEmpty(key)) continue;
                var modifiedText = ChildElements(element, "LastModified").FirstOrDefault()?.Value;
                var modified = DateTimeOffset.TryParse(
                    modifiedText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal,
                    out var parsed)
                    ? parsed
                    : DateTimeOffset.UtcNow;
                var size = long.TryParse(
                    ChildElements(element, "Size").FirstOrDefault()?.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsedSize)
                    ? parsedSize
                    : 0L;
                yield return (key, modified, size);
            }

            var truncated = ChildElements(root, "IsTruncated").FirstOrDefault()?.Value;
            continuationToken = string.Equals(truncated, "true", StringComparison.OrdinalIgnoreCase)
                ? ChildElements(root, "NextContinuationToken").FirstOrDefault()?.Value
                : null;
        }
        while (continuationToken is not null);
    }

    // --- マルチパートアップロード ---

    private void PutFileMultipart(string key, string localPath, long length)
    {
        var uploadId = InitiateMultipartUpload(key);
        try
        {
            var etags = new List<string>();
            var partNumber = 1;
            for (long offset = 0; offset < length; offset += MultipartPartSizeBytes)
            {
                var partLength = Math.Min(MultipartPartSizeBytes, length - offset);
                etags.Add(UploadPart(key, uploadId, partNumber, localPath, offset, partLength));
                partNumber++;
            }
            CompleteMultipartUpload(key, uploadId, etags);
        }
        catch
        {
            // 中断したまま放置すると未完了パートに課金が続くので、後始末を試みる。
            // ここでの失敗は元の例外を隠さないよう握りつぶす。
            try { AbortMultipartUpload(key, uploadId); } catch { /* best-effort cleanup */ }
            throw;
        }
    }

    private string InitiateMultipartUpload(string key)
    {
        using var cts = new CancellationTokenSource(_options.Timeout);
        var query = new List<KeyValuePair<string, string>> { new("uploads", string.Empty) };
        using var response = Send(
            HttpMethod.Post, key, query,
            contentFactory: () => new ByteArrayContent(Array.Empty<byte>()),
            AwsV4Signer.EmptyPayloadHash, headers: null,
            HttpCompletionOption.ResponseContentRead, cts.Token);
        EnsureSuccess(response, $"マルチパートアップロードの開始 ({key})");

        using var stream = response.Content.ReadAsStream(cts.Token);
        var document = XDocument.Load(stream);
        var uploadId = document.Root is null
            ? null
            : ChildElements(document.Root, "UploadId").FirstOrDefault()?.Value;
        if (string.IsNullOrEmpty(uploadId))
        {
            throw new SyncStorageException($"マルチパートアップロードの UploadId を取得できませんでした ({key})");
        }
        return uploadId;
    }

    private string UploadPart(string key, string uploadId, int partNumber, string localPath, long offset, long length)
    {
        using var cts = new CancellationTokenSource(_options.Timeout);
        var query = new List<KeyValuePair<string, string>>
        {
            new("partNumber", partNumber.ToString(CultureInfo.InvariantCulture)),
            new("uploadId", uploadId),
        };
        using var response = Send(
            HttpMethod.Put, key, query,
            contentFactory: () => new FileRangeContent(localPath, offset, length),
            AwsV4Signer.UnsignedPayload, headers: null,
            HttpCompletionOption.ResponseContentRead, cts.Token);
        EnsureSuccess(response, $"パート {partNumber} のアップロード ({key})");

        var etag = ReadETag(response);
        if (string.IsNullOrEmpty(etag))
        {
            throw new SyncStorageException($"パート {partNumber} の ETag を取得できませんでした ({key})");
        }
        return etag;
    }

    private void CompleteMultipartUpload(string key, string uploadId, IReadOnlyList<string> etags)
    {
        var xml = new StringBuilder();
        xml.Append("<CompleteMultipartUpload>");
        for (var i = 0; i < etags.Count; i++)
        {
            xml.Append("<Part><PartNumber>")
               .Append((i + 1).ToString(CultureInfo.InvariantCulture))
               .Append("</PartNumber><ETag>")
               .Append(System.Security.SecurityElement.Escape(etags[i]))
               .Append("</ETag></Part>");
        }
        xml.Append("</CompleteMultipartUpload>");
        var body = Encoding.UTF8.GetBytes(xml.ToString());

        using var cts = new CancellationTokenSource(_options.Timeout);
        var query = new List<KeyValuePair<string, string>> { new("uploadId", uploadId) };
        using var response = Send(
            HttpMethod.Post, key, query,
            contentFactory: () =>
            {
                var content = new ByteArrayContent(body);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                return content;
            },
            AwsV4Signer.Sha256Hex(body), headers: null,
            HttpCompletionOption.ResponseContentRead, cts.Token);
        EnsureSuccess(response, $"マルチパートアップロードの完了 ({key})");

        // 完了要求は HTTP 200 を返してから本文でエラーを伝えることがある。
        var text = ReadBody(response, cts.Token);
        if (text.Contains("<Error", StringComparison.Ordinal))
        {
            var (code, message) = ParseError(text);
            throw new SyncStorageException(
                $"マルチパートアップロードの完了が失敗しました ({key}) [{code}]: {message}");
        }
    }

    private void AbortMultipartUpload(string key, string uploadId)
    {
        using var cts = new CancellationTokenSource(_options.Timeout);
        var query = new List<KeyValuePair<string, string>> { new("uploadId", uploadId) };
        using var response = Send(
            HttpMethod.Delete, key, query, contentFactory: null,
            AwsV4Signer.EmptyPayloadHash, headers: null,
            HttpCompletionOption.ResponseContentRead, cts.Token);
        EnsureSuccess(response, $"マルチパートアップロードの中断 ({key})");
    }

    // --- 送信 ---

    private HttpResponseMessage Send(
        HttpMethod method,
        string key,
        IReadOnlyList<KeyValuePair<string, string>>? query,
        Func<HttpContent?>? contentFactory,
        string payloadHash,
        IReadOnlyList<KeyValuePair<string, string>>? headers,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        var canonicalQuery = AwsV4Signer.BuildCanonicalQuery(query);
        var (uri, canonicalPath) = BuildUri(key, canonicalQuery);

        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                using var request = new HttpRequestMessage(method, uri)
                {
                    Content = contentFactory?.Invoke(),
                };
                if (headers is not null)
                {
                    foreach (var header in headers)
                    {
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
                AwsV4Signer.Sign(
                    request, canonicalPath, canonicalQuery, payloadHash,
                    _options.AccessKeyId, _options.SecretAccessKey, _options.Region,
                    DateTimeOffset.UtcNow);

                response = Http.Send(request, completionOption, cancellationToken);

                if (attempt >= MaxAttempts || !IsTransient(response.StatusCode))
                {
                    return response;
                }
                response.Dispose();
                response = null;
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                response?.Dispose();

                // 名前解決の失敗、接続拒否、タイムアウトは呼び出し側にとって
                // 「保存先へ到達できない」という一つの結果でしかない。HTTP 層の
                // 例外をそのまま漏らすと、設定不備として扱う経路をすり抜けるため、
                // 同期先の例外に包み直す。
                if (attempt >= MaxAttempts || cancellationToken.IsCancellationRequested)
                {
                    throw new SyncStorageException(BuildTransportMessage(uri, ex, cancellationToken), ex);
                }
            }

            // 相手側の一時的な不調とみて、待ち時間を倍にしながら再試行する。
            var delayMilliseconds = 500 * (int)Math.Pow(2, attempt - 1);
            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(delayMilliseconds));
            if (cancellationToken.IsCancellationRequested)
            {
                // 待機中にタイムアウトへ達した経路。ここも呼び出し側から見れば
                // 「保存先へ到達できない」でしかないので、同期先の例外に揃える。
                throw new SyncStorageException($"同期先への通信がタイムアウトしました ({uri.Host})");
            }
        }
    }

    private static bool IsTransportFailure(Exception exception)
        => exception is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException;

    private static string BuildTransportMessage(Uri uri, Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || exception is TaskCanceledException or OperationCanceledException)
        {
            return $"同期先への通信がタイムアウトしました ({uri.Host})";
        }
        // HttpRequestException は内部例外の方が原因を具体的に述べていることが多い。
        var detail = exception.InnerException?.Message ?? exception.Message;
        return $"同期先へ接続できませんでした ({uri.Host}): {detail}";
    }

    private (Uri Uri, string CanonicalPath) BuildUri(string key, string canonicalQuery)
    {
        var encodedKey = key.Length == 0
            ? string.Empty
            : AwsV4Signer.UriEncode(key, preserveSlash: true);

        string canonicalPath;
        string host;
        if (_options.UsePathStyle)
        {
            var encodedBucket = AwsV4Signer.UriEncode(_options.BucketName, preserveSlash: false);
            canonicalPath = _basePath + "/" + encodedBucket
                + (encodedKey.Length == 0 ? string.Empty : "/" + encodedKey);
            host = _endpoint.Host;
        }
        else
        {
            canonicalPath = _basePath + "/" + encodedKey;
            host = _options.BucketName + "." + _endpoint.Host;
        }

        var port = _endpoint.IsDefaultPort
            ? string.Empty
            : ":" + _endpoint.Port.ToString(CultureInfo.InvariantCulture);
        var url = $"{_endpoint.Scheme}://{host}{port}{canonicalPath}";
        if (canonicalQuery.Length > 0)
        {
            url += "?" + canonicalQuery;
        }
        return (new Uri(url, UriKind.Absolute), canonicalPath);
    }

    private static bool IsTransient(HttpStatusCode status)
        => (int)status >= 500
           || status == HttpStatusCode.RequestTimeout
           || status == HttpStatusCode.TooManyRequests;

    /// <summary>
    /// 条件付き書き込みに対応していない応答かを判定する。
    /// 未対応のプロバイダは 501 を返すか、本文のエラーコードで NotImplemented を伝える。
    /// <para>
    /// 汎用の InvalidRequest まで拾うと、無関係な 400 が一度出ただけで
    /// 複数 PC 間の競合検出を黙って捨ててしまうため、明示的な非対応の signal に絞る。
    /// </para>
    /// </summary>
    private static bool IsConditionalWriteUnsupported(HttpStatusCode status, string body)
    {
        if (status == HttpStatusCode.NotImplemented) return true;
        var (code, _) = ParseError(body);
        return string.Equals(code, "NotImplemented", StringComparison.Ordinal);
    }

    /// <summary>
    /// 404 が「オブジェクトが無い」ことを指しているかを判定する。
    /// バケット名の間違いでも 404 が返るため、これを欠落と取り違えると、
    /// 存在しないバケットを指したまま接続テストを通してしまう。
    /// 欠落でなければ例外にする。
    /// </summary>
    private static bool IsMissingObject(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        var body = ReadBody(response, cancellationToken);
        var (code, _) = ParseError(body);
        if (code.Length == 0 || string.Equals(code, "NoSuchKey", StringComparison.Ordinal))
        {
            return true;
        }
        throw BuildFailure(response.StatusCode, body, operation);
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation, CancellationToken cancellationToken = default)
    {
        if (response.IsSuccessStatusCode) return;
        throw BuildFailure(response.StatusCode, ReadBody(response, cancellationToken), operation);
    }

    private static SyncStorageException BuildFailure(HttpStatusCode status, string body, string operation)
    {
        var (code, message) = ParseError(body);
        var detail = string.IsNullOrEmpty(code) ? message : $"{code}: {message}";
        return new SyncStorageException($"{operation} が失敗しました (HTTP {(int)status}) {detail}".TrimEnd());
    }

    /// <summary>
    /// 応答本文を読み出す。本文のストリームは一度しか読めないので、
    /// 呼び出し側は結果を使い回すこと。
    /// <para>
    /// ResponseHeadersRead で受けた応答ではバッファされていないネットワーク
    /// ストリームを読むことになる。トークンを <c>ReadAsStream</c> へ渡しても
    /// 効くのはストリームを取り出すまでで、その後の同期読み取りには効かない。
    /// 期限が来たら応答を破棄して、止まっている読み取りを失敗させる。
    /// </para>
    /// </summary>
    private static string ReadBody(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(
            static state => ((HttpResponseMessage)state!).Dispose(), response);
        try
        {
            using var stream = response.Content.ReadAsStream(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var buffer = new char[MaxErrorBodyChars];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return read <= 0 ? string.Empty : new string(buffer, 0, read);
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            // 期限切れを空の本文として返すと、IsMissingObject が
            // 「エラーコードを読めなかった」= オブジェクトなしと誤読する。
            // 読めなかった理由が期限切れであることは呼び出し元へ伝える。
            throw new SyncStorageException("応答本文の読み取りがタイムアウトしました");
        }
        catch
        {
            // 本文が読めなくても、状態コードだけで失敗の内容は組み立てられる。
            return string.Empty;
        }
    }

    private static (string Code, string Message) ParseError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (string.Empty, string.Empty);
        try
        {
            var document = XDocument.Parse(body);
            if (document.Root is null) return (string.Empty, body);
            var code = ChildElements(document.Root, "Code").FirstOrDefault()?.Value ?? string.Empty;
            var message = ChildElements(document.Root, "Message").FirstOrDefault()?.Value ?? string.Empty;
            return (code, string.IsNullOrEmpty(message) ? body : message);
        }
        catch
        {
            // XML でない応答 (プロキシのエラーページなど) はそのまま見せる。
            return (string.Empty, body);
        }
    }

    private static string? ReadETag(HttpResponseMessage response)
        => response.Headers.TryGetValues("ETag", out var values) ? values.FirstOrDefault() : null;

    /// <summary>
    /// 名前空間を無視して子孫要素を拾う。S3 互換 API は実装ごとに既定名前空間の
    /// 有無が違うため、ローカル名だけで突き合わせる。
    /// </summary>
    private static IEnumerable<XElement> ChildElements(XElement parent, string localName)
        => parent.Descendants().Where(e => e.Name.LocalName == localName);

    /// <summary>ファイルの一部分を本文として送る。再試行のたびに読み直す。</summary>
    private sealed class FileRangeContent : HttpContent
    {
        private const int BufferSize = 128 * 1024;

        private readonly string _path;
        private readonly long _offset;
        private readonly long _length;

        public FileRangeContent(string path, long offset, long length)
        {
            _path = path;
            _offset = offset;
            _length = length;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            using var file = OpenSource(useAsync: false);
            var buffer = new byte[BufferSize];
            var remaining = _length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = file.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0) throw new IOException($"アップロード中にファイルが短くなりました: {_path}");
                stream.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await using var file = OpenSource(useAsync: true);
            var buffer = new byte[BufferSize];
            var remaining = _length;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = await file.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                if (read <= 0) throw new IOException($"アップロード中にファイルが短くなりました: {_path}");
                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }

        private FileStream OpenSource(bool useAsync)
        {
            var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync);
            if (_offset > 0)
            {
                file.Seek(_offset, SeekOrigin.Begin);
            }
            return file;
        }
    }
}
