using System.CommandLine;
using Microsoft.Extensions.Logging;
using VRCToolsDataSync.Core.Domain;
using VRCToolsDataSync.Core.Infra;
using VRCToolsDataSync.Core.UseCase;

var rootCommand = new RootCommand("VRCX / VRC Friend Connect データ同期ツール");

var cloudOption = new Option<string?>(
    aliases: new[] { "--cloud", "-c" },
    description: "同期フォルダのパス（未指定なら settings.json の値を使用。S3 互換モードでは無視される）");

var forceOption = new Option<bool>(
    aliases: new[] { "--force", "-f" },
    description: "リモートが新しい場合でも強制的に Push する");

var noBackupOption = new Option<bool>(
    aliases: new[] { "--no-backup" },
    description: "Pull 前のローカルバックアップを省略する");

var pushCommand = new Command("push", "ローカルデータをクラウドへアップロード");

// NOTE: System.CommandLine 2.0.0-beta4 では、ハンドラ内で Environment.ExitCode を
// 設定しても InvokeAsync は常に 0 を返してしまい、シェルから見た終了コードが
// 0 に上書きされる。各コマンドの InvocationContext.ExitCode に書き戻すことで
// 確実に InvokeAsync の戻り値に反映させる。

var pushVrcxCommand = new Command("vrcx", "VRCX のデータを Push");
pushVrcxCommand.AddOption(cloudOption);
pushVrcxCommand.AddOption(forceOption);
pushVrcxCommand.SetHandler((System.CommandLine.Invocation.InvocationContext ctx) =>
{
    var cloud = ctx.ParseResult.GetValueForOption(cloudOption);
    var force = ctx.ParseResult.GetValueForOption(forceOption);
    ctx.ExitCode = RunPush(cloud, force, "VRCX",
        lf => new VrcxSyncService(logger: lf.CreateLogger<VrcxSyncService>()),
        VrcxSyncService.Key);
});
pushCommand.AddCommand(pushVrcxCommand);

var pushFriendConnectCommand = new Command("friend-connect", "VRC Friend Connect のデータを Push");
pushFriendConnectCommand.AddOption(cloudOption);
pushFriendConnectCommand.AddOption(forceOption);
pushFriendConnectCommand.SetHandler((System.CommandLine.Invocation.InvocationContext ctx) =>
{
    var cloud = ctx.ParseResult.GetValueForOption(cloudOption);
    var force = ctx.ParseResult.GetValueForOption(forceOption);
    ctx.ExitCode = RunPush(cloud, force, "VRC Friend Connect",
        lf => new FriendConnectSyncService(logger: lf.CreateLogger<FriendConnectSyncService>()),
        FriendConnectSyncService.Key);
});
pushCommand.AddCommand(pushFriendConnectCommand);

var pullCommand = new Command("pull", "クラウドからローカルへデータを取得");

var pullVrcxCommand = new Command("vrcx", "VRCX のデータを Pull");
pullVrcxCommand.AddOption(cloudOption);
pullVrcxCommand.AddOption(noBackupOption);
pullVrcxCommand.SetHandler((System.CommandLine.Invocation.InvocationContext ctx) =>
{
    var cloud = ctx.ParseResult.GetValueForOption(cloudOption);
    var noBackup = ctx.ParseResult.GetValueForOption(noBackupOption);
    ctx.ExitCode = RunPull(cloud, noBackup, "VRCX",
        lf => new VrcxSyncService(logger: lf.CreateLogger<VrcxSyncService>()));
});
pullCommand.AddCommand(pullVrcxCommand);

var pullFriendConnectCommand = new Command("friend-connect", "VRC Friend Connect のデータを Pull");
pullFriendConnectCommand.AddOption(cloudOption);
pullFriendConnectCommand.AddOption(noBackupOption);
pullFriendConnectCommand.SetHandler((System.CommandLine.Invocation.InvocationContext ctx) =>
{
    var cloud = ctx.ParseResult.GetValueForOption(cloudOption);
    var noBackup = ctx.ParseResult.GetValueForOption(noBackupOption);
    ctx.ExitCode = RunPull(cloud, noBackup, "VRC Friend Connect",
        lf => new FriendConnectSyncService(logger: lf.CreateLogger<FriendConnectSyncService>()));
});
pullCommand.AddCommand(pullFriendConnectCommand);

var statusCommand = new Command("status", "現在の同期設定と最後の同期情報を表示");
statusCommand.SetHandler((System.CommandLine.Invocation.InvocationContext ctx) =>
{
    ctx.ExitCode = ShowStatus();
});

// --- storage: データの保存先を切り替える ---

var storageCommand = new Command("storage", "データの保存先を設定する");

var storageLocalPathOption = new Option<string>(
    aliases: new[] { "--path", "-p" },
    description: "同期フォルダのパス (OneDrive のローカル同期フォルダなど)")
{ IsRequired = true };

var storageLocalCommand = new Command("local", "保存先をローカル同期フォルダにする");
storageLocalCommand.AddOption(storageLocalPathOption);
storageLocalCommand.SetHandler((System.CommandLine.Invocation.InvocationContext ctx) =>
{
    ctx.ExitCode = ConfigureLocalStorage(ctx.ParseResult.GetValueForOption(storageLocalPathOption)!);
});
storageCommand.AddCommand(storageLocalCommand);

var s3EndpointOption = new Option<string>(
    aliases: new[] { "--endpoint", "-e" },
    description: "エンドポイント URL (R2: https://<アカウントID>.r2.cloudflarestorage.com / S3: https://s3.<リージョン>.amazonaws.com)")
{ IsRequired = true };
var s3BucketOption = new Option<string>(
    aliases: new[] { "--bucket", "-b" },
    description: "バケット名")
{ IsRequired = true };
var s3RegionOption = new Option<string>(
    aliases: new[] { "--region", "-r" },
    getDefaultValue: () => "auto",
    description: "署名に使うリージョン (R2 は auto、S3 はバケットのリージョン)");
var s3PrefixOption = new Option<string>(
    aliases: new[] { "--prefix" },
    getDefaultValue: () => string.Empty,
    description: "バケット内でこのツールが使う位置 (既定はバケット直下)");
var s3AccessKeyOption = new Option<string>(
    aliases: new[] { "--access-key" },
    description: "アクセスキー ID")
{ IsRequired = true };
var s3SecretKeyOption = new Option<string?>(
    aliases: new[] { "--secret-key" },
    description: $"シークレットアクセスキー (省略すると環境変数 {CliConstants.SecretEnvironmentVariable} か対話入力から読む)");
var s3VirtualHostOption = new Option<bool>(
    aliases: new[] { "--virtual-host" },
    description: "バケット名をホスト名に含める形式を使う (既定はパス形式)");
var s3NoConditionalWritesOption = new Option<bool>(
    aliases: new[] { "--no-conditional-writes" },
    description: "manifest.json の更新に ETag の条件付き書き込みを使わない");

var storageS3Command = new Command("s3", "保存先を S3 互換オブジェクトストレージにする");
storageS3Command.AddOption(s3EndpointOption);
storageS3Command.AddOption(s3BucketOption);
storageS3Command.AddOption(s3RegionOption);
storageS3Command.AddOption(s3PrefixOption);
storageS3Command.AddOption(s3AccessKeyOption);
storageS3Command.AddOption(s3SecretKeyOption);
storageS3Command.AddOption(s3VirtualHostOption);
storageS3Command.AddOption(s3NoConditionalWritesOption);
storageS3Command.SetHandler((System.CommandLine.Invocation.InvocationContext ctx) =>
{
    ctx.ExitCode = ConfigureS3Storage(
        endpoint: ctx.ParseResult.GetValueForOption(s3EndpointOption)!,
        bucket: ctx.ParseResult.GetValueForOption(s3BucketOption)!,
        region: ctx.ParseResult.GetValueForOption(s3RegionOption)!,
        prefix: ctx.ParseResult.GetValueForOption(s3PrefixOption)!,
        accessKeyId: ctx.ParseResult.GetValueForOption(s3AccessKeyOption)!,
        secretKeyArgument: ctx.ParseResult.GetValueForOption(s3SecretKeyOption),
        usePathStyle: !ctx.ParseResult.GetValueForOption(s3VirtualHostOption),
        useConditionalWrites: !ctx.ParseResult.GetValueForOption(s3NoConditionalWritesOption));
});
storageCommand.AddCommand(storageS3Command);

var storageTestCommand = new Command("test", "現在の保存先に到達できるかを確認する");
storageTestCommand.SetHandler((System.CommandLine.Invocation.InvocationContext ctx) =>
{
    ctx.ExitCode = TestStorage();
});
storageCommand.AddCommand(storageTestCommand);

var gcGraceOption = new Option<int>(
    aliases: new[] { "--grace-days" },
    getDefaultValue: () => 7,
    description: "この日数より新しいオブジェクトは、参照されていなくても残す");
var gcDryRunOption = new Option<bool>(
    aliases: new[] { "--dry-run" },
    description: "削除せず、対象になるものを数えるだけにする");

var storageGcCommand = new Command(
    "gc",
    "どの manifest からも参照されていないオブジェクトを削除して容量を解放する");
storageGcCommand.AddOption(gcGraceOption);
storageGcCommand.AddOption(gcDryRunOption);
storageGcCommand.SetHandler((System.CommandLine.Invocation.InvocationContext ctx) =>
{
    ctx.ExitCode = CollectGarbage(
        ctx.ParseResult.GetValueForOption(gcGraceOption),
        ctx.ParseResult.GetValueForOption(gcDryRunOption));
});
storageCommand.AddCommand(storageGcCommand);

// --- self-update: 本体の更新の適用 (issue #45 第 3 段階) ---
//
// GUI (App) が展開しておいた新しい一式で、インストール先を置き換える更新ヘルパ。
// 展開先の cli から起動されるため、置き換える対象 (app / cli) のどれも掴んでいない。
// 人が直接叩く想定は無いので、ヘルプには出さない。

var applySourceOption = new Option<string>(
    aliases: new[] { "--source" },
    description: "展開した新しい一式のディレクトリ")
{ IsRequired = true };
var applyTargetOption = new Option<string>(
    aliases: new[] { "--target" },
    description: "インストール先のルート")
{ IsRequired = true };
var applyWaitPidOption = new Option<int?>(
    aliases: new[] { "--wait-pid" },
    description: "このプロセスの終了を待ってから置き換える (呼び出し元の App)");
var applyWaitStartedOption = new Option<long?>(
    aliases: new[] { "--wait-started" },
    description: "--wait-pid のプロセスの開始時刻 (UTC の Ticks)。PID の使い回しを見分ける");
var applyRelaunchOption = new Option<bool>(
    aliases: new[] { "--relaunch" },
    description: "置き換えた後に App を起動し直す");
var applyRelaunchMinimizedOption = new Option<bool>(
    aliases: new[] { "--relaunch-minimized" },
    description: "起動し直すときウィンドウを出さずトレイへ常駐させる (issue #54)");

var selfUpdateApplyCommand = new Command("apply", "取得済みの更新でインストール先を置き換える");
selfUpdateApplyCommand.AddOption(applySourceOption);
selfUpdateApplyCommand.AddOption(applyTargetOption);
selfUpdateApplyCommand.AddOption(applyWaitPidOption);
selfUpdateApplyCommand.AddOption(applyWaitStartedOption);
selfUpdateApplyCommand.AddOption(applyRelaunchOption);
selfUpdateApplyCommand.AddOption(applyRelaunchMinimizedOption);
selfUpdateApplyCommand.SetHandler((System.CommandLine.Invocation.InvocationContext ctx) =>
{
    ctx.ExitCode = ApplySelfUpdate(
        source: ctx.ParseResult.GetValueForOption(applySourceOption)!,
        target: ctx.ParseResult.GetValueForOption(applyTargetOption)!,
        waitPid: ctx.ParseResult.GetValueForOption(applyWaitPidOption),
        waitStarted: ctx.ParseResult.GetValueForOption(applyWaitStartedOption),
        relaunch: ctx.ParseResult.GetValueForOption(applyRelaunchOption),
        relaunchMinimized: ctx.ParseResult.GetValueForOption(applyRelaunchMinimizedOption));
});

var selfUpdateCommand = new Command("self-update", "本体の更新を適用する (App が内部で使う)")
{
    IsHidden = true,
};
selfUpdateCommand.AddCommand(selfUpdateApplyCommand);

rootCommand.AddCommand(pushCommand);
rootCommand.AddCommand(pullCommand);
rootCommand.AddCommand(statusCommand);
rootCommand.AddCommand(storageCommand);
rootCommand.AddCommand(selfUpdateCommand);

return await rootCommand.InvokeAsync(args);

// --- handlers ---

static (SyncRunner runner, SyncSettings settings, ISyncStorage storage, ILoggerFactory loggerFactory)?
    LoadContext(string? cloudOverride)
{
    var store = new SettingsStore();
    var settings = store.Load();
    var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(LogLevel.Information);
        builder.AddProvider(new FileLoggerProvider(FileLoggerProvider.DefaultLogPath()));
    });

    try
    {
        var runner = new SyncRunner(store, loggerFactory);
        // 同期履歴の読み書きは SyncRunner に任せる。保存先ごとのキーの扱いと、
        // 更新前の settings.json からの引き継ぎを一箇所にまとめるため。
        return (runner, settings, runner.CreateStorage(settings, cloudOverride), loggerFactory);
    }
    catch (SyncStorageException ex)
    {
        Console.Error.WriteLine(ex.Message);
        loggerFactory.Dispose();
        return null;
    }
}

static int RunPush(
    string? cloudOverride,
    bool force,
    string toolDisplayName,
    Func<ILoggerFactory, ISyncService> serviceFactory,
    string toolKey)
{
    var ctx = LoadContext(cloudOverride);
    if (ctx is null) return 2;
    var (runner, settings, storage, loggerFactory) = ctx.Value;

    try
    {
        var result = runner.Push(serviceFactory(loggerFactory), settings, storage, force);

        switch (result.Outcome)
        {
            case SyncOutcome.Success:
                Console.WriteLine($"{toolDisplayName} Push 完了 version={result.RemoteVersion}");
                if (!string.IsNullOrEmpty(result.Message)) Console.WriteLine($"  {result.Message}");
                foreach (var f in result.AffectedFiles) Console.WriteLine($"  {f}");
                // Push の後始末として、参照が切れた実体の回収を試みる (issue #55)。
                // CLI はこの後すぐプロセスが終わるため、バックグラウンドではなく
                // ここで待つ。失敗しても Push は成功しているので終了コードには影響しない
                // (CollectGarbageIfDue は例外を投げない)。
                var gc = runner.CollectGarbageIfDue(storage);
                if (gc is not null)
                {
                    Console.WriteLine(
                        $"  ストレージ容量の解放: {gc.Deleted} 件 ({gc.DescribeDeletedBytes()}) を削除" +
                        (gc.AbortedUploads > 0 ? $" / 未完了のアップロード {gc.AbortedUploads} 件を中断" : string.Empty));
                }
                return 0;
            case SyncOutcome.ConflictDetected:
                Console.Error.WriteLine($"コンフリクト: リモート version={result.RemoteVersion}, ローカル lastPulled={result.LastPulledVersion}");
                Console.Error.WriteLine($"先に `pull {toolKey}` を実行するか、`--force` で強制 Push してください。");
                return 3;
            case SyncOutcome.SourceMissing:
                Console.Error.WriteLine(result.Message);
                return 4;
            default:
                Console.Error.WriteLine($"想定外: {result.Outcome} {result.Message}");
                return 1;
        }
    }
    catch (RunningProcessException ex)
    {
        Console.Error.WriteLine(ex.Message);
        Console.Error.WriteLine($"{toolDisplayName} を終了してから再実行してください。");
        return 5;
    }
    catch (SyncStorageConcurrencyException ex)
    {
        // manifest の更新が競合し続けた。コンフリクトと同じ扱いにする。
        Console.Error.WriteLine(ex.Message);
        return 3;
    }
    catch (SyncStorageException ex)
    {
        // 設定不備と、保存先へ到達できない場合をまとめて扱う。README の
        // 終了コード表で 2 と定めているのはこの両方。
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"エラー: {ex.Message}");
        return 1;
    }
    finally
    {
        loggerFactory.Dispose();
    }
}

static int RunPull(
    string? cloudOverride,
    bool noBackup,
    string toolDisplayName,
    Func<ILoggerFactory, ISyncService> serviceFactory)
{
    var ctx = LoadContext(cloudOverride);
    if (ctx is null) return 2;
    var (runner, settings, storage, loggerFactory) = ctx.Value;

    try
    {
        var result = runner.Pull(serviceFactory(loggerFactory), settings, storage, skipBackup: noBackup);

        switch (result.Outcome)
        {
            case SyncOutcome.Success:
                Console.WriteLine($"{toolDisplayName} Pull 完了 version={result.RemoteVersion}");
                if (result.BackupPath is not null) Console.WriteLine($"  backup: {result.BackupPath}");
                foreach (var f in result.AffectedFiles) Console.WriteLine($"  {f}");
                return 0;
            case SyncOutcome.NothingToDo:
            case SyncOutcome.SourceMissing:
                Console.Error.WriteLine(result.Message);
                return 4;
            default:
                Console.Error.WriteLine($"想定外: {result.Outcome} {result.Message}");
                return 1;
        }
    }
    catch (RunningProcessException ex)
    {
        Console.Error.WriteLine(ex.Message);
        Console.Error.WriteLine($"{toolDisplayName} を終了してから再実行してください。");
        return 5;
    }
    catch (SyncStorageConcurrencyException ex)
    {
        // manifest の更新が競合し続けた。コンフリクトと同じ扱いにする。
        Console.Error.WriteLine(ex.Message);
        return 3;
    }
    catch (SyncStorageException ex)
    {
        // 設定不備と、保存先へ到達できない場合をまとめて扱う。README の
        // 終了コード表で 2 と定めているのはこの両方。
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"エラー: {ex.Message}");
        return 1;
    }
    finally
    {
        loggerFactory.Dispose();
    }
}

static int ShowStatus()
{
    var store = new SettingsStore();
    var settings = store.Load();
    Console.WriteLine($"設定ファイル: {store.FilePath}");
    Console.WriteLine($"マシン名: {settings.MachineName}");
    Console.WriteLine($"保存先モード: {(settings.StorageMode == SyncStorageMode.S3 ? "S3 互換ストレージ" : "ローカル同期フォルダ")}");
    Console.WriteLine($"保存先: {SyncStorageFactory.DescribeTarget(settings)}");
    if (settings.StorageMode == SyncStorageMode.S3 && settings.S3 is { } s3)
    {
        Console.WriteLine($"  リージョン: {s3.Region}");
        Console.WriteLine($"  アクセスキー ID: {Mask(s3.AccessKeyId)}");
        Console.WriteLine($"  シークレットキー: {(string.IsNullOrEmpty(s3.ProtectedSecretAccessKey) ? "(未設定)" : "(保存済み)")}");
        Console.WriteLine($"  パス形式: {(s3.UsePathStyle ? "ON" : "OFF")}");
        Console.WriteLine($"  条件付き書き込み: {(s3.UseConditionalWrites ? "ON" : "OFF")}");
    }
    Console.WriteLine($"VRCX 同期: {(settings.SyncVrcx ? "ON" : "OFF")}");
    Console.WriteLine($"Friend Connect 同期: {(settings.SyncFriendConnect ? "ON" : "OFF")}");
    if (settings.ToolState.Count == 0)
    {
        Console.WriteLine("同期履歴: なし");
        return 0;
    }
    Console.WriteLine("同期履歴:");
    foreach (var (key, state) in settings.ToolState)
    {
        Console.WriteLine($"  [{key}] pulled v{state.LastPulledVersion} @ {state.LastPulledAt}, pushed v{state.LastPushedVersion} @ {state.LastPushedAt}");
    }
    return 0;
}

static int ConfigureLocalStorage(string path)
{
    var trimmed = path.Trim();

    var store = new SettingsStore();
    var settings = store.Load();
    settings.StorageMode = SyncStorageMode.LocalFolder;
    settings.CloudFolderPath = trimmed;

    try
    {
        // S3 側と同じく、保存する前に読み書きできることを確かめる。
        SyncStorageFactory.Create(settings).VerifyAccess();
    }
    catch (SyncStorageException ex)
    {
        Console.Error.WriteLine(ex.Message);
        Console.Error.WriteLine("設定は保存していません。");
        return 2;
    }

    // 接続確認には時間がかかりうる。その間に別プロセス (常駐の GUI など) が
    // 変えた設定を巻き戻さないよう、保存の直前に読み直した設定へ
    // 保存先の項目だけを載せて保存する。
    var fresh = store.Load();
    fresh.StorageMode = SyncStorageMode.LocalFolder;
    fresh.CloudFolderPath = trimmed;
    store.Save(fresh);

    Console.WriteLine($"保存先をローカル同期フォルダに設定しました: {trimmed}");
    return 0;
}

static int ConfigureS3Storage(
    string endpoint,
    string bucket,
    string region,
    string prefix,
    string accessKeyId,
    string? secretKeyArgument,
    bool usePathStyle,
    bool useConditionalWrites)
{
    var secretKey = ResolveSecretKey(secretKeyArgument);
    if (string.IsNullOrEmpty(secretKey))
    {
        Console.Error.WriteLine("シークレットアクセスキーが入力されませんでした。");
        return 2;
    }

    var store = new SettingsStore();
    var settings = store.Load();
    settings.StorageMode = SyncStorageMode.S3;
    settings.S3 = new S3Settings
    {
        ServiceUrl = endpoint.Trim(),
        Region = string.IsNullOrWhiteSpace(region) ? "auto" : region.Trim(),
        BucketName = bucket.Trim(),
        KeyPrefix = prefix.Trim().Trim('/'),
        AccessKeyId = accessKeyId.Trim(),
        ProtectedSecretAccessKey = SecretProtector.Protect(secretKey),
        UsePathStyle = usePathStyle,
        UseConditionalWrites = useConditionalWrites,
    };

    try
    {
        // 保存する前に、設定の形と、読み書きできるかをまとめて確かめる。
        SyncStorageFactory.Create(settings).VerifyAccess();
    }
    catch (SyncStorageException ex)
    {
        Console.Error.WriteLine($"保存先に接続できませんでした: {ex.Message}");
        Console.Error.WriteLine("設定は保存していません。");
        return 2;
    }

    // 接続確認は実際に保存先まで往復するため数秒かかりうる。その間に
    // 別プロセス (常駐の GUI など) が変えた設定を巻き戻さないよう、
    // 保存の直前に読み直した設定へ保存先の項目だけを載せて保存する。
    var fresh = store.Load();
    fresh.StorageMode = SyncStorageMode.S3;
    fresh.S3 = settings.S3;
    store.Save(fresh);
    Console.WriteLine($"保存先を S3 互換ストレージに設定しました: {SyncStorageFactory.DescribeTarget(fresh)}");
    Console.WriteLine("シークレットアクセスキーは、この Windows ユーザだけが復号できる形で保存しました。");
    return 0;
}

static int TestStorage()
{
    var settings = new SettingsStore().Load();
    Console.WriteLine($"保存先: {SyncStorageFactory.DescribeTarget(settings)}");
    try
    {
        var storage = SyncStorageFactory.Create(settings);
        storage.VerifyAccess();
        var manifest = storage.LoadManifest().Manifest;
        Console.WriteLine("接続を確認しました (読み取り / 書き込み / 削除)。");
        if (manifest.Tools.Count == 0)
        {
            Console.WriteLine("同期先にはまだデータがありません。");
            return 0;
        }
        foreach (var (key, entry) in manifest.Tools)
        {
            Console.WriteLine($"  [{key}] version={entry.Version} machine={entry.MachineName} updated={entry.UpdatedAt:yyyy-MM-dd HH:mm}");
        }
        return 0;
    }
    catch (SyncStorageException ex)
    {
        Console.Error.WriteLine($"接続できませんでした: {ex.Message}");
        return 2;
    }
}

static int CollectGarbage(int graceDays, bool dryRun)
{
    if (graceDays < 0)
    {
        Console.Error.WriteLine("--grace-days に負の値は指定できません。");
        return 2;
    }
    if (graceDays == 0)
    {
        // 猶予期間は、他の PC が送っている最中の実体を巻き込まないための仕組みで、
        // 回収の安全性はほぼこれに乗っている。0 にすると、書かれたばかりの実体が
        // そのまま対象になる。加えて、削除の直前の読み直しも効かなくなる。
        // 判定に使う S3 の Last-Modified は秒単位なので、猶予期間が十分にあれば
        // 「7 日以上前」と「たった今」を取り違えないが、0 では同じ秒に収まりうる。
        Console.Error.WriteLine(
            "警告: --grace-days 0 では、他の PC が送っている最中の実体を巻き込む可能性があります。");
        Console.Error.WriteLine(
            "      削除の直前の読み直しも、この設定では書き直しを捉えられないことがあります。");
    }

    var store = new SettingsStore();
    var settings = store.Load();
    Console.WriteLine($"保存先: {SyncStorageFactory.DescribeTarget(settings)}");
    try
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(FileLoggerProvider.DefaultLogPath()));
        });
        var storage = SyncStorageFactory.Create(settings, loggerFactory: loggerFactory);

        // BlobGarbageCollector を直接呼ばず、GUI と同じ手動実行の経路を通す。
        // 直接呼ぶと保存先ごとの排他と実行時刻の記録を迂回し、自動回収との
        // 並走で走査が重複するうえ、直後の Push がもう一度回収を走らせる。
        var runner = new SyncRunner(store, loggerFactory);
        var result = runner.CollectGarbageNow(storage, TimeSpan.FromDays(graceDays), dryRun);

        Console.WriteLine(dryRun
            ? $"走査 {result.Scanned} 件: 参照あり {result.Live} / 猶予期間内 {result.Young} / 解放対象 {result.Deleted} 件 ({result.DescribeDeletedBytes()})"
            : $"走査 {result.Scanned} 件: 参照あり {result.Live} / 猶予期間内 {result.Young} / 解放 {result.Deleted} 件 ({result.DescribeDeletedBytes()})");
        if (result.AbortedUploads > 0)
        {
            // 未完了のアップロードは一覧に現れないまま課金される。件数だけ出す。
            Console.WriteLine(dryRun
                ? $"未完了のアップロード: 中断対象 {result.AbortedUploads} 件"
                : $"未完了のアップロード: {result.AbortedUploads} 件を中断");
        }
        if (result.Failed > 0)
        {
            Console.Error.WriteLine($"{result.Failed} 件の削除に失敗しました。次回の実行で再度対象になります。");
        }
        if (result.FailedUploads > 0)
        {
            // 削除の失敗と分けて出す。要る権限が違うので、混ぜると原因を切り分けられない。
            Console.Error.WriteLine(
                $"{result.FailedUploads} 件の未完了のアップロードを中断できませんでした。" +
                "API キーに s3:AbortMultipartUpload の権限があるか確認してください。");
        }
        return result.Failed > 0 || result.FailedUploads > 0 ? 1 : 0;
    }
    catch (SyncStorageException ex)
    {
        Console.Error.WriteLine($"容量を解放できませんでした: {ex.Message}");
        return 2;
    }
    catch (Exception ex)
    {
        // 実行時刻を記録できない場合 (settings.json の破損など) もここに来る。
        Console.Error.WriteLine($"エラー: {ex.Message}");
        return 1;
    }
}

static int ApplySelfUpdate(
    string source, string target, int? waitPid, long? waitStarted, bool relaunch, bool relaunchMinimized)
{
    // ログの置き場所を作れなくても続ける。ここで落ちると、親の App は
    // 「起きてすぐ落ちたヘルパ = 壊れた配布物」と見なして取得を捨てる。
    // 数百 MB の取り直しを、ログを書けないことの代償にしてはいけない。
    ILoggerFactory? loggerFactory = null;
    try
    {
        loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(FileLoggerProvider.DefaultLogPath()));
        });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ログを開けませんでした ({ex.Message})。ログ無しで続行します。");
    }

    using var loggerFactoryScope = loggerFactory;
    var logger = loggerFactory?.CreateLogger("SelfUpdate")
        ?? (ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    // 適用の全体をクロスプロセスのロックで囲う。この間に App を起動されると、
    // 新しいプロセスが同じ展開先を消して展開し直したり、旧版のファイルを掴んだまま
    // こちらのリネームとぶつかったりする。App は起動の先頭でこれを待つ。
    // 名前はインストール先ごとに分かれている。ヘルパ自身の居場所 (展開先) では
    // なく、置き換える対象から引く。
    //
    // 待ちに入るのは、呼び出し元の終了を待つより先である。呼び出し元はロックを
    // 握ったまま終わるので、その時点でこちらが待ち行列に居ないと、放棄された
    // ロックを別の待ち手が先に取る。取った相手が展開先を作り直せば、こちらの
    // 展開元 (--source) が壊れる。
    //
    // 上限は呼び出し元の終了待ちに合わせて広く取る。呼び出し元は終了時に
    // 同期を流すことがあり、その間はロックを握ったままである。
    using var applyMutex = UpdateStage.CreateApplyMutex(target);
    var applyMutexHeld = false;
    try
    {
        // 上限は WaitForCaller と同じ長さにする。呼び出し元が終了時 Push を
        // 流している間、ロックはそちらが握ったままである。
        applyMutexHeld = applyMutex.WaitOne(TimeSpan.FromMinutes(65));
    }
    catch (AbandonedMutexException)
    {
        // 呼び出し元が握ったまま終わった (想定の経路)、または前のヘルパが
        // 握ったまま落ちた。どちらも所有権はこちらに渡っている。
        applyMutexHeld = true;
    }
    if (!applyMutexHeld)
    {
        // 別のヘルパが動いている。二重に適用しない。
        Console.Error.WriteLine("別の更新処理が実行中のため、置き換えを中止しました。");
        try { logger.LogWarning("更新のロックを取れなかったため適用しない"); } catch { /* best-effort */ }
        return 6;
    }

    try
    {
        // ロックを握ってから、呼び出し元が本当に終わったかを確かめる。普通は
        // ロックが渡った時点で終わっているが、放棄によらず (呼び出し元がロックを
        // 取れないまま起動を続けた場合など) 渡ってくることもある。
        if (!WaitForCaller(waitPid, waitStarted, logger)) return 6;

        // 多重起動の抑止も掴む。呼び出し元が終わった時点でこれも空くので、
        // 普通はそのまま取れる。掴まないと、入れ替えの最中に起動した App が
        // 旧 app\ を読み込んで掴み、入れ替えを失敗させたり、置き換え済みの
        // 一式をもう一度置き換えさせて退避した旧版を上書きさせたりする。
        //
        // 「既にあるか」では見ない (#66)。呼び出し元は適用のロックと抑止の
        // 両方を握ったまま終わり、OS はその二つを同時には手放さない。適用の
        // ロックが先に空くと、こちらは抑止がまだ閉じ終わらないうちに見に行き、
        // 居ない App を「動いている」と読む。そうなると置き換えも起動し直しも
        // せずに降りるので、利用者から見れば App が閉じたきりになる。
        using var singleInstance = UpdateInstaller.TryHoldSingleInstance(
            UpdateInstaller.SingleInstanceHandOverTimeout);
        if (singleInstance is null)
        {
            // 待っても空かなかった。呼び出し元とは別の App が動いている。
            // 正規の位置にはまだ触っていないので、取得を残したまま引き下がる。
            // あちらが終わった後の起動でやり直せる。開き直しもしない。画面は
            // 既にあるので、増やしても利用者の役に立たない。
            //
            // 上限を切ってあるのは、あちらが適用のロック (こちらが握っている)
            // を待っている場合に噛み合わなくなるからである。
            Console.Error.WriteLine("App が起動しているため、置き換えを中止しました。");
            try { logger.LogWarning("App が動いているため置き換えない"); } catch { /* best-effort */ }
            return 6;
        }

        return ApplySelfUpdateCore(source, target, relaunch, relaunchMinimized, logger, singleInstance);
    }
    finally
    {
        try { applyMutex.ReleaseMutex(); } catch { /* best-effort */ }
    }
}

/// <summary>
/// 呼び出し元の App が終わるのを待つ。App は終了時に同期を流すことがあり、
/// その間は app\ 配下を掴んだままなので、待たずに進めても退避のリネームで
/// 失敗するだけである。待ちきれなければ、壊す前にここで止める。
/// <para>
/// 上限は長めに取る。短く切ると、Push を終えた App がそのまま終了する一方で
/// ヘルパはもう居らず、置き換えも起動し直しも行われないまま画面だけが閉じる。
/// </para>
/// <para>
/// それでも待ちきれなかった場合は取得済みの更新を残したまま引き下がる。App が
/// 生きていれば次の起動が、終了が遅れただけならその次の起動が、同じ更新を
/// 適用し直す。
/// </para>
/// </summary>
/// <summary>
/// 開始時刻が一致するか。読めない場合は、同じものとして扱って待つ側に倒す
/// (待ちには上限があるが、待たずに進むと掴まれたファイルを入れ替えに行く)。
/// </summary>
static bool SameProcess(System.Diagnostics.Process process, long startedTicks)
{
    try
    {
        return process.StartTime.ToUniversalTime().Ticks == startedTicks;
    }
    catch (Exception)
    {
        return true;
    }
}

static bool WaitForCaller(int? waitPid, long? waitStarted, ILogger logger)
{
    // 終了時 Push の合計に上限は無い。S3Client の 30 分は操作ごとの上限で、
    // manifest の取得・オブジェクトの送信・manifest の保存が直列に続き、それが
    // ツールの数だけ繰り返される。だからここは「Push の上限」ではなく、
    // 「適用のロックを握ったまま待ち続けてよい長さ」として決めている。
    //
    // 待ちきれずに降りても失うものは少ない。取得は残るので、App が終わった後の
    // 起動が適用し直す。逆に待ち続けると、固まった App の裏でヘルパがロックを
    // 握ったままになり、以後の取得の昇格も適用も止まる。
    const int timeoutMilliseconds = 65 * 60 * 1000;

    if (waitPid is not { } pid) return true;

    try
    {
        using var process = System.Diagnostics.Process.GetProcessById(pid);

        // 番号だけでは足りない。呼び出し元が終わった後に OS が同じ番号を
        // 別のプロセスへ回していると、無関係なプロセスを待ってしまう。相手が
        // 長生きなら、こちらはロックを握ったまま上限まで待ち、置き換えも
        // 起動し直しもせずに終わる。開始時刻まで見て同じものかを確かめる。
        if (waitStarted is { } startedTicks && !SameProcess(process, startedTicks))
        {
            return true;
        }

        if (process.WaitForExit(timeoutMilliseconds)) return true;

        Console.Error.WriteLine($"呼び出し元 (PID {pid}) が終了しないため、置き換えを中止しました。次回起動時に適用されます。");
        try { logger.LogError("呼び出し元 (PID {Pid}) の終了を待ちきれなかった", pid); } catch { /* best-effort */ }
        return false;
    }
    catch (ArgumentException)
    {
        // 既に終了している。そのまま進む。
        return true;
    }
}

/// <param name="singleInstance">
/// 置き換えの間だけ掴んでいる多重起動の抑止。App を起動し直す前に手放す。
/// 掴んだまま起動すると、起動した App が「他に動いている」と見て即座に終わる。
/// </param>
static int ApplySelfUpdateCore(
    string source,
    string target,
    bool relaunch,
    bool relaunchMinimized,
    ILogger logger,
    IDisposable? singleInstance = null)
{
    // ヘルパの流れの上にあるログは、失敗しても流れを止めない。
    // ログの出力先 (%AppData%) が書き込み不可だったり容量が尽きていたりすると、
    // このリポジトリのロガーは例外を投げる。入れ替えの後にそれが飛ぶと、
    // staged の破棄にも App の起動し直しにも辿り着かず、利用者から見れば
    // 「再起動して適用」でアプリが閉じただけになる。
    void Log(Action write)
    {
        try { write(); } catch { /* best-effort */ }
    }

    try
    {
        new UpdateInstaller(source, target, logger).Apply();
        Console.WriteLine("本体を置き換えました。");
        Log(() => logger.LogInformation("本体を置き換えた: {Target}", target));
    }
    catch (UpdateDeferredException ex)
    {
        // 正規の位置には触っていない (空き不足、前回の残骸を消せない等)。
        // 取得済みの ZIP を捨てると、利用者は妨げが退いた後に数百 MB を
        // 取り直すことになる。残したまま引き下がり、現行版を開き直す。
        // 妨げが退けば次の起動がそのまま適用する。
        Console.Error.WriteLine($"置き換えを見送りました: {ex.Message}");
        Console.Error.WriteLine("原因を取り除いてから起動し直すと、取得済みの更新がそのまま適用されます。");
        Log(() => logger.LogError(ex, "正規の位置に触る前に断ったため置き換えを見送った"));

        // 見送りの指定つきで開き直す。付けずに開き直すと、その App が同じ
        // 取得をまたこちらへ渡し、こちらがまた空き不足で断念して開き直す、
        // という往復になる。
        singleInstance?.Dispose();
        if (relaunch) TryRelaunchApp(target, logger, relaunchMinimized, skipUpdateApply: true);
        return 9;
    }
    catch (UpdateRollbackException ex)
    {
        // 正規の位置に一式が無い状態。取得済みの ZIP は復旧の材料になるため消さず、
        // 壊れた一式を起動し直そうともしない。
        Console.Error.WriteLine(ex.Message);
        Console.Error.WriteLine("インストール先が壊れた可能性があります。.old ディレクトリを手で戻すか、ZIP を展開し直してください。");
        Log(() => logger.LogError(ex, "置き換えの巻き戻しに失敗した"));
        return 7;
    }
    catch (Exception ex)
    {
        // 巻き戻しは済んでいて、現行版は無傷のまま動かせる。
        Console.Error.WriteLine($"置き換えに失敗しました: {ex.Message}");
        Log(() => logger.LogError(ex, "置き換えに失敗した (巻き戻し済み)"));

        // 取得済みの更新をここで捨てる。残すと次の起動がまた同じ適用へ引き渡して
        // 同じ失敗を繰り返し、書き込めない場所に置かれた環境では現行版すら
        // 開けなくなる。展開先はこのヘルパ自身が動いている場所なので消せないが、
        // ZIP と記録が消えれば次の起動は適用へ入らず、展開先は後始末が拾う。
        //
        // 置き場所はインストール先ごとに分かれているため、対象 (--target) から
        // 引く。ヘルパ自身は展開先から動いており、そこも配布 ZIP と同じ形を
        // しているので、既定の置き場所を見ると自分の展開先を基にした空の場所を
        // 掴む。そこを消せても、本当の ZIP と記録は残ったままになる。
        var stageDirectory = UpdateStage.DirectoryFor(target);
        var applicable = true;
        try
        {
            var stage = new UpdateStage(stageDirectory, logger);
            stage.Discard();

            // 開き直してよいのは「次の起動が同じ適用へ入らない」と言い切れる
            // ときである。両方消せた場合だけでなく、片方だけ消せた場合もそう
            // である。照合は対がそろっていなければ何も返さない。
            //
            // 判定には記録の読み出しではなく、ファイルが残っているかを使う。
            // 読めないだけのものを「消えた」と取り違えると、次の起動がまた
            // 同じ適用へ入り、開いては閉じるのを繰り返す。
            applicable = stage.StagedPairRemains();
        }
        catch (Exception discard)
        {
            Log(() => logger.LogWarning(discard, "取得済みの更新を捨てられなかった"));
        }

        if (applicable)
        {
            // 対がそろったまま残っている。ここで開き直すと、次の起動がまた同じ
            // 適用へ入って失敗し、開いては閉じるのを繰り返す。開き直さずに、
            // 手で片付ける先を伝えて終える。
            Console.Error.WriteLine(
                $"取得済みの更新を消せませんでした。{stageDirectory} を手で削除してから起動してください。");
            Log(() => logger.LogError("取得済みの更新を消せないため、App を起動し直さない"));
            return 8;
        }

        // 利用者から見れば、これは再起動の操作の途中である。現行版を開き直す。
        singleInstance?.Dispose();
        if (relaunch) TryRelaunchApp(target, logger, relaunchMinimized);
        return 1;
    }

    singleInstance?.Dispose();
    if (relaunch && !TryRelaunchApp(target, logger, relaunchMinimized))
    {
        // 置き換えは済んでいる。起動し直しの失敗は利用者の手起動で補える。
        return 1;
    }

    return 0;
}

/// <param name="minimized">
/// ウィンドウを出さずトレイへ常駐させて開き直す場合に true (issue #54)。
/// 呼び出し元の App がそう起動していたときに渡ってくる。付けずに開き直すと、
/// 窓を出さないと決めた利用者の前に、更新のたびに画面が現れる。
/// </param>
/// <param name="skipUpdateApply">
/// 取得しておいたものを残したまま開き直す場合に true。開き直した App が
/// 同じ取得をまたこちらへ渡してこないよう、見送りの指定を渡す。
/// </param>
static bool TryRelaunchApp(
    string target, ILogger logger, bool minimized = false, bool skipUpdateApply = false)
{
    try
    {
        var appDirectory = Path.Combine(target, "app");
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(appDirectory, UpdateInstaller.AppExecutableName),
            WorkingDirectory = appDirectory,
            UseShellExecute = true,
        };

        var switches = new List<string>();
        if (skipUpdateApply) switches.Add(UpdateInstaller.SkipUpdateApplySwitch);
        if (minimized) switches.Add(StartupRegistration.MinimizedSwitch);
        if (switches.Count > 0) start.Arguments = string.Join(" ", switches);
        System.Diagnostics.Process.Start(start);
        return true;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"App を起動し直せませんでした: {ex.Message}");
        try { logger.LogWarning(ex, "App の起動し直しに失敗した"); } catch { /* best-effort */ }
        return false;
    }
}

/// <summary>
/// シークレットアクセスキーを引数 / 環境変数 / 対話入力の順で取得する。
/// コマンドライン引数はシェルの履歴やプロセス一覧に残るので、対話入力を既定にする。
/// </summary>
static string? ResolveSecretKey(string? argument)
{
    if (!string.IsNullOrEmpty(argument)) return argument;

    var fromEnvironment = Environment.GetEnvironmentVariable(CliConstants.SecretEnvironmentVariable);
    if (!string.IsNullOrEmpty(fromEnvironment)) return fromEnvironment;

    if (Console.IsInputRedirected)
    {
        return Console.ReadLine();
    }

    Console.Write("シークレットアクセスキー: ");
    var builder = new System.Text.StringBuilder();
    while (true)
    {
        var pressed = Console.ReadKey(intercept: true);
        if (pressed.Key == ConsoleKey.Enter) break;
        if (pressed.Key == ConsoleKey.Backspace)
        {
            if (builder.Length > 0) builder.Length--;
            continue;
        }
        if (!char.IsControl(pressed.KeyChar)) builder.Append(pressed.KeyChar);
    }
    Console.WriteLine();
    return builder.ToString();
}

static string Mask(string value)
{
    if (string.IsNullOrEmpty(value)) return "(未設定)";
    return value.Length <= 4 ? new string('*', value.Length) : value[..4] + new string('*', value.Length - 4);
}

internal static class CliConstants
{
    /// <summary>シークレットアクセスキーを渡せる環境変数の名前。</summary>
    public const string SecretEnvironmentVariable = "VRCTOOLSDATASYNC_S3_SECRET_KEY";
}
