namespace VoxType.Infrastructure.Audio;

public static class RecordingStopWaiter
{
    public static async Task WaitAsync(Action requestStop, Task stopped, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        // NAudio starts its capture thread asynchronously. Its transition to Capturing
        // can overwrite a StopRecording issued while Starting; repeat until it exits.
        requestStop();
        while (!stopped.IsCompleted)
        {
            await Task.WhenAny(stopped, Task.Delay(TimeSpan.FromMilliseconds(20), clock)).ConfigureAwait(false);
            if (!stopped.IsCompleted) requestStop();
        }
        await stopped.ConfigureAwait(false);
    }
}
