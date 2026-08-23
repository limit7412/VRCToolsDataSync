namespace VRCToolsDataSync.Core.Sync;

/// <summary>
/// 同期先へ置くオブジェクトのキーを内容から決める。
/// <para>
/// 固定キー (<c>vrcx/latest.sqlite3</c> など) へ上書きしていた頃は、「ファイルを
/// 書く」と「manifest を更新する」が不可分でないため、2 台が同時に Push すると
/// manifest の記録と実体がずれることがあった。内容から決まるキーへ置けば、
/// 同じキーには常に同じ中身しか入らないので、このずれは起こりようがない。
/// </para>
/// <para>
/// 同じ内容を二度送らずに済む利点もある。変わっていないファイルはキーも変わらず、
/// 既にあるものとして送信を省ける。
/// </para>
/// </summary>
public static class BlobKeys
{
    /// <summary>内容から決まるオブジェクトを置く場所。</summary>
    public const string Prefix = "blobs/";

    /// <summary>SHA-256 (小文字 16 進) からキーを作る。</summary>
    public static string FromSha256(string sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            throw new ArgumentException("SHA-256 が空です", nameof(sha256));
        }
        return Prefix + sha256.ToLowerInvariant();
    }

    /// <summary>そのキーが内容から決まるオブジェクトのものか。</summary>
    public static bool IsBlobKey(string key)
        => key.StartsWith(Prefix, StringComparison.Ordinal);
}
