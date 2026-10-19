using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoxType.Core.Hotkeys;
using VoxType.Core.Settings;
using VoxType.Core.Transcription;

namespace VoxType_App.ViewModels;

public partial class MainPageViewModel : ObservableObject
{
    private readonly SettingsSession settings;
    private readonly Func<AppSettings, Task> save;
    public IReadOnlyList<ProviderDefinition> Providers => ProviderDefinition.All;
    public IReadOnlyList<string> Languages { get; } = ["auto", "zh", "en", "ja", "ko", "de", "fr", "es"];
    public IReadOnlyList<string> Themes { get; } = ["system", "light", "dark"];
    public IReadOnlyList<string> LocalInferenceBackends { get; } = ["cpu", "vulkan"];
    [ObservableProperty] public partial ProviderDefinition SelectedProvider { get; set; }
    [ObservableProperty] public partial string LocalModelPath { get; set; }
    [ObservableProperty] public partial string LocalInferenceBackend { get; set; }
    [ObservableProperty] public partial string R2T2InferenceBackend { get; set; }
    [ObservableProperty] public partial string R2T2DataDirectory { get; set; }
    [ObservableProperty] public partial bool R2T2StreamOutput { get; set; }
    [ObservableProperty] public partial string BaseUrl { get; set; }
    [ObservableProperty] public partial string Model { get; set; }
    [ObservableProperty] public partial string ApiKey { get; set; }
    [ObservableProperty] public partial string Language { get; set; }
    [ObservableProperty] public partial string Theme { get; set; }
    [ObservableProperty] public partial string Key { get; set; }
    [ObservableProperty] public partial bool Alt { get; set; }
    [ObservableProperty] public partial bool Control { get; set; }
    [ObservableProperty] public partial bool Shift { get; set; }
    [ObservableProperty] public partial bool Windows { get; set; }
    [ObservableProperty] public partial string Message { get; set; } = "录音与识别服务正在接入，目前尚不可用。";
    [ObservableProperty] public partial string LatestText { get; set; } = "";
    [ObservableProperty] public partial bool OverlayPinned { get; set; }
    public event EventHandler<bool>? OverlayPinChanged;
    public event EventHandler? ResetOverlayPositionRequested;
    partial void OnOverlayPinnedChanged(bool value) => OverlayPinChanged?.Invoke(this, value);
    [RelayCommand] private void ResetOverlayPosition() => ResetOverlayPositionRequested?.Invoke(this, EventArgs.Empty);
    [ObservableProperty] public partial int Section { get; set; }
    public bool ShowGeneral => Section == 0;
    public bool ShowRecognition => Section == 1;
    public bool ShowHotkeys => Section == 2;
    public bool HasModelPath => SelectedProvider.Fields.Contains(ProviderField.ModelPath);
    public bool HasBaseUrl => SelectedProvider.Fields.Contains(ProviderField.BaseUrl);
    public bool HasApiKey => SelectedProvider.Fields.Contains(ProviderField.ApiKey);
    public bool HasModel => SelectedProvider.Fields.Contains(ProviderField.Model);
    public bool HasR2T2 => SelectedProvider.Id == "r2t2-server";
    public string ProviderDescription => SelectedProvider.Description;
    public string DefaultModelPath { get; init; } = "";
    public string DefaultR2T2DataDirectory { get; init; } = "";
    public ModelManagerViewModel Models { get; init; } = null!;
    public Func<string, string, bool, IProgress<string>, CancellationToken, Task>? RunLocalR2T2Async { get; init; }
    public Func<Task>? StopR2T2ServiceAsync { get; init; }
    private CancellationTokenSource? r2t2Operation;
    private int r2t2StatusRevision;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunR2T2))]
    [NotifyCanExecuteChangedFor(nameof(PrepareLocalR2T2Command))]
    [NotifyCanExecuteChangedFor(nameof(LaunchLocalR2T2Command))]
    [NotifyCanExecuteChangedFor(nameof(StopLocalR2T2Command))]
    public partial bool IsR2T2Busy { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunR2T2))]
    [NotifyCanExecuteChangedFor(nameof(PrepareLocalR2T2Command))]
    [NotifyCanExecuteChangedFor(nameof(LaunchLocalR2T2Command))]
    [NotifyCanExecuteChangedFor(nameof(StopLocalR2T2Command))]
    public partial bool IsR2T2Running { get; set; }
    [ObservableProperty] public partial string R2T2Status { get; set; } = "首次使用请准备资源，约需下载 1.63 GiB 模型。";
    public bool CanRunR2T2 => !IsR2T2Busy && !IsR2T2Running;
    public bool CanStopR2T2 => r2t2Operation is not null || IsR2T2Running;
    public void ReportR2T2Status(string message, bool running) { r2t2StatusRevision++; R2T2Status = message; IsR2T2Running = running; }
    public bool CanCopy => LatestText.Length > 0;
    public string UsageInstructions => "1. 聚焦外部输入框。\n2. 按住 " + settings.Current.Hotkey.DisplayName + " 说话，松开后识别并输入。"
        + (settings.Current.ProviderId == "r2t2-server" && settings.Current.R2T2StreamOutput ? "\nR2T2 实时预览开启：说话时悬浮层会更新临时文字，松开后校正并输入。" : "")
        + "\n3. 录音或识别中按 ESC 取消。";
    public event EventHandler<string>? ThemeChanged;
    public event EventHandler? CopyRequested;

    public MainPageViewModel(SettingsSession settings, Func<AppSettings, Task> save)
    {
        this.settings = settings;
        settings.Changed += (_, _) => OnPropertyChanged(nameof(UsageInstructions));
        this.save = save;
        var current = settings.Current;
        SelectedProvider = Providers.First(p => p.Id == current.ProviderId);
        LocalModelPath = current.LocalModelPath;
        LocalInferenceBackend = current.LocalInferenceBackend;
        R2T2InferenceBackend = current.R2T2InferenceBackend;
        R2T2DataDirectory = current.R2T2DataDirectory;
        R2T2StreamOutput = current.R2T2StreamOutput;
        BaseUrl = current.BaseUrl;
        Model = current.Model;
        ApiKey = current.ApiKey;
        Language = current.Language;
        Theme = current.Theme;
        Key = KeyName(current.Hotkey.Key);
        Alt = current.Hotkey.Modifiers.HasFlag(KeyModifiers.Alt);
        Control = current.Hotkey.Modifiers.HasFlag(KeyModifiers.Control);
        Shift = current.Hotkey.Modifiers.HasFlag(KeyModifiers.Shift);
        Windows = current.Hotkey.Modifiers.HasFlag(KeyModifiers.Windows);
    }

    partial void OnSectionChanged(int value)
    {
        OnPropertyChanged(nameof(ShowGeneral)); OnPropertyChanged(nameof(ShowRecognition)); OnPropertyChanged(nameof(ShowHotkeys));
    }
    partial void OnSelectedProviderChanged(ProviderDefinition value)
    {
        OnPropertyChanged(nameof(HasModelPath)); OnPropertyChanged(nameof(HasBaseUrl));
        OnPropertyChanged(nameof(HasApiKey)); OnPropertyChanged(nameof(HasModel));
        OnPropertyChanged(nameof(HasR2T2));
        OnPropertyChanged(nameof(ProviderDescription));
    }
    partial void OnThemeChanged(string value) => ThemeChanged?.Invoke(this, value);
    partial void OnLatestTextChanged(string value) => OnPropertyChanged(nameof(CanCopy));

    [RelayCommand]
    private async Task SaveAsync() => await SaveCurrentAsync();

    private async Task<bool> SaveCurrentAsync()
    {
        try
        {
            var modifiers = (Alt ? KeyModifiers.Alt : 0) | (Control ? KeyModifiers.Control : 0) | (Shift ? KeyModifiers.Shift : 0) | (Windows ? KeyModifiers.Windows : 0);
            var hotkey = new HotkeyBinding(modifiers, ParseKey(Key));
            hotkey.Validate();
            if (HasBaseUrl) _ = ServerAddressPolicy.Validate(BaseUrl, SelectedProvider.Id);
            if (HasModel && string.IsNullOrWhiteSpace(Model)) throw new ArgumentException("请输入模型名称。");
            var updated = settings.Current with { ProviderId = SelectedProvider.Id, LocalModelPath = LocalModelPath.Trim(), LocalInferenceBackend = LocalInferenceBackend,
                R2T2InferenceBackend = R2T2InferenceBackend, R2T2DataDirectory = R2T2DataDirectory.Trim(), R2T2StreamOutput = R2T2StreamOutput,
                BaseUrl = BaseUrl.Trim(), Model = Model.Trim(), ApiKey = ApiKey.Trim(), Language = Language, Theme = Theme, Hotkey = hotkey };
            SettingsValidator.Validate(updated);
            bool backendChanged = settings.Current.LocalInferenceBackend != updated.LocalInferenceBackend;
            await save(updated);
            Message = !backendChanged
                ? "设置已保存，下一次语音输入使用新配置。"
                : "设置已保存。若本次运行已经使用过 Whisper，请重启 VoxType 后切换推理设备。";
            return true;
        }
        catch (ArgumentException ex) { Message = ex.Message; }
        catch { Message = "设置未能保存，仍使用上一次有效配置。"; }
        return false;
    }

    [RelayCommand(CanExecute = nameof(CanRunR2T2))]
    private async Task PrepareLocalR2T2Async()
    {
        await RunR2T2Async(prepare: true);
    }

    [RelayCommand(CanExecute = nameof(CanRunR2T2))]
    private async Task LaunchLocalR2T2Async()
    {
        if (IsR2T2Busy || IsR2T2Running) return;
        // Guard the save await too, so preparation cannot race with launch.
        IsR2T2Busy = true;
        BaseUrl = "http://127.0.0.1:8272/v1";
        Model = "r2t2-q4";
        ApiKey = "";
        if (!await SaveCurrentAsync()) { IsR2T2Busy = false; return; }
        IsR2T2Busy = false;
        await RunR2T2Async(prepare: false);
    }

    private async Task RunR2T2Async(bool prepare)
    {
        if (IsR2T2Busy || IsR2T2Running) return;
        using var cancellation = new CancellationTokenSource();
        r2t2Operation = cancellation;
        IsR2T2Busy = true;
        int statusRevision = r2t2StatusRevision;
        R2T2Status = prepare ? "正在准备本机资源…" : "正在启动本机服务…";
        var progress = new Progress<string>(text => { if (ReferenceEquals(r2t2Operation, cancellation)) R2T2Status = text; });
        try
        {
            if (RunLocalR2T2Async is null) throw new InvalidOperationException();
            await RunLocalR2T2Async(R2T2InferenceBackend, R2T2DataDirectory.Trim(), prepare, progress, cancellation.Token);
            // A process may fail just after becoming ready, before this continuation runs.
            // Keep the latest service notification instead of overwriting it with success.
            if (statusRevision == r2t2StatusRevision)
            {
                if (!prepare) IsR2T2Running = true;
                R2T2Status = prepare ? "本机资源已准备完成，可以启动服务。" : "本机服务已就绪，可以在外部输入框按住快捷键说话。";
            }
        }
        catch (OperationCanceledException) { R2T2Status = "已取消，已下载并校验完成的资源会保留。"; }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException) { R2T2Status = ex.Message; }
        catch (HttpRequestException) { R2T2Status = "资源下载失败，请检查网络连接后重试。已校验完成的资源会保留。"; }
        catch { R2T2Status = "本机服务操作失败，请检查数据目录和可用空间后重试。"; }
        finally { r2t2Operation = null; IsR2T2Busy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanStopR2T2))]
    private async Task StopLocalR2T2Async()
    {
        if (r2t2Operation is not null) { await r2t2Operation.CancelAsync(); return; }
        try
        {
            if (StopR2T2ServiceAsync is not null) await StopR2T2ServiceAsync();
            ReportR2T2Status("本机服务已停止。", false);
        }
        catch { R2T2Status = "停止服务失败，请退出 VoxType 后重试。"; }
    }

    [RelayCommand] private void Copy() => CopyRequested?.Invoke(this, EventArgs.Empty);
    public async Task UseInstalledModelAsync(string path)
    {
        // Apply only the model choice, preserving other saved settings and unsaved form edits.
        await save(settings.Current with { ProviderId = "whisper-local", LocalModelPath = path });
        LocalModelPath = path;
        SelectedProvider = Providers.First(p => p.Id == "whisper-local");
        Message = "已保存本地模型选择，下一次语音输入生效。";
    }
    private static int ParseKey(string value)
    {
        var key = value.Trim().ToUpperInvariant();
        if (key == "SPACE") return 0x20;
        if (key.Length == 1) return key[0];
        if (key.StartsWith('F') && int.TryParse(key.AsSpan(1), out int function) && function is >= 1 and <= 11) return 0x6f + function;
        throw new ArgumentException("主键支持 Space、A–Z、0–9 或 F1–F11。");
    }
    private static string KeyName(int key) => key == 0x20 ? "Space" : key is >= 0x70 and <= 0x7a ? "F" + (key - 0x6f) : ((char)key).ToString();
}
