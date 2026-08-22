namespace VRCToolsDataSync.Core.Settings;

/// <summary>データの保存先の種類。</summary>
public enum SyncStorageMode
{
    /// <summary>OneDrive などのローカル同期フォルダ。</summary>
    LocalFolder,

    /// <summary>S3 互換オブジェクトストレージ (Cloudflare R2 / Amazon S3 / MinIO など)。</summary>
    S3,
}

public sealed class SyncSettings
{
    public string CloudFolderPath { get; set; } = string.Empty;
    public string MachineName { get; set; } = Environment.MachineName;
    public bool SyncVrcx { get; set; } = true;
    public bool SyncFriendConnect { get; set; } = true;

    public bool AutoSyncEnabled { get; set; } = false;

    /// <summary>
    /// データの保存先。既定はローカル同期フォルダで、この項目が無い既存の
    /// settings.json もそのまま従来どおり動く。
    /// </summary>
    public SyncStorageMode StorageMode { get; set; } = SyncStorageMode.LocalFolder;

    /// <summary><see cref="SyncStorageMode.S3"/> のときの接続設定。</summary>
    public S3Settings? S3 { get; set; }

    public Dictionary<string, ToolSyncState> ToolState { get; set; } = new();

    // Issue #6: ツールごとの自動起動設定。キーは ISyncService.ToolKey と一致させる
    // ("vrcx", "friend-connect")。
    public Dictionary<string, ToolLaunchConfig> Launch { get; set; } = new();
}

/// <summary>S3 互換オブジェクトストレージの接続設定。</summary>
public sealed class S3Settings
{
    /// <summary>
    /// エンドポイントの URL。
    /// R2 は "https://&lt;アカウント ID&gt;.r2.cloudflarestorage.com"、
    /// S3 は "https://s3.&lt;リージョン&gt;.amazonaws.com"。
    /// </summary>
    public string ServiceUrl { get; set; } = string.Empty;

    /// <summary>署名に使うリージョン。R2 は "auto"、S3 はバケットのリージョン。</summary>
    public string Region { get; set; } = "auto";

    public string BucketName { get; set; } = string.Empty;

    /// <summary>バケット内でこのツールが使う位置。空ならバケット直下。</summary>
    public string KeyPrefix { get; set; } = string.Empty;

    public string AccessKeyId { get; set; } = string.Empty;

    /// <summary>
    /// DPAPI で保護したシークレットアクセスキー。
    /// 平文は settings.json に書かない (<see cref="SecretProtector"/> を参照)。
    /// </summary>
    public string? ProtectedSecretAccessKey { get; set; }

    /// <summary>バケット名をパスに含める形式を使う。R2 と MinIO は true。</summary>
    public bool UsePathStyle { get; set; } = true;

    /// <summary>manifest.json の更新に ETag の条件付き書き込みを使う。</summary>
    public bool UseConditionalWrites { get; set; } = true;

    /// <summary>1 回の通信のタイムアウト (秒)。大きな DB のアップロードを見込む。</summary>
    public int TimeoutSeconds { get; set; } = 1800;

    public S3Settings Clone() => new()
    {
        ServiceUrl = ServiceUrl,
        Region = Region,
        BucketName = BucketName,
        KeyPrefix = KeyPrefix,
        AccessKeyId = AccessKeyId,
        ProtectedSecretAccessKey = ProtectedSecretAccessKey,
        UsePathStyle = UsePathStyle,
        UseConditionalWrites = UseConditionalWrites,
        TimeoutSeconds = TimeoutSeconds,
    };
}

public sealed class ToolSyncState
{
    public long LastPulledVersion { get; set; }
    public DateTimeOffset? LastPulledAt { get; set; }
    public long LastPushedVersion { get; set; }
    public DateTimeOffset? LastPushedAt { get; set; }

    public ToolSyncState Clone() => new()
    {
        LastPulledVersion = LastPulledVersion,
        LastPulledAt = LastPulledAt,
        LastPushedVersion = LastPushedVersion,
        LastPushedAt = LastPushedAt,
    };
}

public sealed class ToolLaunchConfig
{
    // 実行ファイルの絶対パス。null/空なら自動検出 (ToolDataPaths.TryFindExecutable) に委ねる。
    public string? ExecutablePath { get; set; }
    public string? Arguments { get; set; }
    // VRCToolsDataSync 起動時の Pull 完了後に自動起動するかどうか。
    public bool LaunchOnAppStart { get; set; }
}
