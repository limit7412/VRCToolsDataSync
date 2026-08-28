using VRCToolsDataSync.Core.Update;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// インストール先の入れ替えを固定する (issue #45 第 3 段階)。
/// 成功で新しい一式に入れ替わること、失敗で元の一式へ戻ること、
/// 戻しにも失敗した状態が区別されることを、実ディレクトリで確かめる。
/// </summary>
public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vrctoolsdatasync-tests-" + Guid.NewGuid().ToString("N"));

    private string SourceDir => Path.Combine(_root, "source");
    private string TargetDir => Path.Combine(_root, "target");

    public UpdateInstallerTests()
    {
        CreateBundle(SourceDir, "new");
        CreateBundle(TargetDir, "old");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>配布 ZIP と同じ形 (app / cli / 実行ファイル / ランチャー) の一式を作る。</summary>
    private static void CreateBundle(string directory, string marker)
    {
        Directory.CreateDirectory(Path.Combine(directory, "app", "nested"));
        Directory.CreateDirectory(Path.Combine(directory, "cli"));
        File.WriteAllText(Path.Combine(directory, "app", "marker.txt"), marker + "-app");
        File.WriteAllText(Path.Combine(directory, "app", "nested", "marker.txt"), marker + "-nested");
        File.WriteAllText(Path.Combine(directory, "app", UpdateInstaller.AppExecutableName), marker + "-app-exe");
        File.WriteAllText(Path.Combine(directory, "cli", "marker.txt"), marker + "-cli");
        File.WriteAllText(Path.Combine(directory, "cli", UpdateInstaller.CliExecutableName), marker + "-cli-exe");
        File.WriteAllText(Path.Combine(directory, UpdateInstaller.LauncherName), marker + "-cmd");
    }

    private string TargetFile(params string[] parts)
        => Path.Combine(new[] { TargetDir }.Concat(parts).ToArray());

    [Fact(DisplayName = "入れ替えに成功すると、新しい一式と退避した旧一式が残る")]
    public void SwapsBundleAndKeepsBackup()
    {
        new UpdateInstaller(SourceDir, TargetDir).Apply();

        Assert.Equal("new-app", File.ReadAllText(TargetFile("app", "marker.txt")));
        Assert.Equal("new-nested", File.ReadAllText(TargetFile("app", "nested", "marker.txt")));
        Assert.Equal("new-cli", File.ReadAllText(TargetFile("cli", "marker.txt")));
        Assert.Equal("new-cmd", File.ReadAllText(TargetFile(UpdateInstaller.LauncherName)));

        // 旧一式は .old に退避され、次の起動の DiscardPrevious が消す。
        Assert.Equal("old-app", File.ReadAllText(TargetFile("app.old", "marker.txt")));
        Assert.Equal("old-cli", File.ReadAllText(TargetFile("cli.old", "marker.txt")));
        Assert.False(Directory.Exists(TargetFile("app.new")));
        Assert.False(Directory.Exists(TargetFile("cli.new")));

        UpdateInstaller.DiscardPrevious(TargetDir);
        Assert.False(Directory.Exists(TargetFile("app.old")));
        Assert.False(Directory.Exists(TargetFile("cli.old")));
    }

    [Fact(DisplayName = "一式が欠けていれば、正規の位置に触る前に断る")]
    public void RefusesIncompleteSource()
    {
        Directory.Delete(Path.Combine(SourceDir, "cli"), recursive: true);

        Assert.Throws<InvalidOperationException>(() => new UpdateInstaller(SourceDir, TargetDir).Apply());

        // インストール先は無傷のまま。
        Assert.Equal("old-app", File.ReadAllText(TargetFile("app", "marker.txt")));
        Assert.False(Directory.Exists(TargetFile("app.old")));
    }

    [Fact(DisplayName = "app の exe が欠けた一式も、正規の位置に触る前に断る")]
    public void RefusesSourceWithoutAppExecutable()
    {
        // digest は ZIP が配布物そのものであることまでしか保証しない。
        // ディレクトリがそろっていても、起動できない一式で置き換えない。
        File.Delete(Path.Combine(SourceDir, "app", UpdateInstaller.AppExecutableName));

        Assert.Throws<InvalidOperationException>(() => new UpdateInstaller(SourceDir, TargetDir).Apply());

        Assert.Equal("old-app", File.ReadAllText(TargetFile("app", "marker.txt")));
        Assert.False(Directory.Exists(TargetFile("app.old")));
    }

    /// <summary>指定の移動だけを失敗させて、失敗時の経路を作る。</summary>
    private sealed class FailingInstaller : UpdateInstaller
    {
        private readonly Func<string, string, bool> _shouldFail;

        public FailingInstaller(string source, string target, Func<string, string, bool> shouldFail)
            : base(source, target)
        {
            _shouldFail = shouldFail;
        }

        protected override void Move(string from, string to)
        {
            if (_shouldFail(from, to)) throw new IOException("injected failure");
            base.Move(from, to);
        }
    }

    [Fact(DisplayName = "後半の入れ替えに失敗したら、済ませた分ごと元へ戻す")]
    public void RollsBackAllPartsWhenLaterSwapFails()
    {
        var cliNew = TargetFile("cli.new");
        var installer = new FailingInstaller(SourceDir, TargetDir, (from, _) => from == cliNew);

        Assert.Throws<InvalidOperationException>(() => installer.Apply());

        // app は一度入れ替わっているが、cli の失敗で戻される。
        Assert.Equal("old-app", File.ReadAllText(TargetFile("app", "marker.txt")));
        Assert.Equal("old-cli", File.ReadAllText(TargetFile("cli", "marker.txt")));
        Assert.False(Directory.Exists(TargetFile("app.old")));
        Assert.False(Directory.Exists(TargetFile("cli.old")));
    }

    [Fact(DisplayName = "退避まで失敗した状態は、通常の失敗と区別して伝える")]
    public void ReportsRollbackFailureDistinctly()
    {
        // 新しい app を入れる移動と、退避した app を戻す移動は、どちらも
        // 正規の位置 (app) が宛先になる。両方を失敗させると、正規の位置に
        // 一式が無いままになる。
        var appCurrent = TargetFile("app");
        var installer = new FailingInstaller(SourceDir, TargetDir, (_, to) => to == appCurrent);

        Assert.Throws<UpdateRollbackException>(() => installer.Apply());

        // 復旧の材料 (退避した旧一式) は残っている。
        Assert.True(Directory.Exists(TargetFile("app.old")));
    }

    [Fact(DisplayName = "インストール先のルートは配布の形だけを認める")]
    public void FindInstallRootAcceptsOnlyDistributedLayout()
    {
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(TargetDir),
            UpdateInstaller.FindInstallRoot(Path.Combine(TargetDir, "app") + Path.DirectorySeparatorChar));

        // app という名前のディレクトリから動いていなければ配布の形ではない。
        Assert.Null(UpdateInstaller.FindInstallRoot(Path.Combine(TargetDir, "bin")));

        // ランチャーが無い場所も配布の形ではない (開発中の bin\...\app を誤認しない)。
        File.Delete(TargetFile(UpdateInstaller.LauncherName));
        Assert.Null(UpdateInstaller.FindInstallRoot(Path.Combine(TargetDir, "app")));
    }
}
