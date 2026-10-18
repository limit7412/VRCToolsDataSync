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
}
