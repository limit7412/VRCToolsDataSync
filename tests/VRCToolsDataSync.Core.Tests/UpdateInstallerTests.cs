using VRCToolsDataSync.Core.Infra;
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
        File.WriteAllText(Path.Combine(directory, "app", UpdateInstaller.AppAssemblyName), marker + "-app-dll");
        File.WriteAllText(Path.Combine(directory, "cli", "marker.txt"), marker + "-cli");
        File.WriteAllText(Path.Combine(directory, "cli", UpdateInstaller.CliExecutableName), marker + "-cli-exe");
        File.WriteAllText(Path.Combine(directory, "cli", UpdateInstaller.CliAssemblyName), marker + "-cli-dll");
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

    [Fact(DisplayName = "前回が残した .new と .old は、空きを測る前に片付ける")]
    public void ClearsLeftoverDirectoriesBeforeMeasuringSpace()
    {
        // 前回のヘルパが複製の途中で落ち、さらに前回の退避も残っている状況。
        // どちらも次に消す対象なので、数に入れたまま空きを測ると、消せば足りる
        // 場合でも断り続けることになる。
        Directory.CreateDirectory(TargetFile("app.new", "nested"));
        File.WriteAllText(TargetFile("app.new", "stale.txt"), "stale");
        Directory.CreateDirectory(TargetFile("cli.new"));
        File.WriteAllText(TargetFile("cli.new", "stale.txt"), "stale");
        Directory.CreateDirectory(TargetFile("app.old"));
        File.WriteAllText(TargetFile("app.old", "older.txt"), "older");
        Directory.CreateDirectory(TargetFile("cli.old"));
        File.WriteAllText(TargetFile("cli.old", "older.txt"), "older");

        new UpdateInstaller(SourceDir, TargetDir).Apply();

        Assert.Equal("new-app", File.ReadAllText(TargetFile("app", "marker.txt")));
        Assert.False(File.Exists(TargetFile("app", "stale.txt")));
        Assert.False(Directory.Exists(TargetFile("app.new")));
        Assert.False(Directory.Exists(TargetFile("cli.new")));

        // 今回の退避に入れ替わっている (1 つ前の版のものは残らない)。
        Assert.Equal("old-app", File.ReadAllText(TargetFile("app.old", "marker.txt")));
        Assert.False(File.Exists(TargetFile("app.old", "older.txt")));
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

    [Fact(DisplayName = "app の本体 dll が欠けた一式も、正規の位置に触る前に断る")]
    public void RefusesSourceWithoutAppAssembly()
    {
        // 配布は単一ファイルにまとめていないので、exe は起動の入り口でしかない。
        // 隣の dll が欠けていれば、exe がそろっていても起動できない。
        File.Delete(Path.Combine(SourceDir, "app", UpdateInstaller.AppAssemblyName));

        Assert.Throws<InvalidOperationException>(() => new UpdateInstaller(SourceDir, TargetDir).Apply());

        Assert.Equal("old-app", File.ReadAllText(TargetFile("app", "marker.txt")));
        Assert.False(Directory.Exists(TargetFile("app.old")));
    }

    [Fact(DisplayName = "置き換えの途中の形は完了と見なさない")]
    public void LooksCompleteRejectsHalfSwappedLayouts()
    {
        // 巻き戻しにも失敗すると、入れ替え済みの側だけで起動できることがある。
        // その形で後始末へ進むと、復旧の材料まで消してしまう。
        Assert.True(UpdateInstaller.LooksComplete(TargetDir));

        // 途中で終わった跡が残っている。
        Directory.CreateDirectory(TargetFile("cli.new"));
        Assert.False(UpdateInstaller.LooksComplete(TargetDir));
        Directory.Delete(TargetFile("cli.new"));
        Assert.True(UpdateInstaller.LooksComplete(TargetDir));

        // 正規の位置が欠けている。
        Directory.Delete(TargetFile("cli"), recursive: true);
        Assert.False(UpdateInstaller.LooksComplete(TargetDir));
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

    /// <summary>指定のディレクトリだけ「消せない」ことにする。</summary>
    private sealed class UndeletableInstaller : UpdateInstaller
    {
        private readonly Func<string, bool> _undeletable;

        public UndeletableInstaller(string source, string target, Func<string, bool> undeletable)
            : base(source, target)
        {
            _undeletable = undeletable;
        }

        // 消せないのは「そこにある」場合だけである。名前をずらした後の
        // 元の名前は、消えたものとして扱わないと本物の挙動とずれる。
        protected override bool TryDelete(string path)
            => !(Directory.Exists(path) && _undeletable(path)) && base.TryDelete(path);
    }

    [Fact(DisplayName = "消せない .old があっても、名前をずらして置き換えを進める")]
    public void MovesUndeletableLeftoverAsideAndContinues()
    {
        // 消せない残骸で置き換えが止まると、そこから抜け出せなくなる (#61)。
        // 要るのは .old という名前が空いていることだけなので、消せなければ
        // ずらして進む。
        Directory.CreateDirectory(TargetFile("app.old"));
        File.WriteAllText(TargetFile("app.old", "stuck.txt"), "stuck");

        // 名前をずらしても中身は消せないままなので、ずらした先も対象にする。
        var stuck = TargetFile("app.old");
        var installer = new UndeletableInstaller(
            SourceDir, TargetDir, path => path.StartsWith(stuck, StringComparison.Ordinal));

        installer.Apply();

        // 置き換えは通っている。
        Assert.Equal("new-app", File.ReadAllText(TargetFile("app", "marker.txt")));
        Assert.Equal("new-cli", File.ReadAllText(TargetFile("cli", "marker.txt")));

        // 今回の退避が .old を名乗り、消せなかったものは名前をずらして残る。
        Assert.Equal("old-app", File.ReadAllText(TargetFile("app.old", "marker.txt")));
        var movedAside = Directory.GetDirectories(TargetDir, "app.old.trash-*");
        Assert.Single(movedAside);
        Assert.Equal("stuck", File.ReadAllText(Path.Combine(movedAside[0], "stuck.txt")));
    }

    [Fact(DisplayName = "ずらすこともできない残骸は、正規の位置に触る前に断る")]
    public void DefersWhenLeftoverCanBeNeitherDeletedNorMovedAside()
    {
        Directory.CreateDirectory(TargetFile("app.old"));
        var stuck = TargetFile("app.old");
        var installer = new ImmovableLeftoverInstaller(SourceDir, TargetDir, stuck);

        Assert.Throws<UpdateDeferredException>(() => installer.Apply());

        // 正規の位置は無傷のままである。
        Assert.Equal("old-app", File.ReadAllText(TargetFile("app", "marker.txt")));
    }

    /// <summary>消すことも名前をずらすこともできない残骸を作る。</summary>
    private sealed class ImmovableLeftoverInstaller : UpdateInstaller
    {
        private readonly string _stuck;

        public ImmovableLeftoverInstaller(string source, string target, string stuck)
            : base(source, target)
        {
            _stuck = stuck;
        }

        protected override bool TryDelete(string path)
            => !(Directory.Exists(path) && path == _stuck) && base.TryDelete(path);

        protected override void Move(string from, string to)
        {
            if (from == _stuck) throw new IOException("injected failure");
            base.Move(from, to);
        }
    }

    [Fact(DisplayName = "読み取り専用の残骸は、属性を落として消す")]
    public void ClearsReadOnlyAttributesBeforeDeletingLeftover()
    {
        // Directory.Delete は読み取り専用の属性を落とさない。ZIP から展開した
        // 木では、これが Access is denied の最もよくある原因になる (#61)。
        Directory.CreateDirectory(TargetFile("app.old", "locale"));
        var readOnly = TargetFile("app.old", "locale", "resources.dll");
        File.WriteAllText(readOnly, "resource");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        new UpdateInstaller(SourceDir, TargetDir).Apply();

        Assert.Equal("new-app", File.ReadAllText(TargetFile("app", "marker.txt")));
        // 前回の残骸は消えており、ずらした跡も残らない。
        Assert.Empty(Directory.GetDirectories(TargetDir, "app.old.trash-*"));
        Assert.Equal("old-app", File.ReadAllText(TargetFile("app.old", "marker.txt")));
    }

    [Fact(DisplayName = "後始末は、ずらして残った残骸も消す")]
    public void DiscardPreviousRemovesMovedAsideLeftovers()
    {
        Directory.CreateDirectory(TargetFile("app.old"));
        Directory.CreateDirectory(TargetFile("cli.old"));
        Directory.CreateDirectory(TargetFile("app.old.trash-abcd1234"));
        Directory.CreateDirectory(TargetFile("cli.new.trash-abcd1234"));

        UpdateInstaller.DiscardPrevious(TargetDir);

        Assert.False(Directory.Exists(TargetFile("app.old")));
        Assert.False(Directory.Exists(TargetFile("cli.old")));
        Assert.False(Directory.Exists(TargetFile("app.old.trash-abcd1234")));
        Assert.False(Directory.Exists(TargetFile("cli.new.trash-abcd1234")));
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

    [Fact(DisplayName = "インストール先に入っている版は app の下から読む")]
    public void InstalledAppVersionReadsFromAppDirectory()
    {
        // 配布の形でなければ読みようが無い。
        Assert.Null(UpdateInstaller.InstalledAppVersion(null));

        // CreateBundle が置くのはただのテキストなので、版は埋まっていない。
        Assert.Null(UpdateInstaller.InstalledAppVersion(TargetDir));

        // 版を持つファイルを app の下へ置けば読める。ここでは Core 自身の
        // アセンブリを借りる (中身は問わない。読めるかどうかだけを見る)。
        var versioned = typeof(UpdateInstaller).Assembly.Location;
        Assert.False(string.IsNullOrEmpty(versioned));
        File.Copy(versioned, TargetFile("app", UpdateInstaller.AppAssemblyName), overwrite: true);

        Assert.NotNull(UpdateInstaller.InstalledAppVersion(TargetDir));
    }

    [Fact(DisplayName = "掴まれている抑止は待ちきれずに諦める")]
    public void SingleInstanceHoldGivesUpWhileAnotherProcessHoldsIt()
    {
        var name = "vrctoolsdatasync-tests-" + Guid.NewGuid().ToString("N");
        using var release = new ManualResetEventSlim(false);
        using var acquired = new ManualResetEventSlim(false);

        // 抑止は所有権がスレッドに紐づく。掴む側と試す側を分ける。
        var holder = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: true, name: name);
            acquired.Set();
            release.Wait();
            mutex.ReleaseMutex();
        })
        { IsBackground = true };
        holder.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)));

        Assert.Null(UpdateInstaller.TryHoldNamedMutex(name, TimeSpan.FromMilliseconds(200)));

        release.Set();
        Assert.True(holder.Join(TimeSpan.FromSeconds(5)));

        using var held = UpdateInstaller.TryHoldNamedMutex(name, TimeSpan.FromSeconds(5));
        Assert.NotNull(held);
    }

    [Fact(DisplayName = "握ったまま終わった抑止は放棄として掴み直せる")]
    public void SingleInstanceHoldTakesOverAnAbandonedMutex()
    {
        var name = "vrctoolsdatasync-tests-" + Guid.NewGuid().ToString("N");
        using var acquired = new ManualResetEventSlim(false);

        // 手放さずに終わるスレッドで、終了時 Exit の App と同じ形を作る。
        // Mutex 自体はこの場で持つ。掴んだスレッドと一緒に手を捨てると、
        // 待つ相手そのものが消えて放棄の経路を通らない。
        using var owned = new Mutex(initiallyOwned: false, name: name);
        var holder = new Thread(() =>
        {
            owned.WaitOne();
            acquired.Set();
        })
        { IsBackground = true };
        holder.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(holder.Join(TimeSpan.FromSeconds(5)));

        // AbandonedMutexException で降りず、所有権を引き取る。
        using var held = UpdateInstaller.TryHoldNamedMutex(name, TimeSpan.FromSeconds(5));
        Assert.NotNull(held);
    }

    [Fact(DisplayName = "掴んだ抑止を捨てると次の手が掴める")]
    public void SingleInstanceHoldIsReleasedOnDispose()
    {
        var name = "vrctoolsdatasync-tests-" + Guid.NewGuid().ToString("N");

        var held = UpdateInstaller.TryHoldNamedMutex(name, TimeSpan.FromSeconds(5));
        Assert.NotNull(held);

        // ヘルパは起動し直しの前に明示的に手放し、その後 using からもう一度来る。
        held!.Dispose();
        held.Dispose();

        using var again = UpdateInstaller.TryHoldNamedMutex(name, TimeSpan.FromSeconds(5));
        Assert.NotNull(again);
    }
}
