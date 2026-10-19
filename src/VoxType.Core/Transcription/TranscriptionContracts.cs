using VoxType.Core.Audio;
using VoxType.Core.Settings;

namespace VoxType.Core.Transcription;

// PartialText receives the accumulated preview; only the returned result is final.
public sealed record TranscriptionOptions(string Language, Action<string>? PartialText = null);
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
        new("r2t2-local", "R2T2 本机", true, "在本机识别 · 音频不会发送到其他设备 · 准备资源后启用本地引擎，可选择 CPU/Vulkan", [ProviderField.Language]),
        new("r2t2-server", "R2T2 服务器", false, "连接已有的 R2T2 服务器 · 音频发送到所填地址 · 局域网 HTTP 为明文传输", [ProviderField.BaseUrl, ProviderField.ApiKey, ProviderField.Model, ProviderField.Language]),
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
