using VRCToolsDataSync.Core.Settings;
using VRCToolsDataSync.Core.Update;
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
        settings.Update.CheckEnabled = false;
        settings.Update.NotifiedVersion = "0.0.10-test1";

        store.Save(settings);
        var loaded = CreateStore().Load();

        Assert.Equal(UpdateChannel.Test, loaded.Update.Channel);
        Assert.False(loaded.Update.CheckEnabled);
        Assert.Equal("0.0.10-test1", loaded.Update.NotifiedVersion);
    }

    [Fact(DisplayName = "項目の無い既存の settings.json は既定値で読める")]
    public void DefaultsApplyWhenSectionIsMissing()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "settings.json"), """{ "machineName": "PC-1" }""");

        var loaded = CreateStore().Load();

        Assert.Equal(UpdateChannel.Stable, loaded.Update.Channel);
        Assert.True(loaded.Update.CheckEnabled);
        Assert.Equal(string.Empty, loaded.Update.NotifiedVersion);
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
}
