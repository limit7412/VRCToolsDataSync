namespace VRCToolsDataSync.Core.Domain;

/// <summary>同期先ストレージの操作が失敗したことを表す。</summary>
public class SyncStorageException : Exception
{
    public SyncStorageException(string message) : base(message)
    {
    }

    public SyncStorageException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// manifest.json の条件付き更新が、規定回数続けて他クライアントと競合した。
/// </summary>
public sealed class SyncStorageConcurrencyException : SyncStorageException
{
    public SyncStorageConcurrencyException(string message) : base(message)
    {
    }
}

/// <summary>同期先の設定が不足している、または値が不正である。</summary>
public sealed class SyncStorageConfigurationException : SyncStorageException
{
    public SyncStorageConfigurationException(string message) : base(message)
    {
    }
}
