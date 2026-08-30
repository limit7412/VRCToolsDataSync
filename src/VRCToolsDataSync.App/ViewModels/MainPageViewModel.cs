using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using VRCToolsDataSync.Core.Paths;
using VRCToolsDataSync.Core.Settings;
using VRCToolsDataSync.Core.Startup;
using VRCToolsDataSync.Core.Storage;
using VRCToolsDataSync.Core.Sync;
using VRCToolsDataSync.Core.Update;
using VRCToolsDataSync.Core.Watch;
using VRCToolsDataSync_App.Services;

namespace VRCToolsDataSync_App.ViewModels;

public partial class MainPageViewModel : ObservableObject
{
    /// <summary>MainPage.xaml の保存先 ComboBox で S3 互換ストレージを指す位置。</summary>
    private const int S3ModeIndex = 1;

    /// <summary>MainPage.xaml の更新チャンネル ComboBox で test チャンネルを指す位置。</summary>
    private const int TestChannelIndex = 1;

    private readonly SyncRunner _runner;
    private readonly UpdateManager? _updates;
    private SyncSettings _settings;
    private AutoSyncCoordinator? _coordinator;
    private Action<Action>? _uiDispatch;
    // ContentDialog は WinUI 上で同時に複数表示できないため、自動通知の
    // ダイアログ呼び出しはここでシリアライズして待ち合わせる。
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    // x:Bind 用の引数なしコンストラクタ。GUI ホストでは必ず App.Runner を共有して、
    // App 側で構成された FileLoggerProvider 経由でログが出るようにする。
    // (テスト等から MainPageViewModel 単体で生成したい場合は引数付きを使う)
    public MainPageViewModel() : this(App.Runner, App.Updates) { }

    public MainPageViewModel(SyncRunner runner, UpdateManager? updates = null)
    {
        _runner = runner;
        _updates = updates;
        _settings = _runner.LoadSettings();
        MachineName = _settings.MachineName;
        CloudFolderPath = _settings.CloudFolderPath;
        SyncVrcx = _settings.SyncVrcx;
        SyncFriendConnect = _settings.SyncFriendConnect;
        AutoSyncEnabled = _settings.AutoSyncEnabled;
        LoadStorageSettingsToProperties();
        LoadLaunchConfigToProperties();
        LoadUpdateSettingsToProperties();
        RefreshStatusSummaries();
        RefreshStartupState();
        AppendLog($"保存先: {SyncStorageFactory.DescribeTarget(_settings)}");

        if (_updates is not null)
        {
            // 確認も取得もバックグラウンドで終わるため、UI スレッドへ運んでから画面に触る。
            _updates.CheckCompleted += (result, channel) =>
                OnUi(() => HandleUpdateCheckCompleted(result, channel));
            _updates.StagedChanged += () => OnUi(RefreshStagedRow);
            RefreshStagedRow();
        }

        if (UpdateApplier.StartedAfterDeferral)
        {
            // ヘルパが置き換えを断念して開き直した回である (#61)。ログだけだと
            // 「再起動したのに更新されない」としか分からないので、画面にも出す。
            AppendLog("前回の再起動では更新を適用できませんでした。インストール先の app.old / cli.old を消せない場合があります。");
            AppendLog("  詳しい理由は %AppData%\\VRCToolsDataSync\\logs のログを確認してください。");
        }
    }

    /// <summary>
    /// 起動時の SyncRunner.Run のログを GUI に流す。MainPage が VM を取得した
    /// 直後に呼び出される想定 (Window 構築前に走った StartupSyncOrchestrator
    /// のステップを GUI 上のログに反映するため)。
    /// </summary>
    public void IngestStartupSteps(IReadOnlyList<StartupSyncStep> steps, string logPrefix = "startup")
    {
        foreach (var step in steps)
        {
            switch (step.Kind)
            {
                case StartupSyncStepKind.PullStarted:
                    AppendLog($"[{logPrefix}] {step.DisplayName} Pull 開始...");
                    break;
                case StartupSyncStepKind.PullSucceeded:
                    AppendLog($"[{logPrefix}] {step.DisplayName} Pull 完了 v{step.PullResult?.RemoteVersion}");
                    break;
                case StartupSyncStepKind.PullFailed:
                    AppendLog($"[{logPrefix}] {step.DisplayName} Pull 失敗: {step.Message}");
                    break;
                case StartupSyncStepKind.PullSkipped:
                    AppendLog($"[{logPrefix}] {step.DisplayName} Pull スキップ: {step.Message}");
                    break;
                case StartupSyncStepKind.LaunchAttempted:
                    var outcome = step.LaunchResult?.Outcome;
                    var msg = outcome switch
                    {
                        ToolLaunchOutcome.Launched => "起動しました",
                        ToolLaunchOutcome.AlreadyRunning => "既に起動中",
                        ToolLaunchOutcome.ExecutableNotFound => $"実行ファイル未検出: {step.Message}",
                        ToolLaunchOutcome.LaunchFailed => $"起動失敗: {step.Message}",
                        _ => outcome?.ToString() ?? "不明",
                    };
                    AppendLog($"[{logPrefix}] {step.DisplayName} {msg}");
                    break;
                case StartupSyncStepKind.LaunchSkipped:
                    // 自動起動 OFF 時のログはノイズなので出さない。
                    break;
            }
        }
        RefreshSettingsAndStatus();
    }

    /// <summary>
    /// 起動同期 / 終了同期 / 再起動同期 のいずれかが Push/Pull を行った後に呼び、
    /// VM が保持する settings をディスクから読み直して Coordinator にも反映する。
    /// 古い ToolState で続く処理が動かないようにするための共通後処理。
    /// </summary>
    private void RefreshSettingsAndStatus()
    {
        _settings = _runner.LoadSettings();
        _coordinator?.RefreshSettings(_settings);
        RefreshStatusSummaries();
    }

    /// <summary>
    /// バックグラウンドからの通知を UI スレッドへ運ぶ手立てを渡す。
    /// <para>
    /// Coordinator とは切り離して受け取る。保存先が未設定などで Coordinator を
    /// 作れない場合でも、更新確認 (issue #45) の結果は届くためである。
    /// </para>
    /// </summary>
    public void SetUiDispatcher(Action<Action> uiDispatch) => _uiDispatch = uiDispatch;

    public void AttachCoordinator(AutoSyncCoordinator coordinator, Action<Action> uiDispatch)
    {
        _coordinator = coordinator;
        _uiDispatch = uiDispatch;
        coordinator.AutoPushTriggered += e => OnUi(() => AppendLog($"[auto] {e.DisplayName} 終了検知 → Push 開始"));
        coordinator.AutoPushCompleted += e => OnUi(() =>
        {
            if (e.Result is null) return;
            switch (e.Result.Outcome)
            {
                case SyncOutcome.Success:
                    AppendLog($"[auto] {e.DisplayName} Push 完了 v{e.Result.RemoteVersion}");
                    // Coordinator 側の SyncRunner.Push が settings を保存しているが、
                    // VM 側の _settings は別インスタンスのため、再読み込みしないと
                    // 古い LastPulledVersion で次回手動 Push が無駄なコンフリクトを起こす。
                    _settings = _runner.LoadSettings();
                    // VM と Coordinator で同じインスタンスを共有させて、これ以降は
                    // どちらの経路で更新しても両者が即座に最新を見るようにする。
                    coordinator.RefreshSettings(_settings);
                    break;
                case SyncOutcome.ConflictDetected:
                    AppendLog($"[auto] {e.DisplayName} Push 競合 v{e.Result.RemoteVersion}");
                    break;
                case SyncOutcome.Aborted:
                    AppendLog($"[auto] {e.DisplayName} Push 中止: {e.Result.Message}");
                    break;
                default:
                    AppendLog($"[auto] {e.DisplayName} Push: {e.Result.Outcome} {e.Result.Message}");
                    break;
            }
            RefreshStatusSummaries();
        });
        coordinator.AutoPushConflict += e => OnUi(() => _ = HandleAutoPushConflictAsync(e));
        coordinator.RemoteUpdateAvailable += e => OnUi(() => _ = HandleRemoteUpdateAsync(e));
        coordinator.ProcessDetectionChanged += () => OnUi(RefreshProcessDetection);
        // App は Coordinator.Start を背後で走らせてから画面を組み立てるため、ここへ来る
        // 前に最初の通知が出ていることがある。既に起動していたプロセスは監視の開始時に
        // 黙って取り込まれるので、ここで一度読まないとそのツールを閉じるまで表示が
        // 変わらない。
        RefreshProcessDetection();
    }

    private void OnUi(Action action)
    {
        if (_uiDispatch is null) action();
        else _uiDispatch(action);
    }

    [ObservableProperty]
    public partial string MachineName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CloudFolderPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool SyncVrcx { get; set; }

    [ObservableProperty]
    public partial bool SyncFriendConnect { get; set; }

    [ObservableProperty]
    public partial bool AutoSyncEnabled { get; set; }

    // データの保存先。0 = 同期フォルダ、1 = S3 互換ストレージ。
    // MainPage.xaml の ComboBox の並びと対応させる。
    [ObservableProperty]
    public partial int StorageModeIndex { get; set; }

    [ObservableProperty]
    public partial Visibility LocalFolderSettingsVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial Visibility S3SettingsVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial string S3ServiceUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string S3Region { get; set; } = "auto";

    [ObservableProperty]
    public partial string S3BucketName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string S3KeyPrefix { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string S3AccessKeyId { get; set; } = string.Empty;

    // 入力欄には保存済みのキーを出さない。空のまま保存した場合は既存のキーを保つ。
    [ObservableProperty]
    public partial string S3SecretAccessKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string S3SecretStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool S3UsePathStyle { get; set; } = true;

    [ObservableProperty]
    public partial bool S3UseConditionalWrites { get; set; } = true;

    partial void OnStorageModeIndexChanged(int value)
    {
        var isS3 = value == S3ModeIndex;
        LocalFolderSettingsVisibility = isS3 ? Visibility.Collapsed : Visibility.Visible;
        S3SettingsVisibility = isS3 ? Visibility.Visible : Visibility.Collapsed;
    }

    [ObservableProperty]
    public partial string VrcxStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FriendConnectStatus { get; set; } = string.Empty;

    // プロセスの検出状況。同期履歴を出す VrcxStatus / FriendConnectStatus とは
    // 別に持つ。片方は「いつ同期したか」、こちらは「いま動いているか」で、
    // 更新の切っ掛けも違う (前者は同期のたび、後者は起動と終了のたび)。
    [ObservableProperty]
    public partial string VrcxProcessStatus { get; set; } = ProcessDetectionUnknown;

    [ObservableProperty]
    public partial string FriendConnectProcessStatus { get; set; } = ProcessDetectionUnknown;

    /// <summary>監視が始まる前の表示。「動いていない」と書いてしまうと事実と違う。</summary>
    private const string ProcessDetectionUnknown = "プロセス監視: 未開始";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool StartupRegistered { get; set; }

    [ObservableProperty]
    public partial string StartupStatus { get; set; } = string.Empty;

    // VRCX Launch 設定
    [ObservableProperty]
    public partial string VrcxExecutablePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool VrcxLaunchOnAppStart { get; set; }

    // VRC Friend Connect Launch 設定
    [ObservableProperty]
    public partial string FriendConnectExecutablePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool FriendConnectLaunchOnAppStart { get; set; }

    // 本体の更新確認 (issue #45)。0 = 安定版、1 = テスト版。
    // MainPage.xaml の ComboBox の並びと対応させる。
    [ObservableProperty]
    public partial int UpdateChannelIndex { get; set; }

    [ObservableProperty]
    public partial string UpdateStatus { get; set; } = string.Empty;

    // 新しい版が見つかったときだけ出す行 (版の表示とリリースページへのボタン)。
    [ObservableProperty]
    public partial Visibility UpdateAvailableVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial string UpdateAvailableText { get; set; } = string.Empty;

    // リリースページの URL。表示行と一緒に更新する。
    private string? _releasePageUrl;

    // 取得済みで置き換え待ちの更新があるときだけ出す行 (issue #45 第 3 段階)。
    [ObservableProperty]
    public partial Visibility UpdateStagedVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial string UpdateStagedText { get; set; } = string.Empty;

    public ObservableCollection<string> LogEntries { get; } = new();

    public event Func<ConflictPrompt, Task<ConflictChoice>>? ConflictRequested;

    public event Func<RemoteUpdatePrompt, Task<RemoteUpdateChoice>>? RemoteUpdateRequested;

    public event Action? ShowWindowRequested;

    public event Action<string, string>? ToastRequested;

    /// <summary>
    /// 適用の準備に入った後か。準備を始める時点で立て、終了まで下ろさない
    /// (準備に失敗した場合だけ下ろす)。
    /// <para>
    /// ヘルパは渡した時点のチャンネルで照合を済ませている。その後で設定を
    /// 保存されると、保存済みの設定と実際に適用される版が食い違う。準備の間も
    /// 終了時 Push を待っている間もウィンドウは操作できるので、その窓を塞ぐ。
    /// </para>
    /// </summary>
    private bool _handedOverToUpdater;

    [RelayCommand]
    private void SaveSettings()
    {
        if (_handedOverToUpdater)
        {
            // 適用はもう動き出している。ここで設定を書き換えても反映されず、
            // 保存済みの設定と適用される版が食い違うだけになる。
            AppendLog("更新の適用中です。設定の保存は再起動後に行ってください。");
            return;
        }

        // 保存直前にディスクの現行値を読み直し、その上へ画面の値を載せる。
        // GUI を開いている間に CLI 側で設定が変わっていても、画面に無い項目
        // (同期履歴など) を起動時の古い値で巻き戻さないため。
        var settings = _runner.LoadSettings();
        // チャンネルを変えたときに確認し直すため、保存の前のディスク側の値を
        // 控えておく。
        var previousChannel = (settings.Update ?? new UpdateSettings()).Channel;
        settings.MachineName = string.IsNullOrWhiteSpace(MachineName) ? Environment.MachineName : MachineName.Trim();
        settings.CloudFolderPath = CloudFolderPath?.Trim() ?? string.Empty;
        settings.SyncVrcx = SyncVrcx;
        settings.SyncFriendConnect = SyncFriendConnect;
        settings.AutoSyncEnabled = AutoSyncEnabled;
        ApplyStoragePropertiesToSettings(settings);
        ApplyLaunchPropertiesToSettings(settings);
        ApplyUpdatePropertiesToSettings(settings);

        _settings = settings;
        _runner.SaveSettings(_settings);
        _coordinator?.UpdateSettings(_settings);

        // 入力欄のシークレットキーは保存後に消す。画面に残し続ける必要はない。
        S3SecretAccessKey = string.Empty;
        RefreshSecretStatus();
        RefreshStatusSummaries();
        AppendLog($"設定を保存しました (保存先: {SyncStorageFactory.DescribeTarget(_settings)}, " +
                  $"auto-sync={(_settings.AutoSyncEnabled ? "ON" : "OFF")})");

        // チャンネルを変えた直後は、表示が前のチャンネルの結果のまま残る。
        // 状態欄を新しいチャンネルの確認状態から作り直し、まだ確認できて
        // いなければ確認し直す。
        RefreshUpdateBanner();
        // 取得済みの行も作り直す。stable へ切り替えると、test で取ったものは
        // 出さなくなる (押しても捨てられるだけなので、出したままにしない)。
        RefreshStagedRow();
        // ApplyUpdatePropertiesToSettings が必ず入れているが、JSON から明示的な
        // null が入りうる型なので、読む側では既定へ落として扱う。
        var update = _settings.Update ?? new UpdateSettings();
        var channel = update.Channel;
        if (_updates is not null)
        {
            // チャンネルを変えたら、確認済みでも確認し直す。そのチャンネルの
            // 確認済みの印だけでは足りない。stable の結果がある状態で test へ
            // 変え、その確認の途中で stable へ戻すと、印は残っているのに、
            // 遅れて届いた test の結果が確認の状態を上書きしてしまう。次の
            // 定期確認まで最大 1 日、古い結果を「最新」と出し続けることになる。
            if (previousChannel != channel)
            {
                UpdateStatus = "確認しています...";
                _ = _updates.CheckAsync();
            }
            else if (!_updates.HasChecked(channel))
            {
                UpdateStatus = $"未確認 (実行中: {_updates.CurrentVersion})";
                _ = _updates.CheckAsync();
            }
            else if (_updates.Available(channel) is { } available)
            {
                UpdateStatus = $"新しい版 {available.Tag} が出ています (実行中: {_updates.CurrentVersion})";
            }
            else
            {
                UpdateStatus = _updates.IsComplete
                    ? $"最新の版を利用中 ({_updates.CurrentVersion})"
                    : "新しい版は見つかりませんでしたが、一覧を集めきれていないため最新とは言い切れません";
            }
        }
    }

    /// <summary>保存先の設定を画面へ読み込む。</summary>
    private void LoadStorageSettingsToProperties()
    {
        StorageModeIndex = _settings.StorageMode == SyncStorageMode.S3 ? S3ModeIndex : 0;

        var s3 = _settings.S3;
        S3ServiceUrl = s3?.ServiceUrl ?? string.Empty;
        S3Region = string.IsNullOrWhiteSpace(s3?.Region) ? "auto" : s3!.Region;
        S3BucketName = s3?.BucketName ?? string.Empty;
        S3KeyPrefix = s3?.KeyPrefix ?? string.Empty;
        S3AccessKeyId = s3?.AccessKeyId ?? string.Empty;
        S3UsePathStyle = s3?.UsePathStyle ?? true;
        S3UseConditionalWrites = s3?.UseConditionalWrites ?? true;
        S3SecretAccessKey = string.Empty;
        RefreshSecretStatus();
    }

    private void ApplyStoragePropertiesToSettings(SyncSettings settings)
    {
        settings.StorageMode = StorageModeIndex == S3ModeIndex
            ? SyncStorageMode.S3
            : SyncStorageMode.LocalFolder;

        // S3 の欄は、保存先を切り替えても消さずに残す。切り替えて戻したときに
        // 入力し直さずに済むほうが扱いやすい。
        var existing = settings.S3;
        settings.S3 = new S3Settings
        {
            ServiceUrl = S3ServiceUrl?.Trim() ?? string.Empty,
            Region = string.IsNullOrWhiteSpace(S3Region) ? "auto" : S3Region.Trim(),
            BucketName = S3BucketName?.Trim() ?? string.Empty,
            KeyPrefix = S3KeyPrefix?.Trim().Trim('/') ?? string.Empty,
            AccessKeyId = S3AccessKeyId?.Trim() ?? string.Empty,
            // 入力が空なら、既に保存済みのキーをそのまま保つ。
            ProtectedSecretAccessKey = string.IsNullOrEmpty(S3SecretAccessKey)
                ? existing?.ProtectedSecretAccessKey
                : SecretProtector.Protect(S3SecretAccessKey),
            UsePathStyle = S3UsePathStyle,
            UseConditionalWrites = S3UseConditionalWrites,
            TimeoutSeconds = existing?.TimeoutSeconds ?? 1800,
        };
    }

    private void RefreshSecretStatus()
    {
        S3SecretStatus = string.IsNullOrEmpty(_settings.S3?.ProtectedSecretAccessKey)
            ? "未設定。API キー発行時に一度だけ表示される値を貼り付けてください。"
            : "保存済み。変更する場合のみ入力してください (空欄なら現在のキーを保ちます)。";
    }

    /// <summary>保存先へ到達できるかを確かめる。画面の入力内容をそのまま使う。</summary>
    [RelayCommand]
    private async Task TestStorageAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            // 画面の入力を反映した一時的な設定で試す。ここでは保存しない。
            var probe = _runner.LoadSettings();
            ApplyStoragePropertiesToSettings(probe);
            probe.CloudFolderPath = CloudFolderPath?.Trim() ?? string.Empty;

            var manifest = await Task.Run(() =>
            {
                var storage = _runner.CreateStorage(probe);
                // 読み取りだけでなく書き込みと削除まで確認する。読み取り専用の
                // 認証情報だと、保存した後の最初の Push で初めて失敗するため。
                storage.VerifyAccess();
                return storage.LoadManifest().Manifest;
            });

            AppendLog($"接続を確認しました (読み取り / 書き込み / 削除): {SyncStorageFactory.DescribeTarget(probe)}");
            if (manifest.Tools.Count == 0)
            {
                AppendLog("  同期先にはまだデータがありません");
            }
            foreach (var (key, entry) in manifest.Tools)
            {
                AppendLog($"  [{key}] version={entry.Version} machine={entry.MachineName}");
            }
        }
        catch (SyncStorageException ex)
        {
            AppendLog($"接続できませんでした: {ex.Message}");
        }
        catch (Exception ex)
        {
            AppendLog($"接続確認に失敗しました: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void LoadLaunchConfigToProperties()
    {
        var vrcx = _settings.Launch.GetValueOrDefault(VrcxSyncService.Key) ?? new ToolLaunchConfig();
        VrcxExecutablePath = vrcx.ExecutablePath ?? string.Empty;
        VrcxLaunchOnAppStart = vrcx.LaunchOnAppStart;

        var fc = _settings.Launch.GetValueOrDefault(FriendConnectSyncService.Key) ?? new ToolLaunchConfig();
        FriendConnectExecutablePath = fc.ExecutablePath ?? string.Empty;
        FriendConnectLaunchOnAppStart = fc.LaunchOnAppStart;
    }

    private void ApplyLaunchPropertiesToSettings(SyncSettings settings)
    {
        // 既存の Arguments は GUI で編集できないが、JSON を手編集して
        // 起動オプションを与えているユーザもいる。設定保存のたびに
        // 新規 ToolLaunchConfig を作ると Arguments が消えるので、
        // 既存 entry の値を引き継いでから上書きする。
        settings.Launch ??= new Dictionary<string, ToolLaunchConfig>();
        var existingVrcx = settings.Launch.GetValueOrDefault(VrcxSyncService.Key);
        settings.Launch[VrcxSyncService.Key] = new ToolLaunchConfig
        {
            ExecutablePath = string.IsNullOrWhiteSpace(VrcxExecutablePath) ? null : VrcxExecutablePath.Trim(),
            Arguments = existingVrcx?.Arguments,
            LaunchOnAppStart = VrcxLaunchOnAppStart,
        };
        var existingFc = settings.Launch.GetValueOrDefault(FriendConnectSyncService.Key);
        settings.Launch[FriendConnectSyncService.Key] = new ToolLaunchConfig
        {
            ExecutablePath = string.IsNullOrWhiteSpace(FriendConnectExecutablePath) ? null : FriendConnectExecutablePath.Trim(),
            Arguments = existingFc?.Arguments,
            LaunchOnAppStart = FriendConnectLaunchOnAppStart,
        };
    }

    /// <summary>本体の更新確認の設定を画面へ読み込む (issue #45)。</summary>
    private void LoadUpdateSettingsToProperties()
    {
        var update = _settings.Update ?? new UpdateSettings();
        UpdateChannelIndex = update.Channel == UpdateChannel.Test ? TestChannelIndex : 0;
        UpdateStatus = _updates is null
            ? "更新確認は利用できません"
            : $"未確認 (実行中: {_updates.CurrentVersion})";
    }

    private void ApplyUpdatePropertiesToSettings(SyncSettings settings)
    {
        settings.Update ??= new UpdateSettings();
        settings.Update.Channel = UpdateChannelIndex == TestChannelIndex
            ? UpdateChannel.Test
            : UpdateChannel.Stable;
        // NotifiedVersion は画面で編集しない。読み込んだ値をそのまま保つ。
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        var url = _releasePageUrl;
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            // 既定のブラウザで開く。UseShellExecute が無いと URL を直接は開けない。
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppendLog($"リリースページを開けませんでした: {ex.Message}");
        }
    }

    /// <summary>
    /// 確認の結果を画面へ反映する。UI スレッドで呼ばれる。
    /// <para>
    /// 知らせ済みの版は UpToDate に倒されて届く。その場合も新しい版の行は
    /// 出したままにする。通知を抑えるのは繰り返しのバルーンであって、
    /// 画面からの導線ではない。
    /// </para>
    /// </summary>
    private void HandleUpdateCheckCompleted(UpdateCheckResult result, UpdateChannel channel)
    {
        if (_updates is null) return;

        // 確認中にチャンネルを変えて保存すると、前のチャンネルの結果が遅れて届く。
        // 保存済みの設定と食い違う結果を載せると、stable を選び直した直後に
        // プレリリースを知らせ、その記録が以後の stable の通知まで抑えてしまう。
        // 保存の側が新しいチャンネルでの確認を仕掛けており、結果は改めて届く。
        var savedChannel = _settings.Update?.Channel ?? UpdateChannel.Stable;
        if (channel != savedChannel) return;

        var current = _updates.CurrentVersion;

        UpdateStatus = result.Outcome switch
        {
            UpdateCheckOutcome.Available =>
                $"新しい版 {result.Release!.Tag} が出ています (実行中: {current})",
            UpdateCheckOutcome.UpToDate when result.Release is not null =>
                $"新しい版 {result.Release.Tag} が出ています (通知済み)",
            UpdateCheckOutcome.UpToDate =>
                $"最新の版を利用中 ({current})",
            UpdateCheckOutcome.Unreachable =>
                "確認できませんでした。通信できないか、GitHub が応答しませんでした",
            UpdateCheckOutcome.Incomplete =>
                "新しい版は見つかりませんでしたが、一覧を集めきれていないため最新とは言い切れません",
            UpdateCheckOutcome.Unknown =>
                $"手元ビルド ({current}) のため確認しません",
            _ => UpdateStatus,
        };

        RefreshUpdateBanner();

        if (result.Outcome == UpdateCheckOutcome.Available && result.Release is { } release)
        {
            AppendLog($"新しい版 {release.Tag} が出ています (実行中: {current})");
            // 確認は背後で走るので、画面を見ていない利用者にも届くようバルーンを出す。
            ToastRequested?.Invoke(
                "VRCToolsDataSync の更新",
                $"新しい版 {release.Tag} が出ています。ウィンドウの設定から開けます。");
            // 画面と通知に出せた後で覚える。出せなかった版まで覚えると、
            // 利用者が一度も見ないまま以後の確認で抑止される。
            _updates.MarkNotified(release);
        }
    }

    /// <summary>
    /// 取得済みの行を staged の記録へ合わせる。記録はここでは照合しない。
    /// 照合は適用の直前と次の起動が行い、通らなければそこで捨てられる。
    /// </summary>
    private void RefreshStagedRow()
    {
        var staged = _updates?.Staged;

        // 保存されているチャンネルで拾わないものは出さない。test で取った後に
        // stable へ切り替えると、その取得は次の起動でもボタンでも捨てられる。
        // 出したままだと、押して初めて消える食い違いになる。
        var channel = _settings.Update?.Channel ?? UpdateChannel.Stable;
        if (staged is not null && channel != UpdateChannel.Test && !staged.Stable)
        {
            staged = null;
        }

        if (staged is null)
        {
            UpdateStagedVisibility = Visibility.Collapsed;
            UpdateStagedText = string.Empty;
            return;
        }
        UpdateStagedText = UpdateApplier.StartedAfterDeferral
            ? $"{staged.Tag} を取得済み。前回の再起動では適用できませんでした (ログを確認してください)"
            : $"{staged.Tag} を取得済み。次回起動時に適用されます";
        UpdateStagedVisibility = Visibility.Visible;
    }

    /// <summary>
    /// 「再起動して適用」ボタン。更新ヘルパを起動してから通常の終了シーケンスへ
    /// 入る。ヘルパはこのプロセスの終了を待って置き換え、新しい版を立ち上げ直す。
    /// </summary>
    [RelayCommand]
    private async Task ApplyStagedUpdateAsync()
    {
        if (_updates is null || IsBusy) return;

        // 準備の間 (ロックの待ちは最大 11 分、ヘルパの起動確認に 3 秒) も
        // 塞いでおく。ここが空いていると、この後の終了時 Push と並走する手動の
        // 同期を始められてしまい、その同期は Coordinator の追跡の外なので
        // Environment.Exit で途中のまま切れる。
        IsBusy = true;

        // 設定の保存はここから止める。準備の中でヘルパを起こし、その生存を
        // 3 秒見る間も画面は操作できる。渡した後に stable へ変えて保存されると、
        // 保存済みの設定と適用される版が食い違う。準備に失敗したら下ろす。
        _handedOverToUpdater = true;
        bool ready;
        try
        {
            // 起動時の同期がまだ走っていることがある。そのタスクは Coordinator の
            // 追跡の外なので、待たずに進むと終了時 Push と並走した上で
            // Environment.Exit に途中で切られる。
            if (!App.StartupSyncFinished.IsCompleted)
            {
                AppendLog("起動時の同期の完了を待っています...");
                await App.StartupSyncFinished;
            }

            ready = await Task.Run(() => _updates.PrepareApplyAndSpawnUpdater());
        }
        catch
        {
            _handedOverToUpdater = false;
            IsBusy = false;
            throw;
        }

        if (!ready)
        {
            _handedOverToUpdater = false;
            IsBusy = false;
            AppendLog("更新を適用できませんでした。取得し直すか、ログを確認してください。");
            RefreshStagedRow();
            return;
        }
        AppendLog("更新を適用するため再起動します...");

        // 終了処理の途中で多重起動の抑止を手放さない。手放してから実際に終わる
        // までの隙に別の App が起動すると、待っているヘルパと入れ替えがぶつかる。
        App.KeepSingleInstanceUntilExit();

        // Tray「同期して終了」と同じ経路で閉じる。終了時 Push もそのまま流れる。
        await App.ExitApplicationAsync(waitForToolsToExit: null);

        // ここへ戻ってきたのは、終了に至らなかった場合だけである (ログオフの
        // 取り消しや、先に始まっていた終了処理との重なり)。適用のロックは
        // プロセスの終了で手放す約束で握ったままなので、明示的に返す。
        // 持ち続けると、この後の取得の昇格も、起こしたヘルパの適用も止まる。
        UpdateApplier.ReleaseHeldApplyLock();

        // 起こしたヘルパはロックと一緒に止めた。適用は動いていないので、
        // 設定の保存も受け付け直す。
        _handedOverToUpdater = false;
        IsBusy = false;
        AppendLog("終了が取り消されたため、更新は次回起動時に適用されます。");
        RefreshStagedRow();
    }

    /// <summary>
    /// 新しい版の行を、保存済みのチャンネルで見つけているものへ合わせる。
    /// 別のチャンネルの結果は UpdateManager 側が返さないため、
    /// チャンネルを切り替えた直後は行が消える (確認し直すと戻る)。
    /// </summary>
    private void RefreshUpdateBanner()
    {
        var available = _updates?.Available(_settings.Update?.Channel ?? UpdateChannel.Stable);
        if (available is null)
        {
            UpdateAvailableVisibility = Visibility.Collapsed;
            UpdateAvailableText = string.Empty;
            _releasePageUrl = null;
            return;
        }
        UpdateAvailableText = $"{available.Tag} を取得できます";
        _releasePageUrl = available.HtmlUrl;
        UpdateAvailableVisibility = Visibility.Visible;
    }

    /// <summary>
    /// トレイ「同期して起動」と MainPage の同名ボタンから呼ばれる。
    /// 同期 ON のツールを Pull → Launch する。既に動いていれば Launch は no-op。
    /// 未保存の同期フォルダパスが UI にあれば実行前に反映する (TryCreateStorage)。
    /// </summary>
    [RelayCommand]
    private async Task SyncAndLaunchAsync()
    {
        if (IsBusy) return;
        // UI で編集中の CloudFolderPath を _settings へ反映してから走らせる。
        // RunPushAsync/RunPullAsync が TryCreateStorage で行っているのと同じ前処理。
        if (!TryCreateStorage(out _)) return;
        IsBusy = true;
        try
        {
            var orchestrator = new StartupSyncOrchestrator(
                _runner,
                logger: _runner.CreateLogger<StartupSyncOrchestrator>());
            var steps = await Task.Run(() => orchestrator.Run(_settings));
            IngestStartupSteps(steps);
        }
        catch (Exception ex)
        {
            AppendLog($"同期して起動 エラー: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 「自動検出」ボタン用。実行ファイルパスを TryFindExecutable から埋める。
    /// 見つからなければ何もしない (ユーザに「参照…」ボタンを使わせる)。
    /// </summary>
    [RelayCommand]
    private void DetectVrcxExecutable()
    {
        var path = VrcxPaths.TryFindExecutable();
        if (path is null)
        {
            AppendLog("VRCX 実行ファイルを自動検出できませんでした。参照ボタンで指定してください。");
            return;
        }
        VrcxExecutablePath = path;
        AppendLog($"VRCX 実行ファイルを検出: {path}");
    }

    [RelayCommand]
    private void DetectFriendConnectExecutable()
    {
        var path = FriendConnectPaths.TryFindExecutable();
        if (path is null)
        {
            AppendLog("VRC Friend Connect 実行ファイルを自動検出できませんでした。参照ボタンで指定してください。");
            return;
        }
        FriendConnectExecutablePath = path;
        AppendLog($"VRC Friend Connect 実行ファイルを検出: {path}");
    }

    [RelayCommand]
    private void RegisterStartup()
    {
        var path = ResolveExecutablePath();
        if (path is null)
        {
            AppendLog("起動ファイルパスを特定できませんでした (Environment.ProcessPath が null)");
            return;
        }
        try
        {
            StartupRegistration.Register(path);
            AppendLog($"スタートアップに登録しました: {path}");
        }
        catch (Exception ex)
        {
            AppendLog($"スタートアップ登録に失敗: {ex.Message}");
        }
        RefreshStartupState();
    }

    [RelayCommand]
    private void UnregisterStartup()
    {
        try
        {
            StartupRegistration.Unregister();
            AppendLog("スタートアップから解除しました");
        }
        catch (Exception ex)
        {
            AppendLog($"スタートアップ解除に失敗: {ex.Message}");
        }
        RefreshStartupState();
    }

    private static string? ResolveExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) return null;
        // dotnet run の場合は dotnet.exe が返るため、その場合は登録に不向きであることを許容して
        // そのまま返す（実機配布の exe では VRCToolsDataSync.App.exe が返る）
        return path;
    }

    private void RefreshStartupState()
    {
        var registered = StartupRegistration.IsRegistered();
        StartupRegistered = registered;
        if (registered)
        {
            var cmd = StartupRegistration.GetRegisteredCommand();
            StartupStatus = $"登録済み: {cmd}";
        }
        else
        {
            StartupStatus = "未登録";
        }
    }

    [RelayCommand]
    private Task PushVrcx() => RunPushAsync("VRCX", new VrcxSyncService(logger: _runner.CreateLogger<VrcxSyncService>()));

    [RelayCommand]
    private Task PullVrcx() => RunPullAsync("VRCX", new VrcxSyncService(logger: _runner.CreateLogger<VrcxSyncService>()));

    [RelayCommand]
    private Task PushFriendConnect() => RunPushAsync("VRC Friend Connect", new FriendConnectSyncService(logger: _runner.CreateLogger<FriendConnectSyncService>()));

    [RelayCommand]
    private Task PullFriendConnect() => RunPullAsync("VRC Friend Connect", new FriendConnectSyncService(logger: _runner.CreateLogger<FriendConnectSyncService>()));

    private async Task RunPushAsync(string displayName, ISyncService service)
    {
        if (!TryCreateStorage(out var storage)) return;
        IsBusy = true;
        try
        {
            AppendLog($"{displayName} Push 開始...");
            var result = await Task.Run(() => _runner.Push(service, _settings, storage, force: false));

            if (result.Outcome == SyncOutcome.ConflictDetected && ConflictRequested is not null)
            {
                var choice = await ConflictRequested.Invoke(new ConflictPrompt
                {
                    ToolDisplayName = displayName,
                    RemoteVersion = result.RemoteVersion ?? 0,
                    LastPulledVersion = result.LastPulledVersion ?? 0,
                });
                switch (choice)
                {
                    case ConflictChoice.ForceOverwrite:
                        AppendLog($"{displayName} 強制 Push 実行");
                        var forced = await Task.Run(() => _runner.Push(service, _settings, storage, force: true));
                        ReportPushResult(displayName, forced, storage);
                        break;
                    case ConflictChoice.PullFirst:
                        AppendLog($"{displayName} 先に Pull を実行");
                        var pulled = await Task.Run(() => _runner.Pull(service, _settings, storage, skipBackup: false));
                        ReportPullResult(displayName, pulled);
                        break;
                    default:
                        AppendLog($"{displayName} Push をキャンセル");
                        break;
                }
            }
            else
            {
                ReportPushResult(displayName, result, storage);
            }
        }
        catch (RunningProcessException ex)
        {
            AppendLog($"{displayName}: {ex.Message}");
        }
        catch (Exception ex)
        {
            AppendLog($"{displayName} エラー: {ex.Message}");
        }
        finally
        {
            RefreshStatusSummaries();
            IsBusy = false;
        }
    }

    private async Task RunPullAsync(string displayName, ISyncService service)
    {
        if (!TryCreateStorage(out var storage)) return;
        IsBusy = true;
        try
        {
            AppendLog($"{displayName} Pull 開始...");
            var result = await Task.Run(() => _runner.Pull(service, _settings, storage, skipBackup: false));
            ReportPullResult(displayName, result);
        }
        catch (RunningProcessException ex)
        {
            AppendLog($"{displayName}: {ex.Message}");
        }
        catch (Exception ex)
        {
            AppendLog($"{displayName} エラー: {ex.Message}");
        }
        finally
        {
            RefreshStatusSummaries();
            IsBusy = false;
        }
    }

    /// <summary>
    /// 手動での容量の解放 (issue #55) で使う猶予期間 (日)。
    /// <para>
    /// 画面の NumberBox と直に結ぶため double で持つ。設定には保存しない。
    /// 短くするのは「今そこにある容量を空けたい」ときの一回きりの操作で、
    /// 自動実行が使う既定 (<see cref="BlobGarbageCollector.DefaultGracePeriod"/>) を
    /// 恒久的に緩める意味にはならないためである。
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial double GcGraceDays { get; set; } = BlobGarbageCollector.DefaultGracePeriod.TotalDays;

    /// <summary>
    /// 画面から指定できる猶予期間の下限 (日)。
    /// <para>
    /// 0 を選ばせない。猶予期間は他の PC が送っている最中のデータを巻き込まない
    /// ための余裕で、解放の安全性はほぼこれに乗っている。0 では書かれたばかりの
    /// データがそのまま対象になり、削除の直前の読み直しも効かなくなる。
    /// 承知のうえで詰めたい場合は CLI の <c>--grace-days</c> を使う。
    /// </para>
    /// </summary>
    /// <remarks>
    /// x:Bind は静的メンバーをインスタンスの経路から解決できないため、
    /// 画面から読めるようインスタンスのプロパティにしている。
    /// </remarks>
    public double MinGcGraceDays => 1;

    /// <summary>画面から指定できる猶予期間の上限 (日)。</summary>
    public double MaxGcGraceDays => 365;

    /// <summary>
    /// 手動での容量の解放 (issue #55)。自動実行と違って間引かず今すぐ走らせ、
    /// 結果も失敗もログに出す。実行後は自動実行の記録が更新されるので、
    /// 直後の Push で重ねて走ることはない。
    /// </summary>
    [RelayCommand]
    private async Task CollectGarbageAsync()
    {
        if (!TryCreateStorage(out var storage)) return;

        // NumberBox は入力を消すと NaN を返す。範囲の内側へ寄せてから使う。
        var graceDays = double.IsNaN(GcGraceDays)
            ? BlobGarbageCollector.DefaultGracePeriod.TotalDays
            : Math.Clamp(GcGraceDays, MinGcGraceDays, MaxGcGraceDays);
        GcGraceDays = graceDays;

        IsBusy = true;
        try
        {
            AppendLog($"ストレージ容量の解放を開始 (猶予期間 {graceDays:0.#} 日)...");
            var result = await Task.Run(
                () => _runner.CollectGarbageNow(storage, TimeSpan.FromDays(graceDays)));
            AppendLog(
                $"ストレージ容量の解放 完了: {result.Deleted} 件 ({result.DescribeDeletedBytes()}) を削除 " +
                $"/ 参照あり {result.Live} 件 / 猶予期間内 {result.Young} 件");
            if (result.AbortedUploads > 0)
            {
                // 送信が途中で切れた断片は一覧に現れないまま課金される。
                AppendLog($"  未完了のアップロード {result.AbortedUploads} 件を中断しました");
            }
            if (result.Failed > 0)
            {
                AppendLog($"  {result.Failed} 件の削除に失敗しました。次回の実行で再度対象になります");
            }
            if (result.FailedUploads > 0)
            {
                // 削除の失敗と分けて出す。要る権限が違うので、混ぜると原因を切り分けられない。
                AppendLog(
                    $"  {result.FailedUploads} 件の未完了のアップロードを中断できませんでした " +
                    "(API キーの権限を確認してください)");
            }
        }
        catch (Exception ex)
        {
            // 同期先の不調 (SyncStorageException) に限らず、manifest の破損
            // (JsonException) や実行時刻を記録できない場合 (IOException など) も
            // ここで受ける。RelayCommand から漏らすと画面に何も出ない。
            AppendLog($"ストレージ容量の解放 失敗: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReportPushResult(string displayName, SyncResult result, ISyncStorage storage)
    {
        switch (result.Outcome)
        {
            case SyncOutcome.Success:
                AppendLog($"{displayName} Push 完了 version={result.RemoteVersion} files={result.AffectedFiles.Count}");
                // 常駐側 Coordinator が保持する settings の LastPulledVersion を
                // 同期させ、続く自動 Push が古いバージョンで不要な競合通知を
                // 起こさないようにする。
                _coordinator?.RefreshSettings(_settings);
                // Push の後始末として、参照が切れた実体の回収を試みる (issue #55)。
                // UI スレッドを待たせないようバックグラウンドで実行する。
                _runner.CollectGarbageInBackground(storage);
                break;
            case SyncOutcome.SourceMissing:
                AppendLog($"{displayName} Push 中止: {result.Message}");
                break;
            case SyncOutcome.ConflictDetected:
                AppendLog($"{displayName} Push コンフリクト: remote v{result.RemoteVersion}, lastPulled v{result.LastPulledVersion}");
                break;
            default:
                AppendLog($"{displayName} Push: {result.Outcome} {result.Message}");
                break;
        }
    }

    private void ReportPullResult(string displayName, SyncResult result)
    {
        switch (result.Outcome)
        {
            case SyncOutcome.Success:
                AppendLog($"{displayName} Pull 完了 version={result.RemoteVersion} backup={result.BackupPath ?? "(none)"}");
                // 常駐側 Coordinator にも反映 (ReportPushResult と同様の理由)。
                _coordinator?.RefreshSettings(_settings);
                break;
            case SyncOutcome.NothingToDo:
            case SyncOutcome.SourceMissing:
                AppendLog($"{displayName} Pull: {result.Message}");
                break;
            default:
                AppendLog($"{displayName} Pull: {result.Outcome} {result.Message}");
                break;
        }
    }

    private async Task HandleAutoPushConflictAsync(AutoPushConflictEvent e)
    {
        AppendLog($"[auto] {e.DisplayName} Push 競合 remote=v{e.RemoteVersion} (要操作)");
        ToastRequested?.Invoke(
            $"{e.DisplayName}: 自動 Push が競合しました",
            $"リモート v{e.RemoteVersion} と未同期です。ウィンドウで操作を選択してください。");
        ShowWindowRequested?.Invoke();

        if (ConflictRequested is null) return;

        // ContentDialog は WinUI 上で同時に複数表示できないため、
        // 自動通知のダイアログは _dialogGate で1件ずつ処理する。
        await _dialogGate.WaitAsync();
        try
        {
            var choice = await ConflictRequested.Invoke(new ConflictPrompt
            {
                ToolDisplayName = e.DisplayName,
                RemoteVersion = e.RemoteVersion,
                LastPulledVersion = e.LastPulledVersion,
            });

            if (!TryCreateStorage(out var storage)) return;

            switch (choice)
            {
                case ConflictChoice.ForceOverwrite:
                    AppendLog($"[auto] {e.DisplayName} 強制 Push 実行");
                    var pushResult = await Task.Run(() => _runner.Push(e.ServiceFactory(), _settings, storage, force: true));
                    ReportPushResult(e.DisplayName, pushResult, storage);
                    break;
                case ConflictChoice.PullFirst:
                    AppendLog($"[auto] {e.DisplayName} 先に Pull を実行");
                    var pullResult = await Task.Run(() => _runner.Pull(e.ServiceFactory(), _settings, storage, skipBackup: false));
                    ReportPullResult(e.DisplayName, pullResult);
                    break;
                default:
                    AppendLog($"[auto] {e.DisplayName} Push をキャンセル");
                    break;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[auto] {e.DisplayName} 競合処理エラー: {ex.Message}");
        }
        finally
        {
            RefreshStatusSummaries();
            _dialogGate.Release();
        }
    }

    private async Task HandleRemoteUpdateAsync(RemoteUpdateEvent e)
    {
        AppendLog($"[auto] {e.DisplayName} リモート更新 v{e.RemoteVersion} (by {e.MachineName})");
        ToastRequested?.Invoke(
            $"{e.DisplayName}: リモートに更新があります",
            $"{e.MachineName} が v{e.RemoteVersion} を Push しました。Pull しますか？");
        ShowWindowRequested?.Invoke();

        if (RemoteUpdateRequested is null) return;

        // ContentDialog の同時表示は不可。AutoPushConflict と共通の
        // _dialogGate で1件ずつ処理する。
        await _dialogGate.WaitAsync();
        try
        {
            var choice = await RemoteUpdateRequested.Invoke(new RemoteUpdatePrompt
            {
                ToolDisplayName = e.DisplayName,
                RemoteVersion = e.RemoteVersion,
                LocalVersion = e.LocalVersion,
                MachineName = e.MachineName,
            });

            if (choice != RemoteUpdateChoice.PullNow) return;
            if (!TryCreateStorage(out var storage)) return;

            AppendLog($"[auto] {e.DisplayName} Pull 実行");
            var pullResult = await Task.Run(() => _runner.Pull(e.ServiceFactory(), _settings, storage, skipBackup: false));
            ReportPullResult(e.DisplayName, pullResult);
        }
        catch (Exception ex)
        {
            AppendLog($"[auto] {e.DisplayName} Pull エラー: {ex.Message}");
        }
        finally
        {
            RefreshStatusSummaries();
            _dialogGate.Release();
        }
    }

    /// <summary>
    /// 同期を始める前に、設定から同期先を組み立てる。
    /// ローカルフォルダモードでは、UI で編集中のパスを設定へ反映してから作る。
    /// 組み立てられない場合は理由をログに出して false を返す。
    /// </summary>
    private bool TryCreateStorage(out ISyncStorage storage)
    {
        storage = null!;

        if (_settings.StorageMode == SyncStorageMode.LocalFolder)
        {
            var cloud = CloudFolderPath?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(cloud))
            {
                AppendLog("同期フォルダのパスを指定して「設定を保存」してください");
                return false;
            }
            // 設定が未保存だった場合のために、同期実行時にも保存を反映しておく。
            // CloudFolderPath が変わった場合は常駐 Coordinator の監視も
            // 旧パスを見たままになってしまうので、UpdateSettings で再起動して
            // 新パスに張り替える (Watcher 再構築を伴う)。
            if (_settings.CloudFolderPath != cloud && System.IO.Directory.Exists(cloud))
            {
                _settings.CloudFolderPath = cloud;
                _runner.SaveSettings(_settings);
                _coordinator?.UpdateSettings(_settings);
            }
        }

        try
        {
            storage = _runner.CreateStorage(_settings, CloudFolderPath?.Trim());
            return true;
        }
        catch (SyncStorageException ex)
        {
            AppendLog(ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 同期履歴の表示を更新する。同期履歴は保存先ごとに分かれているので、
    /// 現在の保存先の分だけを拾う。保存先を組み立てられない (未設定など) 段階では
    /// 未同期として表示する。
    /// </summary>
    private void RefreshStatusSummaries()
    {
        if (!SyncStorageFactory.TryCreate(_settings, out var storage, out _, CloudFolderPath?.Trim()))
        {
            VrcxStatus = FormatStatus(null);
            FriendConnectStatus = FormatStatus(null);
            return;
        }
        VrcxStatus = FormatStatus(SyncRunner.FindToolState(_settings, storage!, VrcxSyncService.Key));
        FriendConnectStatus = FormatStatus(SyncRunner.FindToolState(_settings, storage!, FriendConnectSyncService.Key));
    }

    /// <summary>
    /// プロセス検出状況の表示を更新する。
    /// <para>
    /// 通知は「変わった」ことしか伝えないので、そのたびに現在の状態を読み直す。
    /// 通知に状態を載せると、遅れて届いた分が新しい表示を古い状態で上書きしうる。
    /// </para>
    /// </summary>
    private void RefreshProcessDetection()
    {
        if (_coordinator is null) return;
        foreach (var detection in _coordinator.GetProcessDetections())
        {
            ApplyProcessDetection(detection);
        }
    }

    /// <summary>検出状況 1 件を宛先の表示へ振り分ける。</summary>
    private void ApplyProcessDetection(ProcessDetectionEvent e)
    {
        var text = FormatProcessDetection(e);
        if (e.ToolKey == VrcxSyncService.Key) VrcxProcessStatus = text;
        else if (e.ToolKey == FriendConnectSyncService.Key) FriendConnectProcessStatus = text;
    }

    /// <summary>
    /// 検出状況を 1 行にする。
    /// <para>
    /// 起動中は<b>当たった名前も出す</b>。実行ファイル名は配布のされ方で変わりうるため
    /// 候補を複数持っており、どれも当たらない場合、利用者には「自動 Push が動かない」
    /// ことしか見えない。当たった名前を出すことで、候補に無い名前で配布されていることに
    /// 気付けるようにする (issue #11)。
    /// </para>
    /// </summary>
    private static string FormatProcessDetection(ProcessDetectionEvent e)
    {
        if (!e.IsWatching) return "プロセス監視: 停止中";
        if (!e.IsRunning) return "プロセス: 未検出";
        return $"プロセス: 検出中 ({string.Join(", ", e.DetectedProcessNames)})";
    }

    private static string FormatStatus(ToolSyncState? state)
    {
        if (state is null) return "未同期";
        var parts = new List<string>();
        if (state.LastPushedAt is { } pushed) parts.Add($"push v{state.LastPushedVersion} @ {pushed.LocalDateTime:yyyy-MM-dd HH:mm}");
        if (state.LastPulledAt is { } pulled) parts.Add($"pull v{state.LastPulledVersion} @ {pulled.LocalDateTime:yyyy-MM-dd HH:mm}");
        return parts.Count == 0 ? "未同期" : string.Join(" / ", parts);
    }

    private void AppendLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        LogEntries.Insert(0, line);
        while (LogEntries.Count > 200) LogEntries.RemoveAt(LogEntries.Count - 1);
    }
}

public sealed class ConflictPrompt
{
    public required string ToolDisplayName { get; init; }
    public required long RemoteVersion { get; init; }
    public required long LastPulledVersion { get; init; }
}

public enum ConflictChoice
{
    Cancel,
    PullFirst,
    ForceOverwrite,
}

public sealed class RemoteUpdatePrompt
{
    public required string ToolDisplayName { get; init; }
    public required long RemoteVersion { get; init; }
    public required long LocalVersion { get; init; }
    public required string MachineName { get; init; }
}

public enum RemoteUpdateChoice
{
    Later,
    PullNow,
}
