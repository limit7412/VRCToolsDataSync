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
            if (SyncTransfer.CanSkipUpload(storage, remoteFiles, config))
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

        var noteFiles = PushNotes(storage, remoteFiles, affected);
        files.AddRange(noteFiles);

        if (SyncTransfer.IsUnchangedSet(existing, files))
        {
            // 送るものが何も無いなら manifest も触らない。version を進めると
            // 他 PC の LastPulledVersion が古くなり、中身が同じデータの
            // ダウンロードを誘発してしまう。
            // ここでは同期先の note を消さない。この経路は manifest の version 検査を
            // 通っていないため、Load してから今までに他 PC が追加した note を
            // 消してしまう恐れがある。孤児の掃除は次の実際の Push に任せる。
            _logger.LogInformation("Friend Connect Push: 変更なし version={Version}", existing!.Version);
            return new SyncResult
            {
                Outcome = SyncOutcome.Success,
                RemoteVersion = existing.Version,
                Message = "前回の Push から変更がないため、同期先はそのままです",
            };
        }

        long nextVersion;
        try
        {
            nextVersion = manifestStore.UpdateToolEntry(Key, existing?.Version ?? 0, version => new ToolManifestEntry
            {
                Version = version,
                MachineName = options.MachineName,
                UpdatedAt = DateTimeOffset.Now,
                Files = files,
            });
        }
        catch (ToolEntryChangedException ex)
        {
            // 送信の可否を判断してから manifest を保存するまでの間に、他の PC が
            // 同じ tool を Push した。ここで押し切ると、相手が上書きしたオブジェクトに
            // 対して「内容が同じだから送らない」と判断した記録を残すことになり、
            // manifest と実データがずれる。
            //
            // なお、この時点で既にファイルは送信済みなので、同期先の実データと
            // manifest の記録がずれた状態になりうる。その状態は Pull 側の
            // ハッシュ検証で検出され、次に Push が通れば解消する (#27)。
            _logger.LogInformation(
                "Friend Connect Push 中止: Push 中に同期先が更新された (expected={Expected}, actual={Actual})",
                ex.ExpectedVersion, ex.ActualVersion);
            return new SyncResult
            {
                Outcome = SyncOutcome.ConflictDetected,
                RemoteVersion = ex.ActualVersion,
                LastPulledVersion = options.LastPulledVersion,
                Message = "Push の途中で他の PC が同期先を更新しました。" +
                          "同期先のファイルと manifest がずれている可能性があるため、" +
                          "この PC のデータで上書きしてよければ強制 Push、" +
                          "相手のデータを採用するなら Pull し直してから Push してください。",
            };
        }

        // manifest が新しい集合を指してから、参照されなくなったオブジェクトを消す。
        // 逆順にすると、manifest 更新が競合で失敗したときに
        // 「manifest はまだ参照しているのに実体が無い」状態が残る。
        RemoveStaleRemoteNotes(manifestStore, storage, remoteFiles, noteFiles);

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

        // (1) 同期先から取り出す。ここではローカルの既存ファイルに触れないので、
        //     途中で失敗しても手元のデータは元のまま残る。
        using var staging = new PullStaging(_logger);

        if (!staging.Fetch(storage, remoteDb, _paths.DbFile))
        {
            return new SyncResult
            {
                Outcome = SyncOutcome.SourceMissing,
                Message = $"クラウド側にスナップショットがありません: {DbKey}",
            };
        }

        remoteFiles.TryGetValue(DbV11Key, out var remoteDbV11);
        remoteFiles.TryGetValue(ConfigKey, out var remoteConfig);
        var remoteNotes = entry.Files
            .Where(f => f.RelativePath.StartsWith(NotesKeyPrefix, StringComparison.Ordinal))
            .ToList();

        // manifest が要求するファイルはすべて揃わなければ Pull を成功にしない。
        // 欠けたまま成功にすると LastPulledVersion だけ進み、以後の起動時 Pull は
        // この version を最新とみなして省略するため、差異が固定される。
        var missing = FetchOptional(staging, storage, remoteDbV11, _paths.DbV11File)
            ?? FetchOptional(staging, storage, remoteConfig, _paths.ConfigJsonFile)
            ?? FetchNotes(staging, storage, remoteNotes);
        if (missing is not null)
        {
            return new SyncResult
            {
                Outcome = SyncOutcome.SourceMissing,
                Message = $"クラウド側にファイルがありません: {missing}",
            };
        }

        // (2) 取り出した内容が manifest の記録と合っているか確かめる。
        if (staging.FindMismatchedKey() is { } mismatched)
        {
            return new SyncResult
            {
                Outcome = SyncOutcome.Aborted,
                Message = $"取得したファイルの内容が manifest の記録と一致しません: {mismatched}。" +
                          "同期先のファイルと manifest がずれています。" +
                          "正しいデータを持つ PC から Push し直すと解消します。",
            };
        }

        // (3) ここから先はローカルを書き換える。
        string? backupPath = null;
        if (!options.SkipBackup)
        {
            // WAL/SHM もバックアップに含める。この後で消すので、含めておかないと
            // 本体へ未反映のローカル変更が復元できなくなる。
            var filesToBackup = new List<string>
            {
                _paths.DbFile,
                _paths.DbV11File,
                _paths.ConfigJsonFile,
                Path.Combine(_paths.DbDirectory, "db.sqlite-wal"),
                Path.Combine(_paths.DbDirectory, "db.sqlite-shm"),
                Path.Combine(_paths.DbDirectory, "db_1.1.sqlite-wal"),
                Path.Combine(_paths.DbDirectory, "db_1.1.sqlite-shm"),
            };

            var dirsToBackup = new List<string>();
            if (Directory.Exists(_paths.NotesDirectory)) dirsToBackup.Add(_paths.NotesDirectory);

            // notes の退避からは取り出し中のファイルを外す。バックアップは
            // 取り出しの後に取るので、除外しないとリモート版の複製が一時名のまま
            // 毎回混ざり、その世代を復元すると実データとして残ってしまう。
            backupPath = _backup.CreateSnapshot(
                Key, filesToBackup, dirsToBackup,
                shouldBackupFile: path => !PullStaging.IsIncomingFile(path));
        }

        // WAL/SHM を消すのは、その DB の本体を差し替えるか削除するとき。残したままだと
        // 古い WAL が別内容の本体に対して再生されてデータが破損するため、
        // --no-backup でも飛ばさない。
        //
        // 明示的な Pull はリモートの内容へ戻す操作なので、本体を差し替えない場合でも
        // 消す (手元の未反映分は破棄される。バックアップには含めてある)。
        // 起動時の自動 Pull (SkipIfNotNewer) では消さない。差し替えるものが無いのに
        // 未反映のローカル変更を捨てることになるため。
        var explicitPull = !options.SkipIfNotNewer;
        if (staging.IsStaged(_paths.DbFile) || explicitPull)
        {
            DeleteIfExists(_paths.DbDirectory, "db.sqlite-shm");
            DeleteIfExists(_paths.DbDirectory, "db.sqlite-wal");
        }
        // db_1.1 はリモートから消えた場合に本体だけ削除する経路がある。
        // そのときも WAL を残すと、次に Friend Connect が本体を作り直したときに
        // 別 DB の WAL が再生されうる。
        if (staging.IsStaged(_paths.DbV11File) || explicitPull || remoteDbV11 is null)
        {
            DeleteIfExists(_paths.DbDirectory, "db_1.1.sqlite-shm");
            DeleteIfExists(_paths.DbDirectory, "db_1.1.sqlite-wal");
        }

        var affected = new List<string>();
        staging.Apply(affected);

        // リモートにない任意ファイルはローカルも削除して状態を揃える。
        // 削除失敗を握りつぶすと、次の Push で古いファイルが manifest に
        // 再登録されてしまうため、失敗は呼び出し側に伝播させる。
        if (remoteDbV11 is null && File.Exists(_paths.DbV11File))
        {
            File.Delete(_paths.DbV11File);
        }
        if (remoteConfig is null && File.Exists(_paths.ConfigJsonFile))
        {
            File.Delete(_paths.ConfigJsonFile);
        }
        RemoveStaleNotes(remoteNotes);

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
        if (SyncTransfer.CanSkipUpload(storage, remoteFiles, described))
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
    /// notes フォルダを送る。ローカルから消えたファイルの後始末は
    /// <see cref="RemoveStaleRemoteNotes"/> が manifest の更新後に行う。
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
                // 中断した Pull が残した取り出し中のファイルは同期対象ではない。
                if (PullStaging.IsIncomingFile(localPath)) continue;
                var relative = StorageKey.FromRelativePath(
                    Path.GetRelativePath(_paths.NotesDirectory, localPath));
                var key = NotesKeyPrefix + relative;
                var described = SyncTransfer.Describe(localPath, key);
                if (SyncTransfer.CanSkipUpload(storage, remoteFiles, described))
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

        return files;
    }

    /// <summary>
    /// ローカルに無くなった note を同期先から取り除く。manifest の更新が
    /// 成功してから呼ぶこと。先に消すと、manifest 更新が競合で失敗したときに
    /// 「manifest はまだ参照しているのに実体が無い」状態が残る。
    /// <para>
    /// 削除対象は「今回の Push の基準にした manifest が参照していた note」から、
    /// 今回も残るものを除いた分に限る。同期先を列挙して差分を取ると、基準にした
    /// manifest を読んだ後に他の PC が上げた note まで消してしまい、相手の
    /// manifest が実体の無いオブジェクトを指すことになる。
    /// </para>
    /// <para>
    /// さらに削除の直前に manifest を読み直し、その時点で参照されているキーは残す。
    /// 自分が manifest を更新してから削除するまでの間に、他の PC が同じ note を
    /// 上げ直して公開している場合があるため。読み直しと削除の間の窓は残るので、
    /// 完全に断つにはオブジェクトを version 固有のキーにする必要がある (#27)。
    /// </para>
    /// <para>
    /// この方法では、中断した Push が残した孤児オブジェクト (どの manifest からも
    /// 参照されていないもの) は残る。回収も #27 に委ねる。
    /// </para>
    /// </summary>
    private void RemoveStaleRemoteNotes(
        ManifestStore manifestStore,
        ISyncStorage storage,
        IReadOnlyList<ManifestFile> previousFiles,
        IReadOnlyList<ManifestFile> keptNotes)
    {
        var candidates = previousFiles
            .Where(f => f.RelativePath.StartsWith(NotesKeyPrefix, StringComparison.Ordinal))
            .Select(f => f.RelativePath)
            .Except(keptNotes.Select(f => f.RelativePath), StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0) return;

        var live = new HashSet<string>(StringComparer.Ordinal);
        if (manifestStore.Load().Tools.TryGetValue(Key, out var current))
        {
            foreach (var file in current.Files) live.Add(file.RelativePath);
        }

        foreach (var key in candidates)
        {
            if (live.Contains(key))
            {
                _logger.LogInformation("削除を見送り (他の PC が復活させた): {Key}", key);
                continue;
            }
            storage.Delete(key);
            _logger.LogInformation("同期先から削除: {Key}", key);
        }
    }

    /// <summary>
    /// 任意ファイルを取り出す。manifest に載っていなければ何もしない。
    /// 載っているのに同期先で見つからない場合は、そのキーを返して呼び出し側に伝える。
    /// </summary>
    private static string? FetchOptional(
        PullStaging staging,
        ISyncStorage storage,
        ManifestFile? remote,
        string targetPath)
    {
        if (remote is null) return null;
        return staging.Fetch(storage, remote, targetPath) ? null : remote.RelativePath;
    }

    /// <summary>notes を取り出す。見つからないキーがあればその名前を返す。</summary>
    private string? FetchNotes(PullStaging staging, ISyncStorage storage, IReadOnlyList<ManifestFile> notes)
    {
        if (notes.Count == 0) return null;

        Directory.CreateDirectory(_paths.NotesDirectory);
        foreach (var note in notes)
        {
            if (!staging.Fetch(storage, note, ToLocalNotePath(note))) return note.RelativePath;
        }
        return null;
    }

    /// <summary>リモートから消えた note をローカルからも取り除く。</summary>
    private void RemoveStaleNotes(IReadOnlyList<ManifestFile> notes)
    {
        if (notes.Count == 0)
        {
            if (Directory.Exists(_paths.NotesDirectory))
            {
                Directory.Delete(_paths.NotesDirectory, recursive: true);
            }
            return;
        }

        if (!Directory.Exists(_paths.NotesDirectory)) return;

        var keep = new HashSet<string>(
            notes.Select(n => Path.GetFullPath(ToLocalNotePath(n))),
            StringComparer.OrdinalIgnoreCase);

        // 握りつぶすと次の Push で古い note が manifest に再登録されてしまう。
        // 列挙しながら削除すると取りこぼしうるので、先に一覧を確定させる。
        var localNotes = Directory
            .EnumerateFiles(_paths.NotesDirectory, "*", SearchOption.AllDirectories)
            .ToList();
        foreach (var localPath in localNotes)
        {
            if (keep.Contains(Path.GetFullPath(localPath))) continue;
            File.Delete(localPath);
        }
    }

    /// <summary>
    /// note のキーをローカルパスへ写す。manifest は他 PC が書いたものなので、
    /// <see cref="StorageKey.ToLocalPath"/> がキーの形を検証し、
    /// notes フォルダの外へ書き出させない。
    /// </summary>
    private string ToLocalNotePath(ManifestFile note)
        => StorageKey.ToLocalPath(_paths.NotesDirectory, note.RelativePath[NotesKeyPrefix.Length..]);

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
