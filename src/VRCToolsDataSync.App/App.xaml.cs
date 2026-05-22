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
    public static System.Collections.Generic.IReadOnlyList<StartupSyncStep> StartupSyncSteps { get; private set; } =
        System.Array.Empty<StartupSyncStep>();

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

                // Issue #6: 起動時の同期 + 自動起動を Coordinator.Start より前に走らせる。
                // Start 後だと Pull 直後にツールを起動するまでの隙間で自動 Push が
                // 暴発する可能性がある。Coordinator.Start は最後にまとめて呼ぶ。
                try
                {
                    var orchestrator = new StartupSyncOrchestrator(
                        Runner,
                        logger: Runner.CreateLogger<StartupSyncOrchestrator>());
                    var steps = orchestrator.Run(settings);
                    StartupSyncSteps = steps;
                    LogLifecycle($"StartupSync.steps={steps.Count}");
                }
                catch (Exception ex) { LogStartupFailure("StartupSyncOrchestrator", ex); }

                Coordinator.Start();
            }
            catch (Exception ex) { LogStartupFailure("Coordinator.Start", ex); }

            Window = new MainWindow();
            Window.Closed += OnWindowClosed;
            Window.Activate();
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

    private static void ExitApplication()
    {
        LogLifecycle("ExitApplication.entered isExiting=" + _isExiting);
        // 既に終了処理中なら再入を防ぐ。
        if (_isExiting) return;
        _isExiting = true;

        // 後始末を best-effort で実行してから Environment.Exit(0) で確実に殺す。
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
}
