using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VRCToolsDataSync.Core.Settings;
using VRCToolsDataSync.Core.Sync;
using VRCToolsDataSync.Core.Update;

namespace VRCToolsDataSync_App.Services;

/// <summary>
/// 本体の更新確認を回す常駐側の入り口 (issue #45)。
/// <para>
/// 起動からしばらく後に一度、以後は 1 日ごとに確認する。手動の確認も
/// ここを通す。確認そのもの (<see cref="UpdateChecker"/>) と、通知済みの
/// 記録の永続化、チャンネル設定の読み出しをまとめる。
/// </para>
/// </summary>
public sealed class UpdateManager : IDisposable
{
    // 起動直後は起動同期 (Pull → Launch) が走っている。その帯域と取り合わない。
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);

    // 常駐したままの利用を見込んで 1 日ごとに見直す。
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly SyncRunner _runner;
    private readonly UpdateChecker _checker;
    private readonly GitHubReleaseRepository _repository;
    private readonly UpdateStage _stage;
    private readonly ILogger _logger;
    private readonly Timer _timer;

    // 手動と定期の確認が重ならないように直列化する。
    private readonly SemaphoreSlim _checkGate = new(1, 1);

    // 取得は 1 本ずつ。同じ ZIP へ 2 本が書くと壊れる。
    private readonly SemaphoreSlim _downloadGate = new(1, 1);

    // 取得の打ち切り。取得の本文の読み取りは HttpClient のタイムアウトの外に
    // あるため、応答が止まったまま進まない取得を放置すると _downloadGate を
    // 持ち続け、以後の確認からの取得がすべてスキップされる。
    // 大きくても百数十 MB の ZIP であり、これで足りない回線では待っても仕方がない。
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    // 後始末が取得の終わりを待つ上限。ここで待たされるのは手動の確認と
    // ぶつかったときだけで、待ちきれなくても次の起動が同じ後始末をやり直す。
    private static readonly TimeSpan CleanUpWait = TimeSpan.FromMinutes(5);

    // 破棄と一緒に、走っている取得を打ち切るための元。
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>
    /// 確認が終わるたびに上がる。定期の確認では通知済みの抑止を通した後の結果になる。
    /// 第 2 引数は確認に使ったチャンネル。確認中にチャンネルを変えて保存すると
    /// 前のチャンネルの結果が遅れて届くため、受け側は保存済みのチャンネルと
    /// 突き合わせてから画面や通知に載せる。
    /// ハンドラはバックグラウンドスレッドで呼ばれるので、UI 側でディスパッチする。
    /// </summary>
    public event Action<UpdateCheckResult, UpdateChannel, bool>? CheckCompleted;

    /// <summary>取得が済んで置き換え待ちになった (または捨てられた) ときに上がる。</summary>
    public event Action? StagedChanged;

    public UpdateManager(SyncRunner runner, ILoggerFactory loggerFactory)
    {
        _runner = runner;
        _logger = loggerFactory.CreateLogger<UpdateManager>();
        _repository = new GitHubReleaseRepository(ReleaseAsset.NameForCurrentArchitecture());
        _checker = new UpdateChecker(_repository, loggerFactory.CreateLogger<UpdateChecker>());
        _stage = new UpdateStage(logger: _logger);

        // 知らせ済みの版を復元してから最初の確認を行う。
        // 先に確認すると、前回知らせた版をもう一度知らせてしまう。
        try
        {
            _checker.RestoreNotifiedTag(_runner.LoadSettings().Update.NotifiedVersion);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "通知済みの版を復元できなかった");
        }

        _timer = new Timer(_ => _ = RunScheduledCheckAsync(), null, InitialDelay, CheckInterval);
    }

    /// <summary>実行中の版。画面の表示にも使う。</summary>
    public string CurrentVersion { get; } = RunningVersion.Current();

    /// <summary>今のチャンネルで見つけている新しい版。</summary>
    public ReleaseInfo? Available(UpdateChannel channel) => _checker.Available(channel);

    /// <summary>今のチャンネルで確認が成り立ったか。</summary>
    public bool HasChecked(UpdateChannel channel) => _checker.HasChecked(channel);

    /// <summary>直近の確認で候補を集めきれたか。</summary>
    public bool IsComplete => _checker.IsComplete;

    /// <summary>
    /// 知らせた版として覚え、設定へ書き戻す。画面や通知に出せた後に呼ぶ。
    /// </summary>
    public void MarkNotified(ReleaseInfo release)
    {
        _checker.MarkNotified(release);
        try
        {
            // 記録だけを書く専用の経路を使う。通常の保存だと、常駐している間に
            // 別プロセスが変えた保存先を、こちらが読んだ古い値で巻き戻す。
            _runner.SaveNotifiedVersion(_checker.NotifiedTag);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "通知済みの版を保存できなかった");
        }
    }

    /// <summary>
    /// 新しい版を探す。チャンネルは呼び出しの時点の設定から読む。
    /// <para>
    /// 定期の確認 (manual=false) は設定で止められ、知らせ済みの版を UpToDate へ
    /// 倒してから返す。手動の確認は設定に関わらず走り、抑止前の結末を返す。
    /// 押した人に「最新である」と答えながら画面に新しい版を出すわけにはいかない。
    /// </para>
    /// </summary>
    public async Task<UpdateCheckResult?> CheckAsync(bool manual, CancellationToken cancellationToken = default)
    {
        UpdateChannel channel;
        try
        {
            var update = _runner.LoadSettings().Update;
            if (!manual && !update.CheckEnabled) return null;
            channel = update.Channel;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新確認の設定を読めなかった");
            return null;
        }

        await _checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _checker.CheckAsync(CurrentVersion, channel, cancellationToken).ConfigureAwait(false);
            if (!manual)
            {
                result = _checker.SuppressNotified(result);

                // 確認の最中に「自動で確認」を切られていたら、この結果は流さない。
                // 開始前の判定だけだと、切った後にバルーンが出て、その版が
                // 通知済みとして記録されてしまう。読めない場合も流さない側に倒す。
                //
                // 捨てるときは確認そのものも無かったことにする。確認済みのまま
                // 残すと、自動確認を戻したときに「確認済み」と見なされて確認を
                // 省き、捨てた候補が画面にだけ出て通知されない状態が次の定期確認
                // まで続く。
                bool enabled;
                try
                {
                    enabled = _runner.LoadSettings().Update.CheckEnabled;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "更新確認の設定を読み直せなかった");
                    enabled = false;
                }
                if (!enabled)
                {
                    _checker.InvalidateChecked();
                    return null;
                }
            }

            try
            {
                CheckCompleted?.Invoke(result, channel, manual);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "更新確認の結果の通知に失敗した");
            }

            // 見つけている版に配布物が付いていれば、常駐している間に取っておく
            // (issue #45 第 3 段階)。置き換えは次の起動で行う。
            // 通知を抑止した版も対象にする。抑えたのは繰り返しの通知であって、
            // 取得まで止める理由は無い。
            var available = _checker.Available(channel);
            if (available?.Asset is not null)
            {
                _ = DownloadIfNeededAsync(available, channel);
            }

            return result;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    /// <summary>取得済みで置き換え待ちの記録。照合はせず、表示にだけ使う。</summary>
    public StagedMetadata? Staged => _stage.TryLoadMetadata();

    /// <summary>
    /// 置き換えまわりの後始末を行い、済んだことを画面へ伝える (issue #45 第 3 段階)。
    /// <para>
    /// ウィンドウを立てられた後に呼ぶ。ここを通さずに後始末すると、適用の済んだ
    /// 取得が消えたことが画面へ伝わらず、「次回起動時に適用されます」の行が
    /// 押すまで残る。
    /// </para>
    /// </summary>
    public void CleanUpAfterSuccessfulStart(ILoggerFactory loggerFactory)
    {
        // 後始末は書きかけ (incoming.zip) も消す。取得と重なると、取り終えて
        // 昇格を待っているものを消してしまい、その確認の取得が空振りに終わる。
        // 取得と同じ入り口で直列化する。取得と同じ順 (取得 → 適用ロック) で
        // 取るので、詰まることはない。
        //
        // 最初の確認は起動から 30 秒後なので、普段この待ちは空振りする。
        // 手動の確認とぶつかった場合も、待ちきれなければ次の起動へ回す。
        var held = false;
        try
        {
            held = _downloadGate.Wait(CleanUpWait);
            if (!held)
            {
                _logger.LogInformation("取得中のため、更新の後始末は次の起動へ回す");
                return;
            }

            UpdateApplier.CleanUpAfterSuccessfulStart(loggerFactory, _stage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新の後始末に入れなかった");
            return;
        }
        finally
        {
            if (held) _downloadGate.Release();
        }

        try
        {
            StagedChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "後始末の通知に失敗した");
        }
    }

    /// <summary>
    /// まだ取っていない版なら取得して staged に置く。取得は 1 本に絞り、
    /// 走っている間の呼び出しは黙って戻る (次の確認がまた呼ぶ)。
    /// </summary>
    /// <param name="channel">この候補を見つけた確認のチャンネル。</param>
    private async Task DownloadIfNeededAsync(ReleaseInfo release, UpdateChannel channel)
    {
        if (release.Asset is not { } asset) return;
        if (!await _downloadGate.WaitAsync(0).ConfigureAwait(false))
        {
            // 走っている取得の裏で、別の確認が新しい候補を見つけた。ここで
            // 落とすと、その候補は次の確認 (定期なら 24 時間後) まで取りに
            // 行かれない。チャンネルを切り替えた直後がこれに当たる。覚えて
            // おいて、走っている取得が終わったらやり直す。
            _pending = new PendingDownload(release, channel);
            return;
        }

        try
        {
            // 確認の最中にチャンネルを変えられていないか見る。確認は数秒かかり、
            // その間の変更は珍しくない。古いチャンネルの候補を取りに行くと、
            // 取得は 1 本ずつなので、後から来た今のチャンネルの取得が省かれる。
            if (!MatchesSavedChannel(channel)) return;
            if (!release.IsInChannel(channel)) return;

            // タグだけでなく digest と大きさも見る。同じタグへ配布物を上げ直す
            // 運用があり (release.yml の --clobber)、タグだけで済ませると
            // 差し替え前のものを適用し続ける。stable の印も見る。プレリリース
            // として取った後で印だけ外された場合、記録が prerelease のままだと
            // stable のチャンネルで捨てられ、適用できないまま留まる。
            var staged = _stage.TryLoadMetadata();
            if (staged is not null
                && string.Equals(staged.Tag, release.Tag, StringComparison.Ordinal)
                && string.Equals(staged.DigestHex, asset.DigestHex, StringComparison.Ordinal)
                && staged.Size == asset.Size
                && staged.Stable == release.IsStable)
            {
                return;
            }

            _logger.LogInformation("更新 {Tag} の取得を始める ({Size} バイト)", release.Tag, asset.Size);
            using var cutoff = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            cutoff.CancelAfter(DownloadTimeout);

            // 正規の場所ではなく一時の場所へ取る。直接書くと、取得済みの版がある
            // 状態で次の取得が途中で失敗したときに、適用できたはずの前の版まで失う。
            await _repository.DownloadAsync(asset, _stage.IncomingZipPath, cutoff.Token).ConfigureAwait(false);

            // 取得の間にチャンネルを変えられていたら昇格しない。画面へ出しても
            // 適用の直前で捨てられるだけで、「取得済み」の表示が消えるのを
            // 見せることになる。
            if (!MatchesSavedChannel(channel))
            {
                _logger.LogInformation("チャンネルが変わったため、取得した {Tag} は昇格しない", release.Tag);
                _stage.DiscardIncoming();
                return;
            }

            // 昇格は適用と同じロックの下で行う。staged の ZIP を入れ替えて展開先を
            // 消すため、適用の側と重なると、起動前のヘルパを消したり、動いている
            // ヘルパの展開元を欠いたりする。
            if (!UpdateApplier.TryWithApplyLock(_logger, () => { _stage.PromoteIncoming(release, asset); return false; }))
            {
                // 適用が動いている。ロックを取れないまま昇格すると、動いている
                // ヘルパの展開元を欠く。書きかけを片付けて見送り、次の確認で
                // 取得からやり直す。
                _logger.LogInformation("更新の適用中のため、取得した {Tag} の昇格を見送る", release.Tag);
                _stage.DiscardIncoming();
                return;
            }

            _logger.LogInformation("次の起動で置き換える更新を取得した: {Tag}", release.Tag);

            try
            {
                StagedChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "取得完了の通知に失敗した");
            }
        }
        catch (Exception ex)
        {
            // 取得の失敗は次の確認でやり直せる。書きかけは取得の側が消しているが、
            // 打ち切られた場合に備えてここでも片付ける。取得済みの版は残る。
            _logger.LogWarning(ex, "更新の取得に失敗した: {Tag}", release.Tag);
            _stage.DiscardIncoming();
        }
        finally
        {
            _downloadGate.Release();

            // 待たせていた要求があればやり直す。取得の枠は今空いたところなので、
            // 次はここを通れる。要求は 1 つだけ覚える (同じ確認から何度も来ても
            // 最後のものだけ追えばよい)。
            var pending = Interlocked.Exchange(ref _pending, null);
            if (pending is not null)
            {
                _ = DownloadIfNeededAsync(pending.Release, pending.Channel);
            }
        }
    }

    /// <summary>取得が走っている間に来た、次に追うべき候補。</summary>
    private sealed record PendingDownload(ReleaseInfo Release, UpdateChannel Channel);

    private PendingDownload? _pending;

    /// <summary>
    /// 取得済みの更新を照合し直し、更新ヘルパを起動する。true が返ったら
    /// 呼び出し側は App を終了させる (ヘルパがこのプロセスの終了を待っている)。
    /// </summary>
    public bool PrepareApplyAndSpawnUpdater()
    {
        try
        {
            var channel = _runner.LoadSettings().Update.Channel;
            var spawned = false;
            var stagedMissing = false;

            // 照合から展開・ヘルパ起動までを一つのロック区間にする。照合の後に
            // ロックを取り直すと、その隙に裏の取得が別の ZIP を昇格させ、
            // 確かめたものとは違う版が展開されうる。
            var locked = UpdateApplier.TryWithApplyLock(_logger, () =>
            {
                var staged = _stage.TryLoadVerified(channel, CurrentVersion);
                if (staged is null)
                {
                    stagedMissing = true;
                    return false;
                }

                var root = UpdateInstaller.FindInstallRoot(AppContext.BaseDirectory);
                if (root is null)
                {
                    _logger.LogInformation("配布の形ではないため、取得済みの {Tag} は適用しない", staged.Tag);
                    return false;
                }

                spawned = UpdateApplier.TrySpawnUpdater(_stage, root, staged.Tag, _logger);

                // ヘルパを起こせたらロックは手放さない。ここで手放すと、終了
                // シーケンス (終了時 Push で数分かかりうる) の間に裏の取得が
                // 昇格し、起こしたヘルパの展開元を消しうる。
                return spawned;
            });

            if (!locked)
            {
                // 既に別の適用が動いている。取得しておいたものはそのまま残るので、
                // その適用が済んだ後の起動、または次の操作でやり直せる。
                _logger.LogInformation("更新の適用中のため、いまは適用に入れない");
                return false;
            }

            if (stagedMissing)
            {
                try { StagedChanged?.Invoke(); } catch { /* best-effort */ }
            }
            return spawned;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新の適用に入れなかった");
            return false;
        }
    }

    /// <summary>
    /// 保存されているチャンネルが <paramref name="channel"/> のままか。
    /// 読めない場合は違うものとして扱う。分からないまま取りに行くよりは、
    /// 次の確認へ回すほうが害が少ない。
    /// </summary>
    private bool MatchesSavedChannel(UpdateChannel channel)
    {
        try
        {
            return _runner.LoadSettings().Update.Channel == channel;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新チャンネルを読み直せなかった");
            return false;
        }
    }

    private async Task RunScheduledCheckAsync()
    {
        try
        {
            await CheckAsync(manual: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 定期確認の失敗で常駐を巻き込まない。次の周期にまた試す。
            _logger.LogWarning(ex, "定期の更新確認に失敗した");
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        try { _lifetime.Cancel(); } catch { /* best-effort */ }
        _lifetime.Dispose();
        _checkGate.Dispose();
        _downloadGate.Dispose();
        _repository.Dispose();
    }
}
