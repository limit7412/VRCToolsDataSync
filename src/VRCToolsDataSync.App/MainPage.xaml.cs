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

        if (App.Coordinator is not null)
        {
            ViewModel.AttachCoordinator(App.Coordinator, action =>
            {
                App.DispatcherQueue.TryEnqueue(() => action());
            });
        }
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
}
