using VoxType.Core.TextInjection;

namespace VoxType.Core.Dictation;

public enum SessionState { Idle, Starting, Listening, Stopping, Processing, Injecting, Completed, Failed, Cancelling }
public sealed record SessionUpdate(long SessionId, SessionState State, string Message, string ProviderName, bool IsLocal, FocusContext Focus, bool FromTray = false, long Revision = 0);
