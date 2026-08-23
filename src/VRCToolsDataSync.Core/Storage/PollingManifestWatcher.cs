using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCToolsDataSync.Core.Sync;

namespace VRCToolsDataSync.Core.Storage;

/// <summary>
/// 一定間隔で manifest を読み直し、内容が変わっていれば通知する。
/// オブジェクトストレージにはファイル監視に相当する仕組みが無いため、
/// ローカルフォルダの <see cref="Watch.CloudWatcher"/> の代わりに使う。
/// <para>
/// 問い合わせは manifest 1 つに対する読み取りだけなので、60 秒間隔でも
/// 1 か月あたり数万回に収まり、主要なプロバイダの無料枠を大きく下回る。
/// </para>
/// </summary>
public sealed class PollingManifestWatcher : IManifestWatcher
{
    private readonly ISyncStorage _storage;
    private readonly System.Timers.Timer _timer;
    private readonly object _pollLock = new();
    private readonly ILogger _logger;

    private string? _lastSignature;
    private bool _started;

    // 失敗が続く間に毎回記録するとログが失敗で埋まる。状態が変わったときだけ残す。
    private bool _failureLogged;

    public event Action<SyncManifest>? ManifestChanged;

    public PollingManifestWatcher(
        ISyncStorage storage,
        TimeSpan interval,
        ILogger<PollingManifestWatcher>? logger = null)
    {
        _storage = storage;
        _logger = logger ?? NullLogger<PollingManifestWatcher>.Instance;
        _timer = new System.Timers.Timer(interval.TotalMilliseconds)
        {
            // 前回の問い合わせが終わってから次を張り直す。通信が遅いときに
            // 問い合わせが積み上がらないようにする。
            AutoReset = false,
        };
        _timer.Elapsed += (_, _) => Poll();
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        // ここで manifest を読みに行かない。呼び出し元 (AutoSyncCoordinator.Start)
        // はライフサイクルのロックを保持しており、通信が詰まると GUI の
        // 「設定を保存」まで巻き込んで固まる。
        _timer.Start();
    }

    /// <summary>
    /// manifest を読み直し、前回と内容が変わっていれば通知する。
    /// <para>
    /// 監視開始後の最初の問い合わせでも通知する。ここで「最初の内容を基準にする」
    /// 扱いにすると、監視を始めてから最初の問い合わせまでの間 (最大で間隔ぶん) に
    /// 他 PC が Push した分を取りこぼし、次に manifest が変わるまで気付けなくなる。
    /// 通知先の <see cref="Watch.AutoSyncCoordinator"/> は、ローカルの
    /// LastPulledVersion より新しく、かつ自分以外のマシンが書いたエントリだけを
    /// 拾うため、余分に通知しても実害は無い。
    /// </para>
    /// </summary>
    private void Poll()
    {
        try
        {
            lock (_pollLock)
            {
                var snapshot = _storage.LoadManifest();
                if (_failureLogged)
                {
                    _failureLogged = false;
                    _logger.LogInformation("manifest の確認が復帰しました: {Target}", _storage.DisplayName);
                }
                var signature = ManifestSignature.Build(snapshot);
                if (signature != _lastSignature)
                {
                    _lastSignature = signature;
                    ManifestChanged?.Invoke(snapshot.Manifest);
                }
            }
        }
        catch (Exception ex)
        {
            // 一時的な通信失敗は次の間隔で拾い直せるので、ここでは再試行しない。
            // ただし黙って捨てると、到達できない状態が続いても利用者は
            // 「他 PC の更新が来ない」ことしか分からない。状態が変わったときだけ記録する。
            if (!_failureLogged)
            {
                _failureLogged = true;
                _logger.LogWarning(
                    ex,
                    "manifest を確認できませんでした。次の確認まで ({Interval} 秒) リモートの更新に気付けません: {Target}",
                    _timer.Interval / 1000,
                    _storage.DisplayName);
            }
        }
        finally
        {
            if (_started)
            {
                try { _timer.Start(); } catch (ObjectDisposedException) { /* Dispose と競合 */ }
            }
        }
    }

    public void Dispose()
    {
        _started = false;
        _timer.Stop();
        _timer.Dispose();
    }
}
