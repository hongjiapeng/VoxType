using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Shapes;
using System.Numerics;
using Windows.UI.ViewManagement;
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
    private readonly UISettings uiSettings = new();
    private readonly DispatcherTimer waveformTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private CancellationTokenSource? hideTimer;
    private long currentSession;
    private long revision;
    private long lastFrame;
    private bool renderingInitialized;
    private bool disposed;
#if DEBUG
    private bool layoutFixture;
#endif
    public OverlayViewModel ViewModel { get; } = new();
    public static Visibility IconVisibility(bool processing) => processing ? Visibility.Collapsed : Visibility.Visible;
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
            bars[i] = (Rectangle)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Rectangle xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='3' Height='3' RadiusX='1.5' RadiusY='1.5' VerticalAlignment='Center' Fill='{ThemeResource OverlayListeningBrush}' />");
            Waveform.Children.Add(bars[i]);
        }
        waveformTimer.Tick += WaveformTick;
    }
    private nint HandleMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        // Use the whole window as the client area, while retaining DWM's rounded corners and shadow.
        if (message == 0x83) return 0; // WM_NCCALCSIZE: remove the residual non-client frame.
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
        VisualStateManager.GoToState(Root, update.State switch
        {
            SessionState.Completed => "Completed",
            SessionState.Failed => "Failed",
            SessionState.Listening => "Listening",
            _ => "Processing"
        }, false);
        if (ViewModel.IsListening) waveformTimer.Start();
        else { waveformTimer.Stop(); ResetLevels(); }
        if (update.State is SessionState.Idle or SessionState.Cancelling) { AppWindow.Hide(); return; }
        bool entering = !AppWindow.IsVisible;
        NativeWindow.PositionOverlay(hwnd, update.Focus.Window);
        AppWindow.Show(false);
        if (!renderingInitialized)
        {
            NativeWindow.InitializeOverlayContent(hwnd);
            renderingInitialized = true;
        }
        if (entering) AnimateEntrance();
#if DEBUG
        // Keep static previews visible long enough to inspect every theme and terminal state.
        if (layoutFixture) return;
#endif
        if (update.State is SessionState.Completed or SessionState.Failed)
        {
            hideTimer = new();
            int delay = update.State == SessionState.Completed ? 1400 : 6000;
            _ = HideLaterAsync(update.SessionId, delay, hideTimer.Token);
        }
    }
    private void AnimateEntrance()
    {
        ElementCompositionPreview.SetIsTranslationEnabled(ContentPanel, true);
        var visual = ElementCompositionPreview.GetElementVisual(ContentPanel);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        visual.Opacity = 1;
        ContentPanel.Translation = Vector3.Zero;
        if (!uiSettings.AnimationsEnabled) return;
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1);
        fade.Duration = TimeSpan.FromMilliseconds(160);
        visual.StartAnimation("Opacity", fade);
        var offset = visual.Compositor.CreateVector3KeyFrameAnimation();
        offset.InsertKeyFrame(0, new Vector3(0, 4, 0));
        offset.InsertKeyFrame(1, Vector3.Zero);
        offset.Duration = TimeSpan.FromMilliseconds(180);
        visual.StartAnimation("Translation", offset);
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
        if (!uiSettings.AnimationsEnabled) RenderLevels(false);
    }
    private void WaveformTick(object? sender, object args)
    {
        if (disposed || !ViewModel.IsListening) { waveformTimer.Stop(); return; }
        // Silence decays to the baseline, including when the capture source stops publishing levels.
        if (Environment.TickCount64 - lastFrame > 150)
        {
            Array.Copy(levels, 1, levels, 0, levels.Length - 1);
            levels[^1] = 0;
        }
        RenderLevels(uiSettings.AnimationsEnabled);
    }
    private void RenderLevels(bool smooth)
    {
        for (int i = 0; i < bars.Length; i++)
        {
            double target = 3 + 19 * Math.Sqrt(Math.Clamp(levels[i] * 18, 0, 1));
            double height = smooth ? bars[i].Height + (target - bars[i].Height) * 0.45 : target;
            if (Math.Abs(height - bars[i].Height) > 0.1) bars[i].Height = height;
        }
    }
#if DEBUG
    public void PreviewAudioLevels(bool animate = false)
    {
        if (!layoutFixture || !ViewModel.IsListening) return;
        float[] sample = [0.002f, 0.004f, 0.008f, 0.024f, 0.014f, 0.007f, 0.035f, 0.052f,
            0.018f, 0.011f, 0.028f, 0.043f, 0.015f, 0.005f, 0.010f, 0.003f];
        sample.CopyTo(levels, 0);
        lastFrame = Environment.TickCount64;
        if (animate) { waveformTimer.Start(); AnimateEntrance(); }
        else { RenderLevels(false); waveformTimer.Stop(); }
        // Declared DEBUG fixtures; production never synthesizes audio levels.
    }
#endif
    private void ResetLevels() { Array.Clear(levels); foreach (var bar in bars) bar.Height = 3; }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        waveformTimer.Stop();
        waveformTimer.Tick -= WaveformTick;
        hideTimer?.Cancel(); hideTimer?.Dispose();
        NativeWindow.RemoveWindowSubclass(hwnd, procedure, 2);
        Close();
        GC.KeepAlive(procedure);
    }
}
