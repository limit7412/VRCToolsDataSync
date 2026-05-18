using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
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

            // unpackaged 起動では AppNotificationManager の登録に COM 設定が要り、
            // 失敗するとプロセスごと落ちるため、起動継続を優先して握り潰す。
            try { AppNotificationManager.Default.Register(); }
            catch (Exception ex) { LogStartupFailure("AppNotificationManager.Register", ex); }

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
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Window is null) return;
            Window.Activate();
            if (Window.AppWindow is not null)
            {
                Window.AppWindow.Show();
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
        if (Window.AppWindow is not null)
        {
            Window.AppWindow.Hide();
        }
    }

    private static void ExitApplication()
    {
        // 既に終了処理中なら再入を防ぐ。
        if (_isExiting) return;
        _isExiting = true;

        DispatcherQueue.TryEnqueue(() =>
        {
            // 順序が重要:
            // 1. プロセス監視 / FileSystemWatcher を止める (Coordinator)
            // 2. トースト通知の登録解除 (AppNotificationManager)
            // 3. メインウィンドウを実際に閉じる (Window.Close → OnWindowClosed が
            //    _isExiting=true を見てそのまま閉じる)
            // 4. タスクトレイアイコンを破棄 (TaskbarIcon)
            // 5. プロセス自体を Environment.Exit で確実に終了させる。
            //    Application.Current.Exit() だけでは TaskbarIcon 内部の COM
            //    オブジェクトが解放されきらずプロセスが残るケースがある。
            try { Coordinator?.Dispose(); } catch { /* best-effort */ }
            Coordinator = null;
            try { AppNotificationManager.Default.Unregister(); } catch { /* best-effort */ }
            try { Window?.Close(); } catch { /* best-effort */ }
            try { Tray.Dispose(); } catch { /* best-effort */ }
            try { Microsoft.UI.Xaml.Application.Current.Exit(); } catch { /* best-effort */ }

            // 上記でメッセージループが抜けないケースに備えて最終手段。
            // 1秒待ってもまだ生きていたら Environment.Exit でプロセスを殺す。
            System.Threading.Tasks.Task.Run(() =>
            {
                System.Threading.Thread.Sleep(1000);
                Environment.Exit(0);
            });
        });
    }
}
