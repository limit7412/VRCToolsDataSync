using System;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace VRCToolsDataSync_App.Tray;

public sealed class TrayIconManager : IDisposable
{
    private TaskbarIcon? _taskbarIcon;

    public event Action? ShowWindowRequested;
    public event Action? ExitRequested;

    public void Initialize()
    {
        if (_taskbarIcon is not null) return;

        var showItem = new MenuFlyoutItem { Text = "ウィンドウを表示" };
        showItem.Click += (_, _) => ShowWindowRequested?.Invoke();

        var exitItem = new MenuFlyoutItem { Text = "終了" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new MenuFlyout();
        menu.Items.Add(showItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exitItem);

        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText = "VRCToolsDataSync",
            ContextFlyout = menu,
            LeftClickCommand = new RelayCommand(() => ShowWindowRequested?.Invoke()),
        };
        _taskbarIcon.ForceCreate();
    }

    public void ShowToast(string title, string body)
    {
        try
        {
            var payload = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body)
                .BuildNotification();
            AppNotificationManager.Default.Show(payload);
        }
        catch
        {
            // 通知未許可など。トレイのまま落とさない
        }
    }

    public void Dispose()
    {
        _taskbarIcon?.Dispose();
        _taskbarIcon = null;
    }

    private sealed class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) { _execute = execute; }
#pragma warning disable CS0067 // ICommand 規約上必要だが、実コマンドの可否は変わらない
        public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
    }
}
