using System.Security.Cryptography;
using System.Text;

namespace VRCToolsDataSync.Core.Settings;

/// <summary>
/// settings.json に載せる秘密情報を Windows の DPAPI で保護する。
/// <para>
/// シークレットアクセスキーは、漏れると保存先の読み書きに加えて、S3 の場合は
/// 転送量課金の踏み台にもされる。settings.json は %AppData% 配下にあり
/// 他ユーザからは読めないが、バックアップや設定の持ち出しで平文が出回るのを
/// 避けるため、ログインユーザだけが復号できる形にしておく。
/// </para>
/// <para>
/// 保護は同じ Windows ユーザプロファイル内でのみ復号できる。settings.json を
/// 別 PC へコピーしても復号できないので、PC ごとにキーを設定し直す必要がある。
/// </para>
/// </summary>
public static class SecretProtector
{
    // 別用途の DPAPI blob と取り違えないための追加エントロピー。
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VRCToolsDataSync.S3Credential.v1");

    /// <summary>平文を保護して Base64 文字列にする。空なら null を返す。</summary>
    public static string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return null;
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    /// <summary>
    /// <see cref="Protect"/> の結果を復号する。
    /// 別ユーザ / 別 PC の settings.json をそのまま持ち込んだ場合など、復号できない
    /// ときは null を返す。呼び出し側は「キー未設定」として扱う。
    /// </summary>
    public static string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try
        {
            var plainBytes = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
