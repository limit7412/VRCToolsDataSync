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

    /// <summary>
    /// 確認が終わるたびに上がる。定期の確認では通知済みの抑止を通した後の結果になる。
    /// ハンドラはバックグラウンドスレッドで呼ばれるので、UI 側でディスパッチする。
    /// </summary>
    public event Action<UpdateCheckResult, bool>? CheckCompleted;

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
            var settings = _runner.LoadSettings();
            settings.Update.NotifiedVersion = _checker.NotifiedTag;
            _runner.SaveSettings(settings);
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
            if (!manual) result = _checker.SuppressNotified(result);

            try
            {
                CheckCompleted?.Invoke(result, manual);
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
    /// まだ取っていない版なら取得して staged に置く。取得は 1 本に絞り、
    /// 走っている間の呼び出しは黙って戻る (次の確認がまた呼ぶ)。
    /// </summary>
    private async Task DownloadIfNeededAsync(ReleaseInfo release)
    {
        if (release.Asset is not { } asset) return;
        if (!await _downloadGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var staged = _stage.TryLoadMetadata();
            if (staged is not null && string.Equals(staged.Tag, release.Tag, StringComparison.Ordinal)) return;

            _logger.LogInformation("更新 {Tag} の取得を始める ({Size} バイト)", release.Tag, asset.Size);
            await _repository.DownloadAsync(asset, _stage.ZipPath).ConfigureAwait(false);
            _stage.SaveMetadata(release, asset);
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
            // 取得の失敗は次の確認でやり直せる。書きかけは取得の側が消している。
            _logger.LogWarning(ex, "更新の取得に失敗した: {Tag}", release.Tag);
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
        _checkGate.Dispose();
        _downloadGate.Dispose();
        _repository.Dispose();
    }
}
