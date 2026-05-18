using System.CommandLine;
using Microsoft.Extensions.Logging;
using VRCToolsDataSync.Core.Logging;
using VRCToolsDataSync.Core.Settings;
using VRCToolsDataSync.Core.Sync;

var rootCommand = new RootCommand("VRCX / VRC Friend Connect データ同期ツール");

var cloudOption = new Option<string?>(
    aliases: new[] { "--cloud", "-c" },
    description: "OneDrive 同期フォルダのパス（未指定なら settings.json の値を使用）");

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
        (lf, _, _) => new VrcxSyncService(logger: lf.CreateLogger<VrcxSyncService>()),
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
        (lf, _, _) => new FriendConnectSyncService(logger: lf.CreateLogger<FriendConnectSyncService>()),
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
        (lf, _, _) => new VrcxSyncService(logger: lf.CreateLogger<VrcxSyncService>()),
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
        (lf, _, _) => new FriendConnectSyncService(logger: lf.CreateLogger<FriendConnectSyncService>()),
        FriendConnectSyncService.Key);
});
pullCommand.AddCommand(pullFriendConnectCommand);

var statusCommand = new Command("status", "現在の同期設定と最後の同期情報を表示");
statusCommand.SetHandler((System.CommandLine.Invocation.InvocationContext ctx) =>
{
    ctx.ExitCode = ShowStatus();
});

rootCommand.AddCommand(pushCommand);
rootCommand.AddCommand(pullCommand);
rootCommand.AddCommand(statusCommand);

return await rootCommand.InvokeAsync(args);

// --- handlers ---

static (SettingsStore store, SyncSettings settings, string cloud, ILoggerFactory loggerFactory)?
    LoadContext(string? cloudOverride)
{
    var store = new SettingsStore();
    var settings = store.Load();
    var cloud = !string.IsNullOrWhiteSpace(cloudOverride) ? cloudOverride! : settings.CloudFolderPath;
    if (string.IsNullOrWhiteSpace(cloud))
    {
        Console.Error.WriteLine("OneDrive フォルダパスが未指定です。--cloud で指定するか settings.json に保存してください。");
        return null;
    }
    if (!Directory.Exists(cloud))
    {
        Console.Error.WriteLine($"指定されたクラウドフォルダが存在しません: {cloud}");
        return null;
    }
    var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(LogLevel.Information);
        builder.AddProvider(new FileLoggerProvider(FileLoggerProvider.DefaultLogPath()));
    });
    return (store, settings, cloud, loggerFactory);
}

static int RunPush(
    string? cloudOverride,
    bool force,
    string toolDisplayName,
    Func<ILoggerFactory, SyncSettings, string, ISyncService> serviceFactory,
    string toolKey)
{
    var ctx = LoadContext(cloudOverride);
    if (ctx is null) return 2;
    var (store, settings, cloud, loggerFactory) = ctx.Value;

    try
    {
        var service = serviceFactory(loggerFactory, settings, cloud);
        var state = settings.ToolState.GetValueOrDefault(toolKey) ?? new ToolSyncState();

        var result = service.Push(new PushOptions
        {
            CloudFolderPath = cloud,
            MachineName = settings.MachineName,
            ForceOverwriteOnConflict = force,
            LastPulledVersion = state.LastPulledVersion == 0 ? null : state.LastPulledVersion,
        });

        switch (result.Outcome)
        {
            case SyncOutcome.Success:
                Console.WriteLine($"{toolDisplayName} Push 完了 version={result.RemoteVersion}");
                foreach (var f in result.AffectedFiles) Console.WriteLine($"  {f}");
                state.LastPushedVersion = result.RemoteVersion ?? state.LastPushedVersion;
                state.LastPushedAt = DateTimeOffset.Now;
                state.LastPulledVersion = result.RemoteVersion ?? state.LastPulledVersion;
                settings.ToolState[toolKey] = state;
                store.Save(settings);
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
    catch (Exception ex)
    {
        Console.Error.WriteLine($"エラー: {ex.Message}");
        return 1;
    }
}

static int RunPull(
    string? cloudOverride,
    bool noBackup,
    string toolDisplayName,
    Func<ILoggerFactory, SyncSettings, string, ISyncService> serviceFactory,
    string toolKey)
{
    var ctx = LoadContext(cloudOverride);
    if (ctx is null) return 2;
    var (store, settings, cloud, loggerFactory) = ctx.Value;

    try
    {
        var service = serviceFactory(loggerFactory, settings, cloud);
        var result = service.Pull(new PullOptions
        {
            CloudFolderPath = cloud,
            SkipBackup = noBackup,
        });

        switch (result.Outcome)
        {
            case SyncOutcome.Success:
                Console.WriteLine($"{toolDisplayName} Pull 完了 version={result.RemoteVersion}");
                if (result.BackupPath is not null) Console.WriteLine($"  backup: {result.BackupPath}");
                foreach (var f in result.AffectedFiles) Console.WriteLine($"  {f}");
                var state = settings.ToolState.GetValueOrDefault(toolKey) ?? new ToolSyncState();
                state.LastPulledVersion = result.RemoteVersion ?? state.LastPulledVersion;
                state.LastPulledAt = DateTimeOffset.Now;
                settings.ToolState[toolKey] = state;
                store.Save(settings);
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
    catch (Exception ex)
    {
        Console.Error.WriteLine($"エラー: {ex.Message}");
        return 1;
    }
}

static int ShowStatus()
{
    var store = new SettingsStore();
    var settings = store.Load();
    Console.WriteLine($"設定ファイル: {store.FilePath}");
    Console.WriteLine($"マシン名: {settings.MachineName}");
    Console.WriteLine($"クラウドフォルダ: {(string.IsNullOrEmpty(settings.CloudFolderPath) ? "(未設定)" : settings.CloudFolderPath)}");
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
