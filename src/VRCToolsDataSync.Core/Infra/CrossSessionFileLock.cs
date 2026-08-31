namespace VRCToolsDataSync.Core.Infra;

/// <summary>
/// 守りたいファイルの隣に置いた錠前ファイルを共有無しで開いて、対話セッションを
/// またいだ排他にする (issue #81)。
/// <para>
/// 名前付きの <see cref="Mutex"/> と違って、名前空間の話が出てこない。ファイル
/// ハンドルの共有の指定は計算機の中で一意に効くので、どの対話セッションから
/// 開いても同じ 1 つを取り合う。<c>Global\</c> を作る権限も要らない。
/// </para>
/// <para>
/// 持ったまま落ちても、次の相手が待たされ続けることはない。ハンドルはプロセスの
/// 終了で OS が閉じる。錠前ファイル自体は残るが、中身は使っていないので害が無い。
/// </para>
/// </summary>
internal sealed class CrossSessionFileLock : IDisposable
{
    // 待ち直す間隔。最初は細かく、取れないうちは粗くする。普通の保存は数十 ms で
    // 終わるので、多くの場合は 1 回目か 2 回目で取れる。
    private static readonly TimeSpan FirstRetry = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan LongestRetry = TimeSpan.FromMilliseconds(50);

    private readonly FileStream? _stream;

    private CrossSessionFileLock(FileStream? stream)
    {
        _stream = stream;
    }

    /// <summary>取れたかどうか。取れなくても呼び出し元は進んでよい (best-effort)。</summary>
    public bool IsHeld => _stream is not null;

    /// <summary>
    /// 錠前を取る。<paramref name="timeout"/> の間に取れなければ諦めて、
    /// 取れなかったことを <see cref="IsHeld"/> で返す。
    /// <para>
    /// 諦めるのは、待ち続けると呼び出し元がその間ずっと止まるためである。普通の
    /// 保存は数十 ms で終わるので、これだけ待って取れない相手はハングに近い。
    /// </para>
    /// </summary>
    public static CrossSessionFileLock Acquire(string path, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        var retry = FirstRetry;

        while (true)
        {
            try
            {
                return new CrossSessionFileLock(new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None));
            }
            catch (IOException)
            {
                // 誰かが持っている。あるいは、ウイルス対策などが一時的に掴んでいる。
                // どちらも待てば空くので、待ち直す。
            }
            catch (UnauthorizedAccessException)
            {
                // 権限や属性で開けない。待っても変わらないので、すぐ諦める。
                return new CrossSessionFileLock(null);
            }

            if (Environment.TickCount64 >= deadline) return new CrossSessionFileLock(null);

            Thread.Sleep(retry);
            retry = retry + retry > LongestRetry ? LongestRetry : retry + retry;
        }
    }

    public void Dispose() => _stream?.Dispose();
}
