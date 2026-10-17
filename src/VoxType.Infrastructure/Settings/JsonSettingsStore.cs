using System.Text.Json;
using VoxType.Core.Hotkeys;
using VoxType.Core.Settings;

namespace VoxType.Infrastructure.Settings;

public interface ISettingsFile
{
    Task<byte[]?> ReadAsync(CancellationToken cancellationToken);
    Task WriteAtomicAsync(byte[] bytes, CancellationToken cancellationToken);
}
public sealed class AtomicSettingsFile(string path) : ISettingsFile
{
    public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (file.Length > 65536) throw new IOException("设置文件过大。");
        byte[] bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }
    public async Task WriteAtomicAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        string absolute = Path.GetFullPath(path), directory = Path.GetDirectoryName(absolute)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(absolute)) File.Replace(temporary, absolute, null);
            else File.Move(temporary, absolute);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class JsonSettingsStore(ISettingsFile file, ISecretProtector protector) : ISettingsStore
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoxType", "settings.json");
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = await file.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (bytes is null) return new(new());
            var dto = JsonSerializer.Deserialize<DiskSettings>(bytes, Json) ?? throw new JsonException();
            string secret = ""; string? warning = null;
            try { secret = protector.Unprotect(dto.EncryptedApiKey); }
            catch { warning = "API Key 解密失败，已清空。请在语音识别设置中重新输入并保存。"; }
            var settings = new AppSettings { Version = dto.Version, ProviderId = dto.ProviderId, Language = dto.Language, Theme = dto.Theme,
                Hotkey = dto.Hotkey, LocalModelPath = dto.LocalModelPath, LocalInferenceBackend = dto.LocalInferenceBackend,
                R2T2InferenceBackend = dto.R2T2InferenceBackend, R2T2DataDirectory = dto.R2T2DataDirectory, R2T2StreamOutput = dto.R2T2StreamOutput,
                BaseUrl = dto.BaseUrl, Model = dto.Model, ApiKey = secret };
            SettingsValidator.Validate(settings);
            if (dto.Version == 1)
            {
                // Migrate the old default once. Version 2 preserves explicitly selected Alt+Space.
                var hotkey = dto.Hotkey == new HotkeyBinding(KeyModifiers.Alt, 0x20) ? HotkeyBinding.Default : dto.Hotkey;
                settings = settings with { Version = 2, Hotkey = hotkey };
                await saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    // Keep the existing DPAPI ciphertext, provider and model paths unchanged.
                    await file.WriteAtomicAsync(JsonSerializer.SerializeToUtf8Bytes(dto with { Version = 2, Hotkey = hotkey }, Json), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch { warning = (warning is null ? "" : warning + "\n") + "升级配置已生效，但未能写入。请重新保存设置。"; }
                finally { saveGate.Release(); }
            }
            return new(settings, warning);
        }
        catch (OperationCanceledException) { throw; }
        catch { return new(new(), "设置文件无法读取或不兼容，已恢复安全的本地默认配置。原文件保留，重新保存后替换。"); }
    }
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        SettingsValidator.Validate(settings);
        await saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dto = new DiskSettings { Version = 2, ProviderId = settings.ProviderId, Language = settings.Language, Theme = settings.Theme,
                Hotkey = settings.Hotkey, LocalModelPath = settings.LocalModelPath, LocalInferenceBackend = settings.LocalInferenceBackend,
                R2T2InferenceBackend = settings.R2T2InferenceBackend, R2T2DataDirectory = settings.R2T2DataDirectory, R2T2StreamOutput = settings.R2T2StreamOutput,
                BaseUrl = settings.BaseUrl, Model = settings.Model,
                EncryptedApiKey = protector.Protect(settings.ApiKey) };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(dto, Json);
            if (bytes.Length > 65536) throw new ArgumentException("配置内容过大，请缩短路径或密钥。");
            await file.WriteAtomicAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        finally { saveGate.Release(); }
    }
    private sealed record DiskSettings
    {
        public required int Version { get; init; }
        public required string ProviderId { get; init; }
        public required string Language { get; init; }
        public required string Theme { get; init; }
        public required HotkeyBinding Hotkey { get; init; }
        public required string LocalModelPath { get; init; }
        public string LocalInferenceBackend { get; init; } = "cpu";
        public string R2T2InferenceBackend { get; init; } = "cpu";
        public string R2T2DataDirectory { get; init; } = "";
        public bool R2T2StreamOutput { get; init; } = true;
        public required string BaseUrl { get; init; }
        public required string Model { get; init; }
        public required string EncryptedApiKey { get; init; }
    }
}
