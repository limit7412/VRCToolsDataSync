using VRCToolsDataSync.Core.Startup;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// 自動起動に登録するコマンドの組み立てと読み取りを固定する (issue #54)。
/// レジストリには触らない。画面のチェックは登録内容から作り直すため、
/// 書いた形と読んだ結果が食い違わないことがそのまま表示の正しさになる。
/// </summary>
public sealed class StartupRegistrationTests
{
    private const string Path = @"C:\Program Files\VRCToolsDataSync\app\VRCToolsDataSync.App.exe";

    [Fact(DisplayName = "パスは引用符で囲む")]
    public void QuotesTheExecutablePath()
    {
        Assert.Equal($"\"{Path}\"", StartupRegistration.BuildCommand(Path, startMinimized: false));
    }

    [Fact(DisplayName = "既に引用符で囲まれたパスは二重に囲まない")]
    public void KeepsAnAlreadyQuotedPath()
    {
        Assert.Equal($"\"{Path}\"", StartupRegistration.BuildCommand($"\"{Path}\"", startMinimized: false));
    }

    [Fact(DisplayName = "トレイ常駐の指定はパスの後ろに付ける")]
    public void AppendsTheMinimizedSwitch()
    {
        Assert.Equal(
            $"\"{Path}\" {StartupRegistration.MinimizedSwitch}",
            StartupRegistration.BuildCommand(Path, startMinimized: true));
    }

    [Fact(DisplayName = "書いた指定はそのまま読み取れる")]
    public void ReadsBackWhatItWrote()
    {
        Assert.True(StartupRegistration.StartsMinimized(
            StartupRegistration.BuildCommand(Path, startMinimized: true)));
        Assert.False(StartupRegistration.StartsMinimized(
            StartupRegistration.BuildCommand(Path, startMinimized: false)));
    }

    [Fact(DisplayName = "パスに同じ綴りが含まれていても指定と取り違えない")]
    public void DoesNotMistakeThePathForTheSwitch()
    {
        // 引用符の内側は実行ファイルのパスであって、引数ではない。
        var command = $"\"C:\\tools\\{StartupRegistration.MinimizedSwitch}\\App.exe\"";
        Assert.False(StartupRegistration.StartsMinimized(command));
    }

    [Fact(DisplayName = "引用符の無いコマンドでも指定を読み取れる")]
    public void ReadsAnUnquotedCommand()
    {
        // 手でレジストリを書き換えられた場合。表示が食い違わないようにする。
        Assert.True(StartupRegistration.StartsMinimized(
            $"C:\\App.exe {StartupRegistration.MinimizedSwitch}"));
        Assert.False(StartupRegistration.StartsMinimized("C:\\App.exe"));
    }

    [Fact(DisplayName = "大文字小文字は問わない")]
    public void IgnoresCase()
    {
        Assert.True(StartupRegistration.StartsMinimized($"\"{Path}\" --MINIMIZED"));
    }

    [Fact(DisplayName = "指定に似ているだけの引数は読み取らない")]
    public void IgnoresArgumentsThatMerelyLookLikeTheSwitch()
    {
        Assert.False(StartupRegistration.StartsMinimized($"\"{Path}\" --minimized-later"));
        Assert.False(StartupRegistration.StartsMinimized($"\"{Path}\" -minimized"));
    }

    [Theory(DisplayName = "登録が無い場合は指定も無い")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TreatsAMissingCommandAsNotMinimized(string? command)
    {
        Assert.False(StartupRegistration.StartsMinimized(command));
    }

    [Fact(DisplayName = "閉じ引用符が無いコマンドは指定が無いものとして扱う")]
    public void TreatsAnUnterminatedQuoteAsNotMinimized()
    {
        // 引数の切れ目を決められない。窓を出す側へ倒す。出さない側へ倒すと、
        // トレイのアイコンを作れていない環境で画面へ辿り着けなくなる。
        Assert.False(StartupRegistration.StartsMinimized($"\"{Path}"));
    }
}
