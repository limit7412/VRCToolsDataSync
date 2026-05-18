using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCToolsDataSync.Core.Settings;

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

    public SyncResult Push(
        ISyncService service,
        SyncSettings settings,
        string cloudFolderPath,
        bool force)
    {
        var state = settings.ToolState.GetValueOrDefault(service.ToolKey) ?? new ToolSyncState();
        var result = service.Push(new PushOptions
        {
            CloudFolderPath = cloudFolderPath,
            MachineName = settings.MachineName,
            ForceOverwriteOnConflict = force,
            LastPulledVersion = state.LastPulledVersion == 0 ? null : state.LastPulledVersion,
        });

        if (result.Outcome == SyncOutcome.Success && result.RemoteVersion.HasValue)
        {
            state.LastPushedVersion = result.RemoteVersion.Value;
            state.LastPushedAt = DateTimeOffset.Now;
            state.LastPulledVersion = result.RemoteVersion.Value;
            settings.ToolState[service.ToolKey] = state;
            _store.Save(settings);
        }
        return result;
    }

    public SyncResult Pull(
        ISyncService service,
        SyncSettings settings,
        string cloudFolderPath,
        bool skipBackup)
    {
        var result = service.Pull(new PullOptions
        {
            CloudFolderPath = cloudFolderPath,
            SkipBackup = skipBackup,
        });

        if (result.Outcome == SyncOutcome.Success && result.RemoteVersion.HasValue)
        {
            var state = settings.ToolState.GetValueOrDefault(service.ToolKey) ?? new ToolSyncState();
            state.LastPulledVersion = result.RemoteVersion.Value;
            state.LastPulledAt = DateTimeOffset.Now;
            settings.ToolState[service.ToolKey] = state;
            _store.Save(settings);
        }
        return result;
    }

    public ILogger<T> CreateLogger<T>() => _loggerFactory.CreateLogger<T>();
}
