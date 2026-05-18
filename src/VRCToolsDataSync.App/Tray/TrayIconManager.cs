using System;
using System.Drawing;
using System.IO;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace VRCToolsDataSync_App.Tray;

public sealed class TrayIconManager : IDisposable
{
    private TaskbarIcon? _taskbarIcon;
    private PopupMenu? _popupMenu;
    private Icon? _icon;

    public event Action? ShowWindowRequested;
    public event Action? ExitRequested;

    public void Initialize()
    {
        if (_taskbarIcon is not null) return;

        // NOTE: WinUI 3 の MenuFlyout / MenuFlyoutItem は、ホストする
        // メインウィンドウが Hide されていると Click イベントが発火しない
        // 既知問題がある。タスクトレイは「ウィンドウが隠れている時こそ
        // 使うもの」なので致命的。
        // 代わりに H.NotifyIcon.Core.PopupMenu (Win32 ネイティブメニュー)
        // を自前で構築し、TaskbarIcon の RightClick / LeftClick から表示する。
        _popupMenu = new PopupMenu();
        _popupMenu.Items.Add(new PopupMenuItem("ウィンドウを表示", (_, _) => ShowWindowRequested?.Invoke()));
        _popupMenu.Items.Add(new PopupMenuSeparator());
        _popupMenu.Items.Add(new PopupMenuItem("終了", (_, _) => ExitRequested?.Invoke()));

        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText = "VRCToolsDataSync",
            // 左クリックでウィンドウ復帰
            LeftClickCommand = new RelayCommand(() => ShowWindowRequested?.Invoke()),
            // 右クリックで PopupMenu を表示
            RightClickCommand = new RelayCommand(ShowPopupMenu),
            // ContextMenuMode は WinUI MenuFlyout 経路を使わない
            // (ContextFlyout を未設定なら不要だが、念のため Active 系を避ける)
            ContextMenuMode = ContextMenuMode.PopupMenu,
        };

        _icon = TryLoadIcon();
        if (_icon is not null)
        {
            _taskbarIcon.Icon = _icon;
        }

        _taskbarIcon.ForceCreate();
    }

    private void ShowPopupMenu()
    {
        if (_popupMenu is null) return;
        try
        {
            // カーソル位置に表示。座標は GetCursorPos で取得。
            CursorPos.GetCursorPos(out var pt);
            _popupMenu.Show(IntPtr.Zero, pt.X, pt.Y);
        }
        catch
        {
            // ポップアップ表示が失敗してもアプリ全体を落とさない。
        }
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
        // ため、ここでは no-op として扱う。将来 MSIX 配布に切り替える際は
        // この呼び出しを Microsoft.Windows.AppNotifications.Builder で
        // 復活させればよい。
        _ = title;
        _ = body;
    }

    public void Dispose()
    {
        _taskbarIcon?.Dispose();
        _taskbarIcon = null;
        _popupMenu = null;
        _icon?.Dispose();
        _icon = null;
    }

    private static class CursorPos
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);
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
