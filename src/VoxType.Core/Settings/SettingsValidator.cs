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
        if (settings.LocalInferenceBackend is not ("cpu" or "vulkan")) throw new ArgumentException("本地推理设备无效。");
        if (settings.R2T2InferenceBackend is not ("cpu" or "vulkan")) throw new ArgumentException("R2T2 推理设备无效。");
        if (settings.R2T2DataDirectory is null || settings.R2T2DataDirectory.Length > 4096) throw new ArgumentException("R2T2 数据目录无效。");
        if (settings.Hotkey is null) throw new ArgumentException("快捷键无效。");
        settings.Hotkey.Validate();
        if (settings.LocalModelPath is null || settings.BaseUrl is null || settings.Model is null || settings.ApiKey is null ||
            settings.LocalModelPath.Length > 4096 || settings.BaseUrl.Length > 4096 || settings.Model.Length > 512 || settings.ApiKey.Length > 16384)
            throw new ArgumentException("配置字段无效或过长。");
        if (settings.ApiKey.Any(char.IsControl)) throw new ArgumentException("API Key 含无效字符。");
        if (settings.ProviderId is "openai-compatible" or "r2t2-server")
        {
            _ = ServerAddressPolicy.Validate(settings.BaseUrl, settings.ProviderId);
            if (string.IsNullOrWhiteSpace(settings.Model)) throw new ArgumentException("请输入模型名称。");
        }
    }
}
