using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using VRCToolsDataSync_App.ViewModels;
using Windows.Storage.Pickers;

namespace VRCToolsDataSync_App;

public sealed partial class MainPage : Page
{
    public MainPageViewModel ViewModel { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        // Issue #6: トレイメニューから VM のコマンドを叩けるよう、App 側に
        // シングルトン参照を持たせる。MainPage は MainWindow の content として
        // 一度だけ生成される想定。
        App.Page = this;
        // 競合とリモート更新の問い合わせは、画面の中の InfoBar が受け持つ
        // (issue #10)。ここでウィンドウを出すのは、常駐中に問い合わせが出た
        // ことに気付いてもらうためであって、問い合わせの成立の条件ではない。
        // 出せなくても問い合わせは画面に残り、後から開いたときに選べる。
        ViewModel.ShowWindowRequested += () => App.ShowMainWindow();
        ViewModel.ToastRequested += (title, body) => App.Tray.ShowToast(title, body);

        // UI スレッドへの運び先は Coordinator と切り離して先に渡す。
        // 保存先が未設定などで Coordinator を作れなかった場合、以前は
        // AttachCoordinator ごと呼ばれず、更新確認 (issue #45) の結果が
        // バックグラウンドスレッドのまま画面へ触れていた。更新確認は
        // 保存先の設定と関係なく動く。
        ViewModel.SetUiDispatcher(action => App.DispatcherQueue.TryEnqueue(() => action()));

        if (App.Coordinator is not null)
        {
            ViewModel.AttachCoordinator(App.Coordinator, action =>
            {
                App.DispatcherQueue.TryEnqueue(() => action());
            });
        }

        // Issue #6: App.OnLaunched でバックグラウンドで走った起動同期 (Pull → Launch)
        // のステップを GUI のログに取り込む。SubscribeStartupSyncSteps が
        // 「既に Run 完了済みなら即時呼び出し」「未完了なら次の完了で呼び出し」を
        // lock 下でアトミックに行うため、判定と購読の隙間でステップを取りこぼしたり
        // 二重取り込みしたりすることが無い。
        App.SubscribeStartupSyncSteps(steps =>
        {
            App.DispatcherQueue.TryEnqueue(() =>
            {
                try { ViewModel.IngestStartupSteps(steps); } catch { /* best-effort */ }
            });
        });
    }

    private async void OnBrowseCloudFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.Desktop,
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            ViewModel.CloudFolderPath = folder.Path;
        }
    }

    private async void OnBrowseVrcxExecutable(object sender, RoutedEventArgs e)
    {
        var path = await PickExecutableAsync();
        if (!string.IsNullOrEmpty(path)) ViewModel.VrcxExecutablePath = path;
    }

    private async void OnBrowseFriendConnectExecutable(object sender, RoutedEventArgs e)
    {
        var path = await PickExecutableAsync();
        if (!string.IsNullOrEmpty(path)) ViewModel.FriendConnectExecutablePath = path;
    }

    private static async Task<string?> PickExecutableAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
