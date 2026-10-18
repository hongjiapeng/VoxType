using VoxType.Core.Audio;
using VoxType.Core.Dictation;
using VoxType.Core.Hotkeys;
using VoxType.Core.Settings;
using VoxType.Core.TextInjection;
using VoxType.Infrastructure.Audio;
using VoxType.Infrastructure.Hotkeys;
using VoxType.Infrastructure.TextInjection;
using VoxType.Infrastructure.Windows;
using VoxType.Infrastructure.Transcription;
using VoxType_App.ViewModels;

namespace VoxType_App;

public sealed class AppServices : IAsyncDisposable
{
    private IFocusContextService? focus;
    private IAudioCaptureService? audio;
    private GlobalHotkeyService? hotkeys;
    private readonly HttpClient transcriptionClient = OpenAiCompatibleProvider.CreateClient();
    private readonly HttpClient modelClient = LocalModelManager.CreateClient();
    private NativeClipboardHost? clipboardHost;
    private ITextInjectionService? injection;
    private readonly LocalWhisperService localWhisper = new(new WhisperBackend());
    private bool disposed;
    private Task overlaySave = Task.CompletedTask;
    private long lastLevelFrame, displayedSession, displayedRevision;
    public SettingsSession Settings { get; }
    public MainPageViewModel SettingsViewModel { get; }
    public MainWindow SettingsWindow { get; }
    public OverlayWindow Overlay { get; }
    public TrayService Tray { get; }
    public DictationCoordinator? Coordinator { get; private set; }
    public AppServices(SettingsLoadResult loaded, ISettingsStore store, string? managedModelPath = null,
        VoxType.Infrastructure.Settings.OverlayPreferencesStore? overlayStore = null, OverlayPreferences? overlayPreferences = null)
    {
        managedModelPath ??= WhisperModelPath.Default;
        Settings = new(loaded.Settings);
        var models = new ModelManagerViewModel(
            new LocalModelManager(modelClient, managedModelPath, localWhisper.DeleteModelAsync),
            path => SettingsViewModel!.UseInstalledModelAsync(path), Dispatch);
        SettingsViewModel = new(Settings, async updated => { await store.SaveAsync(updated); Settings.Publish(updated); })
        { DefaultModelPath = managedModelPath, DefaultR2T2DataDirectory = R2T2LocalLauncher.GetDefaultDataDirectory(AppContext.BaseDirectory), Models = models,
            RunLocalR2T2Async = (backend, directory, prepare) => R2T2LocalLauncher.LaunchAsync(AppContext.BaseDirectory, backend, directory, prepare) };
        SettingsWindow = new(SettingsViewModel);
        Overlay = new(overlayPreferences);
        SettingsViewModel.OverlayPinned = overlayPreferences?.Pinned == true;
        SettingsViewModel.OverlayPinChanged += (_, pinned) => Overlay.SetPinned(pinned);
        SettingsViewModel.ResetOverlayPositionRequested += (_, _) => Overlay.ResetPosition();
        Overlay.PreferencesChanged += (_, preferences) =>
        {
            SettingsViewModel.OverlayPinned = preferences.Pinned;
            if (overlayStore is not null) overlaySave = SaveOverlayAsync(overlaySave, overlayStore, preferences);
        };
        Overlay.StartRequested += (_, _) =>
        {
            // The non-activating button leaves the editor foreground. Snapshot that
            // current target rather than reusing an earlier tray-menu snapshot.
            focus?.Capture();
            (focus as FocusContextService)?.FreezeForTray();
            Start(fromTray: true);
        };
        Overlay.CancelRequested += (_, _) => Coordinator?.Cancel();
        Overlay.SettingsRequested += (_, _) => SettingsWindow.ShowSettings();
        Tray = new(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        Tray.SettingsRequested += (_, _) => SettingsWindow.ShowSettings();
        Tray.ExitRequested += (_, _) => ((App)Microsoft.UI.Xaml.Application.Current).RequestExit();
        Tray.RecordingRequested += (_, _) => Start(fromTray: true);
        Tray.MenuOpening += (_, _) => (focus as FocusContextService)?.FreezeForTray();
        Overlay.ViewModel.StopRequested += (_, _) => Coordinator?.Stop();
        SettingsViewModel.ThemeChanged += (_, theme) => { SettingsWindow.ApplyTheme(theme); Overlay.ApplyTheme(theme); };
        Settings.Changed += (_, updated) =>
        {
            if (Coordinator?.IsBusy != true) hotkeys?.Configure(updated.Hotkey);
            localWhisper.SettingsChanged(updated);
            Tray.HotkeyLabel = updated.Hotkey.DisplayName; Tray.RefreshTooltip();
        };
        Tray.HotkeyLabel = Settings.Current.Hotkey.DisplayName; Tray.RefreshTooltip();
        localWhisper.SettingsChanged(Settings.Current);
        SettingsViewModel.CopyRequested += async (_, _) =>
        {
            try { SettingsViewModel.Message = injection is not null && await injection.CopyAsync(SettingsViewModel.LatestText) ? "最近文本已复制。" : "复制失败，请稍后重试。"; }
            catch { SettingsViewModel.Message = "复制失败，请稍后重试。"; }
        };
        InitializeRecording();
        SettingsWindow.ApplyTheme(Settings.Current.Theme);
        Overlay.ApplyTheme(Settings.Current.Theme);
        Overlay.InitializeStandby();
        if (loaded.Warning is not null) SettingsViewModel.Message = loaded.Warning;
    }
    private void InitializeRecording()
    {
        try
        {
            focus = new FocusContextService();
            audio = new WasapiAudioCaptureService();
            clipboardHost = new NativeClipboardHost();
            injection = new TextInjectionService(new NativeInputTargetPlatform(), new ClipboardService(clipboardHost));
            var providers = new TranscriptionProviderRegistry(new Dictionary<string, Func<AppSettings, VoxType.Core.Transcription.IProviderLease>>
            {
                ["openai-compatible"] = snapshot => new ProviderLease(new OpenAiCompatibleProvider(transcriptionClient, snapshot)),
                ["r2t2-server"] = snapshot => new ProviderLease(new OpenAiCompatibleProvider(transcriptionClient, snapshot)),
                ["whisper-local"] = snapshot =>
                {
                    WhisperModelPath.Validate(WhisperModelPath.Resolve(snapshot.LocalModelPath));
                    return localWhisper.Acquire(snapshot);
                }
            });
            Coordinator = new(Settings, audio, focus, providers, injection);
            Coordinator.Updated += (_, update) => Dispatch(() =>
            {
                if (update.SessionId < displayedSession || update.SessionId == displayedSession && update.Revision < displayedRevision) return;
                displayedSession = update.SessionId; displayedRevision = update.Revision;
                Overlay.Update(update);
                Overlay.ViewModel.CanStart = !Coordinator.IsBusy;
                Tray.RecordingAvailable = !Coordinator.IsBusy;
                if (update.State is SessionState.Failed or SessionState.Completed) SettingsViewModel.Message = update.Message;
                if (update.State == SessionState.Cancelling) SettingsViewModel.Message = "已取消，正在清理当前任务；完成后可再次录音。";
            });
            Coordinator.AudioLevelChanged += (_, update) =>
            {
                long now = Environment.TickCount64, previous = Interlocked.Read(ref lastLevelFrame);
                if (now - previous < 33 || Interlocked.CompareExchange(ref lastLevelFrame, now, previous) != previous) return;
                Dispatch(() => { if (displayedSession == update.SessionId) Overlay.AudioLevel(update.Level); });
            };
            Coordinator.Transcribed += (_, text) => Dispatch(() => SettingsViewModel.LatestText = text);
            Coordinator.SessionFinished += (_, _) => Dispatch(() =>
            {
                Overlay.ViewModel.CanStart = !Coordinator.IsBusy;
                if (hotkeys is null) return;
                hotkeys.CanCancel = Coordinator.IsBusy;
                Tray.RecordingAvailable = !Coordinator.IsBusy;
                if (!Coordinator.IsBusy) hotkeys.Configure(Settings.Current.Hotkey);
                if (!Coordinator.IsBusy && SettingsViewModel.Message == "已取消，正在清理当前任务；完成后可再次录音。")
                    SettingsViewModel.Message = "已取消，可再次按住快捷键录音。";
            });
            hotkeys = new GlobalHotkeyService();
            hotkeys.Unavailable += (_, message) => Dispatch(() => SettingsViewModel.Message = message);
            hotkeys.Action += (_, action) => Dispatch(() =>
            {
                switch (action)
                {
                    case HotkeyAction.Pressed: Start(false); break;
                    case HotkeyAction.Released: Coordinator.Stop(); break;
                    case HotkeyAction.Cancelled or HotkeyAction.Interrupted: Coordinator.Cancel(); break;
                }
            });
            hotkeys.Start(Settings.Current.Hotkey);
            Tray.RecordingAvailable = true;
            SettingsViewModel.Message = "语音输入已就绪。请配置本地模型或在线服务器，然后在外部输入框按住快捷键说话。";
        }
        catch
        {
            hotkeys?.Dispose(); hotkeys = null;
            Overlay.ViewModel.CanStart = false;
            Tray.RecordingAvailable = false;
            SettingsViewModel.Message = "全局录音服务未能启动，请重新启动 VoxType。设置仍可使用。";
        }
    }
    private void Start(bool fromTray)
    {
        if (Coordinator?.IsBusy == true) { SettingsViewModel.Message = "当前语音输入仍在处理或清理，请稍后再试。"; return; }
        if (hotkeys is not null && Coordinator?.Start(fromTray) == true)
        {
            hotkeys.CanCancel = Coordinator.IsBusy;
            Tray.RecordingAvailable = false;
            Overlay.ViewModel.CanStart = false;
        }
    }
#if DEBUG
    internal void StartRecordingForCheck() => Start(true);
#endif
    private void Dispatch(Action action) => App.DispatcherQueue.TryEnqueue(() => { if (!disposed) action(); });
    private async Task SaveOverlayAsync(Task previous, VoxType.Infrastructure.Settings.OverlayPreferencesStore store, OverlayPreferences preferences)
    {
        await previous;
        try { await store.SaveAsync(preferences); }
        catch { if (!disposed) SettingsViewModel.Message = "浮窗设置未能保存，本次运行仍然有效。"; }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        hotkeys?.Dispose();
        try
        {
            await overlaySave;
            await SettingsViewModel.Models.DisposeAsync();
            try
            {
                if (Coordinator is not null) await Coordinator.DisposeAsync();
                else if (audio is not null) await audio.DisposeAsync();
            }
            finally { await localWhisper.DisposeAsync(); }
        }
        finally
        {
            focus?.Dispose();
            transcriptionClient.Dispose();
            modelClient.Dispose();
            clipboardHost?.Dispose();
            Tray.Dispose();
            Overlay.Dispose();
            SettingsWindow.ExitWindow();
        }
    }
}
