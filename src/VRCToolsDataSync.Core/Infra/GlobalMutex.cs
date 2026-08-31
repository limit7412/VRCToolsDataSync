using System.Security.Cryptography;
using System.Text;

namespace VRCToolsDataSync.Core.Infra;

/// <summary>
/// 対話セッションをまたいで見える <see cref="Mutex"/> を作る (issue #52)。
/// <para>
/// 接頭辞を付けないと、名前は対話セッションごとの名前空間に作られる。同じ
/// 利用者がユーザーの切り替えやリモートデスクトップで 2 つのセッションを持つと、
/// 守りたい資源 (<c>%AppData%</c> の下や、インストール先) は共有されるのに、
/// ロックだけが互いに見えなくなる。
/// </para>
/// </summary>
internal static class GlobalMutex
{
    /// <summary>
    /// <c>Global\</c> の名前で作る。作れない場合はセッション内だけの名前で妥協する。
    /// <para>
    /// <c>Global\</c> の名前を作れない構成もある (権限を落とした環境や、名前に
    /// 区切りを許さないプラットフォーム)。そこで投げるより、同じセッションの
    /// 重なりだけでも防げるほうがよい。
    /// </para>
    /// <para>
    /// 作れなかったことは覚えない。毎回試す。<see cref="UnauthorizedAccessException"/>
    /// は「この環境では作れない」だけでなく「その名前のものが既にあり、開く権利が
    /// 無い」でも飛ぶ。名前ごとの事情を環境の事情として覚えると、以後どの名前も
    /// セッション内だけの名前へ落ちて、黙って守れなくなる。例外の往復より、
    /// 取り違えないことを採る。
    /// </para>
    /// </summary>
    public static Mutex Create(string name)
    {
        try
        {
            return new Mutex(initiallyOwned: false, name: @"Global\" + name);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return new Mutex(initiallyOwned: false, name: name);
        }
    }

    /// <summary>
    /// パスを名前に使える短い鍵にする。
    /// <para>
    /// パスはそのまま名前に使えない (区切りを含む) ので縮める。大文字小文字は
    /// Windows のファイルシステムに合わせて畳む。守る相手ごとに名前を分けるため
    /// に使う。まとめてしまうと、無関係な相手どうしが待ち合わせる。
    /// </para>
    /// </summary>
    public static string ScopeKeyOf(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(path).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
