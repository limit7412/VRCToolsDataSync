using System.CommandLine;
using Microsoft.Extensions.Logging;
using VRCToolsDataSync.Core.Logging;
using VRCToolsDataSync.Core.Settings;
using VRCToolsDataSync.Core.Storage;
using VRCToolsDataSync.Core.Sync;
using VRCToolsDataSync.Core.Update;

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
    "どの manifest からも参照されていないオブジェクトを回収する");
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
var applyRelaunchOption = new Option<bool>(
    aliases: new[] { "--relaunch" },
    description: "置き換えた後に App を起動し直す");

var selfUpdateApplyCommand = new Command("apply", "取得済みの更新でインストール先を置き換える");
selfUpdateApplyCommand.AddOption(applySourceOption);
selfUpdateApplyCommand.AddOption(applyTargetOption);
selfUpdateApplyCommand.AddOption(applyWaitPidOption);
selfUpdateApplyCommand.AddOption(applyRelaunchOption);
selfUpdateApplyCommand.SetHandler((System.CommandLine.Invocation.InvocationContext ctx) =>
{
    ctx.ExitCode = ApplySelfUpdate(
        source: ctx.ParseResult.GetValueForOption(applySourceOption)!,
        target: ctx.ParseResult.GetValueForOption(applyTargetOption)!,
        waitPid: ctx.ParseResult.GetValueForOption(applyWaitPidOption),
        relaunch: ctx.ParseResult.GetValueForOption(applyRelaunchOption));
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

    var settings = new SettingsStore().Load();
    Console.WriteLine($"保存先: {SyncStorageFactory.DescribeTarget(settings)}");
    try
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(FileLoggerProvider.DefaultLogPath()));
        });
        var storage = SyncStorageFactory.Create(settings, loggerFactory: loggerFactory);
        var collector = new BlobGarbageCollector(
            storage, loggerFactory.CreateLogger<BlobGarbageCollector>());

        var result = collector.Collect(TimeSpan.FromDays(graceDays), dryRun);

        Console.WriteLine(dryRun
            ? $"走査 {result.Scanned} 件: 参照あり {result.Live} / 猶予期間内 {result.Young} / 回収対象 {result.Deleted} 件 ({FormatBytes(result.DeletedBytes)})"
            : $"走査 {result.Scanned} 件: 参照あり {result.Live} / 猶予期間内 {result.Young} / 回収 {result.Deleted} 件 ({FormatBytes(result.DeletedBytes)})");
        if (result.Failed > 0)
        {
            Console.Error.WriteLine($"{result.Failed} 件の削除に失敗しました。次回の実行で再度対象になります。");
            return 1;
        }
        return 0;
    }
    catch (SyncStorageException ex)
    {
        Console.Error.WriteLine($"回収できませんでした: {ex.Message}");
        return 2;
    }
}

static int ApplySelfUpdate(string source, string target, int? waitPid, bool relaunch)
{
    using var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(LogLevel.Information);
        builder.AddProvider(new FileLoggerProvider(FileLoggerProvider.DefaultLogPath()));
    });
    var logger = loggerFactory.CreateLogger("SelfUpdate");

    // 呼び出し元の終了を先に待ち、ロックはその後で取る。
    //
    // 呼び出し元はロックを握ったまま終わる。手放してから終わると、その隙に
    // 別の待ち手 (裏で走っている取得の昇格) が先に握り、こちらの展開元
    // (--source) を消しうる。握ったまま終われば所有権は OS がこちらへ渡す
    // (放棄された mutex として受け取る)。先にロックを待つ順序では、こちらの
    // 待ちが呼び出し元の終了より先に尽きてしまう。
    if (!WaitForCaller(waitPid, logger)) return 6;

    // 適用の全体をクロスプロセスのロックで囲う。この間に App を起動されると、
    // 新しいプロセスが同じ展開先を消して展開し直したり、旧版のファイルを掴んだまま
    // こちらのリネームとぶつかったりする。App は起動の先頭でこれを待つ。
    // 名前はインストール先ごとに分かれている。ヘルパ自身の居場所 (展開先) では
    // なく、置き換える対象から引く。
    using var applyMutex = UpdateStage.CreateApplyMutex(target);
    var applyMutexHeld = false;
    try
    {
        // 呼び出し元は終わっているので、待つとしたら別のヘルパが動いている
        // ときだけである。
        applyMutexHeld = applyMutex.WaitOne(TimeSpan.FromSeconds(60));
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
        return ApplySelfUpdateCore(source, target, relaunch, logger);
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
/// 終了時の同期は大きな DB や遅い保存先で数分かかりうるため、余裕を持って
/// 10 分まで待つ。それでも待ちきれなかった場合は取得済みの更新を残したまま
/// 引き下がる。App が生きていれば次の起動が、終了が遅れただけならその次の
/// 起動が、同じ更新を適用し直す。
/// </para>
/// </summary>
static bool WaitForCaller(int? waitPid, ILogger logger)
{
    if (waitPid is not { } pid) return true;

    try
    {
        using var process = System.Diagnostics.Process.GetProcessById(pid);
        if (process.WaitForExit(600_000)) return true;

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

static int ApplySelfUpdateCore(string source, string target, bool relaunch, ILogger logger)
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
        var discarded = false;
        try
        {
            discarded = new UpdateStage(stageDirectory, logger).Discard();
        }
        catch (Exception discard)
        {
            Log(() => logger.LogWarning(discard, "取得済みの更新を捨てられなかった"));
        }

        if (!discarded)
        {
            // 捨てられなかった。ここで開き直すと、次の起動がまた同じ適用へ
            // 入って失敗し、開いては閉じるのを繰り返す。開き直さずに、
            // 手で片付ける先を伝えて終える。
            Console.Error.WriteLine(
                $"取得済みの更新を消せませんでした。{stageDirectory} を手で削除してから起動してください。");
            Log(() => logger.LogError("取得済みの更新を消せないため、App を起動し直さない"));
            return 8;
        }

        // 利用者から見れば、これは再起動の操作の途中である。現行版を開き直す。
        if (relaunch) TryRelaunchApp(target, logger);
        return 1;
    }

    if (relaunch && !TryRelaunchApp(target, logger))
    {
        // 置き換えは済んでいる。起動し直しの失敗は利用者の手起動で補える。
        return 1;
    }

    return 0;
}

static bool TryRelaunchApp(string target, ILogger logger)
{
    try
    {
        var appDirectory = Path.Combine(target, "app");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(appDirectory, UpdateInstaller.AppExecutableName),
            WorkingDirectory = appDirectory,
            UseShellExecute = true,
        });
        return true;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"App を起動し直せませんでした: {ex.Message}");
        try { logger.LogWarning(ex, "App の起動し直しに失敗した"); } catch { /* best-effort */ }
        return false;
    }
}

static string FormatBytes(long bytes)
{
    if (bytes < 1024) return $"{bytes} B";
    if (bytes < 1024L * 1024) return $"{bytes / 1024.0:0.#} KB";
    if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
    return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
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
