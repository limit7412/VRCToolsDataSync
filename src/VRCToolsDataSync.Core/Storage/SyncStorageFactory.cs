using Microsoft.Extensions.Logging;
using VRCToolsDataSync.Core.Settings;

namespace VRCToolsDataSync.Core.Storage;

/// <summary>設定から同期先を組み立てる。</summary>
public static class SyncStorageFactory
{
    /// <summary>
    /// <paramref name="settings"/> の保存先モードに従って同期先を作る。
    /// <paramref name="localFolderOverride"/> はローカルフォルダモードのときだけ使い、
    /// CLI の --cloud や GUI の未保存の入力を反映するためのもの。
    /// 設定が足りない場合は <see cref="SyncStorageConfigurationException"/> を投げる。
    /// </summary>
    public static ISyncStorage Create(
        SyncSettings settings,
        string? localFolderOverride = null,
        ILoggerFactory? loggerFactory = null)
    {
        switch (settings.StorageMode)
        {
            case SyncStorageMode.S3:
                return new S3SyncStorage(BuildS3Options(settings.S3), loggerFactory);

            case SyncStorageMode.LocalFolder:
            default:
                var folder = !string.IsNullOrWhiteSpace(localFolderOverride)
                    ? localFolderOverride!.Trim()
                    : settings.CloudFolderPath?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(folder))
                {
                    throw new SyncStorageConfigurationException(
                        "同期フォルダのパスが未設定です。設定画面で指定するか --cloud で渡してください。");
                }
                if (!Directory.Exists(folder))
                {
                    throw new SyncStorageConfigurationException($"指定された同期フォルダが存在しません: {folder}");
                }
                return new LocalFolderSyncStorage(folder, loggerFactory);
        }
    }

    /// <summary>
    /// 同期先を作れるかを確かめ、作れない場合は理由を返す。
    /// 通信は行わないので、認証やバケットの有無までは確認しない。
    /// </summary>
    public static bool TryCreate(
        SyncSettings settings,
        out ISyncStorage? storage,
        out string? error,
        string? localFolderOverride = null,
        ILoggerFactory? loggerFactory = null)
    {
        try
        {
            storage = Create(settings, localFolderOverride, loggerFactory);
            error = null;
            return true;
        }
        catch (SyncStorageException ex)
        {
            storage = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>設定画面やログに出す、保存先の一行説明。</summary>
    public static string DescribeTarget(SyncSettings settings)
    {
        if (settings.StorageMode != SyncStorageMode.S3)
        {
            return string.IsNullOrWhiteSpace(settings.CloudFolderPath)
                ? "同期フォルダ (未設定)"
                : $"同期フォルダ: {settings.CloudFolderPath}";
        }
        var s3 = settings.S3;
        if (s3 is null || string.IsNullOrWhiteSpace(s3.BucketName))
        {
            return "S3 互換ストレージ (未設定)";
        }
        var prefix = s3.KeyPrefix.Trim().Trim('/');
        var location = prefix.Length == 0 ? s3.BucketName : $"{s3.BucketName}/{prefix}";
        return $"S3 互換ストレージ: {location} ({s3.ServiceUrl})";
    }

    private static S3StorageOptions BuildS3Options(S3Settings? settings)
    {
        if (settings is null)
        {
            throw new SyncStorageConfigurationException(
                "S3 互換ストレージの設定がありません。CLI の `storage s3` コマンドで設定してください。");
        }

        var secret = SecretProtector.Unprotect(settings.ProtectedSecretAccessKey);
        if (string.IsNullOrEmpty(secret) && !string.IsNullOrEmpty(settings.ProtectedSecretAccessKey))
        {
            // DPAPI は保存した Windows ユーザ以外では復号できない。設定ファイルを
            // 別 PC / 別ユーザへ持ち込んだ場合に、原因が分かる形で止める。
            throw new SyncStorageConfigurationException(
                "保存されたシークレットアクセスキーを復号できませんでした。" +
                "この PC のユーザで `storage s3` コマンドを実行して設定し直してください。");
        }

        var options = new S3StorageOptions
        {
            ServiceUrl = settings.ServiceUrl?.Trim() ?? string.Empty,
            Region = string.IsNullOrWhiteSpace(settings.Region) ? "auto" : settings.Region.Trim(),
            BucketName = settings.BucketName?.Trim() ?? string.Empty,
            KeyPrefix = settings.KeyPrefix?.Trim() ?? string.Empty,
            AccessKeyId = settings.AccessKeyId?.Trim() ?? string.Empty,
            SecretAccessKey = secret ?? string.Empty,
            UsePathStyle = settings.UsePathStyle,
            UseConditionalWrites = settings.UseConditionalWrites,
            Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds <= 0 ? 1800 : settings.TimeoutSeconds),
        };
        options.Validate();
        return options;
    }
}
