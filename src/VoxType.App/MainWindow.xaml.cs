using Microsoft.UI.Xaml;
using VoxType.Infrastructure.Windows;
using VoxType_App.ViewModels;
using Windows.Graphics;

namespace VoxType_App;

public sealed partial class MainWindow : Window
{
    private bool exiting;
    private readonly nint hwnd;
    public MainWindow(MainPageViewModel viewModel)
    {
        InitializeComponent();
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = NativeWindow.GetDpiForWindow(hwnd) / 96d;
        AppWindow.Resize(new SizeInt32((int)(920 * scale), (int)(680 * scale)));
        Root.Children.Add(new MainPage(viewModel, hwnd));
        Root.ActualThemeChanged += (_, _) => UpdateTitleBarTheme();
        UpdateTitleBarTheme();
        AppWindow.Closing += (_, args) => { if (!exiting) { args.Cancel = true; AppWindow.Hide(); } };
    }
    private void UpdateTitleBarTheme() => AppWindow.TitleBar.PreferredTheme = Root.ActualTheme == ElementTheme.Dark ? Microsoft.UI.Windowing.TitleBarTheme.Dark : Microsoft.UI.Windowing.TitleBarTheme.Light;
    public void ApplyTheme(string theme) { Root.RequestedTheme = theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default }; UpdateTitleBarTheme(); }
    public void ShowSettings()
    {
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        AppWindow.Show(); Activate();
        NativeWindow.SetForegroundWindow(hwnd);
        UpdateTitleBarTheme();
    }
    public void ExitWindow() { exiting = true; Close(); }
}
