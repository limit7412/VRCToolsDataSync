using System.Security.Cryptography;
using System.Text.Json;
using VRCToolsDataSync.Core.Domain;

namespace VRCToolsDataSync.Core.Infra;

/// <summary>
/// manifest の「中身が変わったか」を表す指紋。監視側が同じ内容で二重に通知しない
/// ための比較にだけ使う。
/// </summary>
internal static class ManifestSignature
{
    /// <summary>
    /// ETag を出す同期先はそれを使う。出さない同期先 (ローカルフォルダ) では
    /// manifest の中身そのものから作る。
    /// <para>
    /// 中身を使うのは、ツール名と version だけでは足りないため。ローカルフォルダ
    /// モードには条件付き書き込みが無いので、同じ旧 version から 2 台が並行して
    /// Push すると、どちらも同じ次の version を持つ manifest を作れる。自分の
    /// version 2 を見た後に別 PC の version 2 が届くと、version だけの指紋では
    /// 同じと判定してリモートの更新を握り潰してしまう。MachineName やファイルの
    /// 一覧まで含めれば、この 2 つは別物として扱える。
    /// </para>
    /// </summary>
    public static string Build(ManifestSnapshot snapshot)
    {
        if (snapshot.VersionTag is { Length: > 0 } tag)
        {
            return "tag:" + tag;
        }

        // 直列化したものをそのまま持たず、ハッシュにして比較する。note が多い
        // manifest でも指紋の大きさが一定になる。
        var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot.Manifest, ManifestJson.Options);
        return "content:" + Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }
}
