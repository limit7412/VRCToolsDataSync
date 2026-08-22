namespace VRCToolsDataSync.Core.Storage;

/// <summary>S3 互換ストレージへの接続設定。</summary>
public sealed class S3StorageOptions
{
    /// <summary>
    /// エンドポイントの URL。
    /// Cloudflare R2 なら "https://&lt;アカウント ID&gt;.r2.cloudflarestorage.com"、
    /// Amazon S3 なら "https://s3.&lt;リージョン&gt;.amazonaws.com"。
    /// </summary>
    public required string ServiceUrl { get; init; }

    /// <summary>署名に使うリージョン。R2 は "auto"、S3 はバケットのリージョン。</summary>
    public required string Region { get; init; }

    public required string BucketName { get; init; }

    /// <summary>
    /// バケット内でこのツールが使う位置。空ならバケット直下に manifest.json などを置く。
    /// 末尾の '/' は正規化時に取り除く。
    /// </summary>
    public string KeyPrefix { get; init; } = string.Empty;

    public required string AccessKeyId { get; init; }

    public required string SecretAccessKey { get; init; }

    /// <summary>
    /// バケット名をパスに含める形式 (https://host/bucket/key) を使う。
    /// R2 と MinIO はこちら。false ならホスト名に含める形式 (https://bucket.host/key)。
    /// </summary>
    public bool UsePathStyle { get; init; } = true;

    /// <summary>
    /// manifest.json の更新に ETag の条件付き書き込みを使う。
    /// 対応していないプロバイダでは、最初の失敗を検知した時点で自動的に無効化する。
    /// </summary>
    public bool UseConditionalWrites { get; init; } = true;

    /// <summary>1 回の通信のタイムアウト。大きな DB のアップロードを見込んで長めに取る。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ServiceUrl))
        {
            throw new SyncStorageConfigurationException("S3 互換ストレージのエンドポイント URL が未設定です");
        }
        if (!Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var uri))
        {
            throw new SyncStorageConfigurationException(
                $"S3 互換ストレージのエンドポイント URL が不正です: {ServiceUrl}");
        }
        // ファイルの送信では本文のハッシュを署名に含めない (UNSIGNED-PAYLOAD) ため、
        // 本文の完全性は TLS に委ねている。平文 HTTP を許すと、経路上で
        // アップロード内容を署名を壊さずに書き換えられる。
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new SyncStorageConfigurationException(
                $"エンドポイントは https である必要があります: {ServiceUrl}");
        }
        if (string.IsNullOrWhiteSpace(BucketName))
        {
            throw new SyncStorageConfigurationException("バケット名が未設定です");
        }
        if (string.IsNullOrWhiteSpace(Region))
        {
            throw new SyncStorageConfigurationException("リージョンが未設定です");
        }
        if (string.IsNullOrWhiteSpace(AccessKeyId))
        {
            throw new SyncStorageConfigurationException("アクセスキー ID が未設定です");
        }
        if (string.IsNullOrWhiteSpace(SecretAccessKey))
        {
            throw new SyncStorageConfigurationException(
                "シークレットアクセスキーが未設定です。CLI の `storage s3` コマンドで設定してください。");
        }
    }

    /// <summary>先頭と末尾の '/' を取り除いた接頭辞。空なら空文字。</summary>
    public string NormalizedKeyPrefix => KeyPrefix.Trim().Trim('/');
}
