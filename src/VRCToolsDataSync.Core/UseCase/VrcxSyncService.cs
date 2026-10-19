using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCToolsDataSync.Core.Domain;
using VRCToolsDataSync.Core.Infra;
using VRCToolsDataSync.Core.Merge;

namespace VRCToolsDataSync.Core.UseCase;

public sealed class VrcxSyncService : IMergeableSyncService
{
    public const string Key = "vrcx";

    private const string SnapshotKey = "vrcx/latest.sqlite3";
    private const string SettingsKey = "vrcx/latest.json";

    private readonly VrcxPaths _paths;
    private readonly LocalBackup _backup;
    private readonly ILogger<VrcxSyncService> _logger;

    private const string DisplayName = "VRCX";

    public string ToolKey => Key;

    public TrackedDatabase TrackedDatabase => new(_paths.SqliteFile, MergeProfiles.Vrcx);

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

        // 非常用の強制 Push は記録を一切信用しないので、全ファイルを置き換えて送る。
        var replace = options.ReuploadAll;

        if (!options.ForceOverwriteOnConflict
            && !replace
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
        DatabaseSummary? pushedSummary;
        string snapshotSha256;
        using (var staged = storage.BeginUpload())
        {
            SqliteSnapshot.Create(_paths.SqliteFile, staged.LocalPath);
            var snapshot = SyncTransfer.Describe(staged.LocalPath, SnapshotKey);
            snapshotSha256 = snapshot.Sha256;
            // 次に比べる基準は、送る写しそのものから取る。送った後に手元の DB から取ると、
            // その間に VRCX が書き込んだ未送信の変更が基準に混ざる。
            pushedSummary = DatabaseSummary.Compute(staged.LocalPath, MergeProfiles.Vrcx, out var summaryReason);
            if (pushedSummary is null) _logger.LogInformation("送る写しの要約を取れませんでした: {Reason}", summaryReason);
            // 通常の Push は前回の version の次を取る (違っていれば競合として止まる)。非常用の
            // 強制 Push は他の PC の更新を上書きするので、その間に他の PC が進めていればより
            // 後の version を取る。それでも、同期先が予定の版そのものなら自分の送信ではないと
            // 分かるので、予定として同じ値を控える。
            try
            {
                options.BeforeCommit?.Invoke(snapshot.Sha256, pushedSummary, (existing?.Version ?? 0) + 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 送った写しの記録を控えられないまま送ると、要約の保存にも失敗したときに、
                // 次のマージが送った内容を見分けられず、送った変更を二重に足す。送る前にやめる。
                _logger.LogWarning(ex, "送った写しの記録を控えられないため、Push を中止します");
                return new SyncResult
                {
                    Outcome = SyncOutcome.Aborted,
                    RemoteVersion = existing?.Version,
                    Message = $"送った写しの記録を控えられないため、Push を中止しました: {ex.Message}。" +
                              "ディスクの空き容量と、同期履歴のフォルダへの書き込み権限を確かめてください",
                };
            }
            try
            {
                if (!replace && SyncTransfer.CanSkipUpload(storage, remoteFiles, snapshot))
                {
                    _logger.LogInformation("VRCX スナップショットの送信を省略 (内容が同じ)");
                }
                else
                {
                    SyncTransfer.Commit(staged, ManifestFileKeys.StorageKeyOf(snapshot), replace);
                    affected.Add(SnapshotKey);
                }
            }
            catch
            {
                // manifest を更新する前に失敗した。送信は反映されていないので、記録は要らない。
                // 残すと、同期先が進んだときに反映されたか分からない記録として扱われ、マージが止まる。
                options.NotCommitted?.Invoke();
                throw;
            }
            files.Add(snapshot);
        }

        if (File.Exists(_paths.SettingsJsonFile))
        {
            (ManifestFile settingsFile, bool sent) result;
            try
            {
                result = SyncTransfer.Send(storage, remoteFiles, _paths.SettingsJsonFile, SettingsKey, replace);
            }
            catch
            {
                options.NotCommitted?.Invoke();
                throw;
            }
            var (settingsFile, sent) = result;
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

        // 非常用の強制 Push では、内容が同じでも version を進める。他の PC が壊れた実体を
        // 取りに行って失敗した後でも、新しい version を見れば取り直しに来る。
        if (!replace && SyncTransfer.IsUnchangedSet(existing, files))
        {
            // 送るものが何も無いなら manifest も触らない。version を進めると
            // 他 PC の LastPulledVersion が古くなり、中身が同じデータの
            // ダウンロードを誘発してしまう。
            _logger.LogInformation("VRCX Push: 変更なし version={Version}", existing!.Version);
            // 同期先の今の版は、送ろうとした写しと同じ内容。前回の Push が反映された後に
            // 要約の保存で止まっていた記録を、ここで消してはいけない。要約の保存が
            // また失敗すると、次のマージが送った内容を見分けられず、送った変更を二重に足す。
            // 反映済みの記録として残し、要約の保存が済めば消える。
            options.AfterCommit?.Invoke(snapshotSha256, existing.Version);
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
            // 競合の検査を通さない上書き (強制 Push) は、前の版の内容を取り込んでいない。
            // その版を manifest に残し、後の版がどの版まで取り込んでいるかを見分けられるようにする。
            var forced = options.ForceOverwriteOnConflict || replace;
            ToolManifestEntry BuildEntry(long version) => new()
            {
                Version = version,
                MachineName = options.MachineName,
                UpdatedAt = DateTimeOffset.Now,
                Files = files,
                // 初めての項目は 0。前の項目があれば、その値を null も含めて引き継ぐ。null は
                // この項目を知らない古い版のツールが書いたことを表し、通常の Push で 0 にすると、
                // 古いツールの強制 Push があったかを後から確かめられなくなる。
                LastForcedVersion = forced ? version : existing is null ? 0 : existing.LastForcedVersion,
            };
            // 非常用の強制 Push は全ファイルを置き換え済みなので、途中で他の PC が
            // 更新していても manifest と実データはずれない。競合で止めずに上書きする。
            nextVersion = replace
                ? manifestStore.OverwriteToolEntry(Key, BuildEntry)
                : manifestStore.UpdateToolEntry(Key, existing?.Version ?? 0, BuildEntry);
        }
        catch (SyncStorageConcurrencyException)
        {
            // 条件付きの保存が毎回拒まれた。manifest には一度も反映していないので、送信の
            // 記録は要らない。残すと、他の PC が予定の版の先まで進めたときに反映されたか
            // 分からない記録として扱われ、マージが止まり続ける。
            options.NotCommitted?.Invoke();
            throw;
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
            options.NotCommitted?.Invoke();
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

        options.AfterCommit?.Invoke(snapshotSha256, nextVersion);
        _logger.LogInformation("VRCX Push 完了 version={Version} files={Count}", nextVersion, files.Count);
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
        ProcessGuard.EnsureNotRunning(ProcessGuard.VrcxProcessNames);
        // ここから差し替えまでの間 (取得と要約で長くなりうる) に VRCX が起動して書き込むと、
        // その変更は WAL ごと消える。差し替える直前に、もう一度確かめる。設定の JSON も
        // 差し替えるので、DB と一緒に見る。
        var localFingerprint = FingerprintPullTargets();

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

        // 本体が同期先と同じでも写しを残す。要約は手元の本体ではなく、WAL を伴わない
        // 同期先の内容から取る。
        if (!staging.Fetch(storage, remoteSnapshot, _paths.SqliteFile, keepCopyWhenUnchanged: true))
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
                          "正しいデータを持つ PC から非常用の強制 Push を行うと解消します。",
            };
        }

        // 次に比べる基準は、同期先の内容の写しから取る。手元の DB から取ると、本体が
        // 同期先と同じでも WAL に残る未送信の変更が基準に混ざり、次の同期で失われる。
        var pulledSummary = DatabaseSummary.Compute(staging.SourceOf(_paths.SqliteFile), MergeProfiles.Vrcx, out var summaryReason);
        if (pulledSummary is null) _logger.LogInformation("取り出した写しの要約を取れませんでした: {Reason}", summaryReason);

        // (3) バックアップを取る。ここまではローカルを書き換えない。
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

        // (4) ここから先はローカルを書き換える。その直前に、取得と要約とバックアップの間に
        //     VRCX が起動して書き込んでいないかを確かめる。DB が大きいほどこの間は長い。
        ProcessGuard.EnsureNotRunning(ProcessGuard.VrcxProcessNames);
        if (FingerprintPullTargets() != localFingerprint)
        {
            return new SyncResult
            {
                Outcome = SyncOutcome.Aborted,
                RemoteVersion = entry.Version,
                BackupPath = backupPath,
                Message = "Pull の間に VRCX の DB か設定が書き換えられたため、差し替える前に中止しました。" +
                          "VRCX を終了してからやり直してください。",
            };
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
            SyncedSummary = pulledSummary,
        };
    }

    /// <summary>
    /// 同期先の最新のスナップショットを土台に、手元の DB にしか無い変更を足した DB を作り、
    /// ローカルの DB と置き換える。同期先へは送らない。
    /// <para>
    /// 設定ファイル (VRCX.json) には触れない。続く Push で手元のものが送られる。
    /// 設定は行単位で合わせられず、どちらかを選ぶしかないため、マージを始めた PC の
    /// ものを残す。
    /// </para>
    /// </summary>
    public SyncResult PullMerge(PullMergeOptions options)
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
        var manifest = new ManifestStore(storage).Load();
        if (!manifest.Tools.TryGetValue(Key, out var entry)
            || entry.Files.FirstOrDefault(f => f.RelativePath == SnapshotKey) is not { } remoteSnapshot)
        {
            return new SyncResult
            {
                Outcome = SyncOutcome.NothingToDo,
                Message = "クラウド側に VRCX のデータがありません",
            };
        }

        var work = Path.Combine(Path.GetTempPath(), "VRCToolsDataSync-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // (1) 土台にする同期先のスナップショットを取り出し、記録と合うか確かめる。
            var hostPath = Path.Combine(work, "remote.sqlite3");
            if (!storage.TryDownload(ManifestFileKeys.StorageKeyOf(remoteSnapshot), hostPath))
            {
                return new SyncResult
                {
                    Outcome = SyncOutcome.SourceMissing,
                    Message = $"クラウド側にスナップショットがありません: {SnapshotKey}",
                };
            }
            if (!string.IsNullOrEmpty(remoteSnapshot.Sha256)
                && !string.Equals(FileHasher.Sha256(hostPath), remoteSnapshot.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new SyncResult
                {
                    Outcome = SyncOutcome.Aborted,
                    Message = $"取得したファイルの内容が manifest の記録と一致しません: {SnapshotKey}。" +
                              "正しいデータを持つ PC から非常用の強制 Push を行うと解消します。",
                };
            }

            // (2) 手元の DB の写しを取る。WAL を統合するためと、マージ中に手元の
            //     ファイルを開いたままにしないため。
            // 写しを取っている間か取った後に VRCX が DB を書き換えたら、その更新は写しに
            // 入らず、置き換えで失われる。写しの前後でファイルの状態を控えて比べ、置き換えの
            // 直前にもう一度比べる。
            var localPath = Path.Combine(work, "local.sqlite3");
            var localFingerprint = FingerprintDatabase(_paths.SqliteFile);
            SqliteSnapshot.Create(_paths.SqliteFile, localPath);
            if (FingerprintDatabase(_paths.SqliteFile) != localFingerprint)
            {
                return new SyncResult
                {
                    Outcome = SyncOutcome.Aborted,
                    RemoteVersion = entry.Version,
                    Message = "手元の DB の写しを取る間に VRCX の DB が書き換えられたため、マージを中止しました。" +
                              "VRCX を終了してからやり直してください。",
                };
            }

            // (3) 写しどうしでマージする。手元の DB にはまだ触れない。
            //     マージ後の祖先にするため、土台の要約をマージの前に取っておく。
            var hostBaseline = DatabaseSummary.Compute(hostPath, MergeProfiles.Vrcx, out var hostReason);
            if (hostBaseline is not null) hostBaseline.RemoteVersion = entry.Version;
            else if (!options.DryRun)
            {
                // 要約が無いと、置き換えた後のローカルに祖先が無くなり、次の同期で手元の
                // 変更を見分けられない。置き換える前にやめる。
                return new SyncResult
                {
                    Outcome = SyncOutcome.Aborted,
                    RemoteVersion = entry.Version,
                    Message = $"同期先の DB を要約できないため、マージを中止しました: {hostReason}",
                };
            }
            var baseline = options.Baseline;
            // 送った写しの記録から祖先を決める。マージを中止するなら、その結果を返す。
            SyncResult? ResolveOwnPush(PushedRecord ownPush)
            {
                if (string.Equals(remoteSnapshot.Sha256, ownPush.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    // 同期先は自分が送った写しそのもの。手元との違いはすべて送った後の手元の変更
                    // なので、同期先を祖先にする。
                    _logger.LogInformation("同期先の DB は自分が送ったものなので、それを祖先にします");
                    baseline = hostBaseline;
                }
                else if (ownPush is { Committed: true, Version: long pushedVersion, Summary: not null }
                         && entry.Version > pushedVersion
                         && (baseline is null || baseline.RemoteVersion < pushedVersion))
                {
                    if (entry.LastForcedVersion is null)
                    {
                        // この項目を知らない古い版のツールが書いた版。その後に強制 Push があったかを
                        // 確かめられないので、送った写しを祖先にできない。
                        return new SyncResult
                        {
                            Outcome = SyncOutcome.Aborted,
                            RemoteVersion = entry.Version,
                            Message = $"同期先の版 {entry.Version} を書いた PC のツールが古く、前回の Push (版 {pushedVersion}) の後に強制 Push があったかを確かめられません。" +
                                      "送った変更を二重に数えたり失ったりしないよう、自動マージを中止しました。" +
                                      "すべての PC でツールを更新したうえで、同期先の内容を正とするなら Pull を、手元の内容を正とするなら非常用の強制 Push をしてください",
                        };
                    }
                    if (entry.LastForcedVersion is long forcedVersion && forcedVersion > pushedVersion)
                    {
                        // 送った版の後に別の PC が強制 Push で上書きした。その版は送った内容を
                        // 取り込んでいないので、送った写しを祖先にすると送った変更が消える。
                        // 古い要約を祖先にすると二重に足す。どちらも避けるため止める。
                        return new SyncResult
                        {
                            Outcome = SyncOutcome.Aborted,
                            RemoteVersion = entry.Version,
                            Message = $"前回の Push (版 {pushedVersion}) の後に別の PC が強制 Push (版 {forcedVersion}) で同期先を上書きしています。" +
                                      "送った変更が同期先に残っているか分からないため、自動マージを中止しました。" +
                                      "同期先の内容を正とするなら Pull を、手元の内容を正とするなら非常用の強制 Push をしてください",
                        };
                    }
                    // 送った版の後に別の PC が版を進めた。その版は送った内容を取り込んでいるので、
                    // 送った写しの要約を祖先にする。それより前の要約を祖先にすると、送った変更を
                    // 同期先と手元の両方の変更と見て二重に足す。
                    _logger.LogInformation(
                        "同期先の版 {Remote} は自分が送った版 {Pushed} より後なので、送った写しの要約を祖先にします",
                        entry.Version, pushedVersion);
                    baseline = ownPush.Summary;
                }
                else if (ownPush is { Committed: false, PlannedVersion: null, Summary: not null })
                {
                    // 予定の版を控えない版のツールが送った後に止まった記録。反映されたかを
                    // 版で見分けられないので、同期先の写しが違えば止める。
                    return new SyncResult
                    {
                        Outcome = SyncOutcome.Aborted,
                        RemoteVersion = entry.Version,
                        Message = "前回の Push が同期先に反映されたか分からないまま、同期先が進んでいます。" +
                                  "送った変更を二重に数えないよう、自動マージを中止しました。" +
                                  "同期先の内容を正とするなら Pull を、手元の内容を正とするなら非常用の強制 Push をしてください",
                    };
                }
                else if (ownPush is { Committed: false, PlannedVersion: long plannedVersion }
                         && (baseline is null || baseline.RemoteVersion < plannedVersion))
                {
                    if (entry.Version == plannedVersion)
                    {
                        // 取る予定だった版を別の PC が取った。送信は反映されなかったので、記録は要らない。
                        // dry-run は手元に何も書かないので、消す予定として知らせるだけにする。
                        _logger.LogInformation("前回の送信は反映されていませんでした (版 {Version} は別の PC のもの)", plannedVersion);
                        if (options.DryRun) _logger.LogInformation("dry-run のため、前回の送信の記録はまだ消しません");
                        else options.DiscardOwnPush?.Invoke();
                        // その前の Push の反映済みの記録を抱えていれば、同期先がその版の先へ
                        // 進んでいるので、その記録で祖先を決め直す。
                        if (ownPush.Previous is { } previous) return ResolveOwnPush(previous);
                    }
                    else if (entry.Version > plannedVersion)
                    {
                        // 反映の直後に止まって反映済みの記録を書けなかったか、反映されずに別の PC が
                        // 版を 2 つ以上進めたか、見分けられない。古い要約を祖先にすると、反映されていた
                        // 場合に送った変更を二重に足すので、止める。
                        return new SyncResult
                        {
                            Outcome = SyncOutcome.Aborted,
                            RemoteVersion = entry.Version,
                            Message = $"前回の Push (版 {plannedVersion}) が同期先に反映されたか分からないまま、同期先が版 {entry.Version} まで進んでいます。" +
                                      "送った変更を二重に数えないよう、自動マージを中止しました。" +
                                      "同期先の内容を正とするなら Pull を、手元の内容を正とするなら非常用の強制 Push をしてください",
                        };
                    }
                }
                return null;
            }
            if (options.OwnPush is { } ownPushRecord && ResolveOwnPush(ownPushRecord) is { } ownPushAbort)
            {
                return ownPushAbort;
            }
            var report = SqliteMerger.Merge(hostPath, localPath, MergeProfiles.Vrcx, baseline, options.DryRun);
            if (hostBaseline is not null && report.Succeeded)
            {
                DatabaseSummary.TrimRenumbered(hostBaseline, hostPath, MergeProfiles.Vrcx, report);
            }
            report.HostBaseline = hostBaseline;
            _logger.LogInformation("VRCX マージ: {Report}", report.Describe());
            if (!report.Succeeded)
            {
                return new SyncResult
                {
                    Outcome = SyncOutcome.Aborted,
                    RemoteVersion = entry.Version,
                    Message = report.Describe(),
                    MergeReport = report,
                };
            }
            if (options.DryRun)
            {
                return new SyncResult
                {
                    Outcome = SyncOutcome.Success,
                    RemoteVersion = entry.Version,
                    Message = "(dry-run) " + report.Describe(),
                    MergeReport = report,
                };
            }

            // (4) ここから先はローカルを書き換える。Pull と同じく WAL/SHM ごと退避してから消す。
            string? backupPath = null;
            if (!options.SkipBackup)
            {
                var filesToBackup = new List<string>
                {
                    _paths.SqliteFile,
                    Path.Combine(_paths.RootDirectory, "VRCX.sqlite3-wal"),
                    Path.Combine(_paths.RootDirectory, "VRCX.sqlite3-shm"),
                };
                if (File.Exists(_paths.SettingsJsonFile)) filesToBackup.Add(_paths.SettingsJsonFile);
                backupPath = _backup.CreateSnapshot(Key, filesToBackup);
            }
            // 置き換えた後に止まっても祖先を復旧できるよう、置き換える前後の本体のハッシュを
            // 呼び出し側に渡す。読むのに時間がかかるので、書き換えの確認より前に取る。
            // WAL/SHM を退ける名前も記録に残す。退けた後、本体を置き換える前に止まると、
            // 本体は元のままで WAL だけが無い。次回に戻せるよう、名前を先に決めておく。
            var asideSuffix = AsideSuffix();
            var pending = options.BeforeReplaceLocal is null
                ? null
                : new MergePendingState(report.HostBaseline, entry.Version,
                    FileHasher.Sha256(_paths.SqliteFile), FileHasher.Sha256(hostPath), asideSuffix);
            // 記録の保存には時間がかかりうるので、書き換えの確認より前に済ませる。
            if (pending is not null) options.BeforeReplaceLocal!(pending);
            // マージには時間がかかることがあり、その間に VRCX が起動されうる (起動時の自動
            // Pull が別プロセスのマージを待ちきれずに VRCX を起動した場合など)。動いている
            // DB を置き換えないよう、置き換えの直前にもう一度確かめる。
            ProcessGuard.EnsureNotRunning(ProcessGuard.VrcxProcessNames);
            if (FingerprintDatabase(_paths.SqliteFile) != localFingerprint)
            {
                // 写しを取った後に VRCX が起動して DB を書き換え、もう終了している。
                return new SyncResult
                {
                    Outcome = SyncOutcome.Aborted,
                    RemoteVersion = entry.Version,
                    BackupPath = backupPath,
                    Message = "マージの間に VRCX の DB が書き換えられたため、置き換えずに中止しました。もう一度実行してください",
                    MergeReport = report,
                };
            }
            ReplaceDatabase(hostPath, _paths.SqliteFile, _logger, asideSuffix);
            options.AfterReplaceLocal?.Invoke(entry.Version, report.HostBaseline);

            return new SyncResult
            {
                Outcome = SyncOutcome.Success,
                RemoteVersion = entry.Version,
                BackupPath = backupPath,
                Message = report.Describe(),
                MergeReport = report,
                AffectedFiles = new[] { SnapshotKey },
            };
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>本体と WAL の大きさと更新時刻。書き換えられたかを見分けるのに使う。</summary>
    private static string FingerprintDatabase(string path) => DatabaseFingerprint.Of(path);

    /// <summary>Pull が差し替えるファイル (DB 本体と WAL、設定の JSON) の状態。</summary>
    private string FingerprintPullTargets()
        => DatabaseFingerprint.Of(_paths.SqliteFile) + "|" + DatabaseFingerprint.OfFile(_paths.SettingsJsonFile);

    /// <summary>
    /// SQLite の本体ファイルを <paramref name="source"/> で置き換える。WAL/SHM は消さずに
    /// 脇へ退けておき、置き換えに失敗したら元に戻す。WAL にはまだ本体へ書き戻されて
    /// いない変更が残っていることがあり、先に消すと、置き換えに失敗したときに本体は
    /// 古いまま WAL の変更だけが失われるため。
    /// </summary>
    internal static void ReplaceDatabase(string source, string destination, ILogger logger, string? asideSuffix = null)
    {
        var suffix = asideSuffix ?? AsideSuffix();
        var moved = new List<(string Original, string Aside)>();
        try
        {
            foreach (var path in SidecarsOf(destination))
            {
                if (!File.Exists(path)) continue;
                var aside = path + suffix;
                File.Move(path, aside);
                moved.Add((path, aside));
            }
            // 置き換えは File.Replace で行うので、失敗しても本体は元のまま残る。
            AtomicFile.Copy(source, destination);
        }
        catch
        {
            foreach (var (original, aside) in moved)
            {
                try
                {
                    File.Move(aside, original, overwrite: true);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "退けたファイルを戻せませんでした。{Aside} を {Original} へ手で戻してください", aside, original);
                }
            }
            throw;
        }
        foreach (var (_, aside) in moved)
        {
            try { File.Delete(aside); } catch { /* best-effort cleanup */ }
        }
    }

    private static string AsideSuffix() => ".vrctds-aside-" + Guid.NewGuid().ToString("N");

    private static IEnumerable<string> SidecarsOf(string database) => new[] { database + "-wal", database + "-shm" };

    /// <summary><see cref="ReplaceDatabase"/> が脇へ退けたまま残っている WAL/SHM の場所。</summary>
    internal static string[] FindAsideFiles(string database, string asideSuffix)
        => SidecarsOf(database).Select(p => p + asideSuffix).Where(File.Exists).ToArray();

    /// <summary>
    /// <see cref="ReplaceDatabase"/> が脇へ退けたまま残った WAL/SHM を、本体の隣へ戻す。
    /// 置き換える前に止まったとき用。本体の隣に同じ名前のファイルが既にあれば、その後に
    /// VRCX が作ったものなので上書きせず、退けた方を残して偽を返す。全部戻せれば真。
    /// </summary>
    internal static bool RestoreAsideFiles(string database, string asideSuffix, ILogger logger)
    {
        var restored = true;
        foreach (var path in SidecarsOf(database))
        {
            var aside = path + asideSuffix;
            if (!File.Exists(aside)) continue;
            if (File.Exists(path))
            {
                logger.LogWarning("退けた {Aside} を戻す先に別のファイルがあるため、そのまま残しました", aside);
                restored = false;
                continue;
            }
            File.Move(aside, path);
            logger.LogInformation("前回のマージが退けた {Path} を戻しました", path);
        }
        return restored;
    }

    /// <summary><see cref="ReplaceDatabase"/> が脇へ退けたまま残った WAL/SHM を消す。置き換えた後に止まったとき用。</summary>
    internal static void DeleteAsideFiles(string database, string asideSuffix)
    {
        foreach (var path in SidecarsOf(database))
        {
            try { File.Delete(path + asideSuffix); } catch { /* best-effort cleanup */ }
        }
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
