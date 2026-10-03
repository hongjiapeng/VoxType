namespace VoxType.Core.TextInjection;

public sealed record FocusContext(nint Window, uint ProcessId, uint ThreadId, nint FocusWindow, long? ProcessStartTicks)
{
    public static FocusContext Empty { get; } = new(0, 0, 0, 0, null);
    public bool IsValid => Window != 0 && ProcessId != 0;
}
public interface IFocusContextService : IDisposable
{
    FocusContext Capture();
    FocusContext CaptureBeforeTray();
}
public enum InjectionStatus { Dispatched, CopiedOnly, PendingCopy, Cancelled }
public sealed record InjectionResult(InjectionStatus Status, string Message, bool ClipboardRestored = false);
public interface ITextInjectionService
{
    Task<InjectionResult> InjectAsync(string text, FocusContext focus, long sessionId, CancellationToken cancellationToken);
    Task<bool> CopyAsync(string text, CancellationToken cancellationToken = default);
}
