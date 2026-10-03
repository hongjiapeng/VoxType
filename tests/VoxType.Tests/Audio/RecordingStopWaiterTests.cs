using Microsoft.Extensions.Time.Testing;
using VoxType.Infrastructure.Audio;

namespace VoxType.Tests.Audio;

[TestClass]
public sealed class RecordingStopWaiterTests
{
    [TestMethod]
    public async Task StopOverwrittenByCaptureStartup_IsReissuedUntilStopped()
    {
        var clock = new FakeTimeProvider();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool capturing = false;
        int requests = 0;
        void Stop() { requests++; if (capturing) { capturing = false; stopped.TrySetResult(); } }
        Task stop = RecordingStopWaiter.WaitAsync(Stop, stopped.Task, clock);
        Assert.AreEqual(1, requests);
        Assert.IsFalse(stop.IsCompleted);
        // The native startup thread overwrites the initial stop state.
        capturing = true;
        clock.Advance(TimeSpan.FromMilliseconds(20));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, requests);
        Assert.IsFalse(capturing);
    }

    [TestMethod]
    public async Task CompletedStop_DoesNotRepeatRequests()
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        await RecordingStopWaiter.WaitAsync(() => { requests++; stopped.TrySetResult(); }, stopped.Task);
        Assert.AreEqual(1, requests);
        Assert.IsTrue(stopped.Task.IsCompletedSuccessfully);
    }
}
