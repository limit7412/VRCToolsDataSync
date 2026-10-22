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
                PushId = options.PushId,
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

        // ローカルを書き換える前に、差し替える前後の本体のハッシュを控える。本体を差し替えた後に
        // 設定の差し替えや要約の保存で止まっても、次回に本体が差し替わったかを見分けて、
        // 取り出した写しの要約を祖先にできる。本体が同期先と同じで差し替えない場合も控える。
        // 設定だけの差し替えや削除で止まると、同期履歴だけが前の版に残り、記録が無いと次の
        // Push が競合から自動マージへ進んで前の版の設定を送る。ハッシュを取るのは DB が
        // 大きいほど長いので、書き換えの確認より前に済ませる。記録を残した後、差し替える前に
        // 中止したときは、呼び出し側が記録を消す。
        var databaseStaged = staging.IsStaged(_paths.SqliteFile);
        if (options.BeforeReplaceLocal is not null)
        {
            // 差し替えないなら、本体は取得を省いた時点で同期先の写しと同じ内容。
            var localSha256 = !databaseStaged ? remoteSnapshot.Sha256
                : File.Exists(_paths.SqliteFile) ? FileHasher.Sha256(_paths.SqliteFile) : "";
            options.BeforeReplaceLocal(new MergePendingState(
                pulledSummary, entry.Version, localSha256, remoteSnapshot.Sha256, FromPull: true));
        }

        // (4) ここから先はローカルを書き換える。その直前に、取得と要約とバックアップと記録の
        //     間に VRCX が起動して書き込んでいないかを確かめる。DB が大きいほどこの間は長い。
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
        try
        {
            staging.Apply(affected);

            if (remoteSettings is null && File.Exists(_paths.SettingsJsonFile))
            {
                // リモートに latest.json がなくなったときはローカルも削除して状態を
                // 揃える。握りつぶすと Push 側で削除済み判定との対称性が崩れて、
                // 古い VRCX.json が次の Push で manifest に再登録されてしまうため、
                // 失敗は呼び出し側に伝播させる。
                File.Delete(_paths.SettingsJsonFile);
            }
        }
        catch (Exception) when ((affected.Contains(_paths.SqliteFile) || !databaseStaged) && options.AfterDatabaseReplaced is not null)
        {
            // 本体は取り出した版 (差し替わったか、元から同期先と同じ) で、設定の差し替えか削除で
            // 止まった。要約をその版へそろえてもらう。そろえないと、次の同期が本体の違いを
            // 手元の変更と見て、前の版を祖先に取り込んだ内容を二重に足す。設定の削除の失敗も
            // 同じ扱いにする。扱いを分けると、残った記録から本体の差し替えが済んだと見て
            // 同期履歴まで進め、次の Push が消した設定を同期先へ送り直す。
            options.AfterDatabaseReplaced(entry.Version, pulledSummary, remoteSnapshot.Sha256);
            throw;
        }

        _logger.LogInformation("VRCX Pull 完了 version={Version} backup={Backup}", entry.Version, backupPath ?? "(none)");
        return new SyncResult
        {
            Outcome = SyncOutcome.Success,
            RemoteVersion = entry.Version,
            BackupPath = backupPath,
            AffectedFiles = affected,
            SyncedSummary = pulledSummary,
            SyncedSha256 = remoteSnapshot.Sha256,
        };
    }

    /// <summary>
    /// 同期先の最新のスナップショットを土台に、手元の DB にしか無い変更を足した DB を作り、
    /// ローカルの DB と置き換える。手順は <see cref="DatabaseMergeFlow"/>。
    /// <para>
    /// 設定ファイル (VRCX.json) には触れない。続く Push で手元のものが送られる。
    /// 設定は行単位で合わせられず、どちらかを選ぶしかないため、マージを始めた PC の
    /// ものを残す。
    /// </para>
    /// </summary>
    public SyncResult PullMerge(PullMergeOptions options)
    {
        var flow = new DatabaseMergeFlow
        {
            ToolKey = Key,
            DisplayName = DisplayName,
            SnapshotKey = SnapshotKey,
            DatabasePath = _paths.SqliteFile,
            Profile = MergeProfiles.Vrcx,
            ProcessNames = ProcessGuard.VrcxProcessNames,
            Logger = _logger,
            CreateBackup = () =>
            {
                // Pull と同じく WAL/SHM ごと退避する。この後で消すので、含めておかないと
                // 本体へ未反映のローカル変更が復元できなくなる。
                var filesToBackup = new List<string>
                {
                    _paths.SqliteFile,
                    Path.Combine(_paths.RootDirectory, "VRCX.sqlite3-wal"),
                    Path.Combine(_paths.RootDirectory, "VRCX.sqlite3-shm"),
                };
                if (File.Exists(_paths.SettingsJsonFile)) filesToBackup.Add(_paths.SettingsJsonFile);
                return _backup.CreateSnapshot(Key, filesToBackup);
            },
        };
        return flow.Run(options);
    }

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

    internal static string AsideSuffix() => ".vrctds-aside-" + Guid.NewGuid().ToString("N");

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
