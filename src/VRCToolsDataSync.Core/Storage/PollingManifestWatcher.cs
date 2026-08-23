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

    private string? _lastSignature;
    private bool _started;

    public event Action<SyncManifest>? ManifestChanged;

    public PollingManifestWatcher(ISyncStorage storage, TimeSpan interval)
    {
        _storage = storage;
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
                var signature = BuildSignature(snapshot);
                if (signature != _lastSignature)
                {
                    _lastSignature = signature;
                    ManifestChanged?.Invoke(snapshot.Manifest);
                }
            }
        }
        catch
        {
            // 一時的な通信失敗は次の間隔で拾い直す。
        }
        finally
        {
            if (_started)
            {
                try { _timer.Start(); } catch (ObjectDisposedException) { /* Dispose と競合 */ }
            }
        }
    }

    /// <summary>
    /// 変更検知に使う指紋。ETag を出す同期先はそれを使い、出さない同期先では
    /// ツールごとの version の組で代用する。
    /// </summary>
    private static string BuildSignature(ManifestSnapshot snapshot)
    {
        if (snapshot.VersionTag is { Length: > 0 } tag)
        {
            return "tag:" + tag;
        }
        var versions = snapshot.Manifest.Tools
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value.Version}");
        return "versions:" + string.Join(",", versions);
    }

    public void Dispose()
    {
        _started = false;
        _timer.Stop();
        _timer.Dispose();
    }
}
