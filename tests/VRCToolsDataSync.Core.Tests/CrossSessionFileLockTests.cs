using VRCToolsDataSync.Core.Infra;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// 錠前ファイルによる排他を固定する (issue #81)。
/// <para>
/// 対話セッションをまたいだ確認には環境が要るので、ここで見るのは
/// 「同時に 2 つ取れないこと」「返せば取れること」「取れないときに戻ってくること」
/// の 3 つである。セッションをまたいでも効くことは、名前空間の話が出てこない
/// 仕組みそのものから来ている。
/// </para>
/// </summary>
public sealed class CrossSessionFileLockTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "vrctoolsdatasync-tests-" + Guid.NewGuid().ToString("N"));

    public CrossSessionFileLockTests()
    {
        Directory.CreateDirectory(_directory);
    }

    private string LockPath => Path.Combine(_directory, "settings.json.lock");

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort */ }
    }

    [Fact(DisplayName = "取った錠前は、返すまで他から取れない")]
    public void SecondAcquireWaitsWhileTheFirstIsHeld()
    {
        using var held = CrossSessionFileLock.Acquire(LockPath, TimeSpan.FromSeconds(5));
        Assert.True(held.IsHeld);

        using var second = CrossSessionFileLock.Acquire(LockPath, TimeSpan.FromMilliseconds(200));
        Assert.False(second.IsHeld);
    }

    [Fact(DisplayName = "返した錠前は、次の相手が取れる")]
    public void AcquiresAfterTheHolderReleases()
    {
        var first = CrossSessionFileLock.Acquire(LockPath, TimeSpan.FromSeconds(5));
        Assert.True(first.IsHeld);
        first.Dispose();

        using var second = CrossSessionFileLock.Acquire(LockPath, TimeSpan.FromSeconds(5));
        Assert.True(second.IsHeld);
    }

    [Fact(DisplayName = "取れないときは、待ち続けずに戻ってくる")]
    public void GivesUpWithinTheTimeout()
    {
        using var held = CrossSessionFileLock.Acquire(LockPath, TimeSpan.FromSeconds(5));
        Assert.True(held.IsHeld);

        // 待ち続けると呼び出し元がその間ずっと止まる。諦めて戻ることを見る。
        var waited = System.Diagnostics.Stopwatch.StartNew();
        using var second = CrossSessionFileLock.Acquire(LockPath, TimeSpan.FromMilliseconds(300));
        waited.Stop();

        Assert.False(second.IsHeld);
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(5), $"戻るまでに {waited.Elapsed} かかった");
    }

    [Fact(DisplayName = "前回の錠前ファイルが残っていても取れる")]
    public void AcquiresWhenTheLockFileIsLeftBehind()
    {
        // 持ったまま落ちてもハンドルは OS が閉じる。残るのはファイルだけで、
        // それが次の相手を締め出してはいけない。
        File.WriteAllText(LockPath, string.Empty);

        using var lockFile = CrossSessionFileLock.Acquire(LockPath, TimeSpan.FromSeconds(5));
        Assert.True(lockFile.IsHeld);
    }

    [Fact(DisplayName = "別のファイルの錠前とは、互いに待たない")]
    public void LocksForDifferentFilesDoNotWaitOnEachOther()
    {
        using var mine = CrossSessionFileLock.Acquire(LockPath, TimeSpan.FromSeconds(5));
        using var other = CrossSessionFileLock.Acquire(
            Path.Combine(_directory, "elsewhere.json.lock"), TimeSpan.FromSeconds(5));

        Assert.True(mine.IsHeld);
        Assert.True(other.IsHeld);
    }
}
