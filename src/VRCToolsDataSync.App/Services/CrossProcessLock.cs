using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace VRCToolsDataSync_App.Services;

/// <summary>
/// 握った <see cref="Mutex"/> と、それを所有するスレッドの組。
/// <para>
/// <see cref="Mutex"/> の所有権はスレッドに紐づき、取ったスレッドからしか
/// 手放せない。ここでは、取得から解放までを専用のスレッドに閉じ込め、
/// 外からは <see cref="Dispose"/> の合図だけで手放せるようにする。
/// そうしないと、取得が <c>Task.Run</c> の中で、解放が UI スレッドから、
/// というような組み合わせで解放が黙って失敗する。
/// </para>
/// <para>
/// スレッドは背景スレッドなので、合図が来ないまま残ってもプロセスの終了を
/// 妨げない。所有権はそのときに OS が手放し、待っている相手へ渡る。
/// </para>
/// <para>
/// 適用のロック (<see cref="UpdateApplier"/>) と取得のロック
/// (<see cref="UpdateManager"/>) の両方がこの形を使う。違うのは掴む名前と、
/// 取れなかったときに何をするかだけである (issue #52)。
/// </para>
/// </summary>
internal sealed class CrossProcessLock : IDisposable
{
    // 取得の合図は Task で渡す。待つ側が長く待つ場合 (取得のロックは
    // 数十分になりうる)、スレッドを塞いだまま待たせたくない。
    private readonly TaskCompletionSource<bool> _acquired =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly ManualResetEventSlim _release = new(false);
    private bool _held;

    private CrossProcessLock() { }

    /// <summary>
    /// 掴む。取れなければ null を返す。待っている間はスレッドを塞ぐ。
    /// </summary>
    /// <param name="createMutex">
    /// 掴む <see cref="Mutex"/> を作る。所有するスレッドの上で呼ばれる。
    /// </param>
    /// <param name="threadName">所有するスレッドの名前。ログと診断のためだけに使う。</param>
    /// <param name="timeout">待つ上限。<see cref="TimeSpan.Zero"/> なら待たない。</param>
    /// <param name="onTimeout">待ちきれなかったときに残す記録。省略できる。</param>
    public static CrossProcessLock? TryAcquire(
        Func<Mutex> createMutex, string threadName, TimeSpan timeout, ILogger logger, Action? onTimeout = null)
        => TryAcquireAsync(createMutex, threadName, timeout, logger, onTimeout).GetAwaiter().GetResult();

    /// <summary>
    /// 掴む。取れなければ null を返す。待っている間もスレッドを塞がない。
    /// </summary>
    /// <param name="createMutex">
    /// 掴む <see cref="Mutex"/> を作る。所有するスレッドの上で呼ばれる。
    /// </param>
    /// <param name="threadName">所有するスレッドの名前。ログと診断のためだけに使う。</param>
    /// <param name="timeout">待つ上限。<see cref="TimeSpan.Zero"/> なら待たない。</param>
    /// <param name="onTimeout">待ちきれなかったときに残す記録。省略できる。</param>
    public static async Task<CrossProcessLock?> TryAcquireAsync(
        Func<Mutex> createMutex, string threadName, TimeSpan timeout, ILogger logger, Action? onTimeout = null)
    {
        var owner = new CrossProcessLock();
        try
        {
            var thread = new Thread(() => owner.Own(createMutex, timeout, logger, onTimeout))
            {
                IsBackground = true,
                Name = threadName,
            };
            thread.Start();
        }
        catch (Exception ex)
        {
            LogQuietly(() => logger.LogWarning(ex, "更新のロックを取れなかった"));
            return null;
        }

        if (await owner._acquired.Task.ConfigureAwait(false)) return owner;

        owner.Dispose();
        return null;
    }

    /// <summary>専用スレッドの中身。取得を伝えた後、解放の合図まで持ち続ける。</summary>
    private void Own(Func<Mutex> createMutex, TimeSpan timeout, ILogger logger, Action? onTimeout)
    {
        Mutex? mutex = null;
        try
        {
            mutex = createMutex();
            try
            {
                _held = mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                // 握ったまま落ちたプロセスがある。所有権はこちらに渡っている。
                _held = true;
            }

            if (!_held) onTimeout?.Invoke();
        }
        catch (Exception ex)
        {
            LogQuietly(() => logger.LogWarning(ex, "更新のロックを取れなかった"));
            _held = false;
        }
        finally
        {
            _acquired.TrySetResult(_held);
        }

        if (!_held)
        {
            mutex?.Dispose();
            return;
        }

        _release.Wait();
        try { mutex!.ReleaseMutex(); } catch { /* best-effort */ }
        mutex!.Dispose();
    }

    /// <summary>手放す合図を送る。どのスレッドから呼んでもよい。</summary>
    public void Dispose() => _release.Set();

    private static void LogQuietly(Action write)
    {
        try { write(); } catch { /* best-effort */ }
    }
}
