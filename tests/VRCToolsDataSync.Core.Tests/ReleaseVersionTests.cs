using VRCToolsDataSync.Core.Domain;
using VRCToolsDataSync.Core.Update;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// タグの版の読み取りと順序を固定する (issue #45)。
/// 更新確認の全体がこの比較の上に乗るため、綴りの受け入れ範囲と
/// プレリリースの順序をここで固定しておく。
/// </summary>
public sealed class ReleaseVersionTests
{
    [Theory(DisplayName = "運用しているタグの綴りを読める")]
    [InlineData("0.0.9", 0, 0, 9, null)]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("0.0.10-test1", 0, 0, 10, 1)]
    [InlineData("0.0.10-test09", 0, 0, 10, 9)]
    [InlineData("v1.0.0", 1, 0, 0, null)]
    [InlineData(" 1.0.0 ", 1, 0, 0, null)]
    public void ParsesOperatedTagFormats(string text, int major, int minor, int patch, int? test)
    {
        var version = ReleaseVersion.Parse(text);

        Assert.NotNull(version);
        Assert.Equal(major, version!.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(test, version.Test);
    }

    [Theory(DisplayName = "運用外の綴りは版として扱わない")]
    [InlineData("0.0.0-dev")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1.0.0-rc1")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    // 桁あふれは読み込みの経路ごと落とさず、読めない値として扱う。
    [InlineData("99999999999.0.0")]
    public void RejectsUnknownFormats(string? text)
        => Assert.Null(ReleaseVersion.Parse(text));

    [Fact(DisplayName = "同じ X.Y.Z では安定版がプレリリースより新しい")]
    public void StableIsNewerThanPrereleaseOfSamePatch()
    {
        var stable = ReleaseVersion.Parse("0.0.2")!;
        var test1 = ReleaseVersion.Parse("0.0.2-test1")!;
        var previous = ReleaseVersion.Parse("0.0.1")!;

        // 0.0.2-test1 は 0.0.2 より古く、0.0.1 より新しい。
        Assert.True(test1 < stable);
        Assert.True(previous < test1);
    }

    [Fact(DisplayName = "プレリリース番号は数値として比べる")]
    public void PrereleaseNumbersCompareNumerically()
    {
        var test2 = ReleaseVersion.Parse("0.0.2-test2")!;
        var test10 = ReleaseVersion.Parse("0.0.2-test10")!;

        // 文字列比較だと "test10" < "test2" になってしまう。
        Assert.True(test2 < test10);
    }

    [Fact(DisplayName = "表示はタグの綴りへ戻る")]
    public void RoundTripsToString()
    {
        Assert.Equal("1.2.3", ReleaseVersion.Parse("1.2.3")!.ToString());
        Assert.Equal("0.0.10-test3", ReleaseVersion.Parse("0.0.10-test3")!.ToString());
        // v 付きで読んだものも、記録には v 無しで残す。
        Assert.Equal("1.0.0", ReleaseVersion.Parse("v1.0.0")!.ToString());
    }

    [Fact(DisplayName = "動いていない一式の版も読める")]
    public void ReadsVersionFromFile()
    {
        // 展開した更新が、記録のタグどおりの版かを適用の前に確かめるために使う。
        var self = typeof(ReleaseVersionTests).Assembly.Location;
        Assert.False(string.IsNullOrEmpty(RunningVersion.OfFile(self)));

        // コミット ID は落とす。版の比較には使わない。
        Assert.DoesNotContain("+", RunningVersion.OfFile(self)!);

        // 読めないものは分からないものとして扱う。捨てる判断はここではしない。
        Assert.Null(RunningVersion.OfFile(Path.Combine(Path.GetTempPath(), "no-such-file-" + Guid.NewGuid().ToString("N"))));
    }
}
