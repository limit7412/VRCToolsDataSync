using System;
using System.Drawing;
using System.IO;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;

namespace VRCToolsDataSync_App.Tray;

public sealed class TrayIconManager : IDisposable
{
    private TaskbarIcon? _taskbarIcon;
    private Icon? _icon;

    public event Action? ShowWindowRequested;
    public event Action? ExitRequested;

    public void Initialize()
    {
        if (_taskbarIcon is not null) return;

        // ContextFlyout に WinUI MenuFlyout を設定し、TaskbarIcon を
        // ContextMenuMode.PopupMenu で動かすと、H.NotifyIcon が
        // メニューを Win32 ネイティブメニューとして表示する。
        // クリックの選択結果のみが MenuFlyoutItem.Click に転送されるため、
        // メインウィンドウが Hide 状態でも Click が確実に届く。
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
            ContextMenuMode = ContextMenuMode.PopupMenu,
            LeftClickCommand = new RelayCommand(() => ShowWindowRequested?.Invoke()),
        };

        _icon = TryLoadIcon();
        if (_icon is not null)
        {
            _taskbarIcon.Icon = _icon;
        }

        _taskbarIcon.ForceCreate();
    }

    private static Icon? TryLoadIcon()
    {
        // 1) アプリ exe に埋め込まれたアイコンを抽出 (ApplicationIcon 経由)
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
        {
            try
            {
                var fromExe = Icon.ExtractAssociatedIcon(exePath);
                if (fromExe is not null) return fromExe;
            }
            catch { /* fallback to next strategy */ }
        }

        // 2) フォールバック: Assets\AppIcon.ico を直接読む
        //    (dotnet run など、exe 抽出が失敗する環境向け)
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var icoPath = Path.Combine(baseDir, "Assets", "AppIcon.ico");
            if (File.Exists(icoPath))
            {
                return new Icon(icoPath);
            }
        }
        catch { /* give up */ }

        return null;
    }

    public void ShowToast(string title, string body)
    {
        // NOTE: AppNotificationManager は packaged アプリ用の API で、
        // unpackaged + self-contained 配布では確実に COMException (0x8007007E)
        // を投げる。本アプリは GUI ログと ContentDialog で通知を代替している
        // ため、ここでは no-op として扱う。
        _ = title;
        _ = body;
    }

    public void Dispose()
    {
        _taskbarIcon?.Dispose();
        _taskbarIcon = null;
        _icon?.Dispose();
        _icon = null;
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
