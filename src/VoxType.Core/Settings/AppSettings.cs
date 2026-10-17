using VoxType.Core.Hotkeys;

namespace VoxType.Core.Settings;

public sealed record AppSettings
{
    public int Version { get; init; } = 2;
    public string ProviderId { get; init; } = "whisper-local";
    public string Language { get; init; } = "auto";
    public string Theme { get; init; } = "system";
    public HotkeyBinding Hotkey { get; init; } = HotkeyBinding.Default;
    public string LocalModelPath { get; init; } = "";
    public string LocalInferenceBackend { get; init; } = "cpu";
    public string R2T2InferenceBackend { get; init; } = "cpu";
    public string R2T2DataDirectory { get; init; } = "";
    public string BaseUrl { get; init; } = "https://api.openai.com/v1";
    public string Model { get; init; } = "whisper-1";
    // This runtime model is not serialized to disk. The store uses an encrypted DTO.
    public string ApiKey { get; init; } = "";
}

public interface ISettingsStore
{
    Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public sealed record SettingsLoadResult(AppSettings Settings, string? Warning = null);

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}

public sealed class SettingsSession(AppSettings initial)
{
    private AppSettings current = initial;
    public AppSettings Current => Volatile.Read(ref current);
    public event EventHandler<AppSettings>? Changed;
    public void Publish(AppSettings settings)
    {
        Interlocked.Exchange(ref current, settings);
        Changed?.Invoke(this, settings);
    }
}
