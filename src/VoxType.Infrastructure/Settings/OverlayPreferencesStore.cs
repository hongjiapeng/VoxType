using System.Text.Json;
using VoxType.Core.Dictation;

namespace VoxType.Infrastructure.Settings;

// Independent of transcription settings: dragging must never overwrite an unsaved settings form.
public sealed class OverlayPreferencesStore(ISettingsFile file)
{
    public async Task<OverlayPreferences> LoadAsync()
    {
        try
        {
            var bytes = await file.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            var preferences = bytes is null ? null : JsonSerializer.Deserialize<OverlayPreferences>(bytes);
            return preferences is null ? new() : preferences with { Anchor = preferences.Anchor?.IsValid == true ? preferences.Anchor : null };
        }
        catch { return new(); }
    }

    public Task SaveAsync(OverlayPreferences preferences) => file.WriteAtomicAsync(
        JsonSerializer.SerializeToUtf8Bytes(preferences), CancellationToken.None);
}
