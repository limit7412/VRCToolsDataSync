using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace VRCToolsDataSync.Core.Domain;

/// <summary>更新を拾うチャンネル (issue #45)。</summary>
public enum UpdateChannel
{
    /// <summary>安定版 (X.Y.Z) だけを拾う。</summary>
    Stable,

    /// <summary>プレリリース (X.Y.Z-testN) も拾う。</summary>
    Test,
}

/// <summary>
/// リリースに添付された配布物。
/// <para>
/// digest を持たないものは扱わない。置き換えるのは実行ファイル一式であり、
/// 取ってきたものが本当にそのリリースのものかを確かめずに置くわけにはいかない。
/// 確かめられなければ、リリースページを開いてもらうところまでで止める。
/// </para>
/// </summary>
public sealed partial record ReleaseAsset(string Name, string Url, string DigestHex, long Size)
{
    private const string DigestPrefix = "sha256:";

    // 16 進 64 桁。桁数と字種を見ておかないと、照合の相手として使えない値を持ち込む。
    [GeneratedRegex(@"^[0-9a-f]{64}$")]
    private static partial Regex DigestPattern();

    /// <summary>
    /// API の応答から、置き換えに使えるものだけを組み立てる。
    /// <para>
    /// 名前で絞るのは、リリースに別のアセットが増えても取り違えないためである。
    /// 大きさを見るのは、宣言と実物の食い違いを取得の側で打ち切れるようにするためである。
    /// </para>
    /// </summary>
    public static ReleaseAsset? TryCreate(string? name, string? url, string? digest, long size, string expectedName)
    {
        if (name is null || url is null) return null;
        if (!string.Equals(name, expectedName, StringComparison.Ordinal)) return null;
        if (size <= 0) return null;
        if (digest is null || !digest.StartsWith(DigestPrefix, StringComparison.Ordinal)) return null;

        var hex = digest[DigestPrefix.Length..].ToLowerInvariant();
        if (!DigestPattern().IsMatch(hex)) return null;

        return new ReleaseAsset(name, url, hex, size);
    }

    /// <summary>
    /// 実行中のプロセスのアーキテクチャに合う配布物の名前。
    /// リリースに添付しないアーキテクチャ (x86 など) では null を返す。
    /// 名前は release.yml / prerelease.yml が添付する ZIP の名前と一致させる。
    /// <para>
    /// OS ではなくプロセスのアーキテクチャで選ぶ。置き換えるのは実行中の
    /// 一式であり、OS に合わせると ARM64 の Windows でエミュレーション実行して
    /// いる x64 版へ arm64 の ZIP を渡すことになる。x64 の Windows で動く x86 版も、
    /// 配布の無いアーキテクチャとして null に落ちる。
    /// </para>
    /// </summary>
    public static string? NameForCurrentArchitecture()
        => RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "VRCToolsDataSync-win-x64.zip",
            Architecture.Arm64 => "VRCToolsDataSync-win-arm64.zip",
            _ => null,
        };
}

/// <summary>確認の対象にするリリース。</summary>
public sealed record ReleaseInfo(
    ReleaseVersion Version,
    string Tag,
    string HtmlUrl,
    bool Prerelease,
    ReleaseAsset? Asset)
{
    /// <summary>
    /// 安定版のチャンネルで拾う対象か。
    /// <para>
    /// タグの綴りと GitHub の印の両方を見る。プレリリースは X.Y.Z-testN で
    /// 自動生成されるので普段はどちらも同じことを言うが、手動で作ったリリースに
    /// プレリリースの印だけが付くことはありうる。
    /// </para>
    /// </summary>
    public bool IsStable => !Prerelease && Version.IsStable;

    /// <summary>指定のチャンネルで拾う対象か。test は両方を拾う。</summary>
    public bool IsInChannel(UpdateChannel channel)
        => channel == UpdateChannel.Test || IsStable;
}

/// <summary>
/// 集めた候補と、集めきれたかどうか。
/// <para>
/// 取得には上限があるため、「新しい版は無い」と言い切れないことがある。
/// 集めきれていないのに最新だと言うと、実際には出ている版を見落としたまま
/// 利用者へ「最新である」と伝えることになる。
/// </para>
/// </summary>
public sealed record ReleaseCatalog(IReadOnlyList<ReleaseInfo> Releases, bool Complete);

/// <summary>
/// 確認の結末。
/// <para>
/// 「新しい版が無い」と「確かめられなかった」を呼び出し側が書き分けられるよう、
/// null ではなく結末そのものを返す。バルーンはどちらでも黙るが、画面の状態欄は
/// 「最新の版を利用中」と「確認できませんでした」を書き分ける。
/// </para>
/// </summary>
public enum UpdateCheckOutcome
{
    /// <summary>新しい版が出ている。</summary>
    Available,

    /// <summary>実行中の版が最新である。</summary>
    UpToDate,

    /// <summary>一覧を取れなかった。回線が無いか、API が応えなかった。</summary>
    Unreachable,

    /// <summary>
    /// 候補を集めきれず、最新かどうかを言い切れない。
    /// 取得の上限に掛かり、押し出された範囲に新しい版が残っている場合がこれにあたる。
    /// </summary>
    Incomplete,

    /// <summary>実行中の版を比べられない。手元ビルドの 0.0.0-dev がこれにあたる。</summary>
    Unknown,
}

/// <summary>確認 1 回分の結果。</summary>
public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, ReleaseInfo? Release = null)
{
    public bool IsAvailable => Outcome == UpdateCheckOutcome.Available;
}
