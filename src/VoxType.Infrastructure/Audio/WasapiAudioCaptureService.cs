using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoxType.Core.Audio;

namespace VoxType.Infrastructure.Audio;

public sealed class WasapiAudioCaptureService : IAudioCaptureService, IAudioPreviewSource
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object bufferLock = new();
    private WasapiRecorder? capture;
    private MMDevice? device;
    private MemoryStream? buffer;
    private WaveFormat? format;
    private TaskCompletionSource<Exception?>? stopped;
    private long byteLimit;
    private bool disposed;
    private bool started;
    public event EventHandler<float>? AudioLevelChanged;
    public event EventHandler<Exception>? Faulted;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (capture is not null) throw new InvalidOperationException("麦克风正在使用中。");
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var enumerator = new MMDeviceEnumerator();
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                // RAW avoids broken system enhancement/APO initialization without
                // changing the user's Windows sound settings.
                capture = new WasapiRecorderBuilder().WithDevice(device).WithRawMode().Build();
                format = capture.WaveFormat;
                byteLimit = (long)format.AverageBytesPerSecond * 60;
                if (byteLimit > 128L * 1024 * 1024 || format.Channels is < 1 or > 8)
                    throw new AudioCaptureException("默认麦克风格式不受支持，请在 Windows 声音设置中选择常规采样格式。");
                buffer = new MemoryStream();
                stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
                capture.DataAvailable += OnData;
                capture.RecordingStopped += OnStopped;
                try { capture.StartRecording(); }
                catch (CoreAudioException ex) when (ex.HResult == unchecked((int)0x88890027)) // AUDCLNT_E_RAW_MODE_UNSUPPORTED
                {
                    capture.DataAvailable -= OnData;
                    capture.RecordingStopped -= OnStopped;
                    capture.Dispose();
                    capture = new WasapiRecorderBuilder().WithDevice(device).Build();
                    capture.DataAvailable += OnData;
                    capture.RecordingStopped += OnStopped;
                    capture.StartRecording();
                }
                started = true;
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            await StopAndReleaseAsync().ConfigureAwait(false);
            if (ex is OperationCanceledException or AudioCaptureException) throw;
            throw new AudioCaptureException("无法打开默认麦克风。请检查设备和 Windows 麦克风隐私权限，然后重试。", ex);
        }
        finally { gate.Release(); }
    }

    private void OnData(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        try
        {
            float level = 0;
            lock (bufferLock)
            {
                if (buffer is null || format is null) return;
                int count = (int)Math.Min(data.Length, byteLimit - buffer.Length);
                count -= count % format.BlockAlign;
                if (count == 0) return;
                var packet = data[..count].ToArray();
                if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(packet);
                buffer.Write(packet);
                level = WavConverter.Rms(WavConverter.DecodeMono(packet, count, format));
            }
            AudioLevelChanged?.Invoke(this, level);
        }
        catch (Exception ex)
        {
            ReportFault(new AudioCaptureException("麦克风数据读取失败，请重新录音。", ex));
        }
    }
    private void OnStopped(object? sender, StoppedEventArgs args)
    {
        stopped?.TrySetResult(args.Exception);
        if (args.Exception is not null)
            ReportFault(new AudioCaptureException("麦克风已中断。请检查连接后重新录音。", args.Exception));
    }

    public async Task<AudioData> StopAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (capture is null || format is null || buffer is null) throw new AudioCaptureException("麦克风尚未开始录音。");
            var inputFormat = format;
            await RecordingStopWaiter.WaitAsync(capture.StopRecording, stopped!.Task).ConfigureAwait(false);
            Exception? fault = await stopped!.Task.ConfigureAwait(false);
            byte[] bytes;
            lock (bufferLock) bytes = buffer.ToArray();
            await ReleaseAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (fault is not null) throw new AudioCaptureException("录音设备中断，未发送音频。", fault);
            return await Task.Run(() => WavConverter.ConvertForSpeech(bytes, inputFormat), cancellationToken).ConfigureAwait(false);
        }
        finally { await ReleaseAsync().ConfigureAwait(false); gate.Release(); }
    }

    public async Task<AudioData?> GetPreviewAsync(CancellationToken cancellationToken)
    {
        byte[] bytes;
        WaveFormat snapshotFormat;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (bufferLock)
            {
                if (!started || buffer is null || format is null || buffer.Length < format.AverageBytesPerSecond / 2) return null;
                bytes = buffer.ToArray();
                snapshotFormat = format;
            }
        }
        finally { gate.Release(); }
        // Resample a private copy off the WASAPI callback and UI threads.
        return await Task.Run(() => WavConverter.ConvertForSpeech(bytes, snapshotFormat), cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { await StopAndReleaseAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    private async Task StopAndReleaseAsync()
    {
        if (capture is not null && stopped is not null && started)
        {
            await RecordingStopWaiter.WaitAsync(capture.StopRecording, stopped.Task).ConfigureAwait(false);
        }
        await ReleaseAsync().ConfigureAwait(false);
    }
    private void ReportFault(Exception error) { try { Faulted?.Invoke(this, error); } catch { } }
    private async Task ReleaseAsync()
    {
        if (capture is not null)
        {
            capture.DataAvailable -= OnData;
            capture.RecordingStopped -= OnStopped;
            await capture.DisposeAsync().ConfigureAwait(false); capture = null;
        }
        device?.Dispose(); device = null;
        lock (bufferLock) { buffer?.Dispose(); buffer = null; format = null; }
        stopped = null;
        started = false;
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await CancelAsync().ConfigureAwait(false);
        gate.Dispose();
    }
}
