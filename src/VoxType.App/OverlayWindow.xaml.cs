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
    private readonly Rectangle[] bars = new Rectangle[5];
    private static readonly double[] barWeights = [0.50, 0.80, 1.0, 0.75, 0.55];
    private float latestLevel;
    private double envelope;
    private readonly UISettings uiSettings = new();
    private readonly DispatcherTimer waveformTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private nint positionTarget;
    private double width = 260, height = 56, fromWidth, fromHeight, targetWidth, targetHeight;
    private long resizeStarted;
    private bool sizingQueued;
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
        procedure = HandleMessage;
        if (!NativeWindow.SetWindowSubclass(hwnd, procedure, 2, 0)) throw new InvalidOperationException("无法设置浮层的无激活窗口行为。");
        NativeWindow.ConfigureOverlay(hwnd);
        for (int i = 0; i < bars.Length; i++)
        {
            bars[i] = (Rectangle)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Rectangle xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='4.5' Height='4.8' RadiusX='2.25' RadiusY='2.25' VerticalAlignment='Center' Fill='{ThemeResource OverlayListeningBrush}' />");
            Waveform.Children.Add(bars[i]);
        }
        waveformTimer.Tick += WaveformTick;
        resizeTimer.Tick += ResizeTick;
        ViewModel.PropertyChanged += ViewModelChanged;
        Root.Loaded += (_, _) => { if (AppWindow.IsVisible) ResizeToContent(true); };
    }
    private nint HandleMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        // Use the whole window as the client area; the native region supplies the capsule outline.
        if (message == 0x83) return 0; // WM_NCCALCSIZE: remove the residual non-client frame.
        if (message == 0x21) return 3; // MA_NOACTIVATE: deliver click without activation.
        if (message == 0x31E) NativeWindow.ConfigureOverlayComposition(window); // WM_DWMCOMPOSITIONCHANGED
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
        if (update.State is SessionState.Idle or SessionState.Cancelling) { resizeTimer.Stop(); AppWindow.Hide(); return; }
        bool entering = !AppWindow.IsVisible;
        positionTarget = update.Focus.Window;
        ResizeToContent(!entering);
        AppWindow.Show(false);
        if (!renderingInitialized)
        {
            NativeWindow.RefreshOverlayContent(hwnd);
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
    private void ViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(ViewModel.Title) or nameof(ViewModel.Detail) or nameof(ViewModel.ShowStop) or nameof(ViewModel.IsStreamingPreview)) || sizingQueued) return;
        // Coalesce a state update's property changes, including subsequent command feedback.
        sizingQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            sizingQueued = false;
            if (!disposed && AppWindow.IsVisible) ResizeToContent(true);
        });
    }
    private void ResizeToContent(bool animate)
    {
        // Measure detached text. Measuring the live stack with a different constraint
        // disturbs WinUI's current arrange pass and can clip the first line after resizing.
        var titleMeasure = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = ViewModel.Title, FontFamily = StatusText.FontFamily, FontSize = StatusText.FontSize,
            FontWeight = StatusText.FontWeight
        };
        var detailMeasure = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = ViewModel.Detail, FontFamily = DetailText.FontFamily, FontSize = DetailText.FontSize,
            MaxLines = 1
        };
        var unlimited = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        titleMeasure.Measure(unlimited);
        detailMeasure.Measure(unlimited);
        // Every state shares one compact width, including live previews and tray controls.
        // Only height adapts to the system text scale; content never stretches the capsule.
        const double nextWidth = 320;
        double textHeight = titleMeasure.DesiredSize.Height + (ViewModel.ShowDetail ? detailMeasure.DesiredSize.Height + 2 : 0);
        double nextHeight = Math.Max(56, Math.Ceiling(textHeight + 20));
        if (Math.Abs(nextWidth - targetWidth) < 0.5 && Math.Abs(nextHeight - targetHeight) < 0.5 && resizeTimer.IsEnabled) return;
        resizeTimer.Stop();
        targetWidth = nextWidth; targetHeight = nextHeight;
        if (!animate || !uiSettings.AnimationsEnabled)
        {
            SetCapsuleSize(nextWidth, nextHeight);
            return;
        }
        if (Math.Abs(width - nextWidth) < 0.5 && Math.Abs(height - nextHeight) < 0.5) { SetCapsuleSize(nextWidth, nextHeight); return; }
        fromWidth = width; fromHeight = height;
        resizeStarted = Environment.TickCount64;
        resizeTimer.Start();
    }
    private void ResizeTick(object? sender, object args)
    {
        double progress = Math.Clamp((Environment.TickCount64 - resizeStarted) / 250d, 0, 1);
        double ease = 1 - Math.Pow(1 - progress, 3);
        SetCapsuleSize(fromWidth + (targetWidth - fromWidth) * ease, fromHeight + (targetHeight - fromHeight) * ease);
        if (progress >= 1) resizeTimer.Stop();
    }
    private void SetCapsuleSize(double nextWidth, double nextHeight)
    {
        width = nextWidth; height = nextHeight;
        Capsule.CornerRadius = new CornerRadius(height / 2);
        NativeWindow.PositionOverlay(hwnd, positionTarget, width, height);
        if (renderingInitialized && AppWindow.IsVisible) NativeWindow.RefreshOverlayContent(hwnd);
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
        try { await Task.Delay(delay, token); if (!disposed && currentSession == session && !token.IsCancellationRequested) { resizeTimer.Stop(); AppWindow.Hide(); } }
        catch (OperationCanceledException) { }
    }
    public void AudioLevel(float level)
    {
        if (!ViewModel.IsListening || disposed) return;
        long now = Environment.TickCount64;
        if (now - lastFrame < 33) return;
        lastFrame = now;
        latestLevel = float.IsFinite(level) ? Math.Clamp(level, 0, 1) : 0;
        if (!uiSettings.AnimationsEnabled) RenderLevels(false);
    }
    private void WaveformTick(object? sender, object args)
    {
        if (disposed || !ViewModel.IsListening) { waveformTimer.Stop(); return; }
        // Silence decays to the baseline, including when the capture source stops publishing levels.
        if (Environment.TickCount64 - lastFrame > 150)
        {
            latestLevel = 0;
        }
        RenderLevels(uiSettings.AnimationsEnabled);
    }
    private void RenderLevels(bool smooth)
    {
        double normalized = latestLevel > 0 ? Math.Clamp((20 * Math.Log10(latestLevel) + 50) / 40, 0, 1) : 0;
        envelope = smooth ? envelope + (normalized - envelope) * (normalized > envelope ? 0.4 : 0.15) : normalized;
        for (int i = 0; i < bars.Length; i++)
        {
            double barHeight = 32 * Math.Max(0.15, envelope * barWeights[i]);
            if (Math.Abs(barHeight - bars[i].Height) > 0.1) bars[i].Height = barHeight;
        }
    }
#if DEBUG
    public void PreviewAudioLevels(bool animate = false)
    {
        if (!layoutFixture || !ViewModel.IsListening) return;
        latestLevel = 0.052f;
        lastFrame = Environment.TickCount64;
        if (animate) { waveformTimer.Start(); AnimateEntrance(); }
        else { RenderLevels(false); waveformTimer.Stop(); }
        // Declared DEBUG fixtures; production never synthesizes audio levels.
    }
#endif
    private void ResetLevels() { latestLevel = 0; envelope = 0; foreach (var bar in bars) bar.Height = 4.8; }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        waveformTimer.Stop();
        waveformTimer.Tick -= WaveformTick;
        resizeTimer.Stop();
        resizeTimer.Tick -= ResizeTick;
        ViewModel.PropertyChanged -= ViewModelChanged;
        hideTimer?.Cancel(); hideTimer?.Dispose();
        NativeWindow.RemoveWindowSubclass(hwnd, procedure, 2);
        Close();
        GC.KeepAlive(procedure);
    }
}
