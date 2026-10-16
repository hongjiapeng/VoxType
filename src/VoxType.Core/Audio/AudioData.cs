namespace VoxType.Core.Audio;

public sealed record AudioData(byte[] WavBytes, TimeSpan Duration, float Rms)
{
    public const int SampleRate = 16000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;
    // Capture preprocessing can assess sustained sound independently of pauses.
    // Other callers retain the original whole-recording RMS check.
    public bool? SustainedSignal { get; init; }
    public bool HasSignal => Duration >= TimeSpan.FromMilliseconds(250) && (SustainedSignal ?? Rms >= 0.0031623f);
}

public interface IAudioCaptureService : IAsyncDisposable
{
    event EventHandler<float>? AudioLevelChanged;
    event EventHandler<Exception>? Faulted;
    Task StartAsync(CancellationToken cancellationToken);
    Task<AudioData> StopAsync(CancellationToken cancellationToken);
    Task CancelAsync();
}
