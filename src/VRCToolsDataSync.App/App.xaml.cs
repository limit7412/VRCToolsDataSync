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
        // 閉じるボタンはタスクトレイへの最小化として扱う
        args.Handled = true;
        if (Window.AppWindow is not null)
        {
            Window.AppWindow.Hide();
        }
    }

    private static void ExitApplication()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Coordinator?.Dispose();
            Coordinator = null;
            Tray.Dispose();
            try { AppNotificationManager.Default.Unregister(); } catch { /* best-effort */ }
            Window?.Close();
            Microsoft.UI.Xaml.Application.Current.Exit();
        });
    }
}
