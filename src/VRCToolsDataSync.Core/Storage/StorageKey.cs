namespace VRCToolsDataSync.Core.Storage;

/// <summary>同期先のキーを検証し、ローカルパスへ写す。</summary>
public static class StorageKey
{
    /// <summary>
    /// キーとして扱える形かを検証する。manifest.json は別 PC が書いたものを
    /// そのまま信用してローカルへ展開するため、"../" のような親ディレクトリ参照や
    /// 絶対パスが混ざっていた場合に同期対象外の場所へ書き込まないよう弾く。
    /// </summary>
    public static void Validate(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new SyncStorageException("同期先のキーが空です");
        }
        if (key.StartsWith('/') || key.StartsWith('\\'))
        {
            throw new SyncStorageException($"同期先のキーが '/' で始まっています: {key}");
        }
        if (key.Contains('\\', StringComparison.Ordinal))
        {
            throw new SyncStorageException($"同期先のキーに '\\' が含まれています: {key}");
        }
        if (key.Contains(':', StringComparison.Ordinal))
        {
            throw new SyncStorageException($"同期先のキーに ':' が含まれています: {key}");
        }
        foreach (var segment in key.Split('/'))
        {
            if (segment.Length == 0)
            {
                throw new SyncStorageException($"同期先のキーに空の区切りがあります: {key}");
            }
            if (segment is "." or "..")
            {
                throw new SyncStorageException($"同期先のキーに親ディレクトリ参照が含まれています: {key}");
            }
        }
    }

    /// <summary>
    /// <paramref name="rootDirectory"/> 配下の絶対パスへ写す。
    /// 検証を通ったキーだけを渡すこと。
    /// </summary>
    public static string ToLocalPath(string rootDirectory, string key)
    {
        Validate(key);
        return Path.Combine(rootDirectory, key.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>ローカルの相対パスをキーの形 (区切りは '/') へ揃える。</summary>
    public static string FromRelativePath(string relativePath)
        => relativePath.Replace('\\', '/');
}
