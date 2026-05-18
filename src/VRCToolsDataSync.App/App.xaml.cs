using System;
using System.IO;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using VRCToolsDataSync.Core.Logging;
using VRCToolsDataSync.Core.Settings;
using VRCToolsDataSync.Core.Sync;
using VRCToolsDataSync.Core.Watch;
using VRCToolsDataSync_App.Tray;

namespace VRCToolsDataSync_App;

public partial class App : Application
{
    public static Window Window { get; private set; } = null!;

    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    public static SyncRunner Runner { get; } = new();

    public static AutoSyncCoordinator? Coordinator { get; private set; }

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
        // トレイメニュー等の WinUI 外のコンテキストから呼ばれる可能性があるので、
        // 必ず UI スレッドへディスパッチする。
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Window is null) return;
            // AppWindow.Show だけでは Z オーダーやフォアグラウンドが復帰しない
            // ケースがあるため、H.NotifyIcon の WindowExtensions.Show を使う。
            // 内部で AppWindow.Show + フォアグラウンド復帰 + Activate をまとめて行う。
            try
            {
                WindowExtensions.Show(Window);
            }
            catch
            {
                // フォールバック: 標準 API でできる範囲のことだけやる。
                try { Window.AppWindow?.Show(); } catch { /* best-effort */ }
                try { Window.Activate(); } catch { /* best-effort */ }
            }
        });
    }

    private static void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_isExiting)
        {
            // トレイメニューからの「終了」要求はそのまま閉じさせる。
            return;
        }
        // それ以外（× ボタン押下）はタスクトレイへの最小化として扱う。
        args.Handled = true;
        try
        {
            // H.NotifyIcon の WindowExtensions.Hide はタスクバーからも
            // 確実に消し、後続の Show 呼び出しで復帰できる状態にする。
            WindowExtensions.Hide(Window);
        }
        catch
        {
            try { Window.AppWindow?.Hide(); } catch { /* best-effort */ }
        }
    }

    private static void ExitApplication()
    {
        // 既に終了処理中なら再入を防ぐ。
        if (_isExiting) return;
        _isExiting = true;

        // WinUI 3 + H.NotifyIcon の組み合わせでは Application.Current.Exit() が
        // 期待通りに動かず、TaskbarIcon が残ったままプロセスが停止しないことが
        // 多いため、後始末を best-effort で実行してから Environment.Exit(0) で
        // 確実に殺す。
        try { Coordinator?.Dispose(); } catch { /* best-effort */ }
        Coordinator = null;
        // AppNotificationManager.Register を呼んでいないため Unregister も不要。
        try { Tray.Dispose(); } catch { /* best-effort */ }

        Environment.Exit(0);
    }
}
