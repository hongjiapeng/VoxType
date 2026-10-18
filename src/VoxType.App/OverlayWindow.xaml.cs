using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
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
    private OverlayPreferences preferences;
    private OverlayDisplay? display;
    private OverlayRect bounds;
    private OverlayRect? inputArea;
    private bool hasPlacement, dragging, menuOpen;
    private OverlayRect dragStart;
    private FrameworkElement? activeDragRegion;
    private SessionState state = SessionState.Idle;
    public event EventHandler<OverlayPreferences>? PreferencesChanged;
    public event EventHandler? StartRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? CancelRequested;
#if DEBUG
    private bool layoutFixture;
#endif
    public OverlayViewModel ViewModel { get; } = new();
    public OverlayWindow(OverlayPreferences? preferences = null)
    {
        this.preferences = preferences ?? new();
        InitializeComponent();
        // Like SlidePace's floating HUD, delegate dragging to the system title bar.
        ExtendsContentIntoTitleBar = true;
        // This window never activates, so initialize compiled Window bindings explicitly.
        Bindings.Initialize();
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var presenter = OverlappedPresenter.CreateForToolWindow();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
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
        Root.Loaded += (_, _) => { UpdateDragRegion(); if (AppWindow.IsVisible) ResizeToContent(true); };
    }
    private nint HandleMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        // Use the whole window as the client area; the native region supplies the capsule outline.
        if (message == 0x83) return 0; // WM_NCCALCSIZE: remove the residual non-client frame.
        if (message == 0x21) return 3; // MA_NOACTIVATE: deliver click without activation.
        if (message == 0xA3 && wParam == 2) return 0; // Do not maximize on caption double-click.
        if (message == 0x231) // WM_ENTERSIZEMOVE
        {
            resizeTimer.Stop();
            hideTimer?.Cancel();
            dragging = true;
            dragStart = NativeWindow.OverlayBounds(hwnd);
        }
        if (message == 0x232) DispatcherQueue.TryEnqueue(FinishDrag); // WM_EXITSIZEMOVE
        if (message == 0x31E) NativeWindow.ConfigureOverlayComposition(window); // WM_DWMCOMPOSITIONCHANGED
        if (message is 0x7E or 0x1A) DispatcherQueue.TryEnqueue(() =>
        {
            if (!disposed && AppWindow.IsVisible && !dragging) Reposition();
        }); // Display topology / work area changed.
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
        bool newSession = update.SessionId != currentSession;
        revision = update.Revision;
        currentSession = update.SessionId;
        state = update.State;
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
        if (update.State is SessionState.Idle or SessionState.Cancelling) { ShowStandby(); return; }
        bool entering = !AppWindow.IsVisible;
        positionTarget = update.Focus.Window;
        if (newSession || entering || display is null)
        {
            display = NativeWindow.OverlayDisplayFor(positionTarget, preferences.Anchor?.Monitor);
            inputArea = NativeWindow.OverlayInputArea(positionTarget);
            hasPlacement = false;
        }
        ResizeToContent(!entering && !newSession);
        ShowWithoutActivation();
        if (entering) AnimateEntrance();
#if DEBUG
        // Keep static previews visible long enough to inspect every theme and terminal state.
        if (layoutFixture) return;
#endif
        if (update.State == SessionState.Completed && !dragging && !menuOpen)
        {
            hideTimer = new();
            _ = HideLaterAsync(update.SessionId, 600, hideTimer.Token);
        }
    }
    private void ViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(ViewModel.Title) or nameof(ViewModel.Detail) or nameof(ViewModel.ShowStop) or nameof(ViewModel.IsStreamingPreview) or nameof(ViewModel.IsStandby)) || sizingQueued) return;
        // Coalesce a state update's property changes, including subsequent command feedback.
        sizingQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            sizingQueued = false;
            if (disposed) return;
            UpdateDragRegion();
            if (!disposed && AppWindow.IsVisible) ResizeToContent(true);
        });
    }
    private void ResizeToContent(bool animate)
    {
        // Native resizing dismisses an anchored flyout. Keep its host stable until
        // the menu closes, then apply the latest state's measured dimensions.
        if (menuOpen || dragging) return;
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
        // Active states share one compact width, including live previews and tray controls.
        // Only height adapts to the system text scale; content never stretches the capsule.
        double nextWidth = ViewModel.IsStandby ? 120 : 320;
        double textHeight = titleMeasure.DesiredSize.Height + (ViewModel.ShowDetail ? detailMeasure.DesiredSize.Height + 2 : 0);
        double nextHeight = ViewModel.IsStandby ? 56 : Math.Max(56, Math.Ceiling(textHeight + 20));
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
        display ??= NativeWindow.OverlayDisplayFor(positionTarget, preferences.Anchor?.Monitor);
        int pixelWidth = (int)Math.Round(width * display.Scale), pixelHeight = (int)Math.Round(height * display.Scale);
        var previousBounds = bounds;
        bounds = hasPlacement
            ? OverlayPlacement.Clamp(display.Work, bounds with { Width = pixelWidth, Height = pixelHeight })
            : OverlayPlacement.Place(display.Work, pixelWidth, pixelHeight, display.Scale, preferences.Anchor, inputArea);
        hasPlacement = true;
        if (bounds != previousBounds) NativeWindow.PositionOverlay(hwnd, bounds);
        // Sending synthetic WM_ACTIVATE for an unchanged layout closes an open
        // MenuFlyout. Re-prime composition only after an actual native size change.
        bool sizeChanged = bounds.Width != previousBounds.Width || bounds.Height != previousBounds.Height;
        if (renderingInitialized && AppWindow.IsVisible && sizeChanged && !menuOpen) NativeWindow.RefreshOverlayContent(hwnd);
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
        try { await Task.Delay(delay, token); if (!disposed && currentSession == session && !token.IsCancellationRequested) ShowStandby(); }
        catch (OperationCanceledException) { }
    }
    public void InitializeStandby()
    {
        if (preferences.Pinned) ShowStandby();
    }
    public void SetPinned(bool pinned)
    {
        if (preferences.Pinned == pinned) return;
        preferences = preferences with { Pinned = pinned };
        PreferencesChanged?.Invoke(this, preferences);
        if (ViewModel.IsStandby || state is SessionState.Idle or SessionState.Cancelling) ShowStandby();
    }
    public void ResetPosition()
    {
        preferences = preferences with { Anchor = null };
        PreferencesChanged?.Invoke(this, preferences);
        if (AppWindow.IsVisible) Reposition();
    }
    private void ShowStandby()
    {
        resizeTimer.Stop();
        waveformTimer.Stop();
        ResetLevels();
        ViewModel.Update(new(currentSession, SessionState.Idle, "麦克风未开启", "", false, VoxType.Core.TextInjection.FocusContext.Empty));
        if (!preferences.Pinned) { AppWindow.Hide(); return; }
        VisualStateManager.GoToState(Root, "Listening", false);
        positionTarget = NativeWindow.GetForegroundWindow();
        if (!AppWindow.IsVisible || display is null)
        {
            display = NativeWindow.OverlayDisplayFor(positionTarget, preferences.Anchor?.Monitor);
            inputArea = NativeWindow.OverlayInputArea(positionTarget);
            hasPlacement = false;
        }
        ResizeToContent(false);
        ShowWithoutActivation();
    }
    private void ShowWithoutActivation()
    {
        if (!AppWindow.IsVisible) AppWindow.Show(false);
        if (!renderingInitialized)
        {
            NativeWindow.RefreshOverlayContent(hwnd);
            renderingInitialized = true;
        }
    }
    private void StartClicked(object sender, RoutedEventArgs e)
    {
#if DEBUG
        // Layout fixtures remain microphone- and injection-free when their new
        // start/retry controls are exercised by UI acceptance scripts.
        if (layoutFixture)
        {
            Update(new(currentSession + 1, SessionState.Listening, "静态布局检查", "布局检查", true,
                new(NativeWindow.GetForegroundWindow(), 0, 0, 0, null), FromTray: true));
            return;
        }
#endif
        if (ViewModel.CanStart) StartRequested?.Invoke(this, EventArgs.Empty);
    }
    private void MoreClicked(object sender, RoutedEventArgs e)
    {
        hideTimer?.Cancel();
        resizeTimer.Stop();
        var menu = new MenuFlyout();
        menuOpen = true;
        menu.Closed += (_, _) =>
        {
            menuOpen = false;
            if (disposed) return;
            if (AppWindow.IsVisible) ResizeToContent(false);
            ResumeCompletionHide();
        };
        var pin = new ToggleMenuFlyoutItem { Text = "固定在桌面", IsChecked = preferences.Pinned };
        AutomationProperties.SetAutomationId(pin, "OverlayPin");
        pin.Click += (_, _) =>
        {
            SetPinned(pin.IsChecked);
        };
        menu.Items.Add(pin);
        var reset = new MenuFlyoutItem { Text = "恢复自动位置" };
        AutomationProperties.SetAutomationId(reset, "OverlayResetPosition");
        reset.Click += (_, _) =>
        {
            ResetPosition();
        };
        menu.Items.Add(reset);
        var move = new MenuFlyoutSubItem { Text = "微调位置" };
        AutomationProperties.SetAutomationId(move, "OverlayMove");
        foreach (var (label, dx, dy) in new[] { ("向左", -32, 0), ("向右", 32, 0), ("向上", 0, -32), ("向下", 0, 32) })
        {
            var item = new MenuFlyoutItem { Text = label };
            AutomationProperties.SetAutomationId(item, "OverlayMove" + (dx < 0 ? "Left" : dx > 0 ? "Right" : dy < 0 ? "Up" : "Down"));
            item.Click += (_, _) =>
            {
                resizeTimer.Stop();
                bounds = OverlayPlacement.Clamp(display!.Work, bounds with { X = bounds.X + (int)(dx * display.Scale), Y = bounds.Y + (int)(dy * display.Scale) });
                NativeWindow.PositionOverlay(hwnd, bounds);
                RememberPosition();
            };
            move.Items.Add(item);
        }
        menu.Items.Add(move);
        menu.Items.Add(new MenuFlyoutSeparator());
        var settings = new MenuFlyoutItem { Text = "设置" };
        AutomationProperties.SetAutomationId(settings, "OverlaySettings");
        settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(settings);
        if (state is SessionState.Starting or SessionState.Listening or SessionState.Stopping or SessionState.Processing)
        {
            var cancel = new MenuFlyoutItem { Text = "取消本次输入" };
            AutomationProperties.SetAutomationId(cancel, "OverlayCancel");
            cancel.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(cancel);
        }
        else
        {
            var dismiss = new MenuFlyoutItem { Text = ViewModel.IsStandby ? "关闭常驻入口" : "收起提示" };
            AutomationProperties.SetAutomationId(dismiss, "OverlayDismiss");
            dismiss.Click += (_, _) =>
            {
                if (state is SessionState.Starting or SessionState.Listening or SessionState.Stopping or SessionState.Processing or SessionState.Injecting) return;
                if (ViewModel.IsStandby)
                {
                    preferences = preferences with { Pinned = false };
                    PreferencesChanged?.Invoke(this, preferences);
                }
                ShowStandby();
            };
            menu.Items.Add(dismiss);
        }
        // Menus are transient so opening them does not move XAML focus away from the editor.
        menu.ShowMode = Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.Transient;
        menu.ShowAt((FrameworkElement)sender);
    }
    private void Reposition()
    {
        resizeTimer.Stop();
        positionTarget = NativeWindow.GetForegroundWindow();
        display = NativeWindow.OverlayDisplayFor(positionTarget, preferences.Anchor?.Monitor);
        inputArea = NativeWindow.OverlayInputArea(positionTarget);
        hasPlacement = false;
        ResizeToContent(false);
    }
    private void DragRegion_Loaded(object sender, RoutedEventArgs e) => UpdateDragRegion();
    private void UpdateDragRegion()
    {
        FrameworkElement region = ViewModel.IsStandby ? IdleDragRegion : SessionDragRegion;
        if (region.XamlRoot is null || activeDragRegion == region) return;
        SetTitleBar(region);
        activeDragRegion = region;
    }
    private void FinishDrag()
    {
        if (disposed || !dragging) return;
        bounds = NativeWindow.OverlayBounds(hwnd);
        display = NativeWindow.OverlayDisplayFor(hwnd);
        bounds = OverlayPlacement.Clamp(display.Work, bounds);
        NativeWindow.PositionOverlay(hwnd, bounds);
        hasPlacement = true;
        dragging = false;
        // Save the actual native position, not the last XAML pointer coordinate.
        if (bounds != dragStart) RememberPosition();
        ResizeToContent(false);
        ResumeCompletionHide();
    }
    private void ResumeCompletionHide()
    {
        if (disposed || dragging || menuOpen || state != SessionState.Completed || ViewModel.IsStandby) return;
#if DEBUG
        if (layoutFixture) return;
#endif
        hideTimer?.Cancel(); hideTimer?.Dispose();
        hideTimer = new();
        _ = HideLaterAsync(currentSession, 600, hideTimer.Token);
    }
    private void RememberPosition()
    {
        preferences = preferences with { Anchor = OverlayPlacement.Remember(display!.Device, display.Work, bounds) };
        PreferencesChanged?.Invoke(this, preferences);
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
    internal object DragRegionsForCheck => Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
        .GetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Caption)
        .Select(rect => new { rect.X, rect.Y, rect.Width, rect.Height }).ToArray();
    public async Task CompletePreviewAfterDelayAsync()
    {
        long session = currentSession;
        await Task.Delay(2500);
        if (disposed || !layoutFixture || currentSession != session) return;
        // Deliver internally, like the coordinator. A second external app activation
        // would itself dismiss native popup menus and invalidate this acceptance case.
        Update(new(session + 1, SessionState.Completed, "文字已输入到当前应用", "交互检查", true,
            new(positionTarget, 0, 0, 0, null)));
    }
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
