using VRCToolsDataSync.Core.Domain;
using VRCToolsDataSync.Core.Update;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// 更新確認の判断を固定する (issue #45)。
/// リリースの取得は偽物に差し替え、結末の 5 値と通知の抑止、
/// チャンネルの絞り込みを確かめる。
/// </summary>
public sealed class UpdateCheckerTests
{
    private sealed class FakeReleaseRepository : IReleaseRepository
    {
        public List<ReleaseInfo> Releases { get; } = new();
        public bool Complete { get; set; } = true;
        public Exception? Failure { get; set; }

        public Task<ReleaseCatalog> FetchReleasesAsync(CancellationToken cancellationToken = default)
        {
            if (Failure is not null) throw Failure;
            return Task.FromResult(new ReleaseCatalog(Releases.ToList(), Complete));
        }

        // 確認の判断だけを見るテストであり、取得へは到達しない。
        public Task DownloadAsync(ReleaseAsset asset, string path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private static ReleaseInfo Release(string tag, bool prerelease = false)
        => new(ReleaseVersion.Parse(tag)!, tag, $"https://example.com/{tag}", prerelease, null);

    [Fact(DisplayName = "新しい安定版があれば Available になる")]
    public async Task FindsNewerStableRelease()
    {
        var repository = new FakeReleaseRepository();
        repository.Releases.AddRange(new[] { Release("0.0.9"), Release("0.0.10") });
        var checker = new UpdateChecker(repository);

        var result = await checker.CheckAsync("0.0.9", UpdateChannel.Stable);

        Assert.Equal(UpdateCheckOutcome.Available, result.Outcome);
        Assert.Equal("0.0.10", result.Release!.Tag);
        Assert.Equal("0.0.10", checker.Available(UpdateChannel.Stable)!.Tag);
    }

    [Fact(DisplayName = "実行中が最新なら UpToDate になる")]
    public async Task ReportsUpToDate()
    {
        var repository = new FakeReleaseRepository();
        repository.Releases.Add(Release("0.0.9"));
        var checker = new UpdateChecker(repository);

        var result = await checker.CheckAsync("0.0.9", UpdateChannel.Stable);

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
        Assert.Null(checker.Available(UpdateChannel.Stable));
    }

    [Fact(DisplayName = "手元ビルドの版では確認そのものを行わない")]
    public async Task SkipsCheckForLocalBuilds()
    {
        var repository = new FakeReleaseRepository();
        repository.Releases.Add(Release("9.9.9"));
        var checker = new UpdateChecker(repository);

        var result = await checker.CheckAsync("0.0.0-dev", UpdateChannel.Stable);

        Assert.Equal(UpdateCheckOutcome.Unknown, result.Outcome);
        Assert.False(checker.HasChecked(UpdateChannel.Stable));
    }

    [Fact(DisplayName = "一覧を取れなければ Unreachable になり、例外は漏れない")]
    public async Task ReportsUnreachableOnFetchFailure()
    {
        var repository = new FakeReleaseRepository { Failure = new InvalidOperationException("boom") };
        var checker = new UpdateChecker(repository);

        var result = await checker.CheckAsync("0.0.9", UpdateChannel.Stable);

        Assert.Equal(UpdateCheckOutcome.Unreachable, result.Outcome);
        // 失敗した確認は「確認できた」ことにしない。
        Assert.False(checker.HasChecked(UpdateChannel.Stable));
    }

    [Fact(DisplayName = "HTTP のタイムアウトも Unreachable として扱う")]
    public async Task TreatsHttpTimeoutAsUnreachable()
    {
        // HttpClient のタイムアウトは OperationCanceledException で届くが、
        // 呼び出し側は中止していない。例外として漏らさず、届かなかったと伝える。
        var repository = new FakeReleaseRepository { Failure = new TaskCanceledException("timeout") };
        var checker = new UpdateChecker(repository);

        var result = await checker.CheckAsync("0.0.9", UpdateChannel.Stable);

        Assert.Equal(UpdateCheckOutcome.Unreachable, result.Outcome);
    }

    [Fact(DisplayName = "呼び出し側の中止はそのまま伝える")]
    public async Task PropagatesCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var repository = new FakeReleaseRepository { Failure = new OperationCanceledException(cts.Token) };
        var checker = new UpdateChecker(repository);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => checker.CheckAsync("0.0.9", UpdateChannel.Stable, cts.Token));
    }

    [Fact(DisplayName = "集めきれなかった一覧では最新だと言い切らない")]
    public async Task ReportsIncompleteWhenCatalogIsTruncated()
    {
        var repository = new FakeReleaseRepository { Complete = false };
        repository.Releases.Add(Release("0.0.9"));
        var checker = new UpdateChecker(repository);

        var result = await checker.CheckAsync("0.0.9", UpdateChannel.Stable);

        Assert.Equal(UpdateCheckOutcome.Incomplete, result.Outcome);
        Assert.False(checker.IsComplete);
    }

    [Fact(DisplayName = "stable チャンネルはプレリリースを拾わない")]
    public async Task StableChannelIgnoresPrereleases()
    {
        var repository = new FakeReleaseRepository();
        repository.Releases.AddRange(new[]
        {
            Release("0.0.9"),
            Release("0.0.10-test1", prerelease: true),
            // タグは安定版でも、プレリリースの印が付いたものは拾わない。
            Release("0.0.11", prerelease: true),
        });
        var checker = new UpdateChecker(repository);

        var result = await checker.CheckAsync("0.0.9", UpdateChannel.Stable);

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
    }

    [Fact(DisplayName = "test チャンネルはプレリリースも拾い、最も新しい版を選ぶ")]
    public async Task TestChannelPicksNewestIncludingPrereleases()
    {
        var repository = new FakeReleaseRepository();
        repository.Releases.AddRange(new[]
        {
            Release("0.0.10-test2", prerelease: true),
            // 作成順に頼らないことを見るため、新しい版を先頭に置かない。
            Release("0.0.10-test10", prerelease: true),
            Release("0.0.9"),
        });
        var checker = new UpdateChecker(repository);

        var result = await checker.CheckAsync("0.0.9", UpdateChannel.Test);

        Assert.Equal(UpdateCheckOutcome.Available, result.Outcome);
        Assert.Equal("0.0.10-test10", result.Release!.Tag);
    }

    [Fact(DisplayName = "別のチャンネルで確認した結果は返さない")]
    public async Task AvailableIsScopedToCheckedChannel()
    {
        var repository = new FakeReleaseRepository();
        repository.Releases.AddRange(new[] { Release("0.0.9"), Release("0.0.10-test1", prerelease: true) });
        var checker = new UpdateChecker(repository);

        await checker.CheckAsync("0.0.9", UpdateChannel.Test);

        Assert.NotNull(checker.Available(UpdateChannel.Test));
        // test で見つけたプレリリースを、stable の表示に残さない。
        Assert.Null(checker.Available(UpdateChannel.Stable));
        Assert.False(checker.HasChecked(UpdateChannel.Stable));
    }

    [Fact(DisplayName = "知らせ済みの版は UpToDate へ倒す")]
    public async Task SuppressesAlreadyNotifiedRelease()
    {
        var repository = new FakeReleaseRepository();
        repository.Releases.AddRange(new[] { Release("0.0.9"), Release("0.0.10") });
        var checker = new UpdateChecker(repository);

        var first = await checker.CheckAsync("0.0.9", UpdateChannel.Stable);
        Assert.Equal(UpdateCheckOutcome.Available, checker.SuppressNotified(first).Outcome);

        checker.MarkNotified(first.Release!);
        var second = await checker.CheckAsync("0.0.9", UpdateChannel.Stable);
        var suppressed = checker.SuppressNotified(second);

        Assert.Equal(UpdateCheckOutcome.UpToDate, suppressed.Outcome);
        // 抑えるのは通知だけで、見つけている事実 (Release) は添えて返す。
        Assert.Equal("0.0.10", suppressed.Release!.Tag);
    }

    [Fact(DisplayName = "チャンネルを往復しても、伝えた版より古いものを知らせ直さない")]
    public async Task ChannelRoundTripDoesNotRenotifyOlderReleases()
    {
        var repository = new FakeReleaseRepository();
        repository.Releases.AddRange(new[]
        {
            Release("2.0.0"),
            Release("2.1.0-test1", prerelease: true),
        });
        var checker = new UpdateChecker(repository);

        // stable で 2.0.0 を知らせ、test で 2.1.0-test1 を知らせてから stable へ戻す。
        var stable = await checker.CheckAsync("1.0.0", UpdateChannel.Stable);
        checker.MarkNotified(stable.Release!);
        var test = await checker.CheckAsync("1.0.0", UpdateChannel.Test);
        checker.MarkNotified(test.Release!);

        var back = await checker.CheckAsync("1.0.0", UpdateChannel.Stable);

        // 記録は 2.1.0-test1 だが、等値ではなく「それ以上に新しいものは伝えて
        // ある」と読むので、2.0.0 を知らせ直さない。
        Assert.Equal(UpdateCheckOutcome.UpToDate, checker.SuppressNotified(back).Outcome);
    }

    [Fact(DisplayName = "通知の記録は、より古い版で上書きしない")]
    public void MarkNotifiedNeverMovesBackwards()
    {
        var checker = new UpdateChecker(new FakeReleaseRepository());
        checker.MarkNotified(Release("2.1.0-test1", prerelease: true));

        checker.MarkNotified(Release("2.0.0"));

        Assert.Equal("2.1.0-test1", checker.NotifiedTag);
    }

    [Fact(DisplayName = "通知の記録は設定の文字列と往復できる")]
    public void NotifiedTagRoundTripsThroughSettingsString()
    {
        var checker = new UpdateChecker(new FakeReleaseRepository());

        checker.RestoreNotifiedTag("0.0.10-test2");
        Assert.Equal("0.0.10-test2", checker.NotifiedTag);

        // 読めない値は記録が無いものとして扱う。
        checker.RestoreNotifiedTag("garbage");
        Assert.Equal(string.Empty, checker.NotifiedTag);
    }
}
