using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCToolsDataSync.Core.Domain;
using VRCToolsDataSync.Core.Infra;
using VRCToolsDataSync.Core.Merge;

namespace VRCToolsDataSync.Core.UseCase;

public sealed class FriendConnectSyncService : ISyncService
{
    public const string Key = "friend-connect";

    private const string DbKey = "friend-connect/db/db.sqlite";
    private const string DbV11Key = "friend-connect/db/db_1.1.sqlite";
    private const string ConfigKey = "friend-connect/config.json";
    private const string NotesKeyPrefix = "friend-connect/notes/";
    private const string ThumbnailsKeyPrefix = "friend-connect/thumbnails/";

    private readonly FriendConnectPaths _paths;
    private readonly LocalBackup _backup;
    private readonly ILogger<FriendConnectSyncService> _logger;

    private const string DisplayName = "Friend Connect";

    public string ToolKey => Key;

    public TrackedDatabase TrackedDatabase => new(_paths.DbV11File, MergeProfiles.FriendConnect);

    public FriendConnectSyncService(
        FriendConnectPaths? paths = null,
        LocalBackup? backup = null,
        ILogger<FriendConnectSyncService>? logger = null)
    {
        _paths = paths ?? FriendConnectPaths.Default();
        _backup = backup ?? new LocalBackup();
        _logger = logger ?? NullLogger<FriendConnectSyncService>.Instance;
        _directories = new[]
        {
            new SyncedDirectory(_paths.NotesDirectory, NotesKeyPrefix),
            new SyncedDirectory(_paths.ThumbnailsDirectory, ThumbnailsKeyPrefix),
        };
    }

    /// <summary>
    /// フォルダごと同期するもの。中のファイルを 1 件ずつ manifest に載せる。
    /// </summary>
    private sealed record SyncedDirectory(string LocalDirectory, string KeyPrefix);

    private readonly IReadOnlyList<SyncedDirectory> _directories;

    public SyncResult Push(PushOptions options)
    {
        ProcessGuard.EnsureNotRunning(ProcessGuard.FriendConnectProcessNames);

        // 必須にするのは現行の本体 DB だけ。旧版の db.sqlite は古くから使っている PC に
        // しか無いので、これを必須にすると新しく入れた PC から Push できない。
        if (!_paths.Exists() || !File.Exists(_paths.DbV11File))
        {
            return new SyncResult
            {
                Outcome = SyncOutcome.SourceMissing,
                Message = $"VRC Friend Connect のデータが見つかりません: {_paths.DbV11File}",
            };
        }

        var storage = options.Storage;
        var manifestStore = new ManifestStore(storage);
        var manifest = manifestStore.Load();
        manifest.Tools.TryGetValue(Key, out var existing);

        // 非常用の強制 Push は記録を一切信用しないので、全ファイルを置き換えて送る。
        var replace = options.ReuploadAll;

        if (!options.ForceOverwriteOnConflict
            && !replace
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

        files.Add(PushSqlite(storage, _paths.DbV11File, DbV11Key, remoteFiles, affected, replace, out var pushedSummary));

        if (File.Exists(_paths.DbFile))
        {
            files.Add(PushSqlite(storage, _paths.DbFile, DbKey, remoteFiles, affected, replace, out _));
        }
        // ローカルに無いファイルは manifest の files[] に載せないことで「無い」ことを
        // 表す。Pull は manifest を正としてローカルへ反映するので、削除はそれで伝わる。
        // 実データはここでは消さない (理由は下の note の後始末と同じ)。

        if (File.Exists(_paths.ConfigJsonFile))
        {
            var (config, sent) = SyncTransfer.Send(
                storage, remoteFiles, _paths.ConfigJsonFile, ConfigKey, replace);
            if (sent)
            {
                affected.Add(ConfigKey);
            }
            else
            {
                _logger.LogInformation("Friend Connect config.json の送信を省略 (内容が同じ)");
            }
            files.Add(config);
        }

        foreach (var directory in _directories)
        {
            files.AddRange(PushDirectory(storage, directory, remoteFiles, affected, replace));
        }

        // 非常用の強制 Push では、内容が同じでも version を進める。他の PC が壊れた実体を
        // 取りに行って失敗した後でも、新しい version を見れば取り直しに来る。
        if (!replace && SyncTransfer.IsUnchangedSet(existing, files))
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
                SyncedSummary = pushedSummary,
            };
        }

        long nextVersion;
        try
        {
            ToolManifestEntry BuildEntry(long version) => new()
            {
                Version = version,
                MachineName = options.MachineName,
                UpdatedAt = DateTimeOffset.Now,
                Files = files,
            };
            // 非常用の強制 Push は全ファイルを置き換え済みなので、途中で他の PC が
            // 更新していても manifest と実データはずれない。競合で止めずに上書きする。
            nextVersion = replace
                ? manifestStore.OverwriteToolEntry(Key, BuildEntry)
                : manifestStore.UpdateToolEntry(Key, existing?.Version ?? 0, BuildEntry);
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

        // 参照されなくなった note の実データはここでは消さない。置き場所は内容から
        // 決まるので、同じ内容を他の世代や他の PC が参照していることがある。Push の
        // 後始末として消すと、読み直しから削除までの間に他の PC が公開した manifest が
        // 欠落オブジェクトを指しうる (この経路は以前ここにあった競合そのもの)。
        // 参照されなくなったものは猶予期間を置いて GC (BlobGarbageCollector) が回収する。

        _logger.LogInformation("Friend Connect Push 完了 version={Version} files={Count}", nextVersion, files.Count);
        return new SyncResult
        {
            Outcome = SyncOutcome.Success,
            RemoteVersion = nextVersion,
            AffectedFiles = affected,
            SyncedSummary = pushedSummary,
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

        var remoteFiles = entry.Files.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        remoteFiles.TryGetValue(DbV11Key, out var remoteDbV11);
        remoteFiles.TryGetValue(DbKey, out var remoteDb);

        // Issue #19: 起動時自動 Pull の暴走防止。VRCX 側と同じ判定。
        // 同期先にある DB がローカルで消えているケースでは skip せず
        // 通常 Pull に進めて復元する (#20 レビュー指摘)。_paths.Exists() は
        // Root しか見ないので、DB ファイルの存在も確かめる。
        if (options.SkipIfNotNewer
            && options.LastPulledVersion is long lastPulled
            && entry.Version <= lastPulled
            && _paths.Exists()
            && (remoteDbV11 is null || File.Exists(_paths.DbV11File))
            && (remoteDb is null || File.Exists(_paths.DbFile)))
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

        if (options.SkipIfNotNewer
            && options.FindUnsyncedLocalChanges?.Invoke() is { Count: > 0 } changed)
        {
            // 起動時の自動 Pull は利用者が求めた操作ではない。ここで上書きすると、
            // 前回の同期の後に手元で増えた履歴や編集が、気付かれないまま
            // バックアップにしか残らなくなる。
            _logger.LogInformation(
                "{Tool} Pull 見送り: 手元に未 Push の変更がある (remote={Remote}, tables={Tables})",
                DisplayName, entry.Version, string.Join(", ", changed));
            return SyncResults.LocalChangesPending(DisplayName, entry.Version, changed);
        }

        // どちらの DB も任意として扱い、少なくとも片方があることだけを求める。
        // 以前の版は旧版の db.sqlite を必ず載せていたので、その版が書いた manifest には
        // db_1.1.sqlite が無いことがある。逆に今の版は db.sqlite の無い PC からも Push する。
        if (remoteDbV11 is null && remoteDb is null)
        {
            return new SyncResult
            {
                Outcome = SyncOutcome.SourceMissing,
                Message = $"クラウド側にスナップショットがありません: {DbV11Key}",
            };
        }

        Directory.CreateDirectory(_paths.RootDirectory);
        Directory.CreateDirectory(_paths.DbDirectory);

        // (1) 同期先から取り出す。ここではローカルの既存ファイルに触れないので、
        //     途中で失敗しても手元のデータは元のまま残る。
        using var staging = new PullStaging(_logger);

        remoteFiles.TryGetValue(ConfigKey, out var remoteConfig);
        var remoteDirectoryFiles = _directories.ToDictionary(
            d => d,
            d => entry.Files
                .Where(f => f.RelativePath.StartsWith(d.KeyPrefix, StringComparison.Ordinal))
                .ToList());

        // manifest が要求するファイルはすべて揃わなければ Pull を成功にしない。
        // 欠けたまま成功にすると LastPulledVersion だけ進み、以後の起動時 Pull は
        // この version を最新とみなして省略するため、差異が固定される。
        // db_1.1 は本体が同期先と同じでも写しを残す。要約は手元の本体ではなく、WAL を
        // 伴わない同期先の内容から取る。
        var missing = FetchOptional(staging, storage, remoteDbV11, _paths.DbV11File, keepCopyWhenUnchanged: true)
            ?? FetchOptional(staging, storage, remoteDb, _paths.DbFile)
            ?? FetchOptional(staging, storage, remoteConfig, _paths.ConfigJsonFile)
            ?? _directories
                .Select(d => FetchDirectory(staging, storage, d, remoteDirectoryFiles[d]))
                .FirstOrDefault(key => key is not null);
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
                          "正しいデータを持つ PC から非常用の強制 Push を行うと解消します。",
            };
        }

        // (3) ここから先はローカルを書き換える。
        // 次に比べる基準は、同期先の内容の写しから取る。手元の DB から取ると、本体が
        // 同期先と同じでも WAL に残る未送信の変更が基準に混ざり、次の同期で失われる。
        var pulledSummary = remoteDbV11 is null
            ? null
            : DatabaseSummary.Compute(staging.SourceOf(_paths.DbV11File), TrackedDatabase.Profile, out _);

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

            var dirsToBackup = _directories
                .Select(d => d.LocalDirectory)
                .Where(Directory.Exists)
                .ToList();

            // notes や thumbnails の退避からは取り出し中のファイルを外す。バックアップは
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
        // 旧版の db.sqlite も、リモートから消えた場合に本体だけ削除する経路がある。
        if (staging.IsStaged(_paths.DbFile) || explicitPull || remoteDb is null)
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
        if (remoteDb is null && File.Exists(_paths.DbFile))
        {
            File.Delete(_paths.DbFile);
        }
        if (remoteConfig is null && File.Exists(_paths.ConfigJsonFile))
        {
            File.Delete(_paths.ConfigJsonFile);
        }
        foreach (var directory in _directories)
        {
            RemoveStaleFiles(directory, remoteDirectoryFiles[directory]);
        }

        _logger.LogInformation("Friend Connect Pull 完了 version={Version} backup={Backup}",
            entry.Version, backupPath ?? "(none)");
        return new SyncResult
        {
            Outcome = SyncOutcome.Success,
            RemoteVersion = entry.Version,
            BackupPath = backupPath,
            AffectedFiles = affected,
            SyncedSummary = pulledSummary,
        };
    }

    /// <summary>
    /// SQLite を WAL 統合済みのスナップショットにして送る。<see cref="TrackedDatabase"/> の
    /// DB なら、次に比べる基準にする要約を送る写しから取って <paramref name="summary"/> に返す。
    /// </summary>
    private ManifestFile PushSqlite(
        ISyncStorage storage,
        string sourceDb,
        string key,
        IReadOnlyList<ManifestFile> remoteFiles,
        List<string> affected,
        bool replace,
        out DatabaseSummary? summary)
    {
        using var staged = storage.BeginUpload();
        SqliteSnapshot.Create(sourceDb, staged.LocalPath);
        var described = SyncTransfer.Describe(staged.LocalPath, key);
        summary = string.Equals(sourceDb, TrackedDatabase.Path, StringComparison.Ordinal)
            ? DatabaseSummary.Compute(staged.LocalPath, TrackedDatabase.Profile, out _)
            : null;
        if (!replace && SyncTransfer.CanSkipUpload(storage, remoteFiles, described))
        {
            _logger.LogInformation("送信を省略 (内容が同じ): {Key}", key);
        }
        else
        {
            SyncTransfer.Commit(staged, ManifestFileKeys.StorageKeyOf(described), replace);
            affected.Add(key);
        }
        return described;
    }

    /// <summary>
    /// フォルダ (notes, thumbnails) を送る。ローカルから消えたファイルは、ここで作る
    /// 一覧に載らないことで「無い」ことを表す。実データの回収は
    /// <see cref="BlobGarbageCollector"/> に任せる。
    /// </summary>
    private List<ManifestFile> PushDirectory(
        ISyncStorage storage,
        SyncedDirectory directory,
        IReadOnlyList<ManifestFile> remoteFiles,
        List<string> affected,
        bool replace)
    {
        var files = new List<ManifestFile>();

        if (Directory.Exists(directory.LocalDirectory))
        {
            foreach (var localPath in Directory.EnumerateFiles(directory.LocalDirectory, "*", SearchOption.AllDirectories))
            {
                // 中断した Pull が残した取り出し中のファイルは同期対象ではない。
                if (PullStaging.IsIncomingFile(localPath)) continue;
                var relative = StorageKey.FromRelativePath(
                    Path.GetRelativePath(directory.LocalDirectory, localPath));
                var key = directory.KeyPrefix + relative;
                var (described, sent) = SyncTransfer.Send(storage, remoteFiles, localPath, key, replace);
                if (sent)
                {
                    affected.Add(key);
                }
                else
                {
                    _logger.LogInformation("送信を省略 (内容が同じ): {Key}", key);
                }
                files.Add(described);
            }
        }

        return files;
    }

    /// <summary>
    /// 任意ファイルを取り出す。manifest に載っていなければ何もしない。
    /// 載っているのに同期先で見つからない場合は、そのキーを返して呼び出し側に伝える。
    /// </summary>
    private static string? FetchOptional(
        PullStaging staging,
        ISyncStorage storage,
        ManifestFile? remote,
        string targetPath,
        bool keepCopyWhenUnchanged = false)
    {
        if (remote is null) return null;
        return staging.Fetch(storage, remote, targetPath, keepCopyWhenUnchanged) ? null : remote.RelativePath;
    }

    /// <summary>フォルダの中身を取り出す。見つからないキーがあればその名前を返す。</summary>
    private static string? FetchDirectory(
        PullStaging staging,
        ISyncStorage storage,
        SyncedDirectory directory,
        IReadOnlyList<ManifestFile> files)
    {
        if (files.Count == 0) return null;

        Directory.CreateDirectory(directory.LocalDirectory);
        foreach (var file in files)
        {
            if (!staging.Fetch(storage, file, ToLocalPath(directory, file))) return file.RelativePath;
        }
        return null;
    }

    /// <summary>リモートから消えたファイルをローカルのフォルダからも取り除く。</summary>
    private static void RemoveStaleFiles(SyncedDirectory directory, IReadOnlyList<ManifestFile> files)
    {
        if (files.Count == 0)
        {
            if (Directory.Exists(directory.LocalDirectory))
            {
                Directory.Delete(directory.LocalDirectory, recursive: true);
            }
            return;
        }

        if (!Directory.Exists(directory.LocalDirectory)) return;

        var keep = new HashSet<string>(
            files.Select(f => Path.GetFullPath(ToLocalPath(directory, f))),
            StringComparer.OrdinalIgnoreCase);

        // 握りつぶすと次の Push で古いファイルが manifest に再登録されてしまう。
        // 列挙しながら削除すると取りこぼしうるので、先に一覧を確定させる。
        var localFiles = Directory
            .EnumerateFiles(directory.LocalDirectory, "*", SearchOption.AllDirectories)
            .ToList();
        foreach (var localPath in localFiles)
        {
            if (keep.Contains(Path.GetFullPath(localPath))) continue;
            File.Delete(localPath);
        }
    }

    /// <summary>
    /// フォルダ内のファイルのキーをローカルパスへ写す。manifest は他 PC が書いたものなので、
    /// <see cref="StorageKey.ToLocalPath"/> がキーの形を検証し、
    /// 対象フォルダの外へ書き出させない。
    /// </summary>
    private static string ToLocalPath(SyncedDirectory directory, ManifestFile file)
        => StorageKey.ToLocalPath(directory.LocalDirectory, file.RelativePath[directory.KeyPrefix.Length..]);

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
