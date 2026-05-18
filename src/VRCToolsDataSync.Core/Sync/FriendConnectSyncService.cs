using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCToolsDataSync.Core.Paths;

namespace VRCToolsDataSync.Core.Sync;

public sealed class FriendConnectSyncService : ISyncService
{
    public const string Key = "friend-connect";

    private const string SubFolder = "friend-connect";
    private const string DbSubFolder = "db";
    private const string DbFileName = "db.sqlite";
    private const string DbV11FileName = "db_1.1.sqlite";
    private const string ConfigFileName = "config.json";
    private const string NotesFolderName = "notes";

    private readonly FriendConnectPaths _paths;
    private readonly LocalBackup _backup;
    private readonly ILogger<FriendConnectSyncService> _logger;

    public string ToolKey => Key;

    public FriendConnectSyncService(
        FriendConnectPaths? paths = null,
        LocalBackup? backup = null,
        ILogger<FriendConnectSyncService>? logger = null)
    {
        _paths = paths ?? FriendConnectPaths.Default();
        _backup = backup ?? new LocalBackup();
        _logger = logger ?? NullLogger<FriendConnectSyncService>.Instance;
    }

    public SyncResult Push(PushOptions options)
    {
        ProcessGuard.EnsureNotRunning(ProcessGuard.FriendConnectProcessNames);

        if (!_paths.Exists() || !File.Exists(_paths.DbFile))
        {
            return new SyncResult
            {
                Outcome = SyncOutcome.SourceMissing,
                Message = $"VRC Friend Connect のデータが見つかりません: {_paths.DbFile}",
            };
        }

        var manifestStore = new ManifestStore(options.CloudFolderPath);
        var manifest = manifestStore.Load();

        if (!options.ForceOverwriteOnConflict
            && manifest.Tools.TryGetValue(Key, out var existing)
            && existing.Version > (options.LastPulledVersion ?? 0))
        {
            _logger.LogInformation(
                "Friend Connect Push 中止: リモートの方が新しい (remote={Remote}, lastPulled={LastPulled})",
                existing.Version, options.LastPulledVersion);
            return new SyncResult
            {
                Outcome = SyncOutcome.ConflictDetected,
                RemoteVersion = existing.Version,
                LastPulledVersion = options.LastPulledVersion,
                Message = "リモートにより新しい Friend Connect データがあります",
            };
        }

        var toolFolder = Path.Combine(options.CloudFolderPath, SubFolder);
        var toolDbFolder = Path.Combine(toolFolder, DbSubFolder);
        Directory.CreateDirectory(toolDbFolder);

        var affected = new List<string>();

        SnapshotSqliteTo(Path.Combine(toolDbFolder, DbFileName), _paths.DbFile, affected);

        var destDbV11 = Path.Combine(toolDbFolder, DbV11FileName);
        if (File.Exists(_paths.DbV11File))
        {
            SnapshotSqliteTo(destDbV11, _paths.DbV11File, affected);
        }
        else if (File.Exists(destDbV11))
        {
            try { File.Delete(destDbV11); } catch { /* best-effort */ }
        }

        var destConfig = Path.Combine(toolFolder, ConfigFileName);
        if (File.Exists(_paths.ConfigJsonFile))
        {
            AtomicFile.Copy(_paths.ConfigJsonFile, destConfig, overwrite: true);
            affected.Add(destConfig);
        }
        else if (File.Exists(destConfig))
        {
            try { File.Delete(destConfig); } catch { /* best-effort */ }
        }

        var destNotes = Path.Combine(toolFolder, NotesFolderName);
        if (Directory.Exists(_paths.NotesDirectory))
        {
            ReplaceDirectory(_paths.NotesDirectory, destNotes);
            foreach (var file in Directory.EnumerateFiles(destNotes, "*", SearchOption.AllDirectories))
            {
                affected.Add(file);
            }
        }
        else if (Directory.Exists(destNotes))
        {
            try { Directory.Delete(destNotes, recursive: true); } catch { /* best-effort */ }
        }

        // 保存直前に manifest を再読込し、他 tool のエントリを失わないようにマージする。
        // (別プロセス / 別 SyncService が同時に Push したケースを救済)
        var finalManifest = manifestStore.Load();
        var nextVersion = (finalManifest.Tools.TryGetValue(Key, out var prev) ? prev.Version : 0) + 1;
        finalManifest.Tools[Key] = new ToolManifestEntry
        {
            Version = nextVersion,
            MachineName = options.MachineName,
            UpdatedAt = DateTimeOffset.Now,
            Files = BuildManifestFiles(affected, options.CloudFolderPath),
        };
        manifestStore.Save(finalManifest);

        _logger.LogInformation("Friend Connect Push 完了 version={Version} files={Count}", nextVersion, affected.Count);
        return new SyncResult
        {
            Outcome = SyncOutcome.Success,
            RemoteVersion = nextVersion,
            AffectedFiles = affected,
        };
    }

    public SyncResult Pull(PullOptions options)
    {
        ProcessGuard.EnsureNotRunning(ProcessGuard.FriendConnectProcessNames);

        var manifestStore = new ManifestStore(options.CloudFolderPath);
        var manifest = manifestStore.Load();
        if (!manifest.Tools.TryGetValue(Key, out var entry))
        {
            return new SyncResult
            {
                Outcome = SyncOutcome.NothingToDo,
                Message = "クラウド側に Friend Connect のデータがありません",
            };
        }

        var toolFolder = Path.Combine(options.CloudFolderPath, SubFolder);
        var toolDbFolder = Path.Combine(toolFolder, DbSubFolder);
        var remoteDb = Path.Combine(toolDbFolder, DbFileName);
        if (!File.Exists(remoteDb))
        {
            return new SyncResult
            {
                Outcome = SyncOutcome.SourceMissing,
                Message = $"クラウド側にスナップショットがありません: {remoteDb}",
            };
        }

        Directory.CreateDirectory(_paths.RootDirectory);
        Directory.CreateDirectory(_paths.DbDirectory);

        string? backupPath = null;
        if (!options.SkipBackup)
        {
            var filesToBackup = new List<string>();
            if (File.Exists(_paths.DbFile)) filesToBackup.Add(_paths.DbFile);
            if (File.Exists(_paths.DbV11File)) filesToBackup.Add(_paths.DbV11File);
            if (File.Exists(_paths.ConfigJsonFile)) filesToBackup.Add(_paths.ConfigJsonFile);

            var dirsToBackup = new List<string>();
            if (Directory.Exists(_paths.NotesDirectory)) dirsToBackup.Add(_paths.NotesDirectory);

            backupPath = _backup.CreateSnapshot(Key, filesToBackup, dirsToBackup);
        }

        // WAL/SHM の掃除はバックアップ有無に関わらず必ず実行する。
        // 残しておくと新しい本体 DB に対して古い WAL が適用されて
        // データが破損するため、--no-backup でも飛ばさない。
        DeleteIfExists(_paths.DbDirectory, "db.sqlite-shm");
        DeleteIfExists(_paths.DbDirectory, "db.sqlite-wal");
        DeleteIfExists(_paths.DbDirectory, "db_1.1.sqlite-shm");
        DeleteIfExists(_paths.DbDirectory, "db_1.1.sqlite-wal");

        var affected = new List<string>();

        AtomicFile.Copy(remoteDb, _paths.DbFile, overwrite: true);
        affected.Add(_paths.DbFile);

        var remoteDbV11 = Path.Combine(toolDbFolder, DbV11FileName);
        if (File.Exists(remoteDbV11))
        {
            AtomicFile.Copy(remoteDbV11, _paths.DbV11File, overwrite: true);
            affected.Add(_paths.DbV11File);
        }
        else if (File.Exists(_paths.DbV11File))
        {
            try { File.Delete(_paths.DbV11File); } catch { /* best-effort */ }
        }

        var remoteConfig = Path.Combine(toolFolder, ConfigFileName);
        if (File.Exists(remoteConfig))
        {
            AtomicFile.Copy(remoteConfig, _paths.ConfigJsonFile, overwrite: true);
            affected.Add(_paths.ConfigJsonFile);
        }
        else if (File.Exists(_paths.ConfigJsonFile))
        {
            try { File.Delete(_paths.ConfigJsonFile); } catch { /* best-effort */ }
        }

        var remoteNotes = Path.Combine(toolFolder, NotesFolderName);
        if (Directory.Exists(remoteNotes))
        {
            ReplaceDirectory(remoteNotes, _paths.NotesDirectory);
            foreach (var file in Directory.EnumerateFiles(_paths.NotesDirectory, "*", SearchOption.AllDirectories))
            {
                affected.Add(file);
            }
        }
        else if (Directory.Exists(_paths.NotesDirectory))
        {
            try { Directory.Delete(_paths.NotesDirectory, recursive: true); } catch { /* best-effort */ }
        }

        _logger.LogInformation("Friend Connect Pull 完了 version={Version} backup={Backup}",
            entry.Version, backupPath ?? "(none)");
        return new SyncResult
        {
            Outcome = SyncOutcome.Success,
            RemoteVersion = entry.Version,
            BackupPath = backupPath,
            AffectedFiles = affected,
        };
    }

    private static void SnapshotSqliteTo(string destination, string sourceDb, List<string> affected)
    {
        var tmp = destination + ".building";
        SqliteSnapshot.Create(sourceDb, tmp);
        try
        {
            if (File.Exists(destination))
            {
                File.Replace(tmp, destination, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, destination);
            }
        }
        catch
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { /* best-effort */ }
            }
            throw;
        }
        affected.Add(destination);
    }

    private static void ReplaceDirectory(string source, string destination)
    {
        if (Directory.Exists(destination))
        {
            try { Directory.Delete(destination, recursive: true); }
            catch { /* best-effort */ }
        }
        AtomicFile.CopyDirectory(source, destination, overwrite: true);
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
