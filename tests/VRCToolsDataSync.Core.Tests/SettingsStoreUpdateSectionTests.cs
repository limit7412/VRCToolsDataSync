using VRCToolsDataSync.Core.Domain;
using VRCToolsDataSync.Core.Settings;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// settings.json の update セクションの読み書きを固定する (issue #45)。
/// 保存経路のマージで消えたり巻き戻ったりしないことを確かめる。
/// </summary>
public sealed class SettingsStoreUpdateSectionTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "vrctoolsdatasync-tests-" + Guid.NewGuid().ToString("N"));

    private SettingsStore CreateStore()
        => new(Path.Combine(_directory, "settings.json"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort */ }
    }

    [Fact(DisplayName = "update セクションは保存と読み込みで往復する")]
    public void RoundTripsUpdateSection()
    {
        var store = CreateStore();
        var settings = store.Load();
        settings.Update.Channel = UpdateChannel.Test;
        settings.Update.NotifiedVersion = "0.0.10-test1";

        store.Save(settings);
        var loaded = CreateStore().Load();

        Assert.Equal(UpdateChannel.Test, loaded.Update.Channel);
        Assert.Equal("0.0.10-test1", loaded.Update.NotifiedVersion);
    }

    [Fact(DisplayName = "項目の無い既存の settings.json は既定値で読める")]
    public void DefaultsApplyWhenSectionIsMissing()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "settings.json"), """{ "machineName": "PC-1" }""");

        var loaded = CreateStore().Load();

        Assert.Equal(UpdateChannel.Stable, loaded.Update.Channel);
        Assert.Equal(string.Empty, loaded.Update.NotifiedVersion);
    }

    [Fact(DisplayName = "明示的な null が書かれていても既定値で読める")]
    public void DefaultsApplyWhenSectionIsExplicitlyNull()
    {
        // 手で編集された settings.json には null が書かれうる。読んだ側が
        // 毎回それを気にせずに済むよう、読み込みの時点で既定へ落とす。
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "settings.json"), """{ "update": null }""");

        var loaded = CreateStore().Load();

        Assert.NotNull(loaded.Update);
        Assert.Equal(UpdateChannel.Stable, loaded.Update.Channel);
    }

    [Fact(DisplayName = "以前の checkEnabled が残った settings.json も、そのまま読める")]
    public void IgnoresRemovedCheckEnabledField()
    {
        // 確認を止める設定は無くした。読み込みは未知の項目を無視するので、
        // 以前の値が残っていても読み込み自体が失敗してはいけない。
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, "settings.json"),
            """{ "update": { "channel": "test", "checkEnabled": false } }""");

        var loaded = CreateStore().Load();

        Assert.Equal(UpdateChannel.Test, loaded.Update.Channel);
    }

    [Fact(DisplayName = "ToolState 専用の保存は update セクションを巻き戻さない")]
    public void SaveToolStateOnlyKeepsUpdateSectionOnDisk()
    {
        var store = CreateStore();
        var current = store.Load();
        current.Update.Channel = UpdateChannel.Test;
        current.Update.NotifiedVersion = "0.0.10-test1";
        store.Save(current);

        // Push/Pull 相当の経路が、起動時に読んだ古い settings で保存するケース。
        var stale = new SyncSettings();
        stale.ToolState["folder|x|vrcx"] = new ToolSyncState { LastPushedVersion = 1 };
        CreateStore().SaveToolStateOnly(stale);

        var loaded = CreateStore().Load();
        Assert.Equal(UpdateChannel.Test, loaded.Update.Channel);
        Assert.Equal("0.0.10-test1", loaded.Update.NotifiedVersion);
        // 呼び出し元のインスタンスにもディスク側の値が反映される。
        Assert.Equal(UpdateChannel.Test, stale.Update.Channel);
    }

    [Fact(DisplayName = "通知済みの版は、古い設定の保存で巻き戻らない")]
    public void NotifiedVersionNeverMovesBackwardsOnStaleSave()
    {
        // 別プロセス (接続確認に時間のかかる CLI の storage など) が先に設定を読む。
        var store = CreateStore();
        var stale = store.Load();

        // その間に GUI が通知済みの版を記録する。
        var current = CreateStore().Load();
        current.Update.NotifiedVersion = "0.0.10-test1";
        CreateStore().Save(current);

        // 古い設定 (未通知) のまま通常の Save をしても、記録は巻き戻らない。
        stale.CloudFolderPath = @"C:\sync";
        store.Save(stale);

        var loaded = CreateStore().Load();
        Assert.Equal("0.0.10-test1", loaded.Update.NotifiedVersion);
        Assert.Equal(@"C:\sync", loaded.CloudFolderPath);

        // 逆向き (incoming のほうが新しい記録を持つ) では incoming が勝つ。
        var newer = CreateStore().Load();
        newer.Update.NotifiedVersion = "0.0.11";
        CreateStore().Save(newer);
        Assert.Equal("0.0.11", CreateStore().Load().Update.NotifiedVersion);
    }

    [Fact(DisplayName = "通知済みの版の保存は、他の設定に触れない")]
    public void SaveNotifiedVersionKeepsEverythingElseOnDisk()
    {
        // 常駐 GUI が起動時に読んだ設定。
        var store = CreateStore();
        var atStartup = store.Load();
        atStartup.CloudFolderPath = @"C:\old";
        store.Save(atStartup);

        // その後、別プロセス (CLI の storage など) が保存先を変える。
        var fromCli = CreateStore().Load();
        fromCli.CloudFolderPath = @"D:\new";
        fromCli.Update.Channel = UpdateChannel.Test;
        CreateStore().Save(fromCli);

        // GUI が更新を知らせて記録を書いても、保存先は巻き戻らない。
        store.SaveNotifiedVersion("0.0.10-test1");

        var loaded = CreateStore().Load();
        Assert.Equal(@"D:\new", loaded.CloudFolderPath);
        Assert.Equal(UpdateChannel.Test, loaded.Update.Channel);
        Assert.Equal("0.0.10-test1", loaded.Update.NotifiedVersion);
    }

    [Fact(DisplayName = "チャンネルは stable / test の文字列で書かれる")]
    public void ChannelIsSerializedAsString()
    {
        var store = CreateStore();
        var settings = store.Load();
        settings.Update.Channel = UpdateChannel.Test;
        store.Save(settings);

        var json = File.ReadAllText(store.FilePath);

        // 数値で書くと、目で読めず手直しもできない。
        Assert.Contains("\"test\"", json, StringComparison.Ordinal);
    }
    [Fact(DisplayName = "settings.json が読めない場合、通知済みの版の保存は既定値で上書きせずに失敗する")]
    public void SaveNotifiedVersionDoesNotClobberCorruptedSettings()
    {
        // マージは読めないディスクを「無い」扱いにするので、確かめずに保存すると
        // 既定値だらけの settings が破損したファイルを正常な形で上書きし、
        // 保存先などの設定が無言で消える (#57)。
        var store = CreateStore();
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.FilePath, "{ broken");

        Assert.ThrowsAny<Exception>(() => store.SaveNotifiedVersion("0.0.10-test1"));
        Assert.Equal("{ broken", File.ReadAllText(store.FilePath));
    }

    [Fact(DisplayName = "settings.json がまだ無ければ、記録だけの保存も通る")]
    public void RecordOnlySaveWorksWhenSettingsFileIsAbsent()
    {
        // 「まだ無い」と「あるのに読めない」は別の話である。無いだけなら、
        // その記録を持つ settings を作るのが正しい。
        var store = CreateStore();
        Assert.False(File.Exists(store.FilePath));

        store.SaveNotifiedVersion("0.0.10-test1");

        Assert.Equal("0.0.10-test1", CreateStore().Load().Update.NotifiedVersion);
    }

    [Fact(DisplayName = "通常の保存は、読めない settings.json でも書き切る")]
    public void RegularSaveStillWritesOverUnreadableSettings()
    {
        // 通常の保存が渡すのは、利用者が画面で組み立てた設定一式である。
        // 既定値で塗り潰すわけではないので、ここで止めると壊れた settings.json を
        // 直す手立てが画面から無くなる。
        var store = CreateStore();
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.FilePath, "{ broken");

        var settings = new SyncSettings { CloudFolderPath = @"C:\sync" };
        store.Save(settings);

        Assert.Equal(@"C:\sync", CreateStore().Load().CloudFolderPath);
    }

}
