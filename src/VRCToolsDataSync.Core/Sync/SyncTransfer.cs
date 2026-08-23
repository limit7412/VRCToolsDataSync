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
    /// ローカルにある既存ファイルを同期先へ送り、manifest のエントリを返す。
    /// 送信を省いた場合は <c>Sent</c> が false になる。
    /// <para>
    /// 送る場合は、まず内容が動かない写しを取り、<b>ハッシュも送信もその写しに対して</b>
    /// 行う。元のファイルを直接ハッシュして直接送ると、その間に書き換えられた場合に
    /// 「キーが表す内容」と「実際に置かれる内容」がずれる。置き場所を内容から決めて
    /// いる以上、これは同じキーに別の内容が入るということで、この設計が拠って立つ
    /// 不変性そのものが崩れる。同じ内容を参照している他のエントリまで巻き添えになり、
    /// そちらの Pull もハッシュ不一致で止まる。
    /// </para>
    /// <para>
    /// 写しを取るのは送ると決めた後だけにしている。省略できる場合に写しを作ると、
    /// 同期フォルダモードではファイル数ぶんの書き込みが同期フォルダの中で起きる。
    /// 省略した場合に返すハッシュは元のファイルのものだが、それは
    /// <see cref="CanSkipUpload"/> が実在を確かめた実体の内容と一致している。
    /// </para>
    /// </summary>
    public static (ManifestFile File, bool Sent) Send(
        ISyncStorage storage,
        IReadOnlyList<ManifestFile> remoteFiles,
        string localPath,
        string logicalPath)
    {
        var probe = Describe(localPath, logicalPath);
        if (CanSkipUpload(storage, remoteFiles, probe))
        {
            return (probe, false);
        }

        using var staged = storage.BeginUpload();
        File.Copy(localPath, staged.LocalPath, overwrite: true);
        // File.Copy はコピー元の最終更新時刻を引き継ぐ。同期先は置き去りになった
        // 書き出し中ファイルをこの時刻で見分けるため、刻み直さないと、1 日以上前に
        // 更新された config や note を送っている最中に、別プロセスの BeginUpload が
        // これを置き去りと誤判定して消しうる。
        File.SetLastWriteTimeUtc(staged.LocalPath, DateTime.UtcNow);
        var described = Describe(staged.LocalPath, logicalPath);
        staged.Commit(ManifestFileKeys.StorageKeyOf(described));
        return (described, true);
    }

    /// <summary>
    /// 前回の manifest と今回のファイル集合を比べ、manifest を書き直す必要が無いかを判定する。
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
            if (!IsRecordedAsIs(previous.Files, file)) return false;
        }
        return true;
    }

    /// <summary>
    /// manifest の記録が、今回作ったエントリとそのまま同じかを判定する。
    /// <para>
    /// 内容の一致 (<see cref="IsAlreadyOnRemote"/>) では足りず、実データの置き場所まで
    /// 一致することを求める。schemaVersion 1 の manifest は
    /// <see cref="ManifestFile.RelativePath"/> をそのままキーにしているため、内容が
    /// 同じでも記録は古い置き場所を指したままになる。ここを内容だけで一致と見なすと、
    /// <c>blobs/</c> 側へ送り直した実体を manifest がいつまでも指さず、送り直しだけを
    /// Push のたびに繰り返す。
    /// </para>
    /// </summary>
    private static bool IsRecordedAsIs(IReadOnlyList<ManifestFile> remoteFiles, ManifestFile candidate)
    {
        foreach (var remote in remoteFiles)
        {
            if (!string.Equals(remote.RelativePath, candidate.RelativePath, StringComparison.Ordinal))
            {
                continue;
            }
            return !string.IsNullOrEmpty(remote.Sha256)
                && string.Equals(remote.Sha256, candidate.Sha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    ManifestFileKeys.StorageKeyOf(remote),
                    ManifestFileKeys.StorageKeyOf(candidate),
                    StringComparison.Ordinal);
        }
        return false;
    }
}
