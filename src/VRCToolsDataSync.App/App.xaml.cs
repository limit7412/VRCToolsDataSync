using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
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
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        AppNotificationManager.Default.Register();

        Tray.Initialize();
        Tray.ShowWindowRequested += ShowMainWindow;
        Tray.ExitRequested += ExitApplication;

        var settings = Runner.LoadSettings();
        Coordinator = new AutoSyncCoordinator(Runner, settings, Runner.CreateLogger<AutoSyncCoordinator>());
        Coordinator.Start();

        Window = new MainWindow();
        Window.Closed += OnWindowClosed;
        Window.Activate();
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
