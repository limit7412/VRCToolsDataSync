using System.Security.Cryptography;
using System.Text;

namespace VRCToolsDataSync.Core.Infra;

/// <summary>
/// 対話セッションをまたいで見える <see cref="Mutex"/> を作る (issue #52)。
/// <para>
/// 接頭辞を付けないと、名前は対話セッションごとの名前空間に作られる。同じ
/// 利用者がユーザーの切り替えやリモートデスクトップで 2 つのセッションを持つと、
/// 守りたい資源 (インストール先や、置き換え待ちの置き場所) は共有されるのに、
/// ロックだけが互いに見えなくなる。
/// </para>
/// <para>
/// 設定の保存はこれを使わない。<see cref="CrossSessionFileLock"/> へ移した
/// (issue #81)。<c>Global\</c> を作れない相手や開けない相手がセッション内だけの
/// 名前へ落ちると、誰とも待ち合わせずに進む。設定はそこで利用者の入力を失う。
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
    /// 断られたら、まず開き直す。<see cref="UnauthorizedAccessException"/> は
    /// 「この環境では作れない」だけでなく「その名前のものが既にあり、全部の権利
    /// では開けない」でも飛ぶ。後者でセッション内だけの名前へ落ちると、先客は
    /// <c>Global\</c> の物を、こちらは別の物を、それぞれ同時に持ててしまう。
    /// ロックを持っているつもりで誰とも待ち合わせていない状態になり、守りたかった
    /// 読んで書き戻す一連が並走する。<see cref="Mutex.OpenExisting(string)"/> は
    /// 待ち合わせに要る権利だけを求めるので、作れなくても開ける場合はこちらが通る。
    /// </para>
    /// <para>
    /// 開くこともできなかったときだけ、セッション内だけの名前へ落ちる。ここは
    /// 守り切れていない。相手 (たとえば同じ利用者の昇格したプロセス) とは
    /// 待ち合わせられないままである。それでも落ちるのは、投げると保存そのものが
    /// できなくなり、並走していなくても書けない側の損が確実に出るためである。
    /// 落ちた側は、並走したときだけ失う。
    /// </para>
    /// <para>
    /// 断られたことは覚えない。毎回試す。名前ごとの事情を環境の事情として覚えると、
    /// 以後どの名前もセッション内だけの名前へ落ちて、黙って守れなくなる。
    /// </para>
    /// </summary>
    public static Mutex Create(string name)
    {
        var globalName = @"Global\" + name;
        try
        {
            return new Mutex(initiallyOwned: false, name: globalName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            try
            {
                // 待ち合わせに要る権利 (Synchronize と Modify) だけで開く。
                return Mutex.OpenExisting(globalName);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // 物が無い。つまり断られたのは権限であって、この環境では作れない。
            }
            catch (Exception inner) when (inner is UnauthorizedAccessException or IOException or NotSupportedException)
            {
                // 物はあるが、開くこともできない。
            }

            return new Mutex(initiallyOwned: false, name: name);
        }
    }

    /// <summary>
    /// パスを名前に使える短い鍵にする。
    /// <para>
    /// パスはそのまま名前に使えない (区切りを含む) ので縮める。守る相手ごとに
    /// 名前を分けるために使う。まとめてしまうと、無関係な相手どうしが待ち合わせる。
    /// </para>
    /// <para>
    /// 縮める前に綴りをそろえる。ここで見ているのはファイルそのものではなく
    /// 文字列なので、<c>C:\dir\settings.json</c> と <c>C:\dir\.\settings.json</c>
    /// のように OS が同じ場所へ解決する綴りでも、そのままでは別の鍵になる。
    /// 別の鍵は別のロックであり、同じ資源を守っているつもりで守れていない状態に
    /// なる。<see cref="Path.GetFullPath(string)"/> で絶対パスに直して区切りを
    /// そろえ、末尾の区切りを落とし、大文字小文字を Windows のファイルシステムに
    /// 合わせて畳む。
    /// </para>
    /// </summary>
    public static string ScopeKeyOf(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
