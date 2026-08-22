using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCToolsDataSync.Core.Settings;
using VRCToolsDataSync.Core.Storage;

namespace VRCToolsDataSync.Core.Sync;

public sealed class SyncRunner
{
    private readonly SettingsStore _store;
    private readonly ILoggerFactory _loggerFactory;

    public SyncRunner(SettingsStore? store = null, ILoggerFactory? loggerFactory = null)
    {
        _store = store ?? new SettingsStore();
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public SyncSettings LoadSettings() => _store.Load();
    public void SaveSettings(SyncSettings settings) => _store.Save(settings);

    /// <summary>
    /// 設定から同期先を組み立てる。設定が足りない場合は
    /// <see cref="SyncStorageConfigurationException"/> を投げる。
    /// </summary>
    public ISyncStorage CreateStorage(SyncSettings settings, string? localFolderOverride = null)
        => SyncStorageFactory.Create(settings, localFolderOverride, _loggerFactory);

    public SyncResult Push(
        ISyncService service,
        SyncSettings settings,
        ISyncStorage storage,
        bool force)
    {
        var stateKey = ToolStateKey(storage, service.ToolKey);
        var state = settings.ToolState.GetValueOrDefault(stateKey) ?? new ToolSyncState();
        var result = service.Push(new PushOptions
        {
            Storage = storage,
            MachineName = settings.MachineName,
            ForceOverwriteOnConflict = force,
            LastPulledVersion = state.LastPulledVersion == 0 ? null : state.LastPulledVersion,
        });

        if (result.Outcome == SyncOutcome.Success && result.RemoteVersion.HasValue)
        {
            state.LastPushedVersion = result.RemoteVersion.Value;
            state.LastPushedAt = DateTimeOffset.Now;
            state.LastPulledVersion = result.RemoteVersion.Value;
            settings.ToolState[stateKey] = state;
            // Push 経由の Save は ToolState の更新だけが目的。Top-level 設定は
            // disk 値を優先しないと、古いインスタンスを持った別経路 (CLI / 別
            // SyncRunner) からの Push が、ユーザが GUI で変更した AutoSyncEnabled
            // 等を巻き戻してしまう。
            _store.SaveToolStateOnly(settings);
        }
        return result;
    }

    public SyncResult Pull(
        ISyncService service,
        SyncSettings settings,
        ISyncStorage storage,
        bool skipBackup,
        bool skipIfNotNewer = false)
    {
        // Issue #19: skipIfNotNewer=true の経路 (StartupSyncOrchestrator) では、
        // ローカルが既に最新と分かっているリモート (LastPulledVersion>=Version) の
        // Pull を抑止する。手動 Pull / コンフリクト解消 Pull は呼び出し側でデフォルトの
        // false を使い、従来通り上書き Pull を行う。
        // JSON デシリアライズで ToolState が明示的に null になる可能性に備え、
        // SettingsStore.MergeForSave と同様に null ガードを入れる。
        settings.ToolState ??= new Dictionary<string, ToolSyncState>();
        var stateKey = ToolStateKey(storage, service.ToolKey);
        var state = settings.ToolState.GetValueOrDefault(stateKey);
        var result = service.Pull(new PullOptions
        {
            Storage = storage,
            SkipBackup = skipBackup,
            SkipIfNotNewer = skipIfNotNewer,
            LastPulledVersion = state is null || state.LastPulledVersion == 0
                ? null
                : state.LastPulledVersion,
        });

        if (result.Outcome == SyncOutcome.Success && result.RemoteVersion.HasValue)
        {
            state ??= new ToolSyncState();
            state.LastPulledVersion = result.RemoteVersion.Value;
            state.LastPulledAt = DateTimeOffset.Now;
            settings.ToolState[stateKey] = state;
            // Push と同じ理由で SaveToolStateOnly を使う。
            _store.SaveToolStateOnly(settings);
        }
        return result;
    }

    /// <summary>
    /// 設定から同期先を組み立ててから Push する。同期先を使い回さない
    /// 単発の呼び出し向け。
    /// </summary>
    public SyncResult Push(ISyncService service, SyncSettings settings, string? localFolderOverride, bool force)
        => Push(service, settings, CreateStorage(settings, localFolderOverride), force);

    /// <summary>
    /// 設定から同期先を組み立ててから Pull する。同期先を使い回さない
    /// 単発の呼び出し向け。
    /// </summary>
    public SyncResult Pull(
        ISyncService service,
        SyncSettings settings,
        string? localFolderOverride,
        bool skipBackup,
        bool skipIfNotNewer = false)
        => Pull(service, settings, CreateStorage(settings, localFolderOverride), skipBackup, skipIfNotNewer);

    /// <summary>
    /// <see cref="SyncSettings.ToolState"/> のキー。同期先ごとに分けることで、
    /// 保存先を切り替えても互いの同期履歴を壊さない。
    /// </summary>
    public static string ToolStateKey(ISyncStorage storage, string toolKey)
        => storage.StateKeyPrefix + toolKey;

    public ILogger<T> CreateLogger<T>() => _loggerFactory.CreateLogger<T>();
}
