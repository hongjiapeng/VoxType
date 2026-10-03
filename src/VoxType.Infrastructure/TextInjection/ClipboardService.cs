using System.Text;
using VoxType.Core.TextInjection;

namespace VoxType.Infrastructure.TextInjection;

public interface IClipboardAccess
{
    uint Sequence { get; }
    bool TryOpen();
    void Close();
    IReadOnlyList<uint> Formats();
    byte[]? Read(uint format);
    void Replace(IReadOnlyList<ClipboardFormatData> formats);
    uint Register(string name);
}
public interface IClipboardExecutor
{
    Task<T> ExecuteAsync<T>(Func<IClipboardAccess, T> operation, CancellationToken cancellationToken);
}

public sealed class ClipboardService(IClipboardExecutor executor, TimeProvider? timeProvider = null) : IClipboardService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public async Task<bool> IsOwnedAsync(ClipboardReceipt receipt, CancellationToken cancellationToken)
    {
        long started = clock.GetTimestamp();
        while (true)
        {
            var attempt = await executor.ExecuteAsync<bool?>(c =>
            {
                if (!c.TryOpen()) return null;
                try
                {
                    var marker = c.Read(c.Register("VoxType.SessionOwnership"));
                    return c.Sequence == receipt.Sequence && marker is not null && marker.AsSpan().SequenceEqual(receipt.Marker.ToByteArray());
                }
                finally { c.Close(); }
            }, cancellationToken).ConfigureAwait(false);
            if (attempt is not null) return attempt.Value;
            if (clock.GetElapsedTime(started) >= TimeSpan.FromSeconds(1)) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(25), clock, cancellationToken).ConfigureAwait(false);
        }
    }
    public async Task<ClipboardWriteResult> WriteAsync(string text, bool captureSnapshot, CancellationToken cancellationToken)
    {
        uint initial = await executor.ExecuteAsync(c => c.Sequence, cancellationToken).ConfigureAwait(false);
        long started = clock.GetTimestamp();
        while (true)
        {
            var attempt = await executor.ExecuteAsync(c =>
            {
                if (!c.TryOpen()) return null;
                ClipboardReceipt receipt;
                try
                {
                    if (c.Sequence != initial) return new ClipboardWriteResult(ClipboardWriteStatus.Changed);
                    var snapshot = captureSnapshot ? Snapshot(c) : new ClipboardSnapshot(false, []);
                    var marker = Guid.NewGuid();
                    var formats = new ClipboardFormatData[]
                    {
                        new(13, Encoding.Unicode.GetBytes(text + '\0')),
                        new(c.Register("VoxType.SessionOwnership"), marker.ToByteArray()),
                        new(c.Register("CanIncludeInClipboardHistory"), BitConverter.GetBytes(0u)),
                        new(c.Register("CanUploadToCloudClipboard"), BitConverter.GetBytes(0u)),
                        new(c.Register("ExcludeClipboardContentFromMonitorProcessing"), [0])
                    };
                    cancellationToken.ThrowIfCancellationRequested();
                    try { c.Replace(formats); }
                    catch
                    {
                        // Still inside the same open: no later user copy can be overwritten here.
                        if (snapshot.CanRestore) { try { c.Replace(snapshot.Formats); } catch { } }
                        return new ClipboardWriteResult(ClipboardWriteStatus.Unavailable);
                    }
                    receipt = new(c.Sequence, marker, snapshot);
                }
                finally { c.Close(); }
                // Closing the native clipboard can publish additional formats and
                // advance its sequence. Ownership checks need that final sequence.
                return new ClipboardWriteResult(ClipboardWriteStatus.Written, receipt with { Sequence = c.Sequence });
            }, cancellationToken).ConfigureAwait(false);
            if (attempt is not null) return attempt;
            if (clock.GetElapsedTime(started) >= TimeSpan.FromSeconds(1)) return new(ClipboardWriteStatus.Unavailable);
            await Task.Delay(TimeSpan.FromMilliseconds(25), clock, cancellationToken).ConfigureAwait(false);
        }
    }
    public async Task<ClipboardRestoreStatus> RestoreAsync(ClipboardReceipt receipt, CancellationToken cancellationToken)
    {
        if (!receipt.Snapshot.CanRestore) return ClipboardRestoreStatus.Unsupported;
        long started = clock.GetTimestamp();
        while (true)
        {
            var attempt = await executor.ExecuteAsync<ClipboardRestoreStatus?>(c =>
            {
                if (!c.TryOpen()) return null;
                try
                {
                    var marker = c.Read(c.Register("VoxType.SessionOwnership"));
                    if (c.Sequence != receipt.Sequence || marker is null || !marker.AsSpan().SequenceEqual(receipt.Marker.ToByteArray()))
                        return ClipboardRestoreStatus.OwnershipChanged;
                    try { c.Replace(receipt.Snapshot.Formats); return ClipboardRestoreStatus.Restored; }
                    catch { return ClipboardRestoreStatus.Unavailable; }
                }
                finally { c.Close(); }
            }, cancellationToken).ConfigureAwait(false);
            if (attempt is not null) return attempt.Value;
            if (clock.GetElapsedTime(started) >= TimeSpan.FromSeconds(1)) return ClipboardRestoreStatus.Unavailable;
            await Task.Delay(TimeSpan.FromMilliseconds(25), clock, cancellationToken).ConfigureAwait(false);
        }
    }
    private static ClipboardSnapshot Snapshot(IClipboardAccess clipboard)
    {
        var allowed = new HashSet<uint> { 1, 7, 13, 16, clipboard.Register("HTML Format"), clipboard.Register("Rich Text Format"), clipboard.Register("VoxType.SessionOwnership"),
            clipboard.Register("CanIncludeInClipboardHistory"), clipboard.Register("CanUploadToCloudClipboard"), clipboard.Register("ExcludeClipboardContentFromMonitorProcessing") };
        var formats = new List<ClipboardFormatData>();
        int total = 0;
        foreach (uint format in clipboard.Formats())
        {
            if (!allowed.Contains(format)) return new(false, []);
            var data = clipboard.Read(format);
            if (data is null || data.Length > 4 * 1024 * 1024 || (total += data.Length) > 8 * 1024 * 1024) return new(false, []);
            formats.Add(new(format, data));
        }
        return new(true, formats);
    }
}
