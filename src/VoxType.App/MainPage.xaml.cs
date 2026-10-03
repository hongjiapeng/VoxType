using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoxType_App.ViewModels;

namespace VoxType_App;

public sealed partial class MainPage : Page
{
    public MainPageViewModel ViewModel { get; }
    public ModelManagerViewModel Models => ViewModel.Models;
    private readonly nint owner;
    public MainPage(MainPageViewModel viewModel, nint owner)
    {
        this.owner = owner;
        ViewModel = viewModel;
        InitializeComponent();
        Navigation.SelectedItem = Navigation.MenuItems[0];
        ViewModel.Models.PickModelAsync = PickModelAsync;
        ViewModel.Models.ConfirmDeleteAsync = ConfirmDeleteAsync;
        ViewModel.Models.OpenFolderAsync = OpenFolderAsync;
        Loaded += async (_, _) => await ViewModel.Models.RefreshCommand.ExecuteAsync(null);
    }
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && int.TryParse(item.Tag?.ToString(), out int section)) ViewModel.Section = section;
    }
    private async Task<string?> PickModelAsync(CancellationToken token)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(owner));
        picker.FileTypeFilter.Add(".bin");
        var file = await picker.PickSingleFileAsync().AsTask(token);
        return file?.Path;
    }
    private async Task<bool> ConfirmDeleteAsync(CancellationToken token)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "删除 Whisper base？",
            Content = "将删除程序模型目录中的 ggml-base.bin。再次使用前需要重新下载或导入。手动指定的其他路径文件不受影响。",
            PrimaryButtonText = "删除模型", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ActualTheme
        };
        return await dialog.ShowAsync().AsTask(token) == ContentDialogResult.Primary;
    }
    private static async Task<bool> OpenFolderAsync(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(path);
        return await Windows.System.Launcher.LaunchFolderAsync(folder);
    }
}
