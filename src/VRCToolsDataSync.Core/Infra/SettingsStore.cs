using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using VRCToolsDataSync.Core.Domain;

namespace VRCToolsDataSync.Core.Infra;

public sealed class SettingsStore
{
    /// <summary>現在の <see cref="SyncSettings.ToolStateSchema"/>。</summary>
    private const int CurrentToolStateSchema = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // StorageMode を数値ではなく "localFolder" / "s3" として読み書きする。
        // 数値表記の既存ファイルもこのコンバータで読める。
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly object _saveLock = new();

    // クロスプロセス排他は、設定ファイルの隣に置いた錠前ファイルで取る。
    // GUI (App) と CLI が同じ settings.json に対して並走で
    // read-modify-write すると、プロセス内 _saveLock だけではアトミック性が
    // 担保できず、片方の更新が他方に潰される。
    //
    // 排他は対話セッションをまたいで効く必要がある (issue #52)。%AppData% は
    // ユーザ毎に分かれるが、同じユーザの 2 つの対話セッション (ユーザーの
    // 切り替えやリモートデスクトップ) からは同じ場所を指す。
    //
    // 名前付き Mutex ではなくファイルを使うのは、名前空間の話を持ち込まない
    // ためである (issue #81)。Global\ の名前は作るのに権限が要り、作れない相手や
    // 開けない相手はセッション内だけの名前へ落ちる。落ちた側は誰とも待ち合わせて
    // いないのに、待ち合わせているつもりで進む。ファイルハンドルの共有の指定は
    // 計算機の中で一意に効くので、その分岐がそもそも要らない。
    //
    // 守る相手ごとに分けるのも、置き場所で決まる。錠前は設定ファイルの隣に置く。
    //
    // 旧版と待ち合わせるための Mutex は、移行の間だけ併せて取る (下記)。
    private static string LockPathOf(string filePath) => filePath + ".lock";

    // 錠前ファイルに移る前の 2 つの版が使っていた Mutex の名前。
    // 旧版は錠前ファイルを知らないので、錠前だけでは互いに待ち合わせない。旧版と
    // この版が同じ settings.json を保存すると、以前は効いていた直列化が効かず、
    // read-modify-write の取りこぼしに戻る。失うのは利用者の設定なので、旧名も
    // 取って待ち合わせを保つ。
    //
    // 2 つあるのは、間に 1 度名前を変えているためである。守れる範囲が違う。
    //   - Legacy: 接頭辞の無い名前。対話セッションの中でだけ見える
    //   - CrossSession: Global\ の名前。設定ファイルのパスから引いた鍵で分かれる
    // どちらか片方では、どちらか片方の版と待ち合わせられない。
    //
    // 取る順は「Legacy → CrossSession → 錠前ファイル」で固定する。旧版が取るのは
    // この並びの先頭からの一部でしかないので、順の食い違いは起きない。
    //
    // 旧版が行き渡ったら両方消してよい。
    private const string LegacyCrossProcessMutexName = "VRCToolsDataSync.SettingsStore.Save";
    private const string CrossSessionMutexPrefix = "VRCToolsDataSync.SettingsStore.Save.";

    // 錠前の取得のタイムアウト。普通の Save は数十 ms で終わるため、
    // これだけ待っても取れない場合は別プロセスがハング相当なので、
    // 取得を諦めてプロセス内ロックだけで救済し best-effort で書く。
    private static readonly TimeSpan CrossProcessLockTimeout = TimeSpan.FromSeconds(10);

    public string FilePath { get; }

    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? DefaultFilePath();
    }

    public static string DefaultFilePath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VRCToolsDataSync");
        return Path.Combine(dir, "settings.json");
    }

    public SyncSettings Load()
    {
        SyncSettings settings;
        if (File.Exists(FilePath))
        {
            using var stream = File.OpenRead(FilePath);
            settings = JsonSerializer.Deserialize<SyncSettings>(stream, JsonOptions) ?? new SyncSettings();
        }
        else
        {
            settings = new SyncSettings();
        }
        // JSON に明示的な null が書かれていると、既定値の代わりに null が入る。
        // 読んだ側が毎回それを気にせずに済むよう、ここで既定へ落としておく。
        settings.Update ??= new UpdateSettings();
        settings.LastGcAt ??= new Dictionary<string, DateTimeOffset>();
        MigrateToolStateKeys(settings);
        return settings;
    }

    /// <summary>
    /// 保存先ごとの接頭辞を持たない旧形式の同期履歴を、同期フォルダのキーへ移す。
    /// <para>
    /// 旧形式には「どの保存先に対する履歴か」が記録されていないため、判断材料は
    /// <see cref="SyncSettings.CloudFolderPath"/> しかない。読み込みの直後に一度だけ
    /// 行うことで、利用者が画面や CLI で保存先を変更する前の値を使える。
    /// 遅延して判断すると、変更後の保存先へ別の保存先の履歴を持ち込むことになる。
    /// </para>
    /// <para>
    /// 同期フォルダが未設定の場合、引き継ぎ先を決められないので旧エントリは捨てる。
    /// 次の同期で新しい履歴が作られる。
    /// </para>
    /// </summary>
    private static void MigrateToolStateKeys(SyncSettings settings)
    {
        if (settings.ToolStateSchema >= CurrentToolStateSchema) return;
        settings.ToolStateSchema = CurrentToolStateSchema;

        settings.ToolState ??= new Dictionary<string, ToolSyncState>();
        var legacyKeys = settings.ToolState.Keys.Where(k => !k.Contains('|')).ToList();
        if (legacyKeys.Count == 0) return;

        string? prefix = null;
        var folder = settings.CloudFolderPath?.Trim();
        if (!string.IsNullOrEmpty(folder))
        {
            try
            {
                prefix = new LocalFolderSyncStorage(folder).StateKeyPrefix;
            }
            catch (Exception ex) when (ex is SyncStorageException
                                        or ArgumentException
                                        or NotSupportedException
                                        or PathTooLongException)
            {
                // 設定に壊れたパスが入っている。引き継がない。
            }
        }

        foreach (var key in legacyKeys)
        {
            var state = settings.ToolState[key];
            settings.ToolState.Remove(key);
            if (prefix is null) continue;
            var migrated = prefix + key;
            if (!settings.ToolState.ContainsKey(migrated))
            {
                settings.ToolState[migrated] = state;
            }
        }
    }

    public void Save(SyncSettings settings) => SaveInternal(settings, mergeTopLevelFromDisk: false);

    /// <summary>
    /// ToolState の更新だけが目的の Save。Top-level の設定
    /// (CloudFolderPath / MachineName / SyncVrcx / SyncFriendConnect /
    /// AutoSyncEnabled / StorageMode / S3) はディスク側の現行値を採用し、incoming は ToolState
    /// のみを差し込む形でマージする。
    ///
    /// 通常の Save (= GUI の「設定を保存」ボタン) と違い、Push/Pull のような
    /// 「Top-level 設定をユーザが触っていない経路」から呼ばれることを想定。
    /// これがないと、GUI で AutoSyncEnabled=ON にした直後に CLI 等の別プロセス
    /// が古い settings (AutoSyncEnabled=false) で Push して store.Save を呼ぶと、
    /// その古い値で上書きされて ON 設定が消えてしまう。
    /// </summary>
    public void SaveToolStateOnly(SyncSettings settings) => SaveInternal(settings, mergeTopLevelFromDisk: true);

    /// <summary>
    /// 回収の記録 (<see cref="SyncSettings.LastGcAt"/>) として受け入れる、
    /// 未来方向の時刻のずれの上限。
    /// <para>
    /// これより先の未来を指す記録は無かったものとして扱う。時計が進んだ状態で
    /// 書かれた記録は、時計を直した後も「新しい方」としてマージに勝ち続け、
    /// 正しい時刻の記録で置き換えられないためである。プロセス間のわずかな
    /// 時計差まで無効にしないよう、少しだけ許す。
    /// </para>
    /// </summary>
    public static readonly TimeSpan LastGcAtFutureTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 自動回収を試みた時刻の記録だけを書く (issue #55)。
    /// <para>
    /// 通知済みの版 (<see cref="SaveNotifiedVersion"/>) と同じ理由で、通常の
    /// <see cref="Save"/> は使わない。回収は Push の後始末として常駐中に走るので、
    /// 起動時に読んだ古い settings で保存すると、その後に別プロセスが変えた
    /// 設定を巻き戻す。
    /// </para>
    /// <para>
    /// settings.json があるのに読めない場合は書かずに例外を伝える (#57)。ここで
    /// 渡す settings はこの記録以外すべて既定値なので、読めないまま書くと設定を
    /// 既定値で塗り潰すことになる。判断は保存と同じ排他の下で行うので、確かめて
    /// から書くまでの間に割り込まれない。
    /// </para>
    /// </summary>
    public void SaveLastGcAt(string storageStateKey, DateTimeOffset at)
    {
        // 中身はディスク側を全面的に採用し、この記録だけを載せる。
        // どちらが残るかは MergeForSave がキーごとの新しさで決める。
        var settings = new SyncSettings();
        settings.LastGcAt[storageStateKey] = at;
        SaveInternal(settings, mergeTopLevelFromDisk: true, requireReadableDisk: true);
    }

    /// <summary>
    /// 通知済みの版の記録だけを書く (issue #45)。
    /// <para>
    /// 更新を知らせた時点でこれを呼ぶ。通常の <see cref="Save"/> を使うと、
    /// 常駐している間に別プロセス (CLI の storage など) が変えた保存先を、
    /// こちらが起動時に読んだ古い値で巻き戻す。読み書きはクロスプロセスの
    /// 排他の下で行われるため、読んでから書くまでの間に割り込まれない。
    /// </para>
    /// <para>
    /// settings.json があるのに読めない場合は書かずに例外を伝える (#57)。ここで
    /// 渡す settings はこの記録以外すべて既定値なので、読めないまま書くと設定を
    /// 既定値で塗り潰すことになる。呼び出し元 (UpdateManager.MarkNotified) は
    /// これを捕らえて記録に残す。知らせ直しが起きるだけで、設定は失われない。
    /// </para>
    /// </summary>
    public void SaveNotifiedVersion(string tag)
    {
        // 中身はディスク側を全面的に採用し、通知済みの版だけを載せる。
        // 実際にどちらの版が残るかは MergeForSave が版の新しさで決める。
        var settings = new SyncSettings();
        settings.Update.NotifiedVersion = tag;
        SaveInternal(settings, mergeTopLevelFromDisk: true, requireReadableDisk: true);
    }

    /// <param name="requireReadableDisk">
    /// ディスクの現行値を読めないまま保存してはいけない場合に true。
    /// 記録だけを書く経路 (<see cref="SaveNotifiedVersion"/> /
    /// <see cref="SaveLastGcAt"/>) がこれを使う。あちらが渡す
    /// <see cref="SyncSettings"/> は当の記録以外すべて既定値なので、読めないまま
    /// 書くと設定を既定値で塗り潰すことになる (#57)。
    /// </param>
    private void SaveInternal(
        SyncSettings settings, bool mergeTopLevelFromDisk, bool requireReadableDisk = false)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // クロスプロセス排他: GUI と CLI が並走したケースで read-modify-write を
        // アトミックに完結させる。プロセス内 _saveLock は同一インスタンス内の
        // 並行 Save 直列化用で、別プロセスからの同時 Save は守れない。
        //
        // 旧版と待ち合わせるための Mutex を先に取り、その中で錠前ファイルを取る。
        // 順は固定である。
        using var legacyMutex = new Mutex(initiallyOwned: false, name: LegacyCrossProcessMutexName);
        using var crossSessionMutex = GlobalMutex.Create(
            CrossSessionMutexPrefix + GlobalMutex.ScopeKeyOf(FilePath));
        var legacyAcquired = TryEnter(legacyMutex);
        var crossSessionAcquired = false;
        try
        {
            crossSessionAcquired = TryEnter(crossSessionMutex);

            using var crossProcessLock = CrossSessionFileLock.Acquire(
                LockPathOf(FilePath), CrossProcessLockTimeout);

            // タイムアウトで取れなかった場合 (crossProcessLock.IsHeld が false) は
            // プロセス内ロックだけで best-effort 保存。取得を諦めるよりは書き込んだ
            // 方がマシ (待つほど呼び出し元が長時間ハングする)。

            // 一時ファイル名にも GUID を付けて、錠前を取れなかったときの best-effort 書き込みや
            // 他プロセスからの同時書き込みでも tmp 衝突しないようにする。
            lock (_saveLock)
            {
                // 保存直前にディスクの現行 settings を再読込し、ToolState を
                // tool キー単位でマージする。これにより、別プロセス/別 SyncRunner
                // が同じ settings.json に対して別 tool の状態更新を入れた直後でも、
                // 自分の Save がそれを消し飛ばさない。
                var merged = MergeForSave(settings, mergeTopLevelFromDisk, requireReadableDisk);

                var tmp = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (var stream = File.Create(tmp))
                    {
                        JsonSerializer.Serialize(stream, merged, JsonOptions);
                    }
                    if (File.Exists(FilePath))
                    {
                        File.Replace(tmp, FilePath, destinationBackupFileName: null);
                    }
                    else
                    {
                        File.Move(tmp, FilePath);
                    }

                    // 呼び出し元のインスタンスにも反映しておく。これがないと、
                    // 呼び出し元 settings の ToolState / Launch が古いままで、続けて
                    // 別の経路 (例: GUI ボタンの Push) が走った時に旧情報を
                    // 書き戻してしまう。
                    // Top-level 設定も同期する: SaveToolStateOnly 後に同じ settings
                    // インスタンスで通常 Save が走ると、ディスクから採用した最新の
                    // top-level が in-memory の古い値で上書きされて消えるため。
                    settings.CloudFolderPath = merged.CloudFolderPath;
                    settings.MachineName = merged.MachineName;
                    settings.SyncVrcx = merged.SyncVrcx;
                    settings.SyncFriendConnect = merged.SyncFriendConnect;
                    settings.AutoSyncEnabled = merged.AutoSyncEnabled;
                    settings.StorageMode = merged.StorageMode;
                    settings.S3 = merged.S3;
                    settings.ToolStateSchema = merged.ToolStateSchema;
                    settings.ToolState = merged.ToolState;
                    settings.Launch = merged.Launch;
                    settings.Update = merged.Update;
                    settings.LastGcAt = merged.LastGcAt;
                }
                finally
                {
                    if (File.Exists(tmp))
                    {
                        try { File.Delete(tmp); } catch { /* best-effort */ }
                    }
                }
            }
        }
        finally
        {
            // 取った順の逆に返す。
            Exit(crossSessionMutex, crossSessionAcquired);
            Exit(legacyMutex, legacyAcquired);
        }
    }

    private static bool TryEnter(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(CrossProcessLockTimeout);
        }
        catch (AbandonedMutexException)
        {
            // 他プロセスが Mutex を保持したまま死んだ場合、所有権はこちらに
            // 渡ってくる。Mutex 自体は取れているので続行する。
            return true;
        }
    }

    private static void Exit(Mutex mutex, bool acquired)
    {
        if (!acquired) return;
        try { mutex.ReleaseMutex(); } catch { /* best-effort */ }
    }

    /// <summary>
    /// 呼び出し元の <paramref name="incoming"/> とディスクの現行 settings を
    /// マージした結果を返す。
    /// <para>
    /// <paramref name="mergeTopLevelFromDisk"/> が false (通常の Save) の場合、
    /// Top-level 設定 (CloudFolderPath, MachineName, SyncVrcx, SyncFriendConnect,
    /// AutoSyncEnabled, StorageMode, S3) は incoming を優先する。
    /// </para>
    /// <para>
    /// true (SaveToolStateOnly) の場合、Top-level 設定はディスク側を採用する。
    /// Push/Pull の付随 Save がユーザの Top-level 設定変更を巻き戻すのを防ぐ。
    /// ディスクに settings.json が無い場合 (初回) は incoming を採用する。
    /// </para>
    /// ToolState は tool キーごとに、より新しいタイムスタンプを持つ側を採用する。
    /// </summary>
    private SyncSettings MergeForSave(
        SyncSettings incoming, bool mergeTopLevelFromDisk, bool requireReadableDisk)
    {
        SyncSettings disk;
        var diskAvailable = false;
        try
        {
            diskAvailable = File.Exists(FilePath);
            disk = Load();
        }
        catch (Exception ex)
        {
            // 「まだ無い」と「あるのに読めない」は別の話である (#57)。
            //
            // まだ無いなら、incoming をそのまま使うのが正しい (初回の保存)。
            // あるのに読めない場合に同じ扱いをすると、マージの土台が無いまま
            // incoming を書くことになる。記録だけを書く経路は既定値だらけの
            // settings を渡すので、壊れただけの、あるいは一瞬掴まれていただけの
            // ファイルが既定値で上書きされ、保存先や自動同期の設定が無言で消える。
            //
            // 消える前に止める。ファイルはそのまま残るので、中身を直すか退避して
            // から消せば元に戻せる。
            if (requireReadableDisk && diskAvailable)
            {
                throw new IOException(
                    $"設定ファイルを読めないため保存しません: {FilePath}。" +
                    "このまま書くと、読めなかった内容が失われます。" +
                    "中身を直すか、退避してから消してください。", ex);
            }

            disk = new SyncSettings();
            diskAvailable = false;
        }

        // Top-level の採用元を決める。
        // - 通常 Save: incoming (ユーザが触った最新値)
        // - ToolState 専用 Save: ディスク (Push/Pull は Top-level を変えない)
        //   ただしディスクに既存ファイルが無い場合は incoming にフォールバック
        //   しないと、初回 Push でユーザ設定がデフォルト値に潰れる。
        var topLevelSource = (mergeTopLevelFromDisk && diskAvailable) ? disk : incoming;
        var result = new SyncSettings
        {
            CloudFolderPath = topLevelSource.CloudFolderPath,
            MachineName = topLevelSource.MachineName,
            SyncVrcx = topLevelSource.SyncVrcx,
            SyncFriendConnect = topLevelSource.SyncFriendConnect,
            AutoSyncEnabled = topLevelSource.AutoSyncEnabled,
            // 保存先の種類と S3 接続設定も Top-level と同じ採用元から取る。
            // Push/Pull 経由の Save がユーザの保存先変更を巻き戻さないようにする。
            StorageMode = topLevelSource.StorageMode,
            S3 = topLevelSource.S3?.Clone(),
            // 更新確認の設定も Top-level と同じ扱い。Push/Pull の付随 Save が
            // チャンネルや通知済みの記録を巻き戻さないようにする。
            Update = (topLevelSource.Update ?? new UpdateSettings()).Clone(),
            // 読み込み時に移行済みなので、ディスク側も incoming 側も現行の版になっている。
            ToolStateSchema = CurrentToolStateSchema,
            ToolState = new Dictionary<string, ToolSyncState>(),
            Launch = new Dictionary<string, ToolLaunchConfig>(),
            LastGcAt = new Dictionary<string, DateTimeOffset>(),
        };

        // 両方に存在する tool キーは新しい方を採用、片方だけにあるものはそのまま追加。
        // JSON デシリアライズで明示的に null が入る可能性があるため、null セーフに扱う。
        var diskToolState = disk.ToolState ?? new Dictionary<string, ToolSyncState>();
        var incomingToolState = incoming.ToolState ?? new Dictionary<string, ToolSyncState>();
        var allKeys = new HashSet<string>(diskToolState.Keys, StringComparer.Ordinal);
        foreach (var k in incomingToolState.Keys) allKeys.Add(k);
        foreach (var key in allKeys)
        {
            var inc = incomingToolState.GetValueOrDefault(key);
            var dsk = diskToolState.GetValueOrDefault(key);
            if (inc is null) { result.ToolState[key] = dsk!; continue; }
            if (dsk is null) { result.ToolState[key] = inc; continue; }
            result.ToolState[key] = PickNewer(inc, dsk);
        }

        // 通知済みの版だけは、採用元に関わらずディスクと incoming の新しいほうを
        // 採用する。通知の記録は常に前へ進む (UpdateChecker.MarkNotified が古い版で
        // 上書きしない) ので、新旧は版の比較で決められる。これが無いと、起動時に
        // 読んだ古い設定のまま保存する経路 (接続確認に時間のかかる CLI の storage
        // など) が別プロセスの記録した版を巻き戻し、同じ版を知らせ直す。
        var diskNotified = ReleaseVersion.Parse(disk.Update?.NotifiedVersion);
        var incomingNotified = ReleaseVersion.Parse(incoming.Update?.NotifiedVersion);
        if (diskNotified is not null && (incomingNotified is null || diskNotified > incomingNotified))
        {
            result.Update.NotifiedVersion = disk.Update!.NotifiedVersion;
        }
        else if (incomingNotified is not null)
        {
            result.Update.NotifiedVersion = incoming.Update!.NotifiedVersion;
        }

        // 自動回収の記録は、採用元に関わらずキーごとにディスクと incoming の
        // 新しいほうを採用する。時刻は常に前へ進むので、新旧は比較で決められる。
        // 通知済みの版と同じく、古い settings を持つ経路の保存が別プロセスの
        // 記録を巻き戻して、回収を余分に走らせないため。
        //
        // ただし、未来を指す記録は捨てる (LastGcAtFutureTolerance)。時計が進んだ
        // 状態で書かれた記録は「新しい方」として勝ち続け、時計を直した後の正しい
        // 記録で置き換えられないためである。捨てれば次の保存が正しい時刻で作り直す。
        var latestAcceptableGcAt = DateTimeOffset.Now + LastGcAtFutureTolerance;
        var diskGcAt = disk.LastGcAt ?? new Dictionary<string, DateTimeOffset>();
        var incomingGcAt = incoming.LastGcAt ?? new Dictionary<string, DateTimeOffset>();
        foreach (var kv in diskGcAt)
        {
            if (kv.Value > latestAcceptableGcAt) continue;
            result.LastGcAt[kv.Key] = kv.Value;
        }
        foreach (var kv in incomingGcAt)
        {
            if (kv.Value > latestAcceptableGcAt) continue;
            if (!result.LastGcAt.TryGetValue(kv.Key, out var existing) || kv.Value > existing)
            {
                result.LastGcAt[kv.Key] = kv.Value;
            }
        }

        // Launch は Top-level と同じ採用元から取る。理由:
        // - 通常 Save (= GUI で設定変更) は incoming を採用したい
        // - SaveToolStateOnly (= Push/Pull) は Launch を触らないので disk を残したい
        // Launch には ToolSyncState のようなタイムスタンプが無いため、PickNewer は不要。
        var launchSource = (mergeTopLevelFromDisk && diskAvailable) ? disk : incoming;
        var launchDict = launchSource.Launch ?? new Dictionary<string, ToolLaunchConfig>();
        foreach (var kv in launchDict)
        {
            result.Launch[kv.Key] = kv.Value;
        }

        return result;
    }

    private static ToolSyncState PickNewer(ToolSyncState a, ToolSyncState b)
    {
        // LastPushedAt と LastPulledAt のうち、最新タイムスタンプを比較。
        var aLatest = LatestTimestamp(a);
        var bLatest = LatestTimestamp(b);
        return aLatest >= bLatest ? a : b;
    }

    private static DateTimeOffset LatestTimestamp(ToolSyncState s)
    {
        var p = s.LastPushedAt ?? DateTimeOffset.MinValue;
        var u = s.LastPulledAt ?? DateTimeOffset.MinValue;
        return p > u ? p : u;
    }
}
