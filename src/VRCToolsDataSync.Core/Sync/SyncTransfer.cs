using Microsoft.Extensions.Logging;
using VRCToolsDataSync.Core.Storage;

namespace VRCToolsDataSync.Core.Sync;

/// <summary>
/// Push / Pull で共通して使うファイル単位の受け渡し。
/// <para>
/// manifest が持つ SHA-256 と手元のファイルを突き合わせ、内容が一致する場合は
/// 転送そのものを省く。同期先が S3 互換ストレージのとき、費用のほとんどは
/// ダウンロード転送量で決まるため、この省略がそのまま請求額に効く。
/// ローカルフォルダでも同期時間の短縮になる。
/// </para>
/// </summary>
internal static class SyncTransfer
{
    /// <summary>ローカルファイルから manifest のエントリを作る。</summary>
    public static ManifestFile Describe(string localPath, string key)
    {
        StorageKey.Validate(key);
        return new ManifestFile
        {
            RelativePath = key,
            Size = new FileInfo(localPath).Length,
            Sha256 = FileHasher.Sha256(localPath),
        };
    }

    /// <summary>同じキーの内容が同期先に既にあるかを、manifest の記録から判定する。</summary>
    public static bool IsAlreadyOnRemote(IReadOnlyList<ManifestFile> remoteFiles, ManifestFile candidate)
    {
        foreach (var remote in remoteFiles)
        {
            if (!string.Equals(remote.RelativePath, candidate.RelativePath, StringComparison.Ordinal))
            {
                continue;
            }
            // manifest は同期先へのファイル書き込みがすべて終わってから保存する。
            // つまり manifest が示す内容は必ず「同期先に実在する状態か、それより古い」。
            // 記録と手元のハッシュが一致するなら、同じ内容が既に置かれている。
            return !string.IsNullOrEmpty(remote.Sha256)
                && string.Equals(remote.Sha256, candidate.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>
    /// manifest のエントリをローカルへ反映する。手元のファイルが既に同じ内容なら
    /// ダウンロードを省く。実際に書き込んだ場合だけ <paramref name="affected"/> に積む。
    /// </summary>
    /// <returns>ローカルが目的の内容になったなら true。同期先にキーが無ければ false。</returns>
    public static bool Restore(
        ISyncStorage storage,
        ManifestFile remote,
        string localPath,
        List<string> affected,
        ILogger logger)
    {
        if (!string.IsNullOrEmpty(remote.Sha256)
            && File.Exists(localPath)
            && string.Equals(FileHasher.Sha256(localPath), remote.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("取得を省略 (内容が同じ): {Key}", remote.RelativePath);
            return true;
        }

        if (!storage.TryDownload(remote.RelativePath, localPath))
        {
            return false;
        }
        affected.Add(localPath);
        return true;
    }

    /// <summary>
    /// 前回の manifest と今回のファイル集合を比べ、送るものが何も無かったかを判定する。
    /// </summary>
    public static bool IsUnchangedSet(ToolManifestEntry? previous, IReadOnlyList<ManifestFile> current)
    {
        if (previous is null || previous.Files.Count != current.Count) return false;
        foreach (var file in current)
        {
            if (!IsAlreadyOnRemote(previous.Files, file)) return false;
        }
        return true;
    }
}
