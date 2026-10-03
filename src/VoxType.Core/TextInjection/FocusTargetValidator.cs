namespace VoxType.Core.TextInjection;

public sealed record TargetWindowInfo(uint ProcessId, uint ThreadId, long? ProcessStartTicks, nint FocusWindow, bool IsMinimized, bool IsExternal, int? Integrity);
public interface IWindowTargetAccess
{
    nint ForegroundWindow { get; }
    int? OwnIntegrity { get; }
    TargetWindowInfo? Inspect(nint window);
    bool TrySetForeground(nint window);
}

public sealed class FocusTargetValidator(IWindowTargetAccess windows)
{
    public string? Validate(FocusContext original, bool tryRestore)
    {
        if (!original.IsValid) return "原输入窗口无效。";
        string? error = CheckIdentity(original, windows.Inspect(original.Window));
        if (error is not null) return error;
        if (windows.ForegroundWindow != original.Window)
        {
            if (!tryRestore || !windows.TrySetForeground(original.Window) || windows.ForegroundWindow != original.Window)
                return "无法恢复原输入窗口。";
        }
        var current = windows.Inspect(original.Window);
        error = CheckIdentity(original, current);
        if (error is not null) return error;
        if (original.FocusWindow == 0 || current!.FocusWindow == 0) return "无法验证输入焦点。";
        if (current.FocusWindow != original.FocusWindow) return "窗口中的输入焦点已改变。";
        if (windows.OwnIntegrity is not int own || current.Integrity is not int target) return "无法确认目标窗口权限。";
        if (target > own) return "目标窗口以更高权限运行，无法自动输入。";
        return windows.ForegroundWindow == original.Window ? null : "前台窗口已改变。";
    }
    private static string? CheckIdentity(FocusContext original, TargetWindowInfo? current)
    {
        if (current is null || current.IsMinimized || !current.IsExternal) return "原输入窗口已关闭、最小化或不可用。";
        if (original.ProcessId != current.ProcessId || original.ThreadId != current.ThreadId) return "原窗口身份已改变。";
        if (original.ProcessStartTicks is null || current.ProcessStartTicks is null) return "无法验证原窗口进程身份。";
        if (original.ProcessStartTicks != current.ProcessStartTicks) return "原窗口进程已重新启动。";
        return null;
    }
}
