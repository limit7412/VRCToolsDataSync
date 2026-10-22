using System.Security.Cryptography;
using System.Text;

namespace VRCToolsDataSync.Core.Infra;

/// <summary>
/// SQLite の本体と WAL の状態 (長さと更新時刻) を一つの文字列にする。同期の処理の前後で
/// 比べて、その間にツールが DB を書き換えたかを見るのに使う。
/// </summary>
public static class DatabaseFingerprint
{
    public static string Of(string path)
    {
        static string OfMain(string file)
        {
            var info = new FileInfo(file);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "-";
        }
        static string OfWal(string file)
        {
            // WAL 方式の本体を読み取り専用で開くだけでも、WAL が無ければ空の WAL ができる。
            // 写しや要約を取る自分の読み取りで作られた空の WAL を、ツールの書き込みと見ないよう、
            // 中身の無い WAL は無いものとして扱う。書き込みがあれば長さが変わる。
            var info = new FileInfo(file);
            return info.Exists && info.Length > 0 ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "-";
        }
        return $"{OfMain(path)}|{OfWal(path + "-wal")}";
    }

    /// <summary>
    /// SQLite ではない普通のファイル (設定の JSON など) の状態。無ければ "-"。同期で
    /// 差し替えるファイルは、DB と同じように処理の前後で比べる。
    /// </summary>
    public static string OfFile(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "-";
    }

    /// <summary>
    /// フォルダの中の全ファイル (下の階層も含む) の名前と状態をまとめたハッシュ。無ければ "-"。
    /// 同期で中身を差し替えるフォルダは、ファイルの増減も含めて処理の前後で比べる。
    /// <paramref name="exclude"/> が真を返すファイル (取得の途中のファイルなど) は数えない。
    /// </summary>
    public static string OfDirectory(string path, Func<string, bool>? exclude = null)
    {
        if (!Directory.Exists(path)) return "-";
        var entries = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(file => exclude is null || !exclude(file))
            .Select(file => $"{Path.GetRelativePath(path, file)}|{OfFile(file)}")
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();
        // 数えるファイルが一つも無いフォルダは、無いフォルダと同じ値にする。Pull の取り出しは
        // 無かったフォルダを作ってから取得中のファイルを置くので、ここで区別すると初回の
        // Pull が「取得の間に書き換えられた」と見て必ず中止してしまう。
        if (entries.Count == 0) return "-";
        var text = string.Join("\n", entries);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }
}
