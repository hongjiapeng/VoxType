using System.Buffers.Binary;
using Microsoft.Extensions.Time.Testing;
using VoxType.Core.Audio;
using VoxType.Core.Settings;
using VoxType.Core.Transcription;
using VoxType.Infrastructure.Transcription;

namespace VoxType.Tests.Transcription;

[TestClass]
public sealed class LocalWhisperServiceTests
{
    private static readonly AudioData Audio = new([1, 2], TimeSpan.FromSeconds(1), .1f);
    private static readonly AppSettings Settings = new() { LocalModelPath = Path.Combine(Path.GetTempPath(), "base-model-test.bin") };
    [TestMethod]
    public async Task RepeatedSessions_ReuseOneModel_KeepLanguagePerRequest()
    {
        var backend = new Backend(); await using var service = new LocalWhisperService(backend); service.SettingsChanged(Settings);
        await using (var first = service.Acquire(Settings)) Assert.AreEqual("识别文本", (await first.Provider.TranscribeAsync(Audio, new("zh"), default)).Text);
        await using (var second = service.Acquire(Settings)) await second.Provider.TranscribeAsync(Audio, new("auto"), default);
        Assert.AreEqual(1, backend.Loads); CollectionAssert.AreEqual(new[] { "zh", "auto" }, backend.Models[0].Languages);
        Assert.AreEqual(0, backend.Models[0].Disposals);
    }
    [TestMethod]
    public async Task ParallelRequests_NeverEnterSameNativeModelConcurrently()
    {
        var backend = new Backend { Block = true }; await using var service = new LocalWhisperService(backend);
        await using var first = service.Acquire(Settings); await using var second = service.Acquire(Settings);
        var firstTask = first.Provider.TranscribeAsync(Audio, new("zh"), default);
        await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondTask = second.Provider.TranscribeAsync(Audio, new("en"), default);
        Assert.IsFalse(secondTask.IsCompleted); Assert.AreEqual(1, backend.Models[0].Calls);
        backend.Gate.TrySetResult();
        await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, backend.Models[0].MaximumActive); Assert.AreEqual(2, backend.Models[0].Calls);
    }
    [TestMethod]
    public async Task CancelAndSwitchProvider_DoNotDisposeUntilNativeReturnsAndLeaseEnds()
    {
        var backend = new Backend { Block = true }; await using var service = new LocalWhisperService(backend);
        var lease = service.Acquire(Settings); using var cancellation = new CancellationTokenSource();
        var task = lease.Provider.TranscribeAsync(Audio, new("zh"), cancellation.Token);
        await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); service.SettingsChanged(Settings with { ProviderId = "openai-compatible" });
        Assert.AreEqual(0, backend.Models[0].Disposals); Assert.IsFalse(task.IsCompleted);
        backend.Gate.TrySetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(0, backend.Models[0].Disposals);
        await lease.DisposeAsync(); await lease.DisposeAsync();
        Assert.AreEqual(1, backend.Models[0].Disposals);
    }
    [TestMethod]
    public async Task ModelPathChange_ReloadsOnNextLease()
    {
        var backend = new Backend(); await using var service = new LocalWhisperService(backend); service.SettingsChanged(Settings);
        await using (var lease = service.Acquire(Settings)) await lease.Provider.TranscribeAsync(Audio, new("auto"), default);
        var next = Settings with { LocalModelPath = Settings.LocalModelPath + ".new" }; service.SettingsChanged(next);
        Assert.AreEqual(1, backend.Models[0].Disposals);
        await using (var lease = service.Acquire(next)) await lease.Provider.TranscribeAsync(Audio, new("zh"), default);
        Assert.AreEqual(2, backend.Loads); Assert.AreEqual(WhisperModelPath.Resolve(next.LocalModelPath), backend.Paths[1]);
    }
    [TestMethod]
    public async Task InferenceBackendChange_RetiresModelAndPassesNewSelectionToLoader()
    {
        var backend = new Backend(); await using var service = new LocalWhisperService(backend); service.SettingsChanged(Settings);
        await using (var lease = service.Acquire(Settings)) await lease.Provider.TranscribeAsync(Audio, new("zh"), default);
        var next = Settings with { LocalInferenceBackend = "vulkan" }; service.SettingsChanged(next);
        Assert.AreEqual(1, backend.Models[0].Disposals);
        await using (var lease = service.Acquire(next)) await lease.Provider.TranscribeAsync(Audio, new("zh"), default);
        CollectionAssert.AreEqual(new[] { "cpu", "vulkan" }, backend.InferenceBackends);
        Assert.AreEqual(2, backend.Loads);
    }
    [TestMethod]
    public async Task FiveMinuteIdle_UnloadsAndNextRequestReloads()
    {
        var clock = new FakeTimeProvider(); var backend = new Backend(); await using var service = new LocalWhisperService(backend, clock);
        await using (var lease = service.Acquire(Settings)) await lease.Provider.TranscribeAsync(Audio, new("auto"), default);
        clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59)); Assert.AreEqual(0, backend.Models[0].Disposals);
        clock.Advance(TimeSpan.FromSeconds(1)); Assert.AreEqual(1, backend.Models[0].Disposals);
        await using (var lease = service.Acquire(Settings)) await lease.Provider.TranscribeAsync(Audio, new("auto"), default);
        Assert.AreEqual(2, backend.Loads);
    }
    [TestMethod]
    public async Task IdleTimerFiresEarly_RearmsUntilDeadline_ThenUnloadsAndReloads()
    {
        var clock = new EarlyTimerClock(); var backend = new Backend(); await using var service = new LocalWhisperService(backend, clock);
        await using (var lease = service.Acquire(Settings)) await lease.Provider.TranscribeAsync(Audio, new("zh"), default);
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(0, backend.Models[0].Disposals, "An early timer must not unload before the idle deadline.");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(1, backend.Models[0].Disposals, "The early callback must schedule the remaining interval.");
        await using (var lease = service.Acquire(Settings))
            Assert.AreEqual("识别文本", (await lease.Provider.TranscribeAsync(Audio, new("zh"), default)).Text);
        Assert.AreEqual(2, backend.Loads);
    }
    [TestMethod]
    public async Task IdleWhileLeaseHeld_DefersUnloadUntilRelease()
    {
        var clock = new FakeTimeProvider(); var backend = new Backend(); await using var service = new LocalWhisperService(backend, clock);
        var lease = service.Acquire(Settings); await lease.Provider.TranscribeAsync(Audio, new("auto"), default);
        clock.Advance(TimeSpan.FromMinutes(5)); Assert.AreEqual(0, backend.Models[0].Disposals);
        await lease.DisposeAsync(); Assert.AreEqual(1, backend.Models[0].Disposals);
    }
    [TestMethod]
    public async Task ExitWaitsForNativeCallBeforeDisposal()
    {
        var backend = new Backend { Block = true }; var service = new LocalWhisperService(backend);
        var lease = service.Acquire(Settings); var processing = lease.Provider.TranscribeAsync(Audio, new("auto"), default);
        await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = service.DisposeAsync().AsTask(); Assert.IsFalse(disposal.IsCompleted); Assert.AreEqual(0, backend.Models[0].Disposals);
        backend.Gate.TrySetResult(); await processing.WaitAsync(TimeSpan.FromSeconds(5)); await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, backend.Models[0].Disposals); await lease.DisposeAsync();
        Assert.ThrowsExactly<ObjectDisposedException>(() => service.Acquire(Settings));
    }
    [TestMethod]
    [DataRow("missing", "model-missing")]
    [DataRow("corrupt", "model-format")]
    [DataRow("english", "model-language")]
    [DataRow("tiny", "model-type")]
    [DataRow("base", "ok")]
    public void ModelHeader_RejectsMissingCorruptEnglishAndOtherSizes(string kind, string expected)
    {
        string path = Path.Combine(Path.GetTempPath(), "VoxType-model-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            if (kind != "missing")
            {
                int[] values = [0x67676d6c, 51865, 1500, 512, 8, 6, 448, 512, 8, 6, 80, 1];
                if (kind == "english") values[1] = 51864; if (kind == "tiny") values[3] = 384; if (kind == "corrupt") values[0] = 0;
                byte[] header = new byte[48]; for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(i * 4), values[i]);
                File.WriteAllBytes(path, header);
            }
            if (expected == "ok") { WhisperModelPath.Validate(path); Assert.AreEqual(Path.GetFullPath(path), WhisperModelPath.Resolve(path)); }
            else Assert.AreEqual(expected, Assert.ThrowsExactly<TranscriptionException>(() => WhisperModelPath.Validate(path)).Code);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [TestMethod]
    public async Task DeleteModel_RejectsActiveLease_ThenUnloadsCachedModelBeforeDeleting()
    {
        string path = Path.Combine(Path.GetTempPath(), "VoxType-delete-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]); var backend = new Backend();
            await using var service = new LocalWhisperService(backend);
            var settings = Settings with { LocalModelPath = path }; service.SettingsChanged(settings);
            var lease = service.Acquire(settings); await lease.Provider.TranscribeAsync(Audio, new("zh"), default);
            var error = await Assert.ThrowsExactlyAsync<TranscriptionException>(() => service.DeleteModelAsync(path));
            Assert.AreEqual("model-busy", error.Code); Assert.IsTrue(File.Exists(path)); Assert.AreEqual(0, backend.Models[0].Disposals);
            await lease.DisposeAsync(); await service.DeleteModelAsync(path);
            Assert.IsFalse(File.Exists(path)); Assert.AreEqual(1, backend.Models[0].Disposals);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [TestMethod]
    public async Task DeleteModel_DuringInference_DoesNotDisposeOrDelete()
    {
        string path = Path.Combine(Path.GetTempPath(), "VoxType-delete-running-" + Guid.NewGuid().ToString("N") + ".bin");
        var backend = new Backend { Block = true };
        try
        {
            File.WriteAllBytes(path, [1]); await using var service = new LocalWhisperService(backend);
            await using var lease = service.Acquire(Settings with { LocalModelPath = path });
            var inference = lease.Provider.TranscribeAsync(Audio, new("zh"), default);
            await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                var error = await Assert.ThrowsExactlyAsync<TranscriptionException>(() => service.DeleteModelAsync(path));
                Assert.AreEqual("model-busy", error.Code); Assert.IsTrue(File.Exists(path)); Assert.AreEqual(0, backend.Models[0].Disposals);
            }
            finally { backend.Gate.TrySetResult(); }
            await inference;
        }
        finally { backend.Gate.TrySetResult(); if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class EarlyTimerClock : TimeProvider
    {
        private readonly FakeTimeProvider clock = new();
        public override long TimestampFrequency => clock.TimestampFrequency;
        public override long GetTimestamp() => clock.GetTimestamp();
        public void Advance(TimeSpan interval) => clock.Advance(interval);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new EarlyTimer(clock.CreateTimer(callback, state, Earlier(dueTime), period));
        private static TimeSpan Earlier(TimeSpan dueTime) => dueTime > TimeSpan.FromMilliseconds(1) ? dueTime - TimeSpan.FromMilliseconds(1) : dueTime;
        private sealed class EarlyTimer(ITimer timer) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(Earlier(dueTime), period);
            public void Dispose() => timer.Dispose();
            public ValueTask DisposeAsync() => timer.DisposeAsync();
        }
    }
    private sealed class Backend : IWhisperBackend
    {
        public int Loads; public bool Block; public List<Model> Models { get; } = []; public List<string> Paths { get; } = []; public List<string> InferenceBackends { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IWhisperModel Load(string path, string inferenceBackend) { Loads++; Paths.Add(path); InferenceBackends.Add(inferenceBackend); var model = new Model(this); Models.Add(model); return model; }
    }
    private sealed class Model(Backend backend) : IWhisperModel
    {
        public int Calls, Disposals, Active, MaximumActive; public List<string> Languages { get; } = [];
        public async Task<string> TranscribeAsync(byte[] wav, string language, CancellationToken cancellationToken)
        {
            Calls++; Languages.Add(language); Active++; MaximumActive = Math.Max(MaximumActive, Active); backend.Entered.TrySetResult();
            try { if (backend.Block) await backend.Gate.Task; return "识别文本"; }
            finally { Active--; }
        }
        public void Dispose() { Assert.AreEqual(0, Active, "Never dispose a running native context."); Disposals++; }
    }
}
