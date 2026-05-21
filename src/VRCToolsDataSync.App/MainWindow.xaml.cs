using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace VRCToolsDataSync_App;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    // SetWindowSubclass で保持する callback 実体。GC されるとコールバック後に
    // CallbackOnCollectedDelegate になるためフィールドで握っておく。
    private SUBCLASSPROC? _subclassProc;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));

        // 既存インスタンスへの「ウィンドウ表示要求」メッセージ (App.TryAcquireSingleInstance
        // で broadcast したもの) をここで拾い、ShowMainWindow にディスパッチする。
        HookSingleInstanceMessage();
    }

    private void HookSingleInstanceMessage()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero) return;
            if (App.ShowMainWindowMessageId == 0) return;

            _subclassProc = SubclassProc;
            // dwRefData は使わない。識別子は適当な定数 (1)。
            SetWindowSubclass(hwnd, _subclassProc, uIdSubclass: 1, dwRefData: 0);
        }
        catch
        {
            // フックに失敗してもアプリ本体は動くので致命ではない。
            // 多重起動時の前面化が効かなくなるだけ。
        }
    }

    private IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == App.ShowMainWindowMessageId && App.ShowMainWindowMessageId != 0)
        {
            // 別プロセスから「もう動いてるならウィンドウを出して」と
            // ブロードキャストされたメッセージ。Dispatcher 経由で安全に処理する。
            try { App.ShowMainWindow(); } catch { /* best-effort */ }
            return IntPtr.Zero;
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass, uint dwRefData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
}
