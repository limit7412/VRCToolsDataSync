using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCToolsDataSync.Core.Sync;

namespace VRCToolsDataSync.Core.Storage;

/// <summary>回収の結果。</summary>
/// <param name="Scanned">走査したオブジェクトの数。</param>
/// <param name="Live">現在の manifest から参照されていたもの。</param>
/// <param name="Young">参照されていないが、猶予期間内で残したもの。</param>
/// <param name="Deleted">削除したもの。</param>
/// <param name="DeletedBytes">削除したものの合計サイズ (分かる場合)。</param>
/// <param name="Failed">削除に失敗したもの。</param>
public sealed record BlobGarbageCollectionResult(
    int Scanned,
    int Live,
    int Young,
    int Deleted,
    long DeletedBytes,
    int Failed);

/// <summary>
/// どの manifest からも参照されていないオブジェクトを回収する。
/// <para>
/// Push は実データを消さない。置き場所を内容から決めているため、同じ内容を別の
/// 世代や別の tool が参照していることがあり、Push の後始末として消すと、他の PC が
/// 公開したばかりの manifest が欠落オブジェクトを指しうる。代わりに、参照が切れた
/// ものをここでまとめて回収する。
/// </para>
/// <para>
/// 参照されていないだけでは消さない。<b>猶予期間</b>を過ぎたものだけを対象にする。
/// 他の PC が今まさに送っている最中のオブジェクトは、まだどの manifest からも
/// 参照されていないため、これが無いと進行中の Push を壊す。
/// </para>
/// <para>
/// 参照が 1 件も集まらなかった場合は回収そのものを中止する。manifest を読めない
/// 一瞬に当たっただけかもしれず、その判断のまま走らせると全部消すことになる。
/// </para>
/// <para>
/// 削除の直前には <see cref="ISyncStorage.Stat"/> で更新日時を読み直し、その状態を
/// 条件にして <see cref="ISyncStorage.TryDelete"/> で消す。列挙は取った時点の写しなので、
/// 走査に時間がかかるほど判断が古くなる。
/// </para>
/// <para>
/// <b>読み直しから削除までの隙間は、どちらの同期先でも閉じられない。</b> S3 の条件付き
/// 削除は ETag を条件に取るが、ETag は内容の関数なので、内容から決まるキーでは
/// 送り直しを区別できない。Win32 にも更新時刻を条件にする不可分な削除は無い。
/// 残る幅は 1 往復ぶんで猶予期間 (既定 7 日) に対しては無視でき、ここに入った場合も
/// 次の Push が実体の欠落を見つけて送り直すため自然に回復する。
/// </para>
/// </summary>
public sealed class BlobGarbageCollector
{
    /// <summary>猶予期間の既定値。Push 1 回の所要時間に対して十分長く取る。</summary>
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromDays(7);

    private readonly ISyncStorage _storage;
    private readonly ILogger _logger;

    public BlobGarbageCollector(ISyncStorage storage, ILogger<BlobGarbageCollector>? logger = null)
    {
        _storage = storage;
        _logger = logger ?? NullLogger<BlobGarbageCollector>.Instance;
    }

    /// <summary>
    /// 参照が切れて猶予期間を過ぎたオブジェクトを削除する。
    /// </summary>
    /// <param name="gracePeriod">
    /// これより新しいオブジェクトは、参照されていなくても残す。
    /// </param>
    /// <param name="dryRun">true なら削除せず、対象の数だけ数える。</param>
    public BlobGarbageCollectionResult Collect(TimeSpan? gracePeriod = null, bool dryRun = false)
    {
        var grace = gracePeriod ?? DefaultGracePeriod;
        if (grace < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(gracePeriod), "猶予期間に負の値は指定できません");
        }

        // 生きている参照を先に集める。manifest を読んでから列挙する順にすることで、
        // 「読んだ後に公開された manifest が参照するオブジェクト」は列挙側に現れても
        // 新しいので猶予期間に守られる。逆順にすると、列挙してから manifest を読むまでの
        // 間に公開されたものを取りこぼす。
        var live = CollectLiveKeys();
        if (live.Count == 0)
        {
            // 参照が 1 件も無い。ここから走査すると、猶予期間を過ぎた実体をすべて
            // 孤児と判定して消す。
            //
            // ローカルフォルダでは、同期クライアントが manifest.json を置き換える
            // 一瞬だけファイルが存在しない状態になりうる。そこに当たると
            // LoadManifest は空の manifest を返し、実際には全部生きている実体を
            // 全部孤児と判定する。存在しない manifest と「中身が空の manifest」を
            // 読んだ結果から区別する手立ては無いので、どちらでも走らせない。
            //
            // 本当に何も同期していない場合は、回収すべき実体もまだ無い。止めて困らない。
            throw new SyncStorageException(
                "manifest から参照されている実体が 1 件もありません。" +
                "manifest がまだ無いか、読めない状態だった可能性があります。" +
                "この状態で回収すると生きている実体まで消すため、中止しました。");
        }
        var threshold = DateTimeOffset.UtcNow - grace;

        var scanned = 0;
        var liveCount = 0;
        var young = 0;
        var deleted = 0;
        var deletedBytes = 0L;
        var failed = 0;

        foreach (var stored in _storage.List(BlobKeys.Prefix))
        {
            scanned++;

            if (live.Contains(stored.Key))
            {
                liveCount++;
                continue;
            }
            if (stored.LastModified > threshold)
            {
                // 他の PC が送っている最中かもしれない。次回に回す。
                young++;
                continue;
            }

            if (dryRun)
            {
                deleted++;
                deletedBytes += stored.Size;
                _logger.LogInformation("回収対象 (実行はしない): {Key}", stored.Key);
                continue;
            }

            try
            {
                // 列挙は取った時点の写しでしかない。列挙してからここへ来るまでの間に、
                // 別の PC の Push が同じ内容を再利用して置き直していることがある。
                // その実体はこれから公開される manifest に参照されるので、列挙時の
                // 古い日時のまま消すと欠落になる。削除の直前に読み直して判定し直す。
                var current = _storage.Stat(stored.Key);
                if (current is null)
                {
                    // 既に無い。他の PC の回収と重なっただけなので、成功でも失敗でもない。
                    continue;
                }
                if (current.LastModified > threshold)
                {
                    young++;
                    _logger.LogInformation("列挙後に書き直されたため残します: {Key}", stored.Key);
                    continue;
                }

                // 読み直した状態を条件にして消す。読み直してから削除するまでの間にも
                // 別の PC が置き直しうるので、そこは同期先に判定させる。
                if (!_storage.TryDelete(current))
                {
                    young++;
                    _logger.LogInformation("読み直した後に書き直されたため残します: {Key}", stored.Key);
                    continue;
                }

                deleted++;
                deletedBytes += current.Size;
                _logger.LogInformation("回収しました: {Key}", stored.Key);
            }
            catch (SyncStorageException ex)
            {
                // 1 件の失敗で全体を止めない。残りは次回に回る。
                failed++;
                _logger.LogWarning(ex, "回収に失敗しました: {Key}", stored.Key);
            }
        }

        var result = new BlobGarbageCollectionResult(scanned, liveCount, young, deleted, deletedBytes, failed);
        _logger.LogInformation(
            "回収完了 scanned={Scanned} live={Live} young={Young} deleted={Deleted} failed={Failed}",
            result.Scanned, result.Live, result.Young, result.Deleted, result.Failed);
        return result;
    }

    /// <summary>
    /// 現在の manifest が参照しているキーを集める。
    /// schemaVersion 1 の manifest では RelativePath がそのままキーなので、
    /// <see cref="ManifestFileKeys.StorageKeyOf"/> を通す。
    /// </summary>
    private HashSet<string> CollectLiveKeys()
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        var manifest = new ManifestStore(_storage).Load();

        // 扱えない形式なら Load が投げる。知らない形式が持つ参照は数え切れず、
        // 見えない参照は孤児に見えるため、読めた範囲で進めてはいけない。
        // 下のゼロ件の検査では防げない (既知のエントリが 1 つでもあれば素通りする)。

        foreach (var entry in manifest.Tools.Values)
        {
            foreach (var file in entry.Files)
            {
                live.Add(ManifestFileKeys.StorageKeyOf(file));
            }
        }
        return live;
    }
}
