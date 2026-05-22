using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace VRCToolsDataSync_App.Tray;

public sealed class TrayIconManager : IDisposable
{
    private TaskbarIcon? _taskbarIcon;
    private PopupMenu? _popupMenu;
    private MessageWindow? _messageWindow;
    private EventHandler<MessageWindow.MouseEventReceivedEventArgs>? _mouseHandler;
    private Icon? _icon;

    public event Action? ShowWindowRequested;
    public event Action? ExitRequested;
    // Issue #6: 「同期して起動 / 再起動 / 終了」のトレイメニュー項目から発火。
    // 起動・再起動は MainPage 側でツール起動を伴う Pull → Launch を、
    // 終了は ExitApplicationAsync(forceStopAllSyncedTools=true) を呼ぶ想定。
    public event Action? SyncAndLaunchRequested;
    public event Action? SyncAndRestartRequested;

    public void Initialize()
    {
        if (_taskbarIcon is not null) return;

        // H.NotifyIcon.Core.PopupMenu を Win32 ネイティブメニューとして
        // 構築。WinUI MenuFlyout はメインウィンドウが Hide のとき click が
        // 配送されないので使わない。
        _popupMenu = new PopupMenu();
        _popupMenu.Items.Add(new PopupMenuItem("ウィンドウを表示", (_, _) =>
        {
            LifecycleLog("Tray.PopupMenu Show clicked");
            ShowWindowRequested?.Invoke();
        }));
        _popupMenu.Items.Add(new PopupMenuSeparator());
        _popupMenu.Items.Add(new PopupMenuItem("同期して起動", (_, _) =>
        {
            LifecycleLog("Tray.PopupMenu SyncAndLaunch clicked");
            SyncAndLaunchRequested?.Invoke();
        }));
        _popupMenu.Items.Add(new PopupMenuItem("同期して再起動", (_, _) =>
        {
            LifecycleLog("Tray.PopupMenu SyncAndRestart clicked");
            SyncAndRestartRequested?.Invoke();
        }));
        _popupMenu.Items.Add(new PopupMenuSeparator());
        _popupMenu.Items.Add(new PopupMenuItem("同期して終了", (_, _) =>
        {
            LifecycleLog("Tray.PopupMenu Exit clicked");
            ExitRequested?.Invoke();
        }));

        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText = "VRCToolsDataSync",
            // ContextFlyout / ContextMenuMode は使わない (実機で動かなかった)。
            // 左クリックはコマンド経由で OK。
        };

        _icon = TryLoadIcon();
        if (_icon is not null)
        {
            _taskbarIcon.Icon = _icon;
        }

        _taskbarIcon.ForceCreate();

        // ForceCreate の後に TrayIcon → MessageWindow を取り出してマウス
        // イベントを直接購読する。WinUI 上の Routed Event / Command 経路は
        // メインウィンドウが Hide のとき不安定なため、Win32 メッセージレベル
        // で拾う方が確実。
        TryHookRawMouseEvents();
    }

    private void TryHookRawMouseEvents()
    {
        if (_taskbarIcon is null) return;
        try
        {
            // TaskbarIcon.TrayIcon (H.NotifyIcon.Core.TrayIcon) を取得
            var trayIcon = _taskbarIcon.TrayIcon;
            if (trayIcon is null)
            {
                LifecycleLog("Tray.Hook fail: TaskbarIcon.TrayIcon is null");
                return;
            }

            // Core.TrayIcon は MessageWindow プロパティを公開していないので
            // リフレクションで取得する。
            var messageWindowField = typeof(TrayIcon).GetField(
                "messageWindow",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var messageWindowProp = typeof(TrayIcon).GetProperty(
                "MessageWindow",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            object? mw = messageWindowField?.GetValue(trayIcon)
                         ?? messageWindowProp?.GetValue(trayIcon);
            if (mw is not MessageWindow messageWindow)
            {
                LifecycleLog("Tray.Hook fail: MessageWindow not found via reflection");
                return;
            }

            _messageWindow = messageWindow;
            _mouseHandler = OnRawMouseEvent;
            _messageWindow.MouseEventReceived += _mouseHandler;
            LifecycleLog("Tray.Hook ok");
        }
        catch (Exception ex)
        {
            LifecycleLog("Tray.Hook fail: " + ex.Message);
        }
    }

    private void OnRawMouseEvent(object? sender, MessageWindow.MouseEventReceivedEventArgs e)
    {
        // MouseMove はトレイアイコン上にカーソルがある間連発されるので、
        // ログにもアクションにも回さない (ノイズ抑制)。
        if (e.MouseEvent == MouseEvent.MouseMove) return;

        try
        {
            LifecycleLog("Tray.MouseEvent " + e.MouseEvent);
            switch (e.MouseEvent)
            {
                case MouseEvent.IconLeftMouseUp:
                    ShowWindowRequested?.Invoke();
                    break;
                // 右クリックは Up のみで処理。Down/Up 両方で開くと
                // 表示直後に閉じる挙動になる環境がある。
                case MouseEvent.IconRightMouseUp:
                    ShowPopupMenu();
                    break;
            }
        }
        catch (Exception ex)
        {
            LifecycleLog("Tray.MouseEvent fail: " + ex.Message);
        }
    }

    private void ShowPopupMenu()
    {
        if (_popupMenu is null || _messageWindow is null) return;
        try
        {
            // PopupMenu はオーナー HWND を必要とする。MessageWindow の Handle
            // (タスクトレイ用の非表示ウィンドウ) を使えば、メインウィンドウが
            // Hide でも問題なくメニューが表示される。
            var hwndField = typeof(MessageWindow).GetProperty("Handle",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var hwnd = (IntPtr?)hwndField?.GetValue(_messageWindow) ?? IntPtr.Zero;

            CursorPos.GetCursorPos(out var pt);
            // Show の前にフォアグラウンド化しないと、メニュー外クリックで
            // 閉じない / 項目クリックが届かないなどの問題がある (Win32 仕様)
            if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
            _popupMenu.Show(hwnd, pt.X, pt.Y);
            LifecycleLog("Tray.ShowPopupMenu shown hwnd=0x" + hwnd.ToString("X"));
        }
        catch (Exception ex)
        {
            LifecycleLog("Tray.ShowPopupMenu fail: " + ex.Message);
        }
    }

    private static Icon? TryLoadIcon()
    {
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
        {
            try
            {
                var fromExe = Icon.ExtractAssociatedIcon(exePath);
                if (fromExe is not null) return fromExe;
            }
            catch { /* fallback */ }
        }

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
        // unpackaged では AppNotificationManager が必ず失敗するため no-op。
        _ = title;
        _ = body;
    }

    public void Dispose()
    {
        if (_messageWindow is not null && _mouseHandler is not null)
        {
            try { _messageWindow.MouseEventReceived -= _mouseHandler; } catch { /* best-effort */ }
        }
        _messageWindow = null;
        _mouseHandler = null;

        _taskbarIcon?.Dispose();
        _taskbarIcon = null;
        _popupMenu = null;
        _icon?.Dispose();
        _icon = null;
    }

    private static void LifecycleLog(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VRCToolsDataSync", "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"sync-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [LIFECYCLE] {message}{Environment.NewLine}");
        }
        catch { /* best-effort */ }
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
