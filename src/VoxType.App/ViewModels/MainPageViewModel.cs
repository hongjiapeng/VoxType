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
    [ObservableProperty] public partial ProviderDefinition SelectedProvider { get; set; }
    [ObservableProperty] public partial string LocalModelPath { get; set; }
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
    [ObservableProperty] public partial int Section { get; set; }
    public bool ShowGeneral => Section == 0;
    public bool ShowRecognition => Section == 1;
    public bool ShowHotkeys => Section == 2;
    public bool HasModelPath => SelectedProvider.Fields.Contains(ProviderField.ModelPath);
    public bool HasBaseUrl => SelectedProvider.Fields.Contains(ProviderField.BaseUrl);
    public bool HasApiKey => SelectedProvider.Fields.Contains(ProviderField.ApiKey);
    public bool HasModel => SelectedProvider.Fields.Contains(ProviderField.Model);
    public string ProviderDescription => SelectedProvider.Description;
    public string DefaultModelPath { get; init; } = "";
    public ModelManagerViewModel Models { get; init; } = null!;
    public bool CanCopy => LatestText.Length > 0;
    public string UsageInstructions => "1. 聚焦外部输入框。\n2. 按住 " + settings.Current.Hotkey.DisplayName + " 说话，松开后识别并输入。\n3. 录音或识别中按 ESC 取消。";
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
        OnPropertyChanged(nameof(ProviderDescription));
    }
    partial void OnThemeChanged(string value) => ThemeChanged?.Invoke(this, value);
    partial void OnLatestTextChanged(string value) => OnPropertyChanged(nameof(CanCopy));

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var modifiers = (Alt ? KeyModifiers.Alt : 0) | (Control ? KeyModifiers.Control : 0) | (Shift ? KeyModifiers.Shift : 0) | (Windows ? KeyModifiers.Windows : 0);
            var hotkey = new HotkeyBinding(modifiers, ParseKey(Key));
            hotkey.Validate();
            if (HasBaseUrl) _ = ServerAddressPolicy.Validate(BaseUrl, SelectedProvider.Id);
            if (HasModel && string.IsNullOrWhiteSpace(Model)) throw new ArgumentException("请输入模型名称。");
            var updated = settings.Current with { ProviderId = SelectedProvider.Id, LocalModelPath = LocalModelPath.Trim(), BaseUrl = BaseUrl.Trim(), Model = Model.Trim(), ApiKey = ApiKey.Trim(), Language = Language, Theme = Theme, Hotkey = hotkey };
            SettingsValidator.Validate(updated);
            await save(updated);
            Message = "设置已保存，下一次语音输入使用新配置。";
        }
        catch (ArgumentException ex) { Message = ex.Message; }
        catch { Message = "设置未能保存，仍使用上一次有效配置。"; }
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
