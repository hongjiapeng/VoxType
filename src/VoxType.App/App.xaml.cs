using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace VoxType_App;

public partial class App : Application
{
    private AppInstance? instance;
    private AppServices? services;
    private bool settingsRequested;
    private bool exiting;
#if DEBUG
    private long layoutSession;
    private bool checkingLayout;
#endif
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;
    public App()
    {
        InitializeComponent();
#if DEBUG
        UnhandledException += (_, args) => System.Diagnostics.Debug.WriteLine(args.Exception.ToString());
#endif
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        // Do not create windows, microphone or hook resources until instance registration succeeds.
        instance = AppInstance.FindOrRegisterForKey("VoxType");
        if (!instance.IsCurrent)
        {
            await instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            Exit();
            return;
        }
        instance.Activated += Instance_Activated;
        var activated = AppInstance.GetCurrent().GetActivatedEventArgs();
        string arguments = activated.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch ? launch.Arguments : args.Arguments;
        string settingsPath = VoxType.Infrastructure.Settings.JsonSettingsStore.DefaultPath;
        string modelPath = VoxType.Infrastructure.Transcription.WhisperModelPath.Default;
#if DEBUG
        // Isolated development profiles keep UI acceptance scripts out of user configuration.
        string profile = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(x => x.StartsWith("--test-profile="))?[15..] ?? "";
        if (Guid.TryParseExact(profile, "N", out var profileId))
        {
            settingsPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(settingsPath)!, "development-profiles", profileId.ToString("N"), "settings.json");
            modelPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(modelPath)!, "development-profiles", profileId.ToString("N"), "ggml-base.bin");
        }
#endif
        var store = new VoxType.Infrastructure.Settings.JsonSettingsStore(
            new VoxType.Infrastructure.Settings.AtomicSettingsFile(settingsPath),
            new VoxType.Infrastructure.Settings.DpapiSecretProtector());
        var loaded = await store.LoadAsync();
#if DEBUG
        if (modelPath != VoxType.Infrastructure.Transcription.WhisperModelPath.Default && string.IsNullOrWhiteSpace(loaded.Settings.LocalModelPath))
            loaded = loaded with { Settings = loaded.Settings with { LocalModelPath = modelPath } };
#endif
        var overlayStore = new VoxType.Infrastructure.Settings.OverlayPreferencesStore(
            new VoxType.Infrastructure.Settings.AtomicSettingsFile(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(settingsPath)!, "overlay.json")));
        var overlayPreferences = await overlayStore.LoadAsync();
        services = new(loaded, store, modelPath, overlayStore, overlayPreferences);
#if DEBUG
        services.Overlay.ViewModel.StopRequested += (_, _) => { if (checkingLayout) services.Overlay.ViewModel.Title = "结束按钮检查通过"; };
#endif
        if (settingsRequested) services.SettingsWindow.ShowSettings();
        HandleArguments(arguments);
    }
    private void Instance_Activated(object? sender, AppActivationArguments args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            string arguments = args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch ? launch.Arguments : "";
            if (services is null) settingsRequested = true;
            else HandleArguments(arguments.Length == 0 ? "--settings" : arguments);
        });
    }
    private void HandleArguments(string arguments)
    {
        if (arguments.Contains("--settings", StringComparison.Ordinal)) services?.SettingsWindow.ShowSettings();
#if DEBUG
        // Static Phase 1 visual fixtures. Never capture audio, transcribe or inject text.
        if (arguments.Contains("--exit-test", StringComparison.Ordinal)) { RequestExit(); return; }
        const string diagnosticPrefix = "--check-diagnostics=";
        if (arguments.StartsWith(diagnosticPrefix, StringComparison.Ordinal) && services is not null)
        {
            string modelPath = VoxType.Infrastructure.Transcription.WhisperModelPath.Resolve(services.Settings.Current.LocalModelPath);
            var diagnosticState = new { services.Coordinator?.IsBusy, services.Coordinator?.State,
                services.Settings.Current.ProviderId, Hotkey = services.Settings.Current.Hotkey.DisplayName, SettingsVersion = services.Settings.Current.Version,
                services.Settings.Current.R2T2StreamOutput,
                ModelPath = modelPath, ModelExists = System.IO.File.Exists(modelPath),
                DefaultModelPath = services.SettingsViewModel.DefaultModelPath,
                SettingsPath = VoxType.Infrastructure.Settings.JsonSettingsStore.DefaultPath, OverlayVisible = services.Overlay.AppWindow.IsVisible,
                OverlayDragRegions = services.Overlay.DragRegionsForCheck };
            System.IO.File.WriteAllText(arguments[diagnosticPrefix.Length..].Trim('"'), System.Text.Json.JsonSerializer.Serialize(diagnosticState));
            return;
        }
        if (arguments.Contains("--check-recording=start", StringComparison.Ordinal)) { services?.StartRecordingForCheck(); return; }
        if (arguments.Contains("--check-recording=stop", StringComparison.Ordinal)) { services?.Coordinator?.Stop(); return; }
        if (arguments.Contains("--check-recording=cancel", StringComparison.Ordinal)) { services?.Coordinator?.Cancel(); return; }
        const string prefix = "--check-overlay=";
        int index = arguments.IndexOf(prefix, StringComparison.Ordinal);
        if (index < 0 || services is null) return;
        if (services.Coordinator?.IsBusy == true) return;
        string value = arguments[(index + prefix.Length)..].Split(' ')[0];
        string previewTheme = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(x => x.StartsWith("--check-theme=", StringComparison.Ordinal))?[14..] ?? services.Settings.Current.Theme;
        services.Overlay.ApplyTheme(previewTheme);
        var state = value switch
        {
            "listening" or "voice" or "motion" or "tray" or "preview" or "preview-long" or "menu-completed" => VoxType.Core.Dictation.SessionState.Listening,
            "processing" => VoxType.Core.Dictation.SessionState.Processing,
            "injecting" => VoxType.Core.Dictation.SessionState.Injecting,
            "completed" or "auto-completed" => VoxType.Core.Dictation.SessionState.Completed,
            "failed" or "failed-long" or "failed-short" => VoxType.Core.Dictation.SessionState.Failed,
            _ => VoxType.Core.Dictation.SessionState.Idle
        };
        checkingLayout = state != VoxType.Core.Dictation.SessionState.Idle;
        string previewMessage = value switch
        {
            "failed" => "没有检测到足够音量，请检查麦克风并重试。",
            "failed-short" => "录音太短，请按住快捷键多说一会儿。",
            "failed-long" => "无法将识别结果发送到当前应用。文本已复制到剪贴板，请返回输入框后手动粘贴。",
            "processing" => "正在把语音转换为文字…",
            "completed" => "文字已输入到当前应用",
            _ => "静态布局检查"
        };
        services.Overlay.Update(new(++layoutSession, state, previewMessage, value == "auto-completed" ? "交互检查" : "布局检查", true,
            new(VoxType.Infrastructure.Windows.NativeWindow.GetForegroundWindow(), 0, 0, 0, null), value == "tray",
            PartialText: value switch
            {
                "preview" => "测试，测试。",
                "preview-long" => "这是一段很长的实时转写内容，用来检查浮窗在连续说话时仍然保持紧凑，不会突然拉长，也不会把文字挤到下一行。",
                _ => null
            }));
        if (value == "voice") services.Overlay.PreviewAudioLevels();
        if (value == "motion") services.Overlay.PreviewAudioLevels(true);
        if (value == "menu-completed") _ = services.Overlay.CompletePreviewAfterDelayAsync();
#endif
    }
    public async void RequestExit()
    {
        if (exiting) return;
        exiting = true;
        if (instance is not null) instance.Activated -= Instance_Activated;
        try { if (services is not null) await services.DisposeAsync(); }
        finally { instance?.UnregisterKey(); Exit(); }
    }
}
