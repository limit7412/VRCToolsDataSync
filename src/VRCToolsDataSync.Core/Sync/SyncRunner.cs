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
        var state = ResolveState(settings, storage, service.ToolKey, out var stateKey) ?? new ToolSyncState();
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
        var state = ResolveState(settings, storage, service.ToolKey, out var stateKey);
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
    /// <see cref="SyncSettings.ToolState"/> のキー。同期先ごとに分けることで、
    /// 保存先を切り替えても互いの同期履歴を壊さない。
    /// </summary>
    public static string ToolStateKey(ISyncStorage storage, string toolKey)
        => storage.StateKeyPrefix + toolKey;

    /// <summary>
    /// 同期先とツールに対応する同期履歴を取り出す。
    /// 表示や通知の判定など、更新を伴わない用途からも使う。
    /// </summary>
    public static ToolSyncState? FindToolState(SyncSettings settings, ISyncStorage storage, string toolKey)
        => ResolveState(settings, storage, toolKey, out _);

    /// <summary>
    /// 同期先とツールに対応する同期履歴を取り出す。
    /// <para>
    /// 保存先ごとにキーを分ける前の settings.json では、履歴がツールキーだけで
    /// 入っていた。その履歴は「当時 settings に設定されていた同期フォルダ」に対する
    /// ものなので、同じフォルダを指しているときに限って引き継ぐ。別のフォルダや
    /// S3 互換ストレージへは持ち込まない。
    /// </para>
    /// <para>
    /// 引き継ぎは一度だけ行う。フォルダ別のキーが既にあれば、旧キーが残っていても
    /// 参照しない。旧キーは <see cref="SettingsStore.MergeForSave"/> がディスク側の
    /// 値を残すため消えないので、「引き継ぎ済みかどうか」はフォルダ別キーの有無で
    /// 判断する。これをしないと、別のフォルダへ切り替えたときに旧キーが再び
    /// 拾われ、前のフォルダの履歴が復活してしまう。
    /// </para>
    /// </summary>
    private static ToolSyncState? ResolveState(
        SyncSettings settings,
        ISyncStorage storage,
        string toolKey,
        out string stateKey)
    {
        // JSON デシリアライズで ToolState が明示的に null になる可能性に備え、
        // SettingsStore.MergeForSave と同様に null ガードを入れる。
        settings.ToolState ??= new Dictionary<string, ToolSyncState>();

        stateKey = ToolStateKey(storage, toolKey);
        var state = settings.ToolState.GetValueOrDefault(stateKey);
        if (state is not null) return state;

        if (storage is LocalFolderSyncStorage local
            && IsConfiguredFolder(settings, local)
            && !HasFolderScopedState(settings, toolKey)
            && settings.ToolState.TryGetValue(toolKey, out var legacy))
        {
            // 値を複製する。参照をそのまま返すと、以後の更新が旧キー側の
            // エントリにも反映され、引き継ぎ済みかどうかの判断が狂う。
            return legacy.Clone();
        }
        return null;
    }

    /// <summary>いずれかの同期フォルダについて、このツールの履歴が既にあるか。</summary>
    private static bool HasFolderScopedState(SyncSettings settings, string toolKey)
    {
        var suffix = "|" + toolKey;
        foreach (var key in settings.ToolState.Keys)
        {
            if (key.StartsWith(LocalFolderSyncStorage.StateKeyScheme, StringComparison.Ordinal)
                && key.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsConfiguredFolder(SyncSettings settings, LocalFolderSyncStorage storage)
    {
        var configured = settings.CloudFolderPath?.Trim();
        if (string.IsNullOrEmpty(configured)) return false;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured)),
                storage.RootDirectory,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 設定に壊れたパスが入っている場合は引き継がない。
            return false;
        }
    }

    public ILogger<T> CreateLogger<T>() => _loggerFactory.CreateLogger<T>();
}
