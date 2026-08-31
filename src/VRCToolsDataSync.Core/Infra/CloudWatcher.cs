using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCToolsDataSync.Core.Domain;

namespace VRCToolsDataSync.Core.Infra;

/// <summary>
/// ローカル同期フォルダの manifest.json をファイル監視で追いかける。
/// OneDrive などのクライアントが他 PC の更新を書き戻したタイミングで発火する。
/// </summary>
public sealed class CloudWatcher : IManifestWatcher
{
    /// <summary>
    /// manifest の読み込みに失敗したときの待ち時間。
    /// <para>
    /// 通知はファイルイベントを起点にしているので、読めなかった回を捨てると
    /// 次にイベントが起きるまで「リモートに更新がある」ことに気付けない。
    /// OneDrive が manifest.json を置き換える瞬間と重なると読み取りは短時間だけ
    /// 失敗するため、その場で待って読み直す。
    /// </para>
    /// </summary>
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
    ];

    private readonly LocalFolderSyncStorage _storage;
    private readonly System.Timers.Timer _debounceTimer;
    private readonly ILogger _logger;

    // 再試行の待機中に Dispose された場合、待つのをやめて抜ける。
    private readonly ManualResetEventSlim _disposed = new(false);

    // 読み込みは同時に 1 つだけ走らせる。再試行の待機中に次のファイルイベントが
    // 来るとタイマーが張り直され、別の Elapsed が重なりうるため。
    private readonly object _gate = new();
    private bool _running;
    private bool _rerunRequested;

    // 直前に通知した内容。同じ内容で二度通知しないための比較にだけ使う。
    private string? _lastSignature;

    private FileSystemWatcher? _watcher;

    public event Action<SyncManifest>? ManifestChanged;

    public CloudWatcher(
        LocalFolderSyncStorage storage,
        TimeSpan? debounce = null,
        ILogger<CloudWatcher>? logger = null)
    {
        _storage = storage;
        _logger = logger ?? NullLogger<CloudWatcher>.Instance;
        _debounceTimer = new System.Timers.Timer((debounce ?? TimeSpan.FromSeconds(2)).TotalMilliseconds)
        {
            AutoReset = false,
        };
        _debounceTimer.Elapsed += (_, _) => EmitManifestChanged();
    }

    public void Start()
    {
        if (_watcher is not null) return;
        if (!Directory.Exists(_storage.RootDirectory)) return;

        _watcher = new FileSystemWatcher(_storage.RootDirectory, ManifestKeys.Manifest)
        {
            NotifyFilter = NotifyFilters.LastWrite
                         | NotifyFilters.FileName
                         | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    /// <summary>
    /// manifest の読み込みを 1 つだけ走らせる。
    /// <para>
    /// 再試行で待っている間も、ファイルイベントが来れば debounce タイマーは張り直され、
    /// 別の <c>Elapsed</c> がここへ入ってくる。素通しすると同じ更新に対して
    /// <see cref="ManifestChanged"/> が二重に上がり、GUI ではトーストと Pull の
    /// 問い合わせが重複する。
    /// </para>
    /// <para>
    /// 走っている最中に来た分は「もう一周する」ことだけ伝えて戻る。取りこぼしを
    /// 防ぐために捨てはせず、何回来ても後続の 1 周にまとめる。
    /// </para>
    /// </summary>
    private void EmitManifestChanged()
    {
        lock (_gate)
        {
            if (_running)
            {
                _rerunRequested = true;
                return;
            }
            _running = true;
            _rerunRequested = false;
        }

        try
        {
            while (true)
            {
                ReadAndNotify();

                // 「もう一周するか」の判断と実行権の解放は同じロックの中で行う。
                // 分けると、抜けると決めてから _running を下ろすまでの間に入ってきた
                // 呼び出しが「走っている」と見て要求だけ置いて帰り、その要求を誰も
                // 拾わないまま終わる。次のファイルイベントまで通知が失われる。
                lock (_gate)
                {
                    if (_disposed.IsSet || !_rerunRequested)
                    {
                        _running = false;
                        return;
                    }
                    _rerunRequested = false;
                }
            }
        }
        catch
        {
            // 想定外の例外で実行権を握ったままにすると、以後の通知が一切出なくなる。
            lock (_gate) { _running = false; }
            throw;
        }
    }

    /// <summary>
    /// manifest を読んで通知する。読めなかった場合は <see cref="RetryDelays"/> の
    /// 間隔で読み直し、それでも駄目なら警告を残す。
    /// </summary>
    private void ReadAndNotify()
    {
        Exception? lastError = null;

        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            // 待っている間に Dispose されたら、通知先はもう居ないので抜ける。
            if (attempt > 0 && _disposed.Wait(RetryDelays[attempt - 1])) return;

            try
            {
                var snapshot = _storage.LoadManifest();
                if (snapshot.Manifest.Tools.Count == 0) return;
                if (attempt > 0)
                {
                    _logger.LogInformation(
                        "manifest を読み直して取得しました ({Attempt} 回目)", attempt + 1);
                }

                // 内容が前回の通知から変わっていなければ黙って戻る。
                // ファイルイベントは 1 回の更新で複数回上がることがあり、
                // 中身が同じなら通知すべき新しい更新は無い。
                var signature = ManifestSignature.Build(snapshot);
                if (signature == _lastSignature) return;
                _lastSignature = signature;

                ManifestChanged?.Invoke(snapshot.Manifest);
                return;
            }
            catch (Exception ex)
            {
                // 書き込み途中で読めなかった場合を狙って読み直す。
                lastError = ex;
            }
        }

        // ここまで来ると、次にファイルイベントが起きるまで通知の機会が無い。
        // 黙って捨てると「他 PC で Push したのに気付かない」状態の原因が追えないので、
        // 記録に残す。
        _logger.LogWarning(
            lastError,
            "manifest を読み込めませんでした ({Attempts} 回試行)。" +
            "次にファイルが更新されるまでリモートの更新に気付けません: {Path}",
            RetryDelays.Length + 1,
            _storage.RootDirectory);
    }

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileEvent;
            _watcher.Created -= OnFileEvent;
            _watcher.Renamed -= OnFileEvent;
            _watcher.Dispose();
            _watcher = null;
        }
        // 再試行の待機を先に解いてから、タイマーを畳む。
        _disposed.Set();
        _debounceTimer.Stop();
        _debounceTimer.Dispose();
        // _disposed は破棄しない。待機中のスレッドが Wait を呼んでいる最中に
        // 破棄すると ObjectDisposedException になる。WaitHandle を取り出して
        // いないので、破棄しなくてもハンドルは持たない。
    }
}
