using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using VoxType.Core.Audio;
using VoxType.Core.Dictation;
using VoxType.Core.Settings;
using VoxType.Core.TextInjection;
using VoxType.Core.Transcription;

namespace VoxType.Tests.Dictation;

[TestClass]
public sealed class DictationCoordinatorTests
{
    [TestMethod]
    public async Task FastRelease_DuringDelayedStart_StopsOnceAndLeavesNoCapture()
    {
        await using var h = new Harness(delayStart: true);
        Assert.IsTrue(h.Coordinator.Start());
        await Wait(h.Audio.Entered.Task);
        h.Coordinator.Stop(); h.Coordinator.Stop();
        h.Audio.StartGate.TrySetResult(true);
        await Wait(h.Finished.Task);
        Assert.AreEqual(1, h.Audio.StartCount);
        Assert.AreEqual(1, h.Audio.StopCount);
        Assert.IsFalse(h.Audio.Active);
        Assert.IsFalse(h.Coordinator.IsBusy);
        Assert.IsFalse(h.Updates.Any(x => x.State == SessionState.Listening));
        Assert.AreEqual(1, h.Injection.Calls);
    }

    [TestMethod]
    public async Task Cancel_DuringStartThatIgnoresToken_CleansUpWithoutTranscribing()
    {
        await using var h = new Harness(delayStart: true);
        h.Coordinator.Start(); await Wait(h.Audio.Entered.Task);
        h.Coordinator.Cancel();
        Assert.IsTrue(h.Coordinator.IsBusy, "Ownership remains until delayed initialization returns.");
        Assert.IsFalse(h.Coordinator.Start());
        h.Audio.StartGate.TrySetResult(true);
        await Wait(h.Finished.Task);
        Assert.IsFalse(h.Audio.Active);
        Assert.AreEqual(0, h.Audio.StopCount);
        Assert.AreEqual(0, h.Provider.Calls);
        Assert.AreEqual(0, h.Injection.Calls);
    }

    [TestMethod]
    public async Task Cancel_ProviderIgnoresToken_DiscardsLateResultAndKeepsLeaseUntilReturn()
    {
        await using var h = new Harness(blockProvider: true);
        h.Coordinator.Start(); await Wait(h.Listening.Task); h.Coordinator.Stop();
        await Wait(h.Provider.Entered.Task);
        h.Coordinator.Cancel();
        Assert.AreEqual(SessionState.Cancelling, h.Coordinator.State);
        Assert.IsFalse(h.Coordinator.Start());
        Assert.AreEqual(0, h.Registry.LeasesDisposed);
        h.Provider.Result.TrySetResult("迟到的文本");
        await Wait(h.Finished.Task);
        Assert.AreEqual(0, h.Injection.Calls);
        Assert.IsEmpty(h.Texts);
        Assert.AreEqual(1, h.Registry.LeasesDisposed);
        Assert.AreEqual(SessionState.Idle, h.Coordinator.State);
    }

    [TestMethod]
    public async Task Busy_RequestDoesNotStartSecondRecording()
    {
        await using var h = new Harness(blockProvider: true);
        h.Coordinator.Start(); await Wait(h.Listening.Task); h.Coordinator.Stop();
        await Wait(h.Provider.Entered.Task);
        Assert.IsFalse(h.Coordinator.Start(true));
        h.Coordinator.Stop();
        Assert.AreEqual(1, h.Audio.StartCount);
        Assert.AreEqual(1, h.Audio.StopCount);
        h.Provider.Result.TrySetResult("有效文本");
        await Wait(h.Finished.Task);
        Assert.AreEqual(1, h.Injection.Calls);
    }

    [TestMethod]
    [DataRow(249, 0.5f, "录音太短")]
    [DataRow(1000, 0.0001f, "没有检测到足够音量")]
    public async Task RejectedAudio_DoesNotCallProvider(int milliseconds, float rms, string message)
    {
        await using var h = new Harness();
        h.Audio.Data = new([], TimeSpan.FromMilliseconds(milliseconds), rms);
        h.Coordinator.Start(); await Wait(h.Listening.Task); h.Coordinator.Stop();
        await Wait(h.Finished.Task);
        Assert.AreEqual(0, h.Provider.Calls);
        Assert.AreEqual(0, h.Injection.Calls);
        Assert.IsTrue(h.Updates.Any(x => x.State == SessionState.Failed && x.Message.Contains(message, StringComparison.Ordinal)));
        Assert.IsFalse(h.Audio.Active);
    }

    [TestMethod]
    public async Task SettingsSavedDuringRecording_OnlyNextSessionUsesNewProviderAndLanguage()
    {
        await using var h = new Harness();
        h.Settings.Publish(h.Settings.Current with { ProviderId = "openai-compatible", Language = "zh" });
        h.Coordinator.Start(); await Wait(h.Listening.Task);
        h.Settings.Publish(h.Settings.Current with { ProviderId = "whisper-local", Language = "en" });
        h.Coordinator.Stop(); await Wait(h.Finished.Task);
        Assert.AreEqual("openai-compatible", h.Registry.Acquired[0].ProviderId);
        Assert.AreEqual("zh", h.Provider.Options[0].Language);
        h.ResetSignals();
        h.Coordinator.Start(); await Wait(h.Listening.Task); h.Coordinator.Stop(); await Wait(h.Finished.Task);
        Assert.AreEqual("whisper-local", h.Registry.Acquired[1].ProviderId);
        Assert.AreEqual("en", h.Provider.Options[1].Language);
        Assert.AreEqual(2, h.Injection.Calls);
        Assert.AreEqual(2, h.Updates.Select(x => x.SessionId).Distinct().Count());
    }

    [TestMethod]
    public async Task RecordingLimit_StopsAt60SecondsOnce()
    {
        var clock = new FakeTimeProvider();
        await using var h = new Harness(clock: clock);
        h.Coordinator.Start(); await Wait(h.Listening.Task);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.AreEqual(0, h.Audio.StopCount);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Wait(h.Finished.Task);
        Assert.AreEqual(1, h.Audio.StopCount);
        Assert.IsTrue(h.Updates.Any(x => x.State == SessionState.Stopping && x.Message.Contains("60 秒", StringComparison.Ordinal)));
        Assert.AreEqual(1, h.Injection.Calls);
    }

    [TestMethod]
    public async Task InvalidTarget_DoesNotOpenMicrophone()
    {
        await using var h = new Harness();
        h.Focus.Target = FocusContext.Empty;
        h.Coordinator.Start(); await Wait(h.Finished.Task);
        Assert.AreEqual(0, h.Audio.StartCount);
        Assert.AreEqual(0, h.Provider.Calls);
        Assert.IsTrue(h.Updates.Any(x => x.State == SessionState.Failed && x.Message.Contains("聚焦", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AudioLevels_OnlyListeningCarriesCurrentGeneration()
    {
        await using var h = new Harness(delayStart: true, blockProvider: true);
        var levels = new ConcurrentQueue<AudioLevelUpdate>();
        h.Coordinator.AudioLevelChanged += (_, level) => levels.Enqueue(level);
        h.Coordinator.Start(); await Wait(h.Audio.Entered.Task);
        h.Audio.EmitLevel(0.2f);
        Assert.IsEmpty(levels);
        h.Audio.StartGate.TrySetResult(true); await Wait(h.Listening.Task);
        h.Audio.EmitLevel(0.4f);
        Assert.HasCount(1, levels);
        Assert.AreEqual(0.4f, levels.Single().Level);
        Assert.AreEqual(h.Updates.First().SessionId, levels.Single().SessionId);
        h.Coordinator.Stop(); await Wait(h.Provider.Entered.Task);
        h.Audio.EmitLevel(0.8f);
        Assert.HasCount(1, levels);
        h.Provider.Result.TrySetResult("文本"); await Wait(h.Finished.Task);
    }

    [TestMethod]
    public async Task Dispose_DuringRecognition_PreventsLateInjectionAndNewSessions()
    {
        var h = new Harness(blockProvider: true);
        h.Coordinator.Start(); await Wait(h.Listening.Task); h.Coordinator.Stop();
        await Wait(h.Provider.Entered.Task);
        var dispose = h.Coordinator.DisposeAsync().AsTask();
        Assert.IsFalse(h.Coordinator.Start());
        h.Provider.Result.TrySetResult("退出后的文本"); await Wait(dispose);
        Assert.AreEqual(0, h.Injection.Calls);
        Assert.IsTrue(h.Audio.Disposed);
        Assert.AreEqual(1, h.Registry.LeasesDisposed);
        await h.DisposeAsync();
    }

    [TestMethod]
    public async Task Dispose_WhenProviderReleaseFails_StillDisposesAudioAndUnsubscribes()
    {
        await using var h = new Harness(blockProvider: true);
        h.Registry.FailRelease = true;
        h.Coordinator.Start(); await Wait(h.Listening.Task); h.Coordinator.Stop();
        await Wait(h.Provider.Entered.Task);
        var dispose = h.Coordinator.DisposeAsync().AsTask();
        h.Provider.Result.TrySetResult("退出后的文本");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Wait(dispose));
        Assert.IsTrue(h.Audio.Disposed);
        Assert.IsFalse(h.Audio.Active);
        Assert.AreEqual(0, h.Audio.LevelSubscribers);
        Assert.AreEqual(0, h.Audio.FaultSubscribers);
        Assert.AreEqual(1, h.Registry.LeasesDisposed);
        Assert.AreEqual(0, h.Injection.Calls);
        Assert.IsFalse(h.Coordinator.IsBusy);
        Assert.IsFalse(h.Coordinator.Start());
    }

    [TestMethod]
    public async Task UnavailableProvider_FailsBeforeOpeningMicrophone_AndCanRetry()
    {
        await using var h = new Harness();
        h.Registry.FailAcquire = true;
        Assert.IsTrue(h.Coordinator.Start()); await Wait(h.Finished.Task);
        Assert.AreEqual(0, h.Audio.StartCount);
        Assert.AreEqual(0, h.Provider.Calls);
        Assert.IsFalse(h.Coordinator.IsBusy);
        Assert.IsTrue(h.Updates.Any(x => x.State == SessionState.Failed && x.Message == "模型缺失"));
        h.Registry.FailAcquire = false; h.ResetSignals();
        Assert.IsTrue(h.Coordinator.Start()); await Wait(h.Listening.Task); h.Coordinator.Stop(); await Wait(h.Finished.Task);
        Assert.AreEqual(1, h.Audio.StartCount);
        Assert.AreEqual(1, h.Injection.Calls);
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [TestMethod]
    public async Task StreamingPreview_IsPublishedBeforeFinal_AndOnlyFinalIsInjected()
    {
        await using var h = new Harness(blockProvider: true);
        h.Coordinator.Start(); await Wait(h.Listening.Task); h.Coordinator.Stop();
        await Wait(h.Provider.Entered.Task);
        h.Provider.Options[0].PartialText!("识别中的半句话");
        Assert.AreEqual("识别中的半句话", h.Updates.Last().PartialText);
        Assert.AreEqual(SessionState.Processing, h.Updates.Last().State);
        Assert.IsEmpty(h.Texts);
        Assert.AreEqual(0, h.Injection.Calls);
        h.Provider.Result.TrySetResult("完整的一句话。"); await Wait(h.Finished.Task);
        Assert.AreEqual("完整的一句话。", h.Texts.Single());
        Assert.AreEqual(1, h.Injection.Calls);
        h.Provider.Options[0].PartialText!("完成后迟到的半句话");
        Assert.AreEqual(SessionState.Completed, h.Updates.Last().State);
    }

    [TestMethod]
    public async Task StreamingPreview_AfterCancelIsIgnored_AndNothingIsInjected()
    {
        await using var h = new Harness(blockProvider: true);
        h.Coordinator.Start(); await Wait(h.Listening.Task); h.Coordinator.Stop();
        await Wait(h.Provider.Entered.Task);
        h.Provider.Options[0].PartialText!("先到的文字");
        h.Coordinator.Cancel();
        h.Provider.Options[0].PartialText!("取消后迟到的文字");
        Assert.AreEqual(SessionState.Cancelling, h.Updates.Last().State);
        Assert.IsNull(h.Updates.Last().PartialText);
        h.Provider.Result.TrySetResult("迟到的最终结果"); await Wait(h.Finished.Task);
        Assert.AreEqual(0, h.Injection.Calls);
        Assert.IsEmpty(h.Texts);
    }
    private static Task Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5));

    [TestMethod]
    public async Task LivePreview_ShowsTextWhileMicrophoneIsStillListening_ThenFinalCorrectsIt()
    {
        await using var h = new Harness();
        h.Settings.Publish(new() { ProviderId = "r2t2-server", R2T2StreamOutput = true });
        var preview = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Coordinator.Updated += (_, update) => { if (update.State == SessionState.Listening && update.PartialText is not null) preview.TrySetResult(update.PartialText); };
        h.Provider.Send = (_, options, _) => Task.FromResult(new TranscriptionResult(options.PartialText is null ? "说话中的临时文字|" : "最终校正的完整句子。", "r2t2-server"));
        h.Coordinator.Start();
        Assert.AreEqual("说话中的临时文字", await preview.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(SessionState.Listening, h.Coordinator.State);
        Assert.IsTrue(h.Audio.Active);
        Assert.AreEqual(0, h.Audio.StopCount);
        Assert.AreEqual(0, h.Injection.Calls);
        Assert.IsEmpty(h.Texts);
        h.Coordinator.Stop(); await Wait(h.Finished.Task);
        Assert.AreEqual(1, h.Audio.StopCount);
        Assert.AreEqual(1, h.Injection.Calls);
        Assert.AreEqual("最终校正的完整句子。", h.Texts.Single());
    }

    [TestMethod]
    public async Task LivePreview_StopCancelsInFlightPreviewBeforeFinalRecognition()
    {
        await using var h = new Harness();
        h.Settings.Publish(new() { ProviderId = "r2t2-server", R2T2StreamOutput = true });
        var cancelled = Signal();
        h.Provider.Send = async (_, options, token) =>
        {
            if (options.PartialText is null)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { cancelled.TrySetResult(true); throw; }
            }
            Assert.IsTrue(cancelled.Task.IsCompleted, "Final inference must wait for preview ownership to be released.");
            return new("最终结果", "r2t2-server");
        };
        h.Coordinator.Start(); await Wait(h.Provider.Entered.Task);
        h.Coordinator.Stop(); await Wait(h.Finished.Task);
        Assert.IsTrue(cancelled.Task.IsCompletedSuccessfully);
        Assert.AreEqual(2, h.Provider.Calls);
        Assert.AreEqual("最终结果", h.Texts.Single());
    }

    [TestMethod]
    public async Task LivePreview_CancelDiscardsLateSnapshotAndKeepsLeaseUntilItReturns()
    {
        await using var h = new Harness(blockProvider: true);
        h.Settings.Publish(new() { ProviderId = "r2t2-server", R2T2StreamOutput = true });
        h.Coordinator.Start(); await Wait(h.Provider.Entered.Task);
        h.Coordinator.Cancel();
        Assert.AreEqual(0, h.Registry.LeasesDisposed);
        Assert.IsFalse(h.Coordinator.Start());
        h.Provider.Result.TrySetResult("取消后迟到的预览");
        await Wait(h.Finished.Task);
        Assert.IsFalse(h.Updates.Any(update => update.PartialText == "取消后迟到的预览"));
        Assert.AreEqual(0, h.Injection.Calls);
        Assert.AreEqual(1, h.Registry.LeasesDisposed);
        Assert.IsEmpty(h.Texts);
    }

    [TestMethod]
    public async Task LivePreview_TransientFailureDoesNotStopMicrophoneOrPreventFinalResult()
    {
        await using var h = new Harness();
        h.Settings.Publish(new() { ProviderId = "r2t2-server", R2T2StreamOutput = true });
        h.Provider.Send = (_, options, _) => options.PartialText is null
            ? Task.FromException<TranscriptionResult>(new TranscriptionException("语音尚未完整", "empty"))
            : Task.FromResult(new TranscriptionResult("完整结果", "r2t2-server"));
        h.Coordinator.Start(); await Wait(h.Provider.Entered.Task);
        Assert.AreEqual(SessionState.Listening, h.Coordinator.State);
        Assert.IsTrue(h.Audio.Active);
        h.Coordinator.Stop(); await Wait(h.Finished.Task);
        Assert.AreEqual("完整结果", h.Texts.Single());
        Assert.AreEqual(1, h.Injection.Calls);
    }

    [TestMethod]
    [DataRow("r2t2-server", false)]
    [DataRow("whisper-local", true)]
    [DataRow("openai-compatible", true)]
    public async Task LivePreview_DisabledOrOtherProviderDoesNotRequestSnapshots(string id, bool enabled)
    {
        await using var h = new Harness();
        h.Settings.Publish(new() { ProviderId = id, R2T2StreamOutput = enabled });
        h.Coordinator.Start(); await Wait(h.Listening.Task);
        var quietInterval = Task.Delay(TimeSpan.FromMilliseconds(1000));
        Assert.AreSame(quietInterval, await Task.WhenAny(quietInterval, h.Audio.PreviewRequested.Task));
        h.Coordinator.Stop(); await Wait(h.Finished.Task);
        Assert.AreEqual(0, h.Audio.PreviewCount);
        Assert.AreEqual(1, h.Provider.Calls);
        Assert.AreEqual(1, h.Injection.Calls);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public SettingsSession Settings { get; } = new(new AppSettings());
        public FakeAudio Audio { get; }
        public FakeFocus Focus { get; } = new();
        public FakeProvider Provider { get; }
        public FakeRegistry Registry { get; }
        public FakeInjection Injection { get; } = new();
        public DictationCoordinator Coordinator { get; }
        public ConcurrentQueue<SessionUpdate> Updates { get; } = new();
        public ConcurrentQueue<string> Texts { get; } = new();
        public TaskCompletionSource<bool> Listening { get; private set; } = Signal();
        public TaskCompletionSource<bool> Finished { get; private set; } = Signal();
        public Harness(bool delayStart = false, bool blockProvider = false, TimeProvider? clock = null)
        {
            Audio = new(delayStart); Provider = new(blockProvider); Registry = new(Provider);
            Coordinator = new(Settings, Audio, Focus, Registry, Injection, clock);
            Coordinator.Updated += (_, update) => { Updates.Enqueue(update); if (update.State == SessionState.Listening) Listening.TrySetResult(true); };
            Coordinator.Transcribed += (_, text) => Texts.Enqueue(text);
            Coordinator.SessionFinished += (_, _) => Finished.TrySetResult(true);
        }
        public void ResetSignals() { Listening = Signal(); Finished = Signal(); }
        public async ValueTask DisposeAsync() { Audio.StartGate.TrySetResult(true); Provider.Result.TrySetResult("清理文本"); await Coordinator.DisposeAsync(); }
    }
    private sealed class FakeAudio(bool delayStart) : IAudioCaptureService, IAudioPreviewSource
    {
        private EventHandler<float>? levelChanged;
        public int LevelSubscribers, FaultSubscribers;
        public event EventHandler<float>? AudioLevelChanged { add { levelChanged += value; LevelSubscribers++; } remove { levelChanged -= value; LevelSubscribers--; } }
        public event EventHandler<Exception>? Faulted { add { FaultSubscribers++; } remove { FaultSubscribers--; } }
        public TaskCompletionSource<bool> Entered { get; } = Signal();
        public TaskCompletionSource<bool> StartGate { get; } = Signal();
        public int StartCount, StopCount;
        public int PreviewCount;
        public TaskCompletionSource<bool> PreviewRequested { get; } = Signal();
        public bool Active, Disposed;
        public AudioData Data { get; set; } = new([1], TimeSpan.FromSeconds(1), 0.5f);
        public async Task StartAsync(CancellationToken token) { StartCount++; Entered.TrySetResult(true); if (delayStart) await StartGate.Task; Active = true; }
        public Task<AudioData> StopAsync(CancellationToken token) { StopCount++; Active = false; return Task.FromResult(Data); }
        public Task CancelAsync() { Active = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; Active = false; return ValueTask.CompletedTask; }
        public void EmitLevel(float level) => levelChanged?.Invoke(this, level);
        public Task<AudioData?> GetPreviewAsync(CancellationToken token)
        { PreviewCount++; PreviewRequested.TrySetResult(true); return Task.FromResult<AudioData?>(Data); }
    }
    private sealed class FakeFocus : IFocusContextService
    {
        public FocusContext Target { get; set; } = new(123, 456, 789, 124, 1000);
        public FocusContext Capture() => Target;
        public FocusContext CaptureBeforeTray() => Target;
        public void Dispose() { }
    }
    private sealed class FakeProvider(bool block) : ITranscriptionProvider
    {
        public string Id => "test-provider";
        public string DisplayName => "测试 Provider";
        public int Calls;
        public List<TranscriptionOptions> Options { get; } = [];
        public Func<AudioData, TranscriptionOptions, CancellationToken, Task<TranscriptionResult>>? Send { get; set; }
        public TaskCompletionSource<bool> Entered { get; } = Signal();
        public TaskCompletionSource<string> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TranscriptionResult> TranscribeAsync(AudioData audio, TranscriptionOptions options, CancellationToken token)
        { Calls++; Options.Add(options); Entered.TrySetResult(true); if (Send is not null) return await Send(audio, options, token); return new(block ? await Result.Task : "测试文本", Id); }
    }
    private sealed class FakeRegistry(FakeProvider provider) : ITranscriptionProviderRegistry
    {
        public List<AppSettings> Acquired { get; } = [];
        public int LeasesDisposed;
        public bool FailRelease;
        public bool FailAcquire;
        public IProviderLease Acquire(AppSettings settings) { if (FailAcquire) throw new TranscriptionException("模型缺失", "model-missing"); Acquired.Add(settings); return new Lease(this, provider); }
        private sealed class Lease(FakeRegistry registry, ITranscriptionProvider provider) : IProviderLease
        { public ITranscriptionProvider Provider => provider; public ValueTask DisposeAsync() { registry.LeasesDisposed++; return registry.FailRelease ? ValueTask.FromException(new InvalidOperationException("测试释放失败")) : ValueTask.CompletedTask; } }
    }
    private sealed class FakeInjection : ITextInjectionService
    {
        public int Calls;
        public Task<InjectionResult> InjectAsync(string text, FocusContext target, long sessionId, CancellationToken token)
        { Calls++; return Task.FromResult(new InjectionResult(InjectionStatus.Dispatched, "已发送文本")); }
        public Task<bool> CopyAsync(string text, CancellationToken token) => Task.FromResult(true);
    }
}
