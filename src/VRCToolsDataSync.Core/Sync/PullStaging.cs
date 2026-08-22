using Microsoft.Extensions.Logging;
using VRCToolsDataSync.Core.Storage;

namespace VRCToolsDataSync.Core.Sync;

/// <summary>
/// Pull の「取り出し」と「差し替え」を分ける。
/// <para>
/// Pull は WAL を消してから本体 DB を上書きする。ネットワーク越しの取得が途中で
/// 失敗した場合、先に WAL を消していると、まだ本体へ反映されていないローカルの
/// 変更が復元できなくなる (バックアップは本体ファイルしか保存しない)。
/// そこで、同期先からの取得と内容の検証をすべて済ませてから、ローカルへ手を付ける。
/// </para>
/// <para>
/// 取り出したファイルは目的地の隣に置く。別ドライブの一時領域を経由すると、
/// 差し替えが安価なファイル移動ではなく数百 MB のコピーになるため。
/// </para>
/// </summary>
internal sealed class PullStaging : IDisposable
{
    private readonly List<StagedFile> _staged = new();
    private readonly ILogger _logger;

    public PullStaging(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// manifest のエントリを同期先から取り出す。手元が既に同じ内容なら何もしない。
    /// この時点ではローカルの既存ファイルには一切触れない。
    /// </summary>
    /// <returns>同期先にそのキーが無ければ false。</returns>
    public bool Fetch(ISyncStorage storage, ManifestFile remote, string targetPath)
    {
        if (!string.IsNullOrEmpty(remote.Sha256)
            && File.Exists(targetPath)
            && string.Equals(FileHasher.Sha256(targetPath), remote.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("取得を省略 (内容が同じ): {Key}", remote.RelativePath);
            return true;
        }

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stagingPath = targetPath + ".incoming-" + Guid.NewGuid().ToString("N");
        if (!storage.TryDownload(remote.RelativePath, stagingPath))
        {
            return false;
        }
        _staged.Add(new StagedFile(stagingPath, targetPath, remote));
        return true;
    }

    /// <summary>
    /// 取り出した内容が manifest の記録と一致するかを確かめる。
    /// 一致しないキーがあればその名前を返す。転送の途中切断や、
    /// manifest と実データがずれている状態を、ローカルへ反映する前に捕まえる。
    /// </summary>
    public string? FindMismatchedKey()
    {
        foreach (var staged in _staged)
        {
            if (string.IsNullOrEmpty(staged.Remote.Sha256)) continue;
            var actual = FileHasher.Sha256(staged.StagingPath);
            if (!string.Equals(actual, staged.Remote.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return staged.Remote.RelativePath;
            }
        }
        return null;
    }

    /// <summary>取り出したファイルを目的の場所へ移す。</summary>
    public void Apply(List<string> affected)
    {
        foreach (var staged in _staged)
        {
            if (File.Exists(staged.TargetPath))
            {
                File.Replace(staged.StagingPath, staged.TargetPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(staged.StagingPath, staged.TargetPath);
            }
            affected.Add(staged.TargetPath);
        }
        _staged.Clear();
    }

    /// <summary>差し替えていない取り出し済みファイルを片付ける。</summary>
    public void Dispose()
    {
        foreach (var staged in _staged)
        {
            if (File.Exists(staged.StagingPath))
            {
                try { File.Delete(staged.StagingPath); } catch { /* best-effort cleanup */ }
            }
        }
        _staged.Clear();
    }

    private sealed record StagedFile(string StagingPath, string TargetPath, ManifestFile Remote);
}
