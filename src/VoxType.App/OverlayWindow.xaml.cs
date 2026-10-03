using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using VoxType.Core.Dictation;
using VoxType.Infrastructure.Windows;
using VoxType_App.ViewModels;

namespace VoxType_App;

public sealed partial class OverlayWindow : Window, IDisposable
{
    private readonly nint hwnd;
    private readonly NativeWindow.SubclassProc procedure;
    private readonly Rectangle[] bars = new Rectangle[16];
    private readonly float[] levels = new float[16];
    private CancellationTokenSource? hideTimer;
    private long currentSession;
    private long revision;
    private long lastFrame;
    private bool disposed;
#if DEBUG
    private bool layoutFixture;
#endif
    public OverlayViewModel ViewModel { get; } = new();
    public OverlayWindow()
    {
        InitializeComponent();
        // This window never activates, so initialize compiled Window bindings explicitly.
        Bindings.Initialize();
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var presenter = OverlappedPresenter.CreateForToolWindow();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        NativeWindow.ConfigureOverlay(hwnd);
        procedure = HandleMessage;
        if (!NativeWindow.SetWindowSubclass(hwnd, procedure, 2, 0)) throw new InvalidOperationException("无法设置浮层的无激活窗口行为。");
        for (int i = 0; i < bars.Length; i++)
        {
            bars[i] = (Rectangle)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Rectangle xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='4' Height='2' RadiusX='2' RadiusY='2' VerticalAlignment='Center' Fill='{ThemeResource AccentFillColorDefaultBrush}' />");
            Waveform.Children.Add(bars[i]);
        }
    }
    private nint HandleMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x21) return 3; // MA_NOACTIVATE: deliver click without activation.
        return NativeWindow.DefSubclassProc(window, message, wParam, lParam);
    }
    public void ApplyTheme(string theme) => Root.RequestedTheme = theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
    public void Update(SessionUpdate update)
    {
#if DEBUG
        if (layoutFixture != (update.ProviderName == "布局检查")) { currentSession = 0; revision = 0; layoutFixture = false; }
        if (update.ProviderName == "布局检查") layoutFixture = true;
#endif
        if (disposed || update.SessionId < currentSession || update.SessionId == currentSession && update.Revision < revision) return;
        revision = update.Revision;
        currentSession = update.SessionId;
        hideTimer?.Cancel(); hideTimer?.Dispose(); hideTimer = null;
        ViewModel.Update(update);
        if (!ViewModel.IsListening) ResetLevels();
        if (update.State is SessionState.Idle or SessionState.Cancelling) { AppWindow.Hide(); return; }
        NativeWindow.PositionOverlay(hwnd, update.Focus.Window);
        AppWindow.Show(false);
        if (update.State is SessionState.Completed or SessionState.Failed)
        {
            hideTimer = new();
            int delay = update.State == SessionState.Completed ? 650 : update.Message.Contains("复制", StringComparison.Ordinal) ? 5000 : 3000;
            _ = HideLaterAsync(update.SessionId, delay, hideTimer.Token);
        }
    }
    private async Task HideLaterAsync(long session, int delay, CancellationToken token)
    {
        try { await Task.Delay(delay, token); if (!disposed && currentSession == session && !token.IsCancellationRequested) AppWindow.Hide(); }
        catch (OperationCanceledException) { }
    }
    public void AudioLevel(float level)
    {
        if (!ViewModel.IsListening || disposed) return;
        long now = Environment.TickCount64;
        if (now - lastFrame < 33) return;
        lastFrame = now;
        Array.Copy(levels, 1, levels, 0, levels.Length - 1);
        levels[^1] = Math.Clamp(level, 0, 1);
        for (int i = 0; i < bars.Length; i++) bars[i].Height = 2 + Math.Min(18, levels[i] * 120);
    }
    private void ResetLevels() { Array.Clear(levels); foreach (var bar in bars) bar.Height = 2; }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        hideTimer?.Cancel(); hideTimer?.Dispose();
        NativeWindow.RemoveWindowSubclass(hwnd, procedure, 2);
        Close();
        GC.KeepAlive(procedure);
    }
}
