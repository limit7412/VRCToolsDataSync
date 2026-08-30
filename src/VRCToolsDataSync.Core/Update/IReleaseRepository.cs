namespace VRCToolsDataSync.Core.Update;

/// <summary>
/// リリースを取る境界。実 API を叩かずに <see cref="UpdateChecker"/> を
/// 確かめられるよう、抽象を挟む。
/// </summary>
public interface IReleaseRepository
{
    /// <summary>
    /// 候補になるリリースを返す。並び順は問わない。呼び出し側が版で比べる。
    /// 取れなかった場合は例外を投げる。呼び出し側が握る。
    /// </summary>
    Task<ReleaseCatalog> FetchReleasesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 配布物を path へ取り、宣言された大きさと digest に照合する。
    /// 照合まで通ったときだけ path にファイルが残る。それ以外は例外を投げる。
    /// </summary>
    Task DownloadAsync(ReleaseAsset asset, string path, CancellationToken cancellationToken = default);
}
