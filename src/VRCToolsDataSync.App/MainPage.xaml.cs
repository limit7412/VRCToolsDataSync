using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VRCToolsDataSync_App.ViewModels;
using Windows.Storage.Pickers;

namespace VRCToolsDataSync_App;

public sealed partial class MainPage : Page
{
    public MainPageViewModel ViewModel { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        ViewModel.ConflictRequested += OnConflictRequested;
        ViewModel.RemoteUpdateRequested += OnRemoteUpdateRequested;
        ViewModel.ShowWindowRequested += () => App.ShowMainWindow();
        ViewModel.ToastRequested += (title, body) => App.Tray.ShowToast(title, body);

        if (App.Coordinator is not null)
        {
            ViewModel.AttachCoordinator(App.Coordinator, action =>
            {
                App.DispatcherQueue.TryEnqueue(() => action());
            });
        }

        // Issue #6: App.OnLaunched でバックグラウンドで走った起動同期 (Pull → Launch)
        // のステップを GUI のログに取り込む。
        // - Run が既に完了済み: App.StartupSyncSteps に溜まっているので即時取り込み
        // - 未完了: App.StartupSyncStepsAvailable イベントを購読し、完了時に取り込む
        if (App.StartupSyncSteps.Count > 0)
        {
            ViewModel.IngestStartupSteps(App.StartupSyncSteps);
        }
        App.StartupSyncStepsAvailable += steps =>
        {
            App.DispatcherQueue.TryEnqueue(() =>
            {
                try { ViewModel.IngestStartupSteps(steps); } catch { /* best-effort */ }
            });
        };
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

    private async Task<ConflictChoice> OnConflictRequested(ConflictPrompt prompt)
    {
        var dialog = new ContentDialog
        {
            Title = $"{prompt.ToolDisplayName} コンフリクト",
            Content = $"リモートの方が新しい更新を持っています。\nremote v{prompt.RemoteVersion} / 最後にPullしたバージョン v{prompt.LastPulledVersion}\n\nどう処理しますか？",
            PrimaryButtonText = "先に Pull",
            SecondaryButtonText = "強制 Push（上書き）",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => ConflictChoice.PullFirst,
            ContentDialogResult.Secondary => ConflictChoice.ForceOverwrite,
            _ => ConflictChoice.Cancel,
        };
    }

    private async Task<RemoteUpdateChoice> OnRemoteUpdateRequested(RemoteUpdatePrompt prompt)
    {
        var dialog = new ContentDialog
        {
            Title = $"{prompt.ToolDisplayName}: リモート更新",
            Content = $"{prompt.MachineName} がクラウドに v{prompt.RemoteVersion} を Push しました。\n手元の最終 Pull は v{prompt.LocalVersion} です。\n\n今 Pull しますか？",
            PrimaryButtonText = "Pull する",
            CloseButtonText = "あとで",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? RemoteUpdateChoice.PullNow : RemoteUpdateChoice.Later;
    }
}
