using Microsoft.Extensions.Logging;
using VRCToolsDataSync.Core.Domain;
using VRCToolsDataSync.Core.Infra;

namespace VRCToolsDataSync.Core.UseCase;

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
    /// <summary>取り出し中のファイルに付ける目印。同期対象と取り違えないために使う。</summary>
    public const string IncomingMarker = ".incoming-";

    /// <summary>目印に続く GUID の長さ ("N" 書式)。</summary>
    private const int IncomingSuffixLength = 32;

    /// <summary>
    /// 取り出し中のファイルかを判定する。目印を含むだけで判定すると、
    /// "2024.incoming-notes.txt" のような正当なファイルまで同期対象から外れてしまうため、
    /// 「目印 + 32 桁の 16 進」で終わることまで見る。
    /// </summary>
    public static bool IsIncomingFile(string path)
    {
        var name = Path.GetFileName(path);
        var markerIndex = name.LastIndexOf(IncomingMarker, StringComparison.Ordinal);
        if (markerIndex < 0) return false;
        var suffix = name.AsSpan(markerIndex + IncomingMarker.Length);
        if (suffix.Length != IncomingSuffixLength) return false;
        foreach (var c in suffix)
        {
            if (!char.IsAsciiHexDigitLower(c)) return false;
        }
        return true;
    }

    private readonly List<StagedFile> _staged = new();

    /// <summary>
    /// 取得を省いたファイルのうち、同期先の内容を読むために残した写し。目的地から写しへ。
    /// 差し替えには使わない。
    /// </summary>
    private readonly Dictionary<string, string> _copies = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger _logger;

    public PullStaging(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// manifest のエントリを同期先から取り出す。手元が既に同じ内容なら取得しない。
    /// この時点ではローカルの既存ファイルには一切触れない。
    /// <para>
    /// <paramref name="keepCopyWhenUnchanged"/> を指定すると、取得しない場合も手元の
    /// ファイルの写しを隣に作り、<see cref="SourceOf"/> がそれを返す。SQLite の本体は
    /// ハッシュが同期先と同じでも、隣の WAL にまだ本体へ書き戻していない手元の変更を
    /// 持ちうる。本体をそのまま開くと WAL ごと読むので、同期先の内容を読むには
    /// WAL を伴わない写しが要る。
    /// </para>
    /// </summary>
    /// <returns>同期先にそのキーが無ければ false。</returns>
    public bool Fetch(ISyncStorage storage, ManifestFile remote, string targetPath, bool keepCopyWhenUnchanged = false)
    {
        if (!string.IsNullOrEmpty(remote.Sha256)
            && File.Exists(targetPath)
            && string.Equals(FileHasher.Sha256(targetPath), remote.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            if (!keepCopyWhenUnchanged)
            {
                _logger.LogInformation("取得を省略 (内容が同じ): {Key}", remote.RelativePath);
                return true;
            }
            // ハッシュを取ってから写すまでの間にツールが WAL を本体へ書き戻すと、写しは
            // 同期先と違う内容になる。写した後にもう一度確かめ、違っていれば取得する。
            var copyPath = targetPath + IncomingMarker + Guid.NewGuid().ToString("N");
            File.Copy(targetPath, copyPath, overwrite: true);
            if (string.Equals(FileHasher.Sha256(copyPath), remote.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _copies[targetPath] = copyPath;
                _logger.LogInformation("取得を省略 (内容が同じ): {Key}", remote.RelativePath);
                return true;
            }
            try { File.Delete(copyPath); } catch { /* best-effort cleanup */ }
        }

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stagingPath = targetPath + IncomingMarker + Guid.NewGuid().ToString("N");
        // 取り出し元は内容から決まるキー。schemaVersion 1 の manifest では
        // RelativePath がそのままキーだったので、そちらへ落ちる。
        if (!storage.TryDownload(ManifestFileKeys.StorageKeyOf(remote), stagingPath))
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

    /// <summary>
    /// <paramref name="targetPath"/> を差し替える予定があるか。
    /// 手元が既に同じ内容で取得を省いた場合は false になる。
    /// </summary>
    public bool IsStaged(string targetPath)
    {
        foreach (var staged in _staged)
        {
            if (string.Equals(staged.TargetPath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// <paramref name="targetPath"/> の同期先の内容を持つファイルの場所。取り出したファイルか、
    /// 取得を省いたときに残した写し (<see cref="Fetch"/> の keepCopyWhenUnchanged)。
    /// 差し替える前に、差し替え後の内容を読むのに使う。
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// 写しを残さずに取得を省いたか、取り出していない。手元のファイルを代わりに返すと、
    /// WAL に残る手元の変更まで同期先の内容として読んでしまう。
    /// </exception>
    public string SourceOf(string targetPath)
    {
        foreach (var staged in _staged)
        {
            if (string.Equals(staged.TargetPath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                return staged.StagingPath;
            }
        }
        if (_copies.TryGetValue(targetPath, out var copyPath)) return copyPath;
        throw new InvalidOperationException($"同期先の内容を持つ写しがありません: {targetPath}");
    }

    /// <summary>
    /// 取り出したファイルを目的の場所へ移す。
    /// <para>
    /// ファイル間で不可分ではない。途中で失敗した場合は、どこまで反映したかを
    /// 添えて例外にする。Pull は manifest を正として組み立てるので、
    /// 同じ Pull をやり直せば残りが反映される。
    /// </para>
    /// </summary>
    public void Apply(List<string> affected)
    {
        var applied = new List<string>();
        foreach (var staged in _staged)
        {
            try
            {
                if (File.Exists(staged.TargetPath))
                {
                    File.Replace(staged.StagingPath, staged.TargetPath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(staged.StagingPath, staged.TargetPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var done = applied.Count == 0 ? "(なし)" : string.Join(", ", applied);
                throw new IOException(
                    $"取得したファイルの差し替えに失敗しました: {staged.TargetPath}。" +
                    $"反映済み: {done}。もう一度 Pull すると残りが反映されます。", ex);
            }
            applied.Add(staged.TargetPath);
            affected.Add(staged.TargetPath);
            DeleteSidecars(staged.StagingPath);
        }
        _staged.Clear();
    }

    /// <summary>差し替えていない取り出し済みファイルと、読むために残した写しを片付ける。</summary>
    public void Dispose()
    {
        foreach (var staged in _staged)
        {
            DeleteQuietly(staged.StagingPath);
            DeleteSidecars(staged.StagingPath);
        }
        _staged.Clear();
        foreach (var copyPath in _copies.Values)
        {
            DeleteQuietly(copyPath);
            DeleteSidecars(copyPath);
        }
        _copies.Clear();
    }

    /// <summary>
    /// 取り出した SQLite を差し替える前に読むと、WAL 方式の本体では隣に -wal と -shm が
    /// できる。本体を移した後に残ると、取り出し中の目印の付いたごみになる。
    /// </summary>
    private static void DeleteSidecars(string path)
    {
        DeleteQuietly(path + "-wal");
        DeleteQuietly(path + "-shm");
        DeleteQuietly(path + "-journal");
    }

    private static void DeleteQuietly(string path)
    {
        if (!File.Exists(path)) return;
        try { File.Delete(path); } catch { /* best-effort cleanup */ }
    }

    private sealed record StagedFile(string StagingPath, string TargetPath, ManifestFile Remote);
}
