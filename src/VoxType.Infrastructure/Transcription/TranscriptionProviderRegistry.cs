using VoxType.Core.Settings;
using VoxType.Core.Transcription;

namespace VoxType.Infrastructure.Transcription;

public sealed class TranscriptionProviderRegistry(IReadOnlyDictionary<string, Func<AppSettings, IProviderLease>> factories) : ITranscriptionProviderRegistry
{
    public IProviderLease Acquire(AppSettings settings) => factories.TryGetValue(settings.ProviderId, out var create)
        ? create(settings) : throw new TranscriptionException("所选识别服务尚未就绪，请打开设置检查。", "unavailable");
}

public sealed class ProviderLease(ITranscriptionProvider provider, Func<ValueTask>? release = null) : IProviderLease
{
    private int disposed;
    public ITranscriptionProvider Provider { get; } = provider;
    public ValueTask DisposeAsync() => Interlocked.Exchange(ref disposed, 1) == 0 && release is not null ? release() : ValueTask.CompletedTask;
}
