using System.IO.Compression;
using System.Security.Cryptography;
using VRCToolsDataSync.Core.Update;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// 取得しておいた更新の置き場所の判定を固定する (issue #45 第 3 段階)。
/// 置き換えの直前の関門であり、チャンネル適合・版の前後・記録との照合の
/// どれかで落ちたものはその場で捨てられることを確かめる。
/// </summary>
public sealed class UpdateStageTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "vrctoolsdatasync-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort */ }
    }

    private UpdateStage CreateStage() => new(_directory);

    /// <summary>取得済みの状態 (ZIP と記録の対) を作る。</summary>
    /// <summary>テストを走らせているプロセスに合う配布物の名前。</summary>
    private static string CurrentAssetName =>
        ReleaseAsset.NameForCurrentArchitecture()
        ?? throw new InvalidOperationException("配布のあるアーキテクチャで実行すること");

    private UpdateStage StageWith(
        string tag, bool prerelease = false, byte[]? corruptedContent = null, string? assetName = null)
    {
        var stage = CreateStage();
        Directory.CreateDirectory(_directory);

        var content = new byte[1234];
        Random.Shared.NextBytes(content);
        // 取得と同じ経路を通す。一時の場所へ書いてから置き換え待ちへ昇格させる。
        File.WriteAllBytes(stage.IncomingZipPath, corruptedContent ?? content);

        var version = ReleaseVersion.Parse(tag)!;
        var asset = new ReleaseAsset(
            assetName ?? CurrentAssetName,
            "https://example.com/asset",
            Convert.ToHexStringLower(SHA256.HashData(content)),
            content.Length);
        var release = new ReleaseInfo(version, tag, $"https://example.com/{tag}", prerelease, asset);
        stage.PromoteIncoming(release, asset);
        return stage;
    }

    [Fact(DisplayName = "チャンネルと版と照合の通ったものだけを返す")]
    public void ReturnsVerifiedStagedUpdate()
    {
        var stage = StageWith("0.0.10-test2", prerelease: true);

        var staged = stage.TryLoadVerified(UpdateChannel.Test, "0.0.9");

        Assert.NotNull(staged);
        Assert.Equal("0.0.10-test2", staged!.Tag);
        Assert.False(staged.Stable);
        Assert.True(File.Exists(stage.ZipPath));
    }

    [Fact(DisplayName = "stable チャンネルではプレリリースの取得を捨てる")]
    public void StableChannelDiscardsStagedPrerelease()
    {
        // test で取ったプレリリースを、stable へ変えて再起動したケース。
        var stage = StageWith("0.0.10-test2", prerelease: true);

        Assert.Null(stage.TryLoadVerified(UpdateChannel.Stable, "0.0.9"));
        Assert.False(File.Exists(stage.ZipPath));
        Assert.False(File.Exists(stage.MetadataPath));
    }

    [Fact(DisplayName = "破棄なしの照合は、合わないものを残したまま null を返す")]
    public void NonDestructiveCheckKeepsMismatches()
    {
        // 起動シーケンスの先頭で使う形。置き換え直後の起動がこの後で失敗した
        // 場合の復旧の材料になるため、ここでは捨てない。
        var stage = StageWith("0.0.10");

        Assert.Null(stage.TryLoadVerified(UpdateChannel.Stable, "0.0.10", discardMismatches: false));
        Assert.True(File.Exists(stage.ZipPath));
        Assert.True(File.Exists(stage.MetadataPath));
    }

    [Fact(DisplayName = "実行中より新しくないものは捨てる")]
    public void DiscardsWhenNotNewerThanRunning()
    {
        // 取得後に手で新しい版へ入れ替えられていたケース。引き戻さない。
        var stage = StageWith("0.0.10");

        Assert.Null(stage.TryLoadVerified(UpdateChannel.Stable, "0.0.10"));
        Assert.False(File.Exists(stage.ZipPath));
    }

    [Fact(DisplayName = "記録と合わない ZIP は捨てる")]
    public void DiscardsCorruptedZip()
    {
        var stage = StageWith("0.0.10", corruptedContent: new byte[] { 1, 2, 3 });

        Assert.Null(stage.TryLoadVerified(UpdateChannel.Stable, "0.0.9"));
        Assert.False(File.Exists(stage.ZipPath));
    }

    [Fact(DisplayName = "片方だけ残った取得は起動時の片付けで消える")]
    public void DiscardIncompleteRemovesLonePieces()
    {
        var stage = CreateStage();
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(stage.ZipPath, new byte[] { 1 });

        stage.DiscardIncomplete();

        Assert.False(File.Exists(stage.ZipPath));

        // そろっている対は消さない。
        var complete = StageWith("0.0.10");
        complete.DiscardIncomplete();
        Assert.True(File.Exists(complete.ZipPath));
    }

    [Fact(DisplayName = "次の取得が途中で失敗しても、取得済みの版は残る")]
    public void FailedIncomingDownloadKeepsPreviousStaged()
    {
        var stage = StageWith("0.0.10");

        // 次の版の取得が途中で落ちた状況。一時の場所にだけ書きかけが残る。
        File.WriteAllBytes(stage.IncomingZipPath, new byte[] { 9, 9, 9 });
        stage.DiscardIncoming();

        // 適用できたはずの前の版は無傷のまま。
        var staged = stage.TryLoadVerified(UpdateChannel.Stable, "0.0.9");
        Assert.NotNull(staged);
        Assert.Equal("0.0.10", staged!.Tag);
    }

    [Fact(DisplayName = "昇格は前の版の ZIP と記録を置き換える")]
    public void PromoteReplacesPreviousPair()
    {
        var stage = StageWith("0.0.10");
        var first = File.ReadAllBytes(stage.ZipPath);

        var second = StageWith("0.0.11");

        Assert.Equal("0.0.11", second.TryLoadMetadata()!.Tag);
        Assert.NotEqual(first, File.ReadAllBytes(second.ZipPath));
        // 一時の場所は空にしておく。次の取得の書きかけと取り違えない。
        Assert.False(File.Exists(second.IncomingZipPath));
    }

    [Fact(DisplayName = "別のアーキテクチャ向けの取得は適用しない")]
    public void DiscardsStagedForAnotherArchitecture()
    {
        // ARM64 の Windows では、ネイティブの版とエミュレーションの x64 版が
        // 同じ置き場所を共有しうる。片方の取得をもう片方が適用してはいけない。
        var stage = StageWith("0.0.10", assetName: "VRCToolsDataSync-win-somethingelse.zip");

        Assert.Null(stage.TryLoadVerified(UpdateChannel.Stable, "0.0.9"));
        Assert.False(File.Exists(stage.ZipPath));
    }

    [Fact(DisplayName = "配布物の名前を持たない古い記録は適用しない")]
    public void DiscardsStagedWithoutAssetName()
    {
        var stage = StageWith("0.0.10");

        // 名前の項目が無い、この変更より前に書かれた記録へ差し替える。
        var size = new FileInfo(stage.ZipPath).Length;
        var digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(stage.ZipPath)));
        File.WriteAllText(stage.MetadataPath, $$"""
            {
              "tag": "0.0.10",
              "digestHex": "{{digest}}",
              "size": {{size}},
              "stable": true
            }
            """);

        // 分からないものを適用するわけにはいかない。
        Assert.Null(stage.TryLoadVerified(UpdateChannel.Stable, "0.0.9"));
    }

    [Fact(DisplayName = "別のインストール先の取得は適用も破棄もしない")]
    public void KeepsStagedFromAnotherInstallRoot()
    {
        var stage = StageWith("0.0.10");

        // 別の場所へ展開したコピーが取った記録に見せかける。
        // テストを走らせているプロセスは配布の形ではないので、記録の
        // installRoot は空で書かれている。
        var json = File.ReadAllText(stage.MetadataPath);
        var altered = json.Replace("\"installRoot\": \"\"", "\"installRoot\": \"D:\\\\elsewhere\"");
        Assert.NotEqual(json, altered);
        File.WriteAllText(stage.MetadataPath, altered);

        Assert.Null(stage.TryLoadVerified(UpdateChannel.Stable, "0.0.9"));
        // 相手のコピーが適用できるよう、こちらでは捨てない。
        Assert.True(File.Exists(stage.ZipPath));
        Assert.True(File.Exists(stage.MetadataPath));
    }

    [Fact(DisplayName = "破棄は消せたかどうかを返す")]
    public void DiscardReportsWhetherItRemovedThePair()
    {
        var stage = StageWith("0.0.10");

        // 消せたときだけ true。呼び出し側はこれを見て、次の起動が同じものを
        // 適用しに行かないことを確かめる。
        Assert.True(stage.Discard());
        Assert.False(File.Exists(stage.ZipPath));
        Assert.False(File.Exists(stage.MetadataPath));

        // もともと無い場合も消せたものとして扱う。
        Assert.True(stage.Discard());
    }

    [Fact(DisplayName = "表示用の読み出しは対がそろっているときだけ返す")]
    public void TryLoadMetadataRequiresBothPieces()
    {
        var stage = StageWith("0.0.10");
        Assert.Equal("0.0.10", stage.TryLoadMetadata()!.Tag);

        File.Delete(stage.ZipPath);
        Assert.Null(stage.TryLoadMetadata());
    }

    [Fact(DisplayName = "既定の置き場所はインストール先ごとに分かれる")]
    public void DefaultDirectoryIsScopedPerInstallRoot()
    {
        var shared = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VRCToolsDataSync", "update");

        // 共有の update\ をそのまま使うと、複数の場所へ展開したコピーが
        // 互いの取得を「取得済み」と見て取得を省き、しかし適用はできない
        // 行き止まりに入る。1 段掘って分ける。
        var actual = UpdateStage.DefaultDirectory();
        Assert.NotEqual(shared, Path.TrimEndingDirectorySeparator(actual));
        Assert.Equal(shared, Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(actual)));

        // インストール先が違えば置き場所も違う。末尾の区切りと大文字小文字は
        // 同じ場所として畳む (Windows のファイルシステムに合わせる)。
        var a = UpdateStage.DirectoryFor(Path.Combine("C:", "apps", "vrctds"));
        var b = UpdateStage.DirectoryFor(Path.Combine("D:", "elsewhere", "vrctds"));
        Assert.NotEqual(a, b);
        Assert.Equal(a, UpdateStage.DirectoryFor(Path.Combine("C:", "Apps", "VRCTDS") + Path.DirectorySeparatorChar));

        // 配布の形でない場合は 1 つにまとめる。
        Assert.Equal(Path.Combine(shared, "local"), UpdateStage.DirectoryFor(null));
    }

    /// <summary>
    /// 別のスレッドから同じ名前のロックを取ってみる。Mutex は取った本人には
    /// 何度でも渡るので、分かれているかどうかは別のスレッドからしか見えない。
    /// </summary>
    private static bool CanHoldOnAnotherThread(string installRoot)
    {
        var acquired = false;
        var thread = new Thread(() =>
        {
            using var mutex = UpdateStage.CreateApplyMutex(installRoot);
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            finally { if (acquired) mutex.ReleaseMutex(); }
        });
        thread.Start();
        thread.Join();
        return acquired;
    }

    [Fact(DisplayName = "適用のロックはインストール先ごとに分かれる")]
    public void ApplyMutexIsScopedPerInstallRoot()
    {
        // 名前は実行ごとに変える。同時に走る別のテスト実行と取り合わない。
        var mine = Path.Combine("C:", "apps", "vrctds-" + Guid.NewGuid().ToString("N"));
        var other = Path.Combine("D:", "elsewhere", "vrctds-" + Guid.NewGuid().ToString("N"));

        using var held = UpdateStage.CreateApplyMutex(mine);
        Assert.True(held.WaitOne(0));
        try
        {
            // 同じインストール先なら待たされる。
            Assert.False(CanHoldOnAnotherThread(mine));
            // 別のインストール先なら待つ理由が無い。置き場所も分かれている。
            Assert.True(CanHoldOnAnotherThread(other));
        }
        finally
        {
            held.ReleaseMutex();
        }
    }


    [Fact(DisplayName = "同じパスの項目を持つ ZIP は展開せずに断る")]
    public void RefusesArchiveWithDuplicateEntries()
    {
        var stage = CreateStage();
        Directory.CreateDirectory(_directory);

        // 書式としては正しく digest も通るが、上書きしない展開は 2 つ目で
        // 落ちる。容量不足などと区別できるよう、配布物そのものの問題として
        // 投げ分ける。
        using (var file = File.Create(stage.ZipPath))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            archive.CreateEntry("app/VRCToolsDataSync.App.exe");
            archive.CreateEntry("app/VRCToolsDataSync.App.exe");
        }

        Assert.Throws<InvalidDataException>(() => stage.ExtractForApply());
    }

    [Fact(DisplayName = "区切りの書き方だけが違う項目も同じ場所として断る")]
    public void RefusesArchiveWithEntriesThatCollideAfterNormalisation()
    {
        var stage = CreateStage();
        Directory.CreateDirectory(_directory);

        // ZIP の区切りは "/" と決まっているが "\" で書かれたものも出回る。
        // Windows ではどちらも区切りなので、展開先は同じになる。
        using (var file = File.Create(stage.ZipPath))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            archive.CreateEntry("app/VRCToolsDataSync.App.exe");
            archive.CreateEntry("app\\VRCToolsDataSync.App.exe");
            archive.CreateEntry("./cli/VRCToolsDataSync.Cli.exe");
        }

        Assert.Throws<InvalidDataException>(() => stage.ExtractForApply());
    }
}
