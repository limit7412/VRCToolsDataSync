using System.CommandLine;
using Microsoft.Extensions.Logging;
using VRCToolsDataSync.Core.Logging;
using VRCToolsDataSync.Core.Settings;
using VRCToolsDataSync.Core.Storage;
using VRCToolsDataSync.Core.Sync;

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
        lf => new VrcxSyncService(logger: lf.CreateLogger<VrcxSyncService>()),
        VrcxSyncService.Key);
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
        lf => new FriendConnectSyncService(logger: lf.CreateLogger<FriendConnectSyncService>()),
        FriendConnectSyncService.Key);
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

rootCommand.AddCommand(pushCommand);
rootCommand.AddCommand(pullCommand);
rootCommand.AddCommand(statusCommand);
rootCommand.AddCommand(storageCommand);

return await rootCommand.InvokeAsync(args);

// --- handlers ---

static (SettingsStore store, SyncSettings settings, ISyncStorage storage, ILoggerFactory loggerFactory)?
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
        var storage = SyncStorageFactory.Create(settings, cloudOverride, loggerFactory);
        return (store, settings, storage, loggerFactory);
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
    var (store, settings, storage, loggerFactory) = ctx.Value;

    try
    {
        var service = serviceFactory(loggerFactory);
        var stateKey = SyncRunner.ToolStateKey(storage, toolKey);
        var state = settings.ToolState.GetValueOrDefault(stateKey) ?? new ToolSyncState();

        var result = service.Push(new PushOptions
        {
            Storage = storage,
            MachineName = settings.MachineName,
            ForceOverwriteOnConflict = force,
            LastPulledVersion = state.LastPulledVersion == 0 ? null : state.LastPulledVersion,
        });

        switch (result.Outcome)
        {
            case SyncOutcome.Success:
                Console.WriteLine($"{toolDisplayName} Push 完了 version={result.RemoteVersion}");
                if (!string.IsNullOrEmpty(result.Message)) Console.WriteLine($"  {result.Message}");
                foreach (var f in result.AffectedFiles) Console.WriteLine($"  {f}");
                state.LastPushedVersion = result.RemoteVersion ?? state.LastPushedVersion;
                state.LastPushedAt = DateTimeOffset.Now;
                state.LastPulledVersion = result.RemoteVersion ?? state.LastPulledVersion;
                settings.ToolState[stateKey] = state;
                // Push 経由の Save では Top-level 設定を書き戻さない
                // (GUI 等で変更された AutoSyncEnabled 等を巻き戻さないため)。
                store.SaveToolStateOnly(settings);
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
    catch (SyncStorageConfigurationException ex)
    {
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
    Func<ILoggerFactory, ISyncService> serviceFactory,
    string toolKey)
{
    var ctx = LoadContext(cloudOverride);
    if (ctx is null) return 2;
    var (store, settings, storage, loggerFactory) = ctx.Value;

    try
    {
        var service = serviceFactory(loggerFactory);
        var result = service.Pull(new PullOptions
        {
            Storage = storage,
            SkipBackup = noBackup,
        });

        switch (result.Outcome)
        {
            case SyncOutcome.Success:
                Console.WriteLine($"{toolDisplayName} Pull 完了 version={result.RemoteVersion}");
                if (result.BackupPath is not null) Console.WriteLine($"  backup: {result.BackupPath}");
                foreach (var f in result.AffectedFiles) Console.WriteLine($"  {f}");
                var stateKey = SyncRunner.ToolStateKey(storage, toolKey);
                var state = settings.ToolState.GetValueOrDefault(stateKey) ?? new ToolSyncState();
                state.LastPulledVersion = result.RemoteVersion ?? state.LastPulledVersion;
                state.LastPulledAt = DateTimeOffset.Now;
                settings.ToolState[stateKey] = state;
                // Pull 経由の Save では Top-level 設定を書き戻さない (Push と同様)。
                store.SaveToolStateOnly(settings);
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
    catch (SyncStorageConfigurationException ex)
    {
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
    if (!Directory.Exists(trimmed))
    {
        Console.Error.WriteLine($"指定されたフォルダが存在しません: {trimmed}");
        return 2;
    }

    var store = new SettingsStore();
    var settings = store.Load();
    settings.StorageMode = SyncStorageMode.LocalFolder;
    settings.CloudFolderPath = trimmed;
    store.Save(settings);

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
        // 保存する前に、設定の形と実際の到達性をまとめて確かめる。
        var storage = (S3SyncStorage)SyncStorageFactory.Create(settings);
        storage.VerifyAccess();
    }
    catch (SyncStorageException ex)
    {
        Console.Error.WriteLine($"保存先に接続できませんでした: {ex.Message}");
        Console.Error.WriteLine("設定は保存していません。");
        return 2;
    }

    store.Save(settings);
    Console.WriteLine($"保存先を S3 互換ストレージに設定しました: {SyncStorageFactory.DescribeTarget(settings)}");
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
        var manifest = storage.LoadManifest().Manifest;
        Console.WriteLine("接続を確認しました。");
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
