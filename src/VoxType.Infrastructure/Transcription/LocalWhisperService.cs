using VoxType.Core.Audio;
using VoxType.Core.Settings;
using VoxType.Core.Transcription;

namespace VoxType.Infrastructure.Transcription;

public sealed class LocalWhisperService : IAsyncDisposable
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(5);
    private readonly object sync = new();
    private readonly SemaphoreSlim nativeGate = new(1, 1);
    private readonly IWhisperBackend backend;
    private readonly TimeProvider clock;
    private readonly ITimer idleTimer;
    private IWhisperModel? model;
    private string? loadedPath;
    private string? desiredPath;
    private bool hasSettings;
    private long lastUse;
    private int leases;
    private bool retire, disposed;
    public LocalWhisperService(IWhisperBackend backend, TimeProvider? clock = null)
    {
        this.backend = backend; this.clock = clock ?? TimeProvider.System;
        idleTimer = this.clock.CreateTimer(state => { _ = UnloadIfUnusedAsync(idleOnly: true); }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }
    public IProviderLease Acquire(AppSettings snapshot)
    {
        string path = WhisperModelPath.Resolve(snapshot.LocalModelPath);
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); if (!hasSettings) desiredPath = path; leases++; }
        return new ProviderLease(new Provider(this, path), ReleaseAsync);
    }
    public void SettingsChanged(AppSettings settings)
    {
        string? selectedPath = settings.ProviderId == "whisper-local" ? WhisperModelPath.Resolve(settings.LocalModelPath) : null;
        lock (sync) { if (disposed) return; hasSettings = true; desiredPath = selectedPath; if (!SamePath(selectedPath, loadedPath)) retire = true; }
        _ = UnloadIfUnusedAsync(idleOnly: false);
    }
    private async ValueTask ReleaseAsync()
    {
        lock (sync) { leases--; if (leases < 0) throw new InvalidOperationException("Provider lease imbalance."); }
        await UnloadIfUnusedAsync(idleOnly: true).ConfigureAwait(false);
    }
    private async Task<string> TranscribeAsync(string path, AudioData audio, TranscriptionOptions options, CancellationToken token)
    {
        await nativeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
            if (!SamePath(loadedPath, path) || model is null)
            {
                ClearModel();
                model = await Task.Run(() => backend.Load(path), CancellationToken.None).ConfigureAwait(false);
                lock (sync) { loadedPath = path; retire = !SamePath(desiredPath, path); }
            }
            token.ThrowIfCancellationRequested();
            var result = await model.TranscribeAsync(audio.WavBytes, options.Language, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(result)) throw new TranscriptionException("未识别到语音，请重新录音。", "empty");
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (TranscriptionException) { throw; }
        catch { throw new TranscriptionException("本地识别失败，请检查模型和原生 runtime 后重试。", "local-inference"); }
        finally
        {
            lock (sync)
            {
                lastUse = clock.GetTimestamp();
                if (!disposed) idleTimer.Change(IdleDelay, Timeout.InfiniteTimeSpan);
            }
            nativeGate.Release();
        }
    }
    private async Task UnloadIfUnusedAsync(bool idleOnly)
    {
        if (!await nativeGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            bool unload;
            lock (sync)
            {
                if (leases != 0 || disposed) return;
                var elapsed = clock.GetElapsedTime(lastUse);
                unload = retire || idleOnly && model is not null && elapsed >= IdleDelay;
                if (unload) retire = false;
                else if (idleOnly && model is not null)
                {
                    // Real timers may fire just before the monotonic deadline. Do not
                    // lose the only idle callback: rearm for the remaining interval.
                    var remaining = IdleDelay - elapsed;
                    idleTimer.Change(remaining < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : remaining, Timeout.InfiniteTimeSpan);
                }
            }
            if (unload) ClearModel();
        }
        finally { nativeGate.Release(); }
    }
    private static bool SamePath(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private void ClearModel()
    {
        IWhisperModel? old;
        lock (sync) { old = model; model = null; loadedPath = null; }
        old?.Dispose();
    }
    public async Task DeleteModelAsync(string path)
    {
        if (!await nativeGate.WaitAsync(0).ConfigureAwait(false))
            throw new TranscriptionException("模型正在识别，请结束当前语音输入后再删除。", "model-busy");
        try
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (leases != 0) throw new TranscriptionException("当前语音输入正在使用模型，请结束后再删除。", "model-busy");
                if (SamePath(loadedPath, path)) ClearModel();
                File.Delete(path);
            }
        }
        finally { nativeGate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        lock (sync) { if (disposed) return; disposed = true; idleTimer.Dispose(); }
        await nativeGate.WaitAsync().ConfigureAwait(false);
        try { ClearModel(); }
        finally { nativeGate.Release(); }
    }
    private sealed class Provider(LocalWhisperService service, string path) : ITranscriptionProvider
    {
        public string Id => "whisper-local"; public string DisplayName => "Whisper 本地";
        public async Task<TranscriptionResult> TranscribeAsync(AudioData audio, TranscriptionOptions options, CancellationToken cancellationToken) =>
            new(await service.TranscribeAsync(path, audio, options, cancellationToken).ConfigureAwait(false), Id, options.Language == "auto" ? null : options.Language);
    }
}
