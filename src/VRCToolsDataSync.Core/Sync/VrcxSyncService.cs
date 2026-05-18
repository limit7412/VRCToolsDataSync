using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCToolsDataSync.Core.Paths;

namespace VRCToolsDataSync.Core.Sync;

public sealed class VrcxSyncService : ISyncService
{
    public const string Key = "vrcx";

    private const string SubFolder = "vrcx";
    private const string SnapshotFileName = "latest.sqlite3";
    private const string SettingsFileName = "latest.json";

    private readonly VrcxPaths _paths;
    private readonly LocalBackup _backup;
    private readonly ILogger<VrcxSyncService> _logger;

    public string ToolKey => Key;

    public VrcxSyncService(VrcxPaths? paths = null, LocalBackup? backup = null, ILogger<VrcxSyncService>? logger = null)
    {
        _paths = paths ?? VrcxPaths.Default();
        _backup = backup ?? new LocalBackup();
        _logger = logger ?? NullLogger<VrcxSyncService>.Instance;
    }

    public SyncResult Push(PushOptions options)
    {
        ProcessGuard.EnsureNotRunning(ProcessGuard.VrcxProcessNames);

        if (!_paths.Exists())
        {
            return new SyncResult
            {
                Outcome = SyncOutcome.SourceMissing,
                Message = $"VRCX のデータが見つかりません: {_paths.SqliteFile}",
            };
        }

        var manifestStore = new ManifestStore(options.CloudFolderPath);
        var manifest = manifestStore.Load();

        if (!options.ForceOverwriteOnConflict
            && manifest.Tools.TryGetValue(Key, out var existing)
            && existing.Version > (options.LastPulledVersion ?? 0))
        {
            _logger.LogInformation(
                "VRCX Push 中止: リモートの方が新しい (remote={Remote}, lastPulled={LastPulled})",
                existing.Version, options.LastPulledVersion);
            return new SyncResult
            {
                Outcome = SyncOutcome.ConflictDetected,
                RemoteVersion = existing.Version,
                LastPulledVersion = options.LastPulledVersion,
                Message = "リモートにより新しい VRCX データがあります",
            };
        }

        var toolFolder = Path.Combine(options.CloudFolderPath, SubFolder);
        Directory.CreateDirectory(toolFolder);

        var snapshotDest = Path.Combine(toolFolder, SnapshotFileName);
        var snapshotTmp = snapshotDest + ".building";
        SqliteSnapshot.Create(_paths.SqliteFile, snapshotTmp);

        try
        {
            if (File.Exists(snapshotDest))
            {
                File.Replace(snapshotTmp, snapshotDest, destinationBackupFileName: null);
            }
            else
            {
                File.Move(snapshotTmp, snapshotDest);
            }
        }
        catch
        {
            if (File.Exists(snapshotTmp))
            {
                try { File.Delete(snapshotTmp); } catch { /* best-effort */ }
            }
            throw;
        }

        var affected = new List<string> { snapshotDest };

        if (File.Exists(_paths.SettingsJsonFile))
        {
            var settingsDest = Path.Combine(toolFolder, SettingsFileName);
            AtomicFile.Copy(_paths.SettingsJsonFile, settingsDest, overwrite: true);
            affected.Add(settingsDest);
        }

        var nextVersion = (manifest.Tools.TryGetValue(Key, out var prev) ? prev.Version : 0) + 1;
        manifest.Tools[Key] = new ToolManifestEntry
        {
            Version = nextVersion,
            MachineName = options.MachineName,
            UpdatedAt = DateTimeOffset.Now,
            Files = BuildManifestFiles(affected, options.CloudFolderPath),
        };
        manifestStore.Save(manifest);

        _logger.LogInformation("VRCX Push 完了 version={Version} files={Count}", nextVersion, affected.Count);
        return new SyncResult
        {
            Outcome = SyncOutcome.Success,
            RemoteVersion = nextVersion,
            AffectedFiles = affected,
        };
    }

    public SyncResult Pull(PullOptions options)
    {
        ProcessGuard.EnsureNotRunning(ProcessGuard.VrcxProcessNames);

        var manifestStore = new ManifestStore(options.CloudFolderPath);
        var manifest = manifestStore.Load();
        if (!manifest.Tools.TryGetValue(Key, out var entry))
        {
            return new SyncResult
            {
                Outcome = SyncOutcome.NothingToDo,
                Message = "クラウド側に VRCX のデータがありません",
            };
        }

        var toolFolder = Path.Combine(options.CloudFolderPath, SubFolder);
        var remoteSnapshot = Path.Combine(toolFolder, SnapshotFileName);
        var remoteSettings = Path.Combine(toolFolder, SettingsFileName);
        if (!File.Exists(remoteSnapshot))
        {
            return new SyncResult
            {
                Outcome = SyncOutcome.SourceMissing,
                Message = $"クラウド側にスナップショットがありません: {remoteSnapshot}",
            };
        }

        Directory.CreateDirectory(_paths.RootDirectory);

        string? backupPath = null;
        if (!options.SkipBackup)
        {
            var filesToBackup = new List<string> { _paths.SqliteFile };
            if (File.Exists(_paths.SettingsJsonFile)) filesToBackup.Add(_paths.SettingsJsonFile);
            backupPath = _backup.CreateSnapshot(Key, filesToBackup);

            DeleteIfExists(_paths.RootDirectory, "VRCX.sqlite3-shm");
            DeleteIfExists(_paths.RootDirectory, "VRCX.sqlite3-wal");
        }

        var affected = new List<string>();
        AtomicFile.Copy(remoteSnapshot, _paths.SqliteFile, overwrite: true);
        affected.Add(_paths.SqliteFile);

        if (File.Exists(remoteSettings))
        {
            AtomicFile.Copy(remoteSettings, _paths.SettingsJsonFile, overwrite: true);
            affected.Add(_paths.SettingsJsonFile);
        }

        _logger.LogInformation("VRCX Pull 完了 version={Version} backup={Backup}", entry.Version, backupPath ?? "(none)");
        return new SyncResult
        {
            Outcome = SyncOutcome.Success,
            RemoteVersion = entry.Version,
            BackupPath = backupPath,
            AffectedFiles = affected,
        };
    }

    private static void DeleteIfExists(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }

    private static List<ManifestFile> BuildManifestFiles(IEnumerable<string> paths, string cloudFolder)
    {
        var list = new List<ManifestFile>();
        foreach (var p in paths)
        {
            var info = new FileInfo(p);
            list.Add(new ManifestFile
            {
                RelativePath = Path.GetRelativePath(cloudFolder, p).Replace('\\', '/'),
                Size = info.Length,
                Sha256 = FileHasher.Sha256(p),
            });
        }
        return list;
    }
}
