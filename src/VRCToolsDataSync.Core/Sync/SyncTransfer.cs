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
    /// <summary>
    /// ローカルファイルから manifest のエントリを作る。
    /// <paramref name="logicalPath"/> は同期先の中での位置を表す名前で、
    /// 実データの置き場所は内容から決める (<see cref="BlobKeys"/>)。
    /// </summary>
    public static ManifestFile Describe(string localPath, string logicalPath)
    {
        StorageKey.Validate(logicalPath);
        var sha256 = FileHasher.Sha256(localPath);
        return new ManifestFile
        {
            RelativePath = logicalPath,
            Size = new FileInfo(localPath).Length,
            Sha256 = sha256,
            BlobKey = BlobKeys.FromSha256(sha256),
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
    /// 送信を省けるか。manifest の記録と一致することに加えて、同期先に実体が
    /// あることまで確かめる。
    /// <para>
    /// 記録だけを見ると、中断した Push などで実体が欠けている場合に送信を省き続け、
    /// 欠落が直らない。その状態では他の PC の Pull が失敗し続ける。
    /// 実体の確認は本文を伴わない問い合わせなので、転送量には効かない。
    /// </para>
    /// </summary>
    public static bool CanSkipUpload(
        ISyncStorage storage,
        IReadOnlyList<ManifestFile> remoteFiles,
        ManifestFile candidate)
        => IsAlreadyOnRemote(remoteFiles, candidate)
           && storage.Exists(ManifestFileKeys.StorageKeyOf(candidate));

    /// <summary>
    /// 前回の manifest と今回のファイル集合を比べ、送るものが何も無かったかを判定する。
    /// <para>
    /// ここでは実体の有無は見ない。実体が欠けていた分は上の判定で送り直されており、
    /// その時点で manifest の記録と実体が揃うため、manifest を書き直す必要は無い。
    /// </para>
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
