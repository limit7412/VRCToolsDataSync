using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCToolsDataSync.Core.Domain;
using VRCToolsDataSync.Core.Paths;
using VRCToolsDataSync.Core.Storage;

namespace VRCToolsDataSync.Core.Sync;

public sealed class VrcxSyncService : ISyncService
{
    public const string Key = "vrcx";

    private const string SnapshotKey = "vrcx/latest.sqlite3";
    private const string SettingsKey = "vrcx/latest.json";

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

        var storage = options.Storage;
        var manifestStore = new ManifestStore(storage);
        var manifest = manifestStore.Load();
        manifest.Tools.TryGetValue(Key, out var existing);

        if (!options.ForceOverwriteOnConflict
            && existing is not null
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

        // 同期先に既にある内容は送り直さない。判断材料は前回の manifest エントリ。
        var remoteFiles = existing?.Files ?? new List<ManifestFile>();
        var files = new List<ManifestFile>();
        var affected = new List<string>();

        // WAL を統合したスナップショットを作ってから送る。ローカルフォルダなら
        // 同期先の一時ファイルへ直接 VACUUM し、S3 互換なら一時ファイル経由で
        // アップロードする (どちらを使うかは同期先の実装が決める)。
        using (var staged = storage.BeginUpload())
        {
            SqliteSnapshot.Create(_paths.SqliteFile, staged.LocalPath);
            var snapshot = SyncTransfer.Describe(staged.LocalPath, SnapshotKey);
            if (SyncTransfer.CanSkipUpload(storage, remoteFiles, snapshot))
            {
                _logger.LogInformation("VRCX スナップショットの送信を省略 (内容が同じ)");
            }
            else
            {
                staged.Commit(ManifestFileKeys.StorageKeyOf(snapshot));
                affected.Add(SnapshotKey);
            }
            files.Add(snapshot);
        }

        if (File.Exists(_paths.SettingsJsonFile))
        {
            var (settingsFile, sent) = SyncTransfer.Send(
                storage, remoteFiles, _paths.SettingsJsonFile, SettingsKey);
            if (sent)
            {
                affected.Add(SettingsKey);
            }
            else
            {
                _logger.LogInformation("VRCX 設定ファイルの送信を省略 (内容が同じ)");
            }
            files.Add(settingsFile);
        }
        // ローカルから消えた任意ファイルは、manifest の files[] に載せないことで
        // 「無い」ことを表す。Pull 側は manifest を正としてローカルへ反映するので、
        // 削除はそれで伝わる。
        //
        // 実データの削除はここでは行わない。置き場所は内容から決まるので、同じ内容を
        // 他の tool や他の世代が参照していることがあり、Push の後始末として消すと
        // 他の PC が参照しているオブジェクトを巻き込む。参照されなくなったものは
        // 猶予期間を置いて GC (BlobGarbageCollector) が回収する。

        if (SyncTransfer.IsUnchangedSet(existing, files))
        {
            // 送るものが何も無いなら manifest も触らない。version を進めると
            // 他 PC の LastPulledVersion が古くなり、中身が同じデータの
            // ダウンロードを誘発してしまう。
            _logger.LogInformation("VRCX Push: 変更なし version={Version}", existing!.Version);
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
                "VRCX Push 中止: Push 中に同期先が更新された (expected={Expected}, actual={Actual})",
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

        _logger.LogInformation("VRCX Push 完了 version={Version} files={Count}", nextVersion, files.Count);
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

        var storage = options.Storage;
        var manifest = new ManifestStore(storage).Load();
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
        //
        // ただし、ローカルの必須ファイル (sqlite) が消えている場合は復元のため
        // 通常 Pull に進める。settings.json に LastPulledVersion だけ残っているが
        // 実体は再インストール / 手動削除で無いケース (#20 レビュー指摘) で、skip して
        // ローカルが復元不能になるのを防ぐ。
        if (options.SkipIfNotNewer
            && options.LastPulledVersion is long lastPulled
            && entry.Version <= lastPulled
            && _paths.Exists())
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

        // manifest に載っているファイル集合を正としてローカルへ反映する。
        // 同期先を実際に走査するのではなく manifest を基準にすることで、
        // ローカルフォルダと S3 互換で同じ判断になる。
        var remoteFiles = entry.Files.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        if (!remoteFiles.TryGetValue(SnapshotKey, out var remoteSnapshot))
        {
            return new SyncResult
            {
                Outcome = SyncOutcome.SourceMissing,
                Message = $"クラウド側にスナップショットがありません: {SnapshotKey}",
            };
        }

        Directory.CreateDirectory(_paths.RootDirectory);

        // (1) 同期先から取り出す。ここではローカルの既存ファイルに触れないので、
        //     途中で失敗しても手元のデータは元のまま残る。
        using var staging = new PullStaging(_logger);

        if (!staging.Fetch(storage, remoteSnapshot, _paths.SqliteFile))
        {
            return new SyncResult
            {
                Outcome = SyncOutcome.SourceMissing,
                Message = $"クラウド側にスナップショットがありません: {SnapshotKey}",
            };
        }

        remoteFiles.TryGetValue(SettingsKey, out var remoteSettings);
        if (remoteSettings is not null && !staging.Fetch(storage, remoteSettings, _paths.SettingsJsonFile))
        {
            // manifest が要求するファイルが同期先に無い。ここで成功にしてしまうと
            // LastPulledVersion だけ進み、以後の起動時 Pull はこの version を
            // 最新とみなして省略するため、欠けたまま固定される。
            return new SyncResult
            {
                Outcome = SyncOutcome.SourceMissing,
                Message = $"クラウド側にファイルがありません: {SettingsKey}",
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
                _paths.SqliteFile,
                Path.Combine(_paths.RootDirectory, "VRCX.sqlite3-wal"),
                Path.Combine(_paths.RootDirectory, "VRCX.sqlite3-shm"),
            };
            if (File.Exists(_paths.SettingsJsonFile)) filesToBackup.Add(_paths.SettingsJsonFile);
            backupPath = _backup.CreateSnapshot(Key, filesToBackup);
        }

        // WAL/SHM を消すのは、本体 DB を差し替えるとき。残したまま差し替えると
        // 古い WAL が新しい本体に対して再生されてデータが破損するため、
        // --no-backup でも飛ばさない。
        //
        // 明示的な Pull はリモートの内容へ戻す操作なので、本体を差し替えない場合でも
        // 消す (手元の未反映分は破棄される。バックアップには含めてある)。
        // 起動時の自動 Pull (SkipIfNotNewer) では消さない。リモートで変わったのが
        // latest.json だけだった場合に、差し替えるものが無いのに未反映のローカル変更を
        // 捨てることになるため。
        if (staging.IsStaged(_paths.SqliteFile) || !options.SkipIfNotNewer)
        {
            DeleteIfExists(_paths.RootDirectory, "VRCX.sqlite3-shm");
            DeleteIfExists(_paths.RootDirectory, "VRCX.sqlite3-wal");
        }

        var affected = new List<string>();
        staging.Apply(affected);

        if (remoteSettings is null && File.Exists(_paths.SettingsJsonFile))
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
}
