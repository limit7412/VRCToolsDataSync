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
        // 別プロセスの Push と一時ファイル名が衝突しないよう GUID を含める。
        var snapshotTmp = snapshotDest + ".building-" + Guid.NewGuid().ToString("N");
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

        var settingsDest = Path.Combine(toolFolder, SettingsFileName);
        if (File.Exists(_paths.SettingsJsonFile))
        {
            AtomicFile.Copy(_paths.SettingsJsonFile, settingsDest, overwrite: true);
            affected.Add(settingsDest);
        }
        else if (File.Exists(settingsDest))
        {
            // ローカルから消えた任意ファイルはクラウドからも削除する。
            // ここで握りつぶすと Pull 側はリモートの実ファイル存在を見るため、
            // 古い latest.json が他 PC へ復元され、削除した設定が復活する。
            // 失敗した場合は Push 全体を失敗扱いにして上位に伝える。
            File.Delete(settingsDest);
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

        // Issue #19: 起動時自動 Pull の暴走防止。
        // ローカルの LastPulledVersion がリモートの Version 以上なら、
        // 「ローカルが新しいかリモートと同じ」なので Pull で上書きしない。
        // SkipIfNotNewer は呼び出し側 (StartupSyncOrchestrator) で true にする想定。
        if (options.SkipIfNotNewer
            && options.LastPulledVersion is long lastPulled
            && entry.Version <= lastPulled)
        {
            _logger.LogInformation(
                "VRCX Pull スキップ: ローカルが最新 (remote={Remote}, lastPulled={LastPulled})",
                entry.Version, lastPulled);
            return new SyncResult
            {
                Outcome = SyncOutcome.NothingToDo,
                RemoteVersion = entry.Version,
                LastPulledVersion = lastPulled,
                Message = "ローカルが最新のため Pull スキップ",
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
        }

        // WAL/SHM の掃除はバックアップ有無に関わらず必ず実行する。
        // 残しておくと新しい本体 DB に対して古い WAL が適用されて
        // データが破損するため、--no-backup でも飛ばさない。
        DeleteIfExists(_paths.RootDirectory, "VRCX.sqlite3-shm");
        DeleteIfExists(_paths.RootDirectory, "VRCX.sqlite3-wal");

        var affected = new List<string>();
        AtomicFile.Copy(remoteSnapshot, _paths.SqliteFile, overwrite: true);
        affected.Add(_paths.SqliteFile);

        if (File.Exists(remoteSettings))
        {
            AtomicFile.Copy(remoteSettings, _paths.SettingsJsonFile, overwrite: true);
            affected.Add(_paths.SettingsJsonFile);
        }
        else if (File.Exists(_paths.SettingsJsonFile))
        {
            // リモートに latest.json がなくなったときはローカルも削除して状態を
            // 揃える。握りつぶすと Push 側で削除済み判定との対称性が崩れて、
            // 古い VRCX.json が次の Push で manifest に再登録されてしまうため、
            // 失敗は呼び出し側に伝播させる。
            File.Delete(_paths.SettingsJsonFile);
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
            // WAL/SHM 削除失敗を握りつぶすと、直後にリモート DB で本体ファイルを
            // 上書きしても古い WAL が新しい本体に対して再生されて Pull 内容が
            // 破損する。失敗は呼び出し側 (Pull) に例外で伝え、Aborted にする。
            File.Delete(path);
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
