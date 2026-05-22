using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace VRCToolsDataSync_App;

/// <summary>
/// XAML 自動生成版 Program.Main の置き換え。Application.Start に入る前に
/// Named Mutex で多重起動を検出し、2 つ目のインスタンスは既存のメインウィンドウへ
/// 復帰要求メッセージを送って自身は即終了する。
/// Issue #2 (P1): 自動起動 + ショートカット二重起動で AutoSyncCoordinator が
/// 並走し、manifest.json の競合検知が暴発する問題への対策。
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] _)
    {
        if (!App.TryAcquireSingleInstance())
        {
            // 既に動いている。既存インスタンスに復帰要求を送るのは
            // TryAcquireSingleInstance 内で済ませているので、ここでは即終了する。
            return;
        }

        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start((p) =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }
        finally
        {
            App.ReleaseSingleInstance();
        }
    }
}
