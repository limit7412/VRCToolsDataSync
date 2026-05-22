using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using H.NotifyIcon;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using VRCToolsDataSync.Core.Logging;
using VRCToolsDataSync.Core.Settings;
using VRCToolsDataSync.Core.Sync;
using VRCToolsDataSync.Core.Watch;
using VRCToolsDataSync_App.Tray;

namespace VRCToolsDataSync_App;

public partial class App : Application
{
    // 多重起動防止用の Named Mutex。
    // 自動起動 + ユーザのショートカット二重起動などで 2 つの App プロセスが
    // 並走すると、AutoSyncCoordinator が二重 Push を行って manifest.json の
    // 競合検知が暴発するため、プロセス内 _autoPushLock では守れない領域として
    // ここでガードする。Global\ プリフィクスは付けずユーザセッション内のみ排他にする。
    private const string SingleInstanceMutexName = "VRCToolsDataSync.App.SingleInstance";
    // 既存インスタンスへの「メインウィンドウ復帰」要求に使う Win32 メッセージ。
    // RegisterWindowMessage はユーザセッション内で同名 → 同一 ID が返るため、
    // 別プロセス間でも安全に同期できる。
    private const string ShowWindowMessageName = "VRCToolsDataSync.App.ShowMainWindow";
    private static Mutex? _singleInstanceMutex;
    internal static uint ShowMainWindowMessageId { get; private set; }
    public static Window Window { get; private set; } = null!;

    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    // AutoSync 系の挙動を %AppData%\VRCToolsDataSync\logs\sync-YYYYMMDD.log に
    // 残すため、GUI でも CLI と同じ FileLoggerProvider を共有する。
    // SyncRunner / AutoSyncCoordinator / 各 SyncService がここから ILogger を取得する。
    public static ILoggerFactory LoggerFactory { get; } = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(LogLevel.Information);
        builder.AddProvider(new FileLoggerProvider(FileLoggerProvider.DefaultLogPath()));
    });

    public static SyncRunner Runner { get; } = new(loggerFactory: LoggerFactory);

    public static AutoSyncCoordinator? Coordinator { get; private set; }

    // Issue #6: 起動時の Pull → Launch のステップログ。MainPage / UI 側で
    // 起動直後のサマリをログに出すのに使う (GUI 構築前に走るため、ここに溜める)。
    // 直接読まずに必ず SubscribeStartupSyncSteps 経由で取り出すこと
    // (取り込みと購読の競合を回避するため)。
    private static readonly object _startupSyncStepsLock = new();
    private static System.Collections.Generic.IReadOnlyList<StartupSyncStep>? _startupSyncSteps;
    private static Action<System.Collections.Generic.IReadOnlyList<StartupSyncStep>>? _startupSyncStepsHandlers;

    /// <summary>
    /// 起動同期 (Pull → Launch) のステップ取り込み口を購読する。
    /// 「既に Run 完了済みなら直ちに <paramref name="handler"/> を呼ぶ」「未完了なら次の Run 完了時に呼ぶ」を
    /// lock 下でアトミックに行うので、判定と購読の隙間でステップを取りこぼしたり
    /// 二重取り込みしたりすることが無い。
    /// </summary>
    public static void SubscribeStartupSyncSteps(Action<System.Collections.Generic.IReadOnlyList<StartupSyncStep>> handler)
    {
        System.Collections.Generic.IReadOnlyList<StartupSyncStep>? alreadyAvailable;
        lock (_startupSyncStepsLock)
        {
            alreadyAvailable = _startupSyncSteps;
            _startupSyncStepsHandlers += handler;
        }
        if (alreadyAvailable is not null)
        {
            try { handler(alreadyAvailable); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// バックグラウンドの Orchestrator.Run 完了時に呼び、ステップを保存して
    /// 既存購読者へも通知する。lock で <see cref="SubscribeStartupSyncSteps"/> と
    /// 競合しないようにする。
    /// </summary>
    private static void PublishStartupSyncSteps(System.Collections.Generic.IReadOnlyList<StartupSyncStep> steps)
    {
        Action<System.Collections.Generic.IReadOnlyList<StartupSyncStep>>? handlers;
        lock (_startupSyncStepsLock)
        {
            _startupSyncSteps = steps;
            handlers = _startupSyncStepsHandlers;
        }
        if (handlers is not null)
        {
            try { handlers(steps); } catch { /* best-effort */ }
        }
    }

    public static TrayIconManager Tray { get; } = new();

    // タスクトレイから「終了」を選んだとき、Window.Closed で
    // タスクトレイ最小化に切り替えないために立てるフラグ。
    private static bool _isExiting;

    public App()
    {
        InitializeComponent();

        UnhandledException += (_, e) =>
        {
            LogStartupFailure("UnhandledException", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            LogStartupFailure("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogStartupFailure("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// 多重起動を検出した場合に true を返す。既存インスタンスに対しては
    /// HWND_BROADCAST で復帰要求メッセージを送ってから自身は終了する想定。
    /// </summary>
    internal static bool TryAcquireSingleInstance()
    {
        try
        {
            ShowMainWindowMessageId = RegisterWindowMessage(ShowWindowMessageName);
            _singleInstanceMutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, out var createdNew);
            if (createdNew)
            {
                LogLifecycle("SingleInstance.acquired");
                return true;
            }
            LogLifecycle("SingleInstance.already-running -> notify existing");
            // 既存インスタンスに対して「ウィンドウを出して」とブロードキャスト。
            // 受け側 (MainWindow の WndProc) がこのメッセージを拾って ShowMainWindow する。
            if (ShowMainWindowMessageId != 0)
            {
                PostMessage(HWND_BROADCAST, ShowMainWindowMessageId, IntPtr.Zero, IntPtr.Zero);
            }
            return false;
        }
        catch (Exception ex)
        {
            // ガードに失敗した場合は安全側に倒して起動を許可する。
            LogStartupFailure("TryAcquireSingleInstance", ex);
            return true;
        }
    }

    internal static void ReleaseSingleInstance()
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch { /* best-effort */ }
        try
        {
            _singleInstanceMutex?.Dispose();
        }
        catch { /* best-effort */ }
        _singleInstanceMutex = null;
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            // NOTE: AppNotificationManager.Default.Register() は packaged アプリ
            // 用の API で、unpackaged + self-contained 配布では
            // Microsoft.WindowsAppRuntime.Insights.Resource.dll が見つからずに
            // 必ず COMException (0x8007007E) を出す。GUI 自体は問題なく動く
            // ものの、毎回起動ログが汚れて分かりづらいため、最初から呼ばない。
            // 通知 UI はトースト経由ではなく、GUI 上のログ表示や ContentDialog
            // で十分代替できている。

            try
            {
                Tray.Initialize();
                Tray.ShowWindowRequested += ShowMainWindow;
                Tray.ExitRequested += ExitApplication;
            }
            catch (Exception ex) { LogStartupFailure("Tray.Initialize", ex); }

            try
            {
                var settings = Runner.LoadSettings();
                Coordinator = new AutoSyncCoordinator(Runner, settings, Runner.CreateLogger<AutoSyncCoordinator>());

                // Issue #6: 起動時の同期 + 自動起動 (Pull → Launch) は OneDrive 経由の
                // ネットワーク I/O を伴うため、UI スレッドで同期実行するとアプリ
                // ウィンドウが表示されるまでフリーズに見える。Window を先に表示し、
                // バックグラウンドで Run → 完了通知を出してから Coordinator.Start を呼ぶ。
                // Coordinator.Start を後回しにするのは、Start 直後に走る監視より
                // 先に Pull を済ませて自動 Push 暴発を避けるため。
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var orchestrator = new StartupSyncOrchestrator(
                            Runner,
                            logger: Runner.CreateLogger<StartupSyncOrchestrator>());
                        var steps = orchestrator.Run(settings);
                        LogLifecycle($"StartupSync.steps={steps.Count}");
                        // ステップ保存と既存購読者への通知を 1 つのロック内で
                        // 行う。これで MainPage の SubscribeStartupSyncSteps と
                        // 競合しても取りこぼし/二重取り込みが起こらない。
                        PublishStartupSyncSteps(steps);
                    }
                    catch (Exception ex) { LogStartupFailure("StartupSyncOrchestrator", ex); }
                    finally
                    {
                        try { Coordinator?.Start(); LogLifecycle("Coordinator.Start ok (post-startup-sync)"); }
                        catch (Exception ex) { LogStartupFailure("Coordinator.Start", ex); }
                    }
                });
            }
            catch (Exception ex) { LogStartupFailure("Coordinator.Start", ex); }

            Window = new MainWindow();
            Window.Closed += OnWindowClosed;
            Window.Activate();

            // Issue #6: Windows ログオフ / シャットダウン時にも同期を流す。
            // 既定の猶予は 5 秒程度しかないため、ShutdownBlockReasonCreate で
            // 最大 15 秒だけシャットダウンを延長して Push を完了させる。
            try
            {
                Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding;
                LogLifecycle("SessionEnding handler registered");
            }
            catch (Exception ex) { LogStartupFailure("SessionEnding.register", ex); }
        }
        catch (Exception ex)
        {
            LogStartupFailure("OnLaunched", ex);
            throw;
        }
    }

    private static void LogStartupFailure(string source, Exception? ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(FileLoggerProvider.DefaultLogPath());
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var path = FileLoggerProvider.DefaultLogPath();
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [STARTUP-FAIL] {source}: {ex}";
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch
        {
            // ロガー自体が落ちる環境では諦める
        }
    }

    public static void ShowMainWindow()
    {
        LogLifecycle("ShowMainWindow.entered");
        // トレイメニュー等の WinUI 外のコンテキストから呼ばれる可能性があるので、
        // 必ず UI スレッドへディスパッチする。
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Window is null)
            {
                LogLifecycle("ShowMainWindow.skip: Window is null");
                return;
            }

            // 1) H.NotifyIcon の Show でタスクバー再表示 + Efficiency Mode 解除。
            try
            {
                WindowExtensions.Show(Window, disableEfficiencyMode: true);
                LogLifecycle("ShowMainWindow.WindowExtensions.Show ok");
            }
            catch (Exception ex)
            {
                LogLifecycle("ShowMainWindow.WindowExtensions.Show fail: " + ex.Message);
                try { Window.AppWindow?.Show(); } catch { /* best-effort */ }
            }

            // 2) Win32 で確実にウィンドウ復元 + フォアグラウンド化。
            //    WinUI 3 の Activate / AppWindow.Show 単体では、Hide 経由で
            //    隠したウィンドウが Z オーダー最下層に残るケースがあるため。
            try
            {
                var hwnd = WindowHandle;
                if (hwnd != IntPtr.Zero)
                {
                    if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                    else ShowWindow(hwnd, SW_SHOW);
                    SetForegroundWindow(hwnd);
                    LogLifecycle("ShowMainWindow.Win32 ok hwnd=0x" + hwnd.ToString("X"));
                }
                else
                {
                    LogLifecycle("ShowMainWindow.skip: hwnd is zero");
                }
            }
            catch (Exception ex)
            {
                LogLifecycle("ShowMainWindow.Win32 fail: " + ex.Message);
            }

            try { Window.Activate(); } catch { /* best-effort */ }
        });
    }

    private static void OnWindowClosed(object sender, WindowEventArgs args)
    {
        LogLifecycle("OnWindowClosed.entered isExiting=" + _isExiting);
        if (_isExiting)
        {
            // トレイメニューからの「終了」要求はそのまま閉じさせる。
            return;
        }
        // それ以外（× ボタン押下）はタスクトレイへの最小化として扱う。
        args.Handled = true;
        try
        {
            WindowExtensions.Hide(Window, enableEfficiencyMode: false);
            LogLifecycle("OnWindowClosed.WindowExtensions.Hide ok");
        }
        catch (Exception ex)
        {
            LogLifecycle("OnWindowClosed.WindowExtensions.Hide fail: " + ex.Message);
            try { Window.AppWindow?.Hide(); } catch { /* best-effort */ }
        }
    }

    // Tray の ExitRequested は同期メソッド型なので、async ロジックを内部関数に
    // 切り出してファイア&フォーゲットで走らせる。多重呼び出しは _isExiting で防止。
    // Tray「終了」では各ツールを能動停止せず、既に終了しているツールだけ Push する。
    // (VRCX / VRC Friend Connect は WM_CLOSE では「トレイに最小化」だけでプロセスが
    //  生き残るため、自前で停止することは諦めた)
    private static void ExitApplication() => _ = ExitApplicationAsync(waitForToolsToExit: null);

    /// Windows ログオフ / シャットダウンのハンドラ。SystemEvents から呼ばれる。
    /// 既定の Windows のシャットダウン猶予 (約 5 秒) では Push が間に合わないため、
    /// <c>ShutdownBlockReasonCreate</c> で「同期中…」と表示して最大 <c>SessionEndPushTimeout</c>
    /// 秒だけシャットダウンを延長する。
    /// この経路では各ツール (VRCX / VRC Friend Connect) も同時にシャットダウン
    /// シーケンスで終了していくため、waitForToolsToExit にタイムアウトを渡して
    /// 自然終了を待ち、終了済みになったツールについて Push する。
    /// </summary>
    private static readonly TimeSpan SessionEndPushTimeout = TimeSpan.FromSeconds(15);

    private static void OnSessionEnding(object sender, Microsoft.Win32.SessionEndingEventArgs e)
    {
        LogLifecycle("OnSessionEnding entered reason=" + e.Reason);
        if (_isExiting)
        {
            LogLifecycle("OnSessionEnding skip: already exiting");
            return;
        }

        // シャットダウンを最大 SessionEndPushTimeout 秒だけ延長する。
        try
        {
            var hwnd = Window is null ? IntPtr.Zero : WindowHandle;
            if (hwnd != IntPtr.Zero)
            {
                ShutdownBlockReasonCreate(hwnd, "同期処理を完了するまでお待ちください...");
            }
        }
        catch (Exception ex) { LogLifecycle("ShutdownBlockReasonCreate fail: " + ex.Message); }

        try
        {
            // SystemEvents のスレッドではダイアログを出せないので、同期的に完了を待つ。
            // 15 秒の壁を越えると Windows 側に殺される可能性があるため、ツール終了待ちと
            // 全体ウェイトを 15 秒に揃えて諦める。SessionEnding 経路では各ツールも
            // 同時にシャットダウンで終了していくはずなので、自然終了を待ってから Push する。
            var task = ExitApplicationAsync(waitForToolsToExit: SessionEndPushTimeout);
            task.Wait(SessionEndPushTimeout);
        }
        catch (Exception ex) { LogLifecycle("OnSessionEnding ExitApplicationAsync fail: " + ex.Message); }
        finally
        {
            try
            {
                var hwnd = Window is null ? IntPtr.Zero : WindowHandle;
                if (hwnd != IntPtr.Zero) ShutdownBlockReasonDestroy(hwnd);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// 終了シーケンス。Coordinator を停止 → 各ツールが既に終了しているか確認 →
    /// 終了済みのツールだけ Push → Environment.Exit。
    /// <paramref name="waitForToolsToExit"/> null = 待たない (Tray「終了」経路)、
    /// 値あり = タイムアウトまで自然終了を待つ (SessionEnding 経路)。
    /// </summary>
    internal static async System.Threading.Tasks.Task ExitApplicationAsync(TimeSpan? waitForToolsToExit)
    {
        LogLifecycle("ExitApplication.entered isExiting=" + _isExiting);
        // 既に終了処理中なら再入を防ぐ。
        if (_isExiting) return;
        _isExiting = true;

        // (0) Coordinator を停止 (Dispose ではなく Stop)。これ以降の Push は手動で呼ぶ。
        //     Stop は監視解除 + 世代 Cancel しかしないため、HandleProcessExited で
        //     既に走り始めた AutoPush は止まらない。Stop 後に WaitForInFlightPushAsync
        //     で完了を待たないと、終了時 Push と並走して manifest 競合する。
        try { Coordinator?.Stop(); LogLifecycle("ExitApplication.Coordinator.Stop ok"); } catch (Exception ex) { LogLifecycle("ExitApplication.Coordinator.Stop fail: " + ex.Message); }
        var inFlightDone = true;
        IReadOnlyList<string> autoPushedTools = System.Array.Empty<string>();
        try
        {
            if (Coordinator is not null)
            {
                var waitResult = await Coordinator.WaitForInFlightPushAsync(TimeSpan.FromSeconds(20));
                inFlightDone = waitResult.Completed;
                autoPushedTools = waitResult.PushedToolKeys;
                LogLifecycle(inFlightDone
                    ? $"ExitApplication.WaitForInFlightPush ok autoPushed=[{string.Join(",", autoPushedTools)}]"
                    : "ExitApplication.WaitForInFlightPush TIMED OUT - AutoPush may still be running, will skip shutdown push to avoid manifest conflict");
            }
        }
        catch (Exception ex) { LogLifecycle("ExitApplication.WaitForInFlightPush fail: " + ex.Message); }

        // (1) 各ツールが終了済みかチェック → 終了済みのツールだけ Push。
        //     SessionEnding 経路では waitForToolsToExit が指定されるので、その時間まで
        //     自然終了を待つ。Tray「終了」経路では null = 即時チェックして起動中なら Push スキップ。
        //     AutoPush 待機がタイムアウトしている場合は二重 Push を避けるため全件 Push スキップ。
        //     直近 AutoPush で Push 済みのツール (autoPushedTools) は二重 Push を避けてスキップ。
        try
        {
            var settings = Runner.LoadSettings();
            var orchestrator = new ShutdownSyncOrchestrator(
                Runner,
                logger: Runner.CreateLogger<ShutdownSyncOrchestrator>());
            var steps = await orchestrator.RunAsync(settings, new ShutdownSyncOptions
            {
                WaitForToolsToExit = waitForToolsToExit,
                SkipPush = !inFlightDone,
                SkipPushForTools = autoPushedTools,
            });
            LogLifecycle($"ExitApplication.ShutdownSync.steps={steps.Count} skipPush={!inFlightDone} waitForExit={waitForToolsToExit?.TotalSeconds.ToString("0.#") ?? "null"} skipForTools=[{string.Join(",", autoPushedTools)}]");
        }
        catch (Exception ex) { LogLifecycle("ExitApplication.ShutdownSync fail: " + ex.Message); }

        // (2) Coordinator を完全に Dispose してから Tray もきれいに片付ける。
        try { Coordinator?.Dispose(); LogLifecycle("ExitApplication.Coordinator.Dispose ok"); } catch (Exception ex) { LogLifecycle("ExitApplication.Coordinator.Dispose fail: " + ex.Message); }
        Coordinator = null;
        try { Tray.Dispose(); LogLifecycle("ExitApplication.Tray.Dispose ok"); } catch (Exception ex) { LogLifecycle("ExitApplication.Tray.Dispose fail: " + ex.Message); }

        LogLifecycle("ExitApplication.Environment.Exit(0)");
        Environment.Exit(0);
    }

    private static void LogLifecycle(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(FileLoggerProvider.DefaultLogPath());
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var path = FileLoggerProvider.DefaultLogPath();
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [LIFECYCLE] {message}";
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch { /* best-effort */ }
    }

    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    // SessionEnding 中にシャットダウンを一時的にブロックして、Push を完了させる時間を稼ぐ。
    // 表示文字列はユーザに「待ち時間が発生する理由」を伝える。
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string pwszReason);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);
}
