using VoxType.Core.Transcription;

namespace VoxType.Core.Settings;

public static class SettingsValidator
{
    public static void Validate(AppSettings settings)
    {
        if (settings.Version is not (1 or 2)) throw new ArgumentException("设置版本不受支持。");
        if (!ProviderDefinition.All.Any(p => p.Id == settings.ProviderId)) throw new ArgumentException("识别服务无效。");
        if (settings.Theme is not ("system" or "light" or "dark")) throw new ArgumentException("主题无效。");
        if (settings.Language is not ("auto" or "zh" or "en" or "ja" or "ko" or "de" or "fr" or "es")) throw new ArgumentException("识别语言无效。");
        if (settings.Hotkey is null) throw new ArgumentException("快捷键无效。");
        settings.Hotkey.Validate();
        if (settings.LocalModelPath is null || settings.BaseUrl is null || settings.Model is null || settings.ApiKey is null ||
            settings.LocalModelPath.Length > 4096 || settings.BaseUrl.Length > 4096 || settings.Model.Length > 512 || settings.ApiKey.Length > 16384)
            throw new ArgumentException("配置字段无效或过长。");
        if (settings.ApiKey.Any(char.IsControl)) throw new ArgumentException("API Key 含无效字符。");
        if (settings.ProviderId == "openai-compatible")
        {
            if (!Uri.TryCreate(settings.BaseUrl.Trim(), UriKind.Absolute, out var uri) ||
                !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new ArgumentException("Base URL 需要 HTTPS（本机回环可使用 HTTP），且不包含账号、查询或片段。");
            if (string.IsNullOrWhiteSpace(settings.Model)) throw new ArgumentException("请输入在线模型名称。");
        }
    }
}
