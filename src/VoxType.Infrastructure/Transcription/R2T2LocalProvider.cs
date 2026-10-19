using VoxType.Core.Audio;
using VoxType.Core.Settings;
using VoxType.Core.Transcription;
using VoxType.R2T2;

namespace VoxType.Infrastructure.Transcription;

public interface IR2T2LocalRuntime
{
    void ValidateConfiguration(string backend, string directory);
    Task<string> TranscribeAsync(string backend, string directory, byte[] wav, string language, Action<string>? partialText, CancellationToken token);
}

public sealed class R2T2LocalProvider : ITranscriptionProvider
{
    private readonly IR2T2LocalRuntime runtime;
    private readonly AppSettings settings;
    public string Id => "r2t2-local";
    public string DisplayName => "R2T2 本机";

    public R2T2LocalProvider(IR2T2LocalRuntime runtime, AppSettings settings)
    {
        this.runtime = runtime;
        this.settings = settings;
        // Validate readiness before the dictation coordinator opens the microphone.
        try { runtime.ValidateConfiguration(settings.R2T2InferenceBackend, settings.R2T2DataDirectory); }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
        { throw new TranscriptionException(ex.Message, "configuration"); }
    }

    public async Task<TranscriptionResult> TranscribeAsync(AudioData audio, TranscriptionOptions options, CancellationToken cancellationToken)
    {
        try
        {
            string text = await runtime.TranscribeAsync(settings.R2T2InferenceBackend, settings.R2T2DataDirectory,
                audio.WavBytes, options.Language, settings.R2T2StreamOutput ? options.PartialText : null, cancellationToken).ConfigureAwait(false);
            return new(text, Id, options.Language == "auto" ? null : options.Language);
        }
        catch (R2T2Exception ex) { throw new TranscriptionException(ex.Message, ex.Code); }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
        { throw new TranscriptionException(ex.Message, "unavailable"); }
    }
}
