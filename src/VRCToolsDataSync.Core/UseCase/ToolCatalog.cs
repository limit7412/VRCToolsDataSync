
using VRCToolsDataSync.Core.Domain;
using VRCToolsDataSync.Core.Infra;
namespace VRCToolsDataSync.Core.UseCase;

/// <summary>
/// 同期の対象になるツール 1 つの定義。
/// <para>
/// 設定にも <see cref="SyncRunner"/> にも依らない部分だけを持つ。それらに依る値は、
/// 引数で受け取って返す形にしてある。一覧を要るのは起動時・終了時・監視中の三箇所で、
/// そのとき手元にある設定や runner は同じとは限らない。
/// </para>
/// </summary>
public sealed class ToolDefinition
{
    /// <summary>設定ファイルや manifest で使う識別子。</summary>
    public required string Key { get; init; }

    /// <summary>ログと UI に出す名前。</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// 実行ファイル名の候補。配布のされ方で変わりうるため複数持ち、どれか 1 つでも
    /// 当たれば起動中と見なす (<see cref="ProcessGuard"/>)。
    /// </summary>
    public required IReadOnlyList<string> ProcessNames { get; init; }

    /// <summary>実行ファイルの場所を探す。見つからなければ null。</summary>
    public required Func<string?> FindExecutable { get; init; }

    /// <summary>設定でこのツールの同期が有効かを返す。</summary>
    public required Func<SyncSettings, bool> IsSyncEnabled { get; init; }

    /// <summary>同期の処理本体を作る。</summary>
    public required Func<SyncRunner, ISyncService> CreateService { get; init; }
}

/// <summary>
/// 同期の対象になるツールの一覧。
/// <para>
/// 起動時 (<see cref="StartupSyncOrchestrator"/>)、終了時 (<see cref="ShutdownSyncOrchestrator"/>)、
/// 監視中 (<see cref="Watch.AutoSyncCoordinator"/>) が、それぞれ同じ一覧を要る。書き分けると
/// ツールを足したときに片方だけ直す取りこぼしが起きるため、ここに集める (issue #18)。
/// </para>
/// <para>
/// ツールを足すときに触るのはここだけになる。<b>ただし表示の側は別で、</b>
/// MainPage の各カードは XAML に直接書かれている。
/// </para>
/// </summary>
public static class ToolCatalog
{
    public static IReadOnlyList<ToolDefinition> All { get; } = new ToolDefinition[]
    {
        new()
        {
            Key = VrcxSyncService.Key,
            DisplayName = "VRCX",
            ProcessNames = ProcessGuard.VrcxProcessNames,
            FindExecutable = VrcxPaths.TryFindExecutable,
            IsSyncEnabled = settings => settings.SyncVrcx,
            CreateService = runner => new VrcxSyncService(logger: runner.CreateLogger<VrcxSyncService>()),
        },
        new()
        {
            Key = FriendConnectSyncService.Key,
            DisplayName = "VRC Friend Connect",
            ProcessNames = ProcessGuard.FriendConnectProcessNames,
            FindExecutable = FriendConnectPaths.TryFindExecutable,
            IsSyncEnabled = settings => settings.SyncFriendConnect,
            CreateService = runner => new FriendConnectSyncService(
                logger: runner.CreateLogger<FriendConnectSyncService>()),
        },
    };
}
