using VoxType.Core.Audio;
using VoxType.Core.Settings;

namespace VoxType.Core.Transcription;

public sealed record TranscriptionOptions(string Language);
public sealed record TranscriptionResult(string Text, string ProviderId, string? Language = null);

public interface ITranscriptionProvider
{
    string Id { get; }
    string DisplayName { get; }
    Task<TranscriptionResult> TranscribeAsync(AudioData audio, TranscriptionOptions options, CancellationToken cancellationToken);
}

public enum ProviderField { ModelPath, BaseUrl, ApiKey, Model, Language }
public sealed record ProviderDefinition(string Id, string DisplayName, bool IsLocal, string Description, IReadOnlyList<ProviderField> Fields)
{
    public static IReadOnlyList<ProviderDefinition> All { get; } =
    [
        new("whisper-local", "Whisper 本地", true, "本地运行 · 音频不会上传到服务器", [ProviderField.ModelPath, ProviderField.Language]),
        new("r2t2-server", "R2T2 本机／局域网", false, "音频发送到你配置的 R2T2 设备 · CPU/Vulkan 由服务端启动脚本选择 · 本机或私有 IP 可使用 HTTP，局域网 HTTP 为明文传输", [ProviderField.BaseUrl, ProviderField.ApiKey, ProviderField.Model, ProviderField.Language]),
        new("openai-compatible", "OpenAI Compatible", false, "在线识别 · 音频上传至你配置的服务器", [ProviderField.BaseUrl, ProviderField.ApiKey, ProviderField.Model, ProviderField.Language])
    ];
}

public sealed class TranscriptionException(string message, string code, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public interface IProviderLease : IAsyncDisposable { ITranscriptionProvider Provider { get; } }
public interface ITranscriptionProviderRegistry { IProviderLease Acquire(AppSettings settings); }
