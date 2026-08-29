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
                _ = DownloadIfNeededAsync(available);
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
        UpdateApplier.CleanUpAfterSuccessfulStart(loggerFactory, _stage);
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
    private async Task DownloadIfNeededAsync(ReleaseInfo release)
    {
        if (release.Asset is not { } asset) return;
        if (!await _downloadGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            // タグだけでなく digest と大きさも見る。同じタグへ配布物を上げ直す
            // 運用があり (release.yml の --clobber)、タグだけで済ませると
            // 差し替え前のものを適用し続ける。
            var staged = _stage.TryLoadMetadata();
            if (staged is not null
                && string.Equals(staged.Tag, release.Tag, StringComparison.Ordinal)
                && string.Equals(staged.DigestHex, asset.DigestHex, StringComparison.Ordinal)
                && staged.Size == asset.Size)
            {
                return;
            }

            _logger.LogInformation("更新 {Tag} の取得を始める ({Size} バイト)", release.Tag, asset.Size);
            using var cutoff = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            cutoff.CancelAfter(DownloadTimeout);

            // 正規の場所ではなく一時の場所へ取る。直接書くと、取得済みの版がある
            // 状態で次の取得が途中で失敗したときに、適用できたはずの前の版まで失う。
            await _repository.DownloadAsync(asset, _stage.IncomingZipPath, cutoff.Token).ConfigureAwait(false);
            _stage.PromoteIncoming(release, asset);
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
        }
    }

    /// <summary>
    /// 取得済みの更新を照合し直し、更新ヘルパを起動する。true が返ったら
    /// 呼び出し側は App を終了させる (ヘルパがこのプロセスの終了を待っている)。
    /// </summary>
    public bool PrepareApplyAndSpawnUpdater()
    {
        try
        {
            var channel = _runner.LoadSettings().Update.Channel;
            var staged = _stage.TryLoadVerified(channel, CurrentVersion);
            if (staged is null)
            {
                try { StagedChanged?.Invoke(); } catch { /* best-effort */ }
                return false;
            }

            var root = UpdateInstaller.FindInstallRoot(AppContext.BaseDirectory);
            if (root is null)
            {
                _logger.LogInformation("配布の形ではないため、取得済みの {Tag} は適用しない", staged.Tag);
                return false;
            }

            return UpdateApplier.TrySpawnUpdater(_stage, root, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新の適用に入れなかった");
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
