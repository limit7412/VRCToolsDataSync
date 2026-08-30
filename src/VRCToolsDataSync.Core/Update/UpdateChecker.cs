using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VRCToolsDataSync.Core.Update;

/// <summary>
/// 新しい版を探し、見つけたかどうかを覚える (issue #45)。
/// <para>
/// 状態の読み書きはロックで守る。定期の確認と、チャンネルを変えたときの確認が
/// 並走しうるためである。
/// </para>
/// </summary>
public sealed class UpdateChecker
{
    private readonly IReleaseRepository _repository;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private ReleaseInfo? _available;

    // 知らせ済みの版。同じ版を確認のたびに知らせないための記録である。
    // 起動のたびに知らせ直さないよう、設定へ残して次の起動で復元する。
    private ReleaseVersion? _notified;

    // 直近の確認に使ったチャンネル。まだ確認できていなければ null。
    //
    // 結果と一緒に覚えておかないと、設定を test から stable へ変えたときに、
    // 直前に見つけたプレリリースが次の確認まで表示に残る。
    // 逆向きでは、プレリリースを一度も調べていないのに「最新である」と出る。
    private UpdateChannel? _checkedChannel;

    // 直近の確認で候補を集めきれたか。
    private bool _complete = true;

    public UpdateChecker(IReleaseRepository repository, ILogger? logger = null)
    {
        _repository = repository;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>知らせ済みの版のタグ。設定へ書き戻すために使う。</summary>
    public string NotifiedTag
    {
        get { lock (_gate) return _notified?.ToString() ?? string.Empty; }
    }

    /// <summary>設定から復元する。版として読めない値は記録が無いものとして扱う。</summary>
    public void RestoreNotifiedTag(string tag)
    {
        var version = ReleaseVersion.Parse(tag);
        lock (_gate) _notified = version;
    }

    /// <summary>
    /// 知らせた版として覚える。通知を出せた後に呼ぶ。
    /// <para>
    /// 覚えるのはここだけである。
    /// 確認の側で覚えると、出せなかった版まで覚えてしまい、
    /// 利用者が一度も見ないまま以後の確認で抑止される。
    /// </para>
    /// <para>
    /// 記録より古い版で上書きしない。チャンネルを往復したときに、
    /// 既に伝えてある版より古いものへ記録が下がると、その版をまた知らせることになる。
    /// </para>
    /// </summary>
    public void MarkNotified(ReleaseInfo release)
    {
        lock (_gate)
        {
            if (_notified is not null && _notified >= release.Version) return;
            _notified = release.Version;
        }
    }

    /// <summary>
    /// 今のチャンネルで見つけている新しい版。
    /// 別のチャンネルで確認した結果は、設定と食い違うため返さない。
    /// </summary>
    public ReleaseInfo? Available(UpdateChannel channel)
    {
        lock (_gate)
        {
            return _checkedChannel == channel ? _available : null;
        }
    }

    /// <summary>
    /// 今のチャンネルで確認が成り立ったか。
    /// 「新しい版は無い」と「まだ確かめていない」を画面で書き分けるために使う。
    /// </summary>
    public bool HasChecked(UpdateChannel channel)
    {
        lock (_gate) return _checkedChannel == channel;
    }

    /// <summary>
    /// 直近の確認で候補を集めきれたか。
    /// 集めきれていない場合、画面は「最新である」と言い切らない。
    /// </summary>
    public bool IsComplete
    {
        get { lock (_gate) return _complete; }
    }

    /// <summary>
    /// 既に知らせた版なら UpToDate へ倒す。
    /// <para>
    /// 同じ版のバルーンを起動のたびに出さないためである。倒すのは結末だけで、
    /// 候補そのものは残る。画面の行と取得はそちらを見る。
    /// </para>
    /// <para>
    /// 等値で見るわけにはいかない。チャンネルを往復すると記録が入れ替わるためである。
    /// stable の 2.0.0 を知らせた後に test の 2.1.0-test1 を知らせて stable へ戻すと、
    /// 記録は 2.1.0-test1 になっており、等値では 2.0.0 を未通知と判断して知らせ直す。
    /// 「これ以上に新しいものは既に伝えてある」と読めば、往復しても増えない。
    /// </para>
    /// <para>
    /// ここでは覚えない。覚えるのは <see cref="MarkNotified"/> であり、呼ぶのは
    /// 通知を出せた後である。ここで覚えると、出せなかった版まで以後の確認で抑止される。
    /// </para>
    /// </summary>
    public UpdateCheckResult SuppressNotified(UpdateCheckResult result)
    {
        if (!result.IsAvailable || result.Release is null) return result;

        lock (_gate)
        {
            if (_notified is not null && _notified >= result.Release.Version)
            {
                return new UpdateCheckResult(UpdateCheckOutcome.UpToDate, result.Release);
            }
        }
        return result;
    }

    /// <summary>
    /// 新しい版を探す。
    /// <para>
    /// 手元ビルドの版 (0.0.0-dev) は運用しているタグの綴りから外れており、
    /// 何と比べても順序が決まらない。確認そのものを行わず Unknown を返す。
    /// </para>
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(
        string currentVersion,
        UpdateChannel channel,
        CancellationToken cancellationToken = default)
    {
        var running = ReleaseVersion.Parse(currentVersion);
        if (running is null)
        {
            _logger.LogDebug("実行中の版を比べられないため確認しない: {Version}", currentVersion);
            return new UpdateCheckResult(UpdateCheckOutcome.Unknown);
        }

        ReleaseCatalog catalog;
        try
        {
            catalog = await _repository.FetchReleasesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 通すのは呼び出し側の中止だけにする。HttpClient のタイムアウトも
            // OperationCanceledException で届くため、型だけで再送出すると、
            // 遅い回線の確認が Unreachable にならず例外として漏れる。
            throw;
        }
        catch (Exception ex)
        {
            // 回線が無い環境では起動のたびに失敗する。利用者に対処のしようが
            // 無いため、警告ログに留めて知らせない。
            _logger.LogWarning(ex, "更新の確認に失敗した");
            return new UpdateCheckResult(UpdateCheckOutcome.Unreachable);
        }

        var newest = NewestIn(catalog.Releases, channel);
        lock (_gate)
        {
            _checkedChannel = channel;
            _complete = catalog.Complete;

            if (newest is null || newest.Version <= running)
            {
                _available = null;

                // 候補を集めきれていないなら、最新だとは言い切れない。
                // 押し出された範囲に、版番号がより大きい安定版が残っている可能性がある。
                // 確かめていないことを「最新である」と伝えるわけにはいかない。
                if (!catalog.Complete) return new UpdateCheckResult(UpdateCheckOutcome.Incomplete);

                return new UpdateCheckResult(UpdateCheckOutcome.UpToDate);
            }

            _available = newest;
        }

        _logger.LogInformation("新しい版が出ている: {Tag} (実行中は {Current})", newest.Tag, currentVersion);
        return new UpdateCheckResult(UpdateCheckOutcome.Available, newest);
    }

    /// <summary>
    /// 応答の並び順に頼らず、対象のうち最も新しいものを選ぶ。
    /// <para>
    /// stable の絞り込みは <see cref="ReleaseInfo.IsInChannel"/> に寄せてあり、
    /// 取得の側とここの両方が同じ判定を通る。
    /// </para>
    /// </summary>
    private static ReleaseInfo? NewestIn(IReadOnlyList<ReleaseInfo> releases, UpdateChannel channel)
    {
        ReleaseInfo? newest = null;
        foreach (var release in releases)
        {
            if (!release.IsInChannel(channel)) continue;
            if (newest is null || release.Version > newest.Version) newest = release;
        }
        return newest;
    }
}
