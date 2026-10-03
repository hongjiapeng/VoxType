namespace VoxType.Core.TextInjection;

public enum ClipboardWriteStatus { Written, Changed, Unavailable }
public sealed record ClipboardFormatData(uint Format, byte[] Data);
public sealed record ClipboardSnapshot(bool CanRestore, IReadOnlyList<ClipboardFormatData> Formats);
public sealed record ClipboardReceipt(uint Sequence, Guid Marker, ClipboardSnapshot Snapshot);
public sealed record ClipboardWriteResult(ClipboardWriteStatus Status, ClipboardReceipt? Receipt = null);
public enum ClipboardRestoreStatus { Restored, OwnershipChanged, Unsupported, Unavailable }
public interface IClipboardService
{
    Task<ClipboardWriteResult> WriteAsync(string text, bool captureSnapshot, CancellationToken cancellationToken);
    Task<ClipboardRestoreStatus> RestoreAsync(ClipboardReceipt receipt, CancellationToken cancellationToken);
    Task<bool> IsOwnedAsync(ClipboardReceipt receipt, CancellationToken cancellationToken);
}

public interface IInputTargetPlatform
{
    bool IsInteractiveDesktop { get; }
    // Returns a user-facing reason for refusing to send, or null for a verified target.
    string? ValidateTarget(FocusContext target, bool tryRestore);
    bool AnyPhysicalModifiersDown { get; }
    uint SendPaste();
    void ReleaseSyntheticKeys(uint sentCount);
}
