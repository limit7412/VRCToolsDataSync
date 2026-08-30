using VRCToolsDataSync.Core.Domain;
using VRCToolsDataSync.Core.Sync;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// ツールの一覧の性質を固定する。
/// <para>
/// ここは起動時・終了時・監視中の三箇所が共有する。ツールを足すときに触るのが
/// ここだけで済むことが値打ちなので、<b>一覧そのものが壊れていないか</b>を見る。
/// </para>
/// </summary>
public sealed class ToolCatalogTests
{
    [Fact(DisplayName = "一覧は空ではない")]
    public void TheCatalogIsNotEmpty()
        => Assert.NotEmpty(ToolCatalog.All);

    [Fact(DisplayName = "識別子が重複しない")]
    public void KeysAreUnique()
    {
        // 重複すると、設定や manifest の中でツールを取り違える。
        var keys = ToolCatalog.All.Select(t => t.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact(DisplayName = "識別子と表示名が埋まっている")]
    public void EveryToolHasAKeyAndADisplayName()
    {
        Assert.All(ToolCatalog.All, tool =>
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Key));
            Assert.False(string.IsNullOrWhiteSpace(tool.DisplayName));
        });
    }

    [Fact(DisplayName = "プロセス名の候補が 1 つ以上ある")]
    public void EveryToolHasAtLeastOneProcessName()
    {
        // 候補が無いと、起動中の判定も検出状況の表示も成立しない。
        Assert.All(ToolCatalog.All, tool => Assert.NotEmpty(tool.ProcessNames));
    }

    [Fact(DisplayName = "同期の可否は、ツールごとに別の設定を見る")]
    public void EachToolReadsItsOwnSyncFlag()
    {
        // 一覧をコピーして書き足すと、設定を引く式まで前のツールのまま残りやすい。
        // そうなると片方を切ったつもりで両方が切れる。
        var onlyVrcx = new SyncSettings { SyncVrcx = true, SyncFriendConnect = false };
        var onlyFriendConnect = new SyncSettings { SyncVrcx = false, SyncFriendConnect = true };

        var enabledForVrcx = Assert.Single(ToolCatalog.All.Where(t => t.IsSyncEnabled(onlyVrcx)));
        var enabledForFriendConnect =
            Assert.Single(ToolCatalog.All.Where(t => t.IsSyncEnabled(onlyFriendConnect)));

        Assert.Equal(VrcxSyncService.Key, enabledForVrcx.Key);
        Assert.Equal(FriendConnectSyncService.Key, enabledForFriendConnect.Key);
    }

    [Fact(DisplayName = "すべて切れば、どのツールも同期の対象にならない")]
    public void NothingIsEnabledWhenEverySyncFlagIsOff()
    {
        var settings = new SyncSettings { SyncVrcx = false, SyncFriendConnect = false };

        Assert.Empty(ToolCatalog.All.Where(t => t.IsSyncEnabled(settings)));
    }
}
