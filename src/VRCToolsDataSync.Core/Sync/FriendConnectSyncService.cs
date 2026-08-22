using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCToolsDataSync.Core.Paths;
using VRCToolsDataSync.Core.Storage;

namespace VRCToolsDataSync.Core.Sync;

public sealed class FriendConnectSyncService : ISyncService
{
    public const string Key = "friend-connect";

    private const string DbKey = "friend-connect/db/db.sqlite";
    private const string DbV11Key = "friend-connect/db/db_1.1.sqlite";
    private const string ConfigKey = "friend-connect/config.json";
    private const string NotesKeyPrefix = "friend-connect/notes/";

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

        var storage = options.Storage;
        var manifestStore = new ManifestStore(storage);
        var manifest = manifestStore.Load();
        manifest.Tools.TryGetValue(Key, out var existing);

        if (!options.ForceOverwriteOnConflict
            && existing is not null
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

        var remoteFiles = existing?.Files ?? new List<ManifestFile>();
        var files = new List<ManifestFile>();
        var affected = new List<string>();

        files.Add(PushSqlite(storage, _paths.DbFile, DbKey, remoteFiles, affected));

        if (File.Exists(_paths.DbV11File))
        {
            files.Add(PushSqlite(storage, _paths.DbV11File, DbV11Key, remoteFiles, affected));
        }
        else
        {
            // 削除失敗を握りつぶすと Pull 側でリモートの古いファイルが
            // 復元されて削除済みデータが復活するため、失敗時は Push を
            // 失敗扱いにする。
            storage.Delete(DbV11Key);
        }

        if (File.Exists(_paths.ConfigJsonFile))
        {
            var config = SyncTransfer.Describe(_paths.ConfigJsonFile, ConfigKey);
            if (SyncTransfer.IsAlreadyOnRemote(remoteFiles, config))
            {
                _logger.LogInformation("Friend Connect config.json の送信を省略 (内容が同じ)");
            }
            else
            {
                storage.Upload(_paths.ConfigJsonFile, ConfigKey);
                affected.Add(ConfigKey);
            }
            files.Add(config);
        }
        else
        {
            storage.Delete(ConfigKey);
        }

        files.AddRange(PushNotes(storage, remoteFiles, affected));

        if (SyncTransfer.IsUnchangedSet(existing, files))
        {
            // 送るものが何も無いなら manifest も触らない。version を進めると
            // 他 PC の LastPulledVersion が古くなり、中身が同じデータの
            // ダウンロードを誘発してしまう。
            _logger.LogInformation("Friend Connect Push: 変更なし version={Version}", existing!.Version);
            return new SyncResult
            {
                Outcome = SyncOutcome.Success,
                RemoteVersion = existing.Version,
                Message = "前回の Push から変更がないため、同期先はそのままです",
            };
        }

        var nextVersion = manifestStore.UpdateToolEntry(Key, version => new ToolManifestEntry
        {
            Version = version,
            MachineName = options.MachineName,
            UpdatedAt = DateTimeOffset.Now,
            Files = files,
        });

        _logger.LogInformation("Friend Connect Push 完了 version={Version} files={Count}", nextVersion, files.Count);
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

        var storage = options.Storage;
        var manifest = new ManifestStore(storage).Load();
        if (!manifest.Tools.TryGetValue(Key, out var entry))
        {
            return new SyncResult
            {
                Outcome = SyncOutcome.NothingToDo,
                Message = "クラウド側に Friend Connect のデータがありません",
            };
        }

        // Issue #19: 起動時自動 Pull の暴走防止。VRCX 側と同じ判定。
        // ローカル必須ファイル (db.sqlite) が消えているケースでは skip せず
        // 通常 Pull に進めて復元する (#20 レビュー指摘)。Push 側のガードと同じく
        // _paths.Exists() (Root のみ) では不十分なので DbFile の存在もチェック。
        if (options.SkipIfNotNewer
            && options.LastPulledVersion is long lastPulled
            && entry.Version <= lastPulled
            && _paths.Exists()
            && File.Exists(_paths.DbFile))
        {
            _logger.LogInformation(
                "Friend Connect Pull スキップ: ローカルが最新 (remote={Remote}, lastPulled={LastPulled})",
                entry.Version, lastPulled);
            return new SyncResult
            {
                Outcome = SyncOutcome.NothingToDo,
                RemoteVersion = entry.Version,
                LastPulledVersion = lastPulled,
                Message = "ローカルが最新のため Pull スキップ",
            };
        }

        var remoteFiles = entry.Files.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        if (!remoteFiles.TryGetValue(DbKey, out var remoteDb))
        {
            return new SyncResult
            {
                Outcome = SyncOutcome.SourceMissing,
                Message = $"クラウド側にスナップショットがありません: {DbKey}",
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

        if (!SyncTransfer.Restore(storage, remoteDb, _paths.DbFile, affected, _logger))
        {
            return new SyncResult
            {
                Outcome = SyncOutcome.SourceMissing,
                Message = $"クラウド側にスナップショットがありません: {DbKey}",
            };
        }

        if (remoteFiles.TryGetValue(DbV11Key, out var remoteDbV11))
        {
            SyncTransfer.Restore(storage, remoteDbV11, _paths.DbV11File, affected, _logger);
        }
        else if (File.Exists(_paths.DbV11File))
        {
            // リモートにない任意ファイルはローカルも削除して状態を揃える。
            // 削除失敗を握りつぶすと、次の Push で古いファイルが manifest に
            // 再登録されてしまうため、失敗は呼び出し側に伝播させる。
            File.Delete(_paths.DbV11File);
        }

        if (remoteFiles.TryGetValue(ConfigKey, out var remoteConfig))
        {
            SyncTransfer.Restore(storage, remoteConfig, _paths.ConfigJsonFile, affected, _logger);
        }
        else if (File.Exists(_paths.ConfigJsonFile))
        {
            File.Delete(_paths.ConfigJsonFile);
        }

        PullNotes(storage, entry.Files, affected);

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

    /// <summary>SQLite を WAL 統合済みのスナップショットにして送る。</summary>
    private ManifestFile PushSqlite(
        ISyncStorage storage,
        string sourceDb,
        string key,
        IReadOnlyList<ManifestFile> remoteFiles,
        List<string> affected)
    {
        using var staged = storage.BeginUpload(key);
        SqliteSnapshot.Create(sourceDb, staged.LocalPath);
        var described = SyncTransfer.Describe(staged.LocalPath, key);
        if (SyncTransfer.IsAlreadyOnRemote(remoteFiles, described))
        {
            _logger.LogInformation("送信を省略 (内容が同じ): {Key}", key);
        }
        else
        {
            staged.Commit();
            affected.Add(key);
        }
        return described;
    }

    /// <summary>
    /// notes フォルダを送る。ローカルに無くなったファイルは同期先からも消す。
    /// 削除対象は同期先の列挙から求める。manifest だけを見ると、過去に中断した
    /// Push が残した孤児オブジェクトを取りこぼす。
    /// </summary>
    private List<ManifestFile> PushNotes(
        ISyncStorage storage,
        IReadOnlyList<ManifestFile> remoteFiles,
        List<string> affected)
    {
        var files = new List<ManifestFile>();

        if (Directory.Exists(_paths.NotesDirectory))
        {
            foreach (var localPath in Directory.EnumerateFiles(_paths.NotesDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = StorageKey.FromRelativePath(
                    Path.GetRelativePath(_paths.NotesDirectory, localPath));
                var key = NotesKeyPrefix + relative;
                var described = SyncTransfer.Describe(localPath, key);
                if (SyncTransfer.IsAlreadyOnRemote(remoteFiles, described))
                {
                    _logger.LogInformation("送信を省略 (内容が同じ): {Key}", key);
                }
                else
                {
                    storage.Upload(localPath, key);
                    affected.Add(key);
                }
                files.Add(described);
            }
        }

        var keep = new HashSet<string>(files.Select(f => f.RelativePath), StringComparer.Ordinal);
        foreach (var remoteKey in storage.List(NotesKeyPrefix))
        {
            if (keep.Contains(remoteKey)) continue;
            storage.Delete(remoteKey);
            _logger.LogInformation("同期先から削除: {Key}", remoteKey);
        }

        return files;
    }

    /// <summary>notes フォルダを取り出す。リモートに無いファイルはローカルからも消す。</summary>
    private void PullNotes(ISyncStorage storage, IReadOnlyList<ManifestFile> remoteFiles, List<string> affected)
    {
        var notes = remoteFiles
            .Where(f => f.RelativePath.StartsWith(NotesKeyPrefix, StringComparison.Ordinal))
            .ToList();

        if (notes.Count == 0)
        {
            if (Directory.Exists(_paths.NotesDirectory))
            {
                Directory.Delete(_paths.NotesDirectory, recursive: true);
            }
            return;
        }

        Directory.CreateDirectory(_paths.NotesDirectory);

        var restored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var note in notes)
        {
            // manifest は他 PC が書いたものなので、キーの形を検証してから
            // ローカルパスに写す。notes フォルダの外へ書き出させない。
            var localPath = StorageKey.ToLocalPath(
                _paths.NotesDirectory, note.RelativePath[NotesKeyPrefix.Length..]);
            SyncTransfer.Restore(storage, note, localPath, affected, _logger);
            restored.Add(Path.GetFullPath(localPath));
        }

        // リモートから消えた note はローカルからも消して状態を揃える。
        // 握りつぶすと次の Push で古い note が manifest に再登録されてしまう。
        foreach (var localPath in Directory.EnumerateFiles(_paths.NotesDirectory, "*", SearchOption.AllDirectories))
        {
            if (restored.Contains(Path.GetFullPath(localPath))) continue;
            File.Delete(localPath);
        }
    }

    private static void DeleteIfExists(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        if (File.Exists(path))
        {
            // VRCX 側と同様: WAL/SHM 削除失敗を握りつぶすと、リモート DB を
            // 上書きしても古い WAL が新しい本体 DB に再生される事故が起きるため、
            // 失敗は呼び出し側 (Pull) に伝播させて Aborted にする。
            File.Delete(path);
        }
    }
}
