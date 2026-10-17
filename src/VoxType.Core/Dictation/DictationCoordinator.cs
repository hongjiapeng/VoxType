using VoxType.Core.Audio;
using VoxType.Core.Settings;
using VoxType.Core.TextInjection;
using VoxType.Core.Transcription;

namespace VoxType.Core.Dictation;

public sealed class DictationCoordinator : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly SettingsSession settings;
    private readonly IAudioCaptureService audio;
    private readonly IFocusContextService focus;
    private readonly ITranscriptionProviderRegistry? providers;
    private readonly ITextInjectionService? injection;
    private readonly TimeProvider clock;
    private Session? active;
    private long generation;
    private bool disposed;
    public event EventHandler<SessionUpdate>? Updated;
    public event EventHandler<AudioLevelUpdate>? AudioLevelChanged;
    public event EventHandler<string>? Transcribed;
    public event EventHandler? SessionFinished;
    public bool IsBusy { get { lock (sync) return active is not null; } }
    public SessionState State { get { lock (sync) return active?.State ?? SessionState.Idle; } }

    public DictationCoordinator(SettingsSession settings, IAudioCaptureService audio, IFocusContextService focus,
        ITranscriptionProviderRegistry? providers = null, ITextInjectionService? injection = null, TimeProvider? clock = null)
    {
        this.settings = settings; this.audio = audio; this.focus = focus;
        this.providers = providers; this.injection = injection; this.clock = clock ?? TimeProvider.System;
        audio.AudioLevelChanged += OnLevel;
        audio.Faulted += OnFault;
    }

    public bool Start(bool fromTray = false)
    {
        Session session;
        lock (sync)
        {
            if (disposed || active is not null) return false;
            var target = fromTray ? focus.CaptureBeforeTray() : focus.Capture();
            var snapshot = settings.Current;
            var definition = ProviderDefinition.All.First(p => p.Id == snapshot.ProviderId);
            session = new(++generation, target, snapshot, definition, fromTray);
            active = session;
            // Queue the pipeline first: consumers can safely request Stop/Cancel immediately.
            session.Completion = Task.Run(() => RunAsync(session));
        }
        return true;
    }
    public void Stop()
    {
        lock (sync)
            if (active is { State: SessionState.Starting or SessionState.Listening } session)
                session.End.TrySetResult(EndReason.Stop);
    }
    public void Cancel()
    {
        Session? session;
        lock (sync)
        {
            session = active;
            if (session is null) return;
            session.End.TrySetResult(EndReason.Cancel);
            session.State = SessionState.Cancelling;
        }
        try { session.Cancellation.Cancel(); } catch (ObjectDisposedException) { return; }
        Update(session, SessionState.Cancelling, "已取消");
    }
    private void OnFault(object? sender, Exception error)
    {
        lock (sync) { if (active is not null) active.Fault = error; }
        Cancel();
    }
    private void OnLevel(object? sender, float level)
    {
        long id;
        lock (sync) { if (active?.State != SessionState.Listening || active.Cancellation.IsCancellationRequested) return; id = active.Id; }
        AudioLevelChanged?.Invoke(this, new(id, level));
    }
    private void Update(Session session, SessionState state, string message, string? partialText = null)
    {
        long revision;
        lock (sync)
        {
            if (!ReferenceEquals(active, session)) return;
            if (partialText is not null && session.State != SessionState.Processing) return;
            if (session.Cancellation.IsCancellationRequested && state is not (SessionState.Cancelling or SessionState.Failed or SessionState.Completed)) return;
            session.State = state;
            revision = ++session.Revision;
        }
        Updated?.Invoke(this, new(session.Id, state, message, session.Definition.DisplayName, session.Definition.IsLocal, session.Focus, session.FromTray, revision, partialText));
    }
    private async Task RunAsync(Session session)
    {
        var token = session.Cancellation.Token;
        IProviderLease? lease = null;
        ITimer? limit = null;
        try
        {
            if (!session.Focus.IsValid) throw new AudioCaptureException("请先聚焦外部输入框，再使用快捷键开始。");
            // Acquire validates the selected service before opening the microphone.
            if (providers is not null) lease = providers.Acquire(session.Settings);
            Update(session, SessionState.Starting, "正在打开默认麦克风");
            await audio.StartAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            limit = clock.CreateTimer(_ => session.End.TrySetResult(EndReason.Limit), null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
            if (!session.End.Task.IsCompleted) Update(session, SessionState.Listening, "");
            var reason = await session.End.Task.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (reason == EndReason.Cancel) throw new OperationCanceledException(token);
            Update(session, SessionState.Stopping, reason == EndReason.Limit ? "已达到 60 秒上限" : "正在结束录音");
            var recording = await audio.StopAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (recording.Duration < TimeSpan.FromMilliseconds(250)) throw new AudioCaptureException("录音太短，请按住快捷键多说一会儿。");
            if (!recording.HasSignal) throw new AudioCaptureException("没有检测到足够音量，请检查麦克风并重试。");
            Update(session, SessionState.Processing, session.Definition.DisplayName);
            if (providers is null) throw new TranscriptionException("录音已完成，识别服务尚未接入。", "unavailable");
            var result = await lease!.Provider.TranscribeAsync(recording,
                new(session.Settings.Language, text => Update(session, SessionState.Processing, session.Definition.DisplayName, text)), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(result.Text)) throw new TranscriptionException("识别结果为空，请重试。", "empty");
            Transcribed?.Invoke(this, result.Text);
            if (injection is null) throw new TranscriptionException("识别已完成，自动输入尚未接入。可在设置中查看最近文本。", "injection-unavailable");
            Update(session, SessionState.Injecting, "正在发送文本");
            token.ThrowIfCancellationRequested();
            var delivered = await injection.InjectAsync(result.Text, session.Focus, session.Id, token).ConfigureAwait(false);
            if (delivered.Status != InjectionStatus.Dispatched) token.ThrowIfCancellationRequested();
            Update(session, delivered.Status == InjectionStatus.Dispatched ? SessionState.Completed : SessionState.Failed, delivered.Message);
        }
        catch (OperationCanceledException)
        {
            Update(session, session.Fault is null ? SessionState.Cancelling : SessionState.Failed,
                session.Fault is AudioCaptureException fault ? fault.Message : session.Fault is null ? "已取消" : "麦克风已中断，请重新录音。");
        }
        catch (AudioCaptureException ex) { Update(session, SessionState.Failed, ex.Message); }
        catch (TranscriptionException ex) { Update(session, SessionState.Failed, ex.Message); }
        catch { Update(session, SessionState.Failed, "语音输入未能完成，请检查设置后重试。"); }
        finally
        {
            limit?.Dispose();
            try { await audio.CancelAsync().ConfigureAwait(false); }
            catch { /* release failure must not prevent remaining ownership cleanup */ }
            try { if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                lock (sync) { if (ReferenceEquals(active, session)) active = null; }
                session.Cancellation.Dispose();
                SessionFinished?.Invoke(this, EventArgs.Empty);
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        Task? completion;
        lock (sync) { if (disposed) return; disposed = true; completion = active?.Completion; }
        Cancel();
        try { if (completion is not null) await completion.ConfigureAwait(false); }
        finally
        {
            audio.AudioLevelChanged -= OnLevel;
            audio.Faulted -= OnFault;
            await audio.DisposeAsync().ConfigureAwait(false);
        }
    }
    private enum EndReason { Stop, Cancel, Limit }
    private sealed class Session(long id, FocusContext focus, AppSettings settings, ProviderDefinition definition, bool fromTray)
    {
        public long Id { get; } = id;
        public FocusContext Focus { get; } = focus;
        public AppSettings Settings { get; } = settings;
        public ProviderDefinition Definition { get; } = definition;
        public bool FromTray { get; } = fromTray;
        public SessionState State { get; set; } = SessionState.Starting;
        public long Revision { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<EndReason> End { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion { get; set; } = Task.CompletedTask;
        public Exception? Fault { get; set; }
    }
}

public sealed record AudioLevelUpdate(long SessionId, float Level);
