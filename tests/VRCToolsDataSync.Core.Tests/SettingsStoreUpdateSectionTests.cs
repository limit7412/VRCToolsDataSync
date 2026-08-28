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
