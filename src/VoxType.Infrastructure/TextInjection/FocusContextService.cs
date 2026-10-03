using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using VoxType.Core.TextInjection;
using VoxType.Infrastructure.Windows;

namespace VoxType.Infrastructure.TextInjection;

public sealed class FocusContextService : IFocusContextService
{
    private readonly WinEventProcedure callback;
    private nint foregroundHook, focusHook;
    private FocusContext last = FocusContext.Empty;
    private FocusContext? traySnapshot;
    public void FreezeForTray() => traySnapshot = Volatile.Read(ref last);
    public FocusContextService()
    {
        callback = OnEvent;
        foregroundHook = SetWinEventHook(3, 3, 0, callback, 0, 0, 2);
        focusHook = SetWinEventHook(0x8005, 0x8005, 0, callback, 0, 0, 2);
        if (foregroundHook == 0 || focusHook == 0)
        {
            Dispose();
            throw new InvalidOperationException("无法监听输入目标窗口。");
        }
        Capture();
    }
    private void OnEvent(nint hook, uint eventId, nint hwnd, int objectId, int childId, uint eventThread, uint eventTime)
    {
        try
        {
            if (!NativeDesktop.IsInteractive) return;
            nint targetWindow = eventId == 0x8005 ? GetAncestor(hwnd, 2) : hwnd;
            if (eventId == 0x8005 && targetWindow != NativeWindow.GetForegroundWindow()) return;
            var target = CaptureWindow(targetWindow);
            if (target.IsValid) Volatile.Write(ref last, target);
        }
        catch { /* destroyed or protected windows cannot become injection targets */ }
    }
    public FocusContext Capture()
    {
        if (!NativeDesktop.IsInteractive) return FocusContext.Empty;
        var target = CaptureWindow(NativeWindow.GetForegroundWindow());
        if (target.IsValid) Volatile.Write(ref last, target);
        return target;
    }
    public FocusContext CaptureBeforeTray()
    {
        if (!NativeDesktop.IsInteractive) return FocusContext.Empty;
        var cached = Interlocked.Exchange(ref traySnapshot, null) ?? Volatile.Read(ref last);
        var current = CaptureWindow(cached.Window);
        if (!current.IsValid || current.ProcessId != cached.ProcessId || current.ThreadId != cached.ThreadId ||
            cached.ProcessStartTicks is not null && current.ProcessStartTicks != cached.ProcessStartTicks) return FocusContext.Empty;
        return cached;
    }
    private static FocusContext CaptureWindow(nint hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd) || IsIconic(hwnd)) return FocusContext.Empty;
        uint thread = GetWindowThreadProcessId(hwnd, out uint process);
        if (process == 0 || process == Environment.ProcessId) return FocusContext.Empty;
        var className = new StringBuilder(256);
        GetClassName(hwnd, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "#32768") return FocusContext.Empty;
        var gui = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        nint child = GetGUIThreadInfo(thread, ref gui) ? gui.Focus : 0;
        if (child != 0 && GetAncestor(child, 2) != hwnd) return FocusContext.Empty;
        long? created = null;
        try { using var target = Process.GetProcessById((int)process); created = target.StartTime.ToUniversalTime().Ticks; }
        catch { /* retained as unknown, later injection must validate safely */ }
        return new(hwnd, process, thread, child, created);
    }
    public void Dispose()
    {
        if (foregroundHook != 0) { UnhookWinEvent(foregroundHook); foregroundHook = 0; }
        if (focusHook != 0) { UnhookWinEvent(focusHook); focusHook = 0; }
        GC.KeepAlive(callback);
    }
    private delegate void WinEventProcedure(nint hook, uint eventId, nint hwnd, int objectId, int childId, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public uint Size, Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProcedure procedure, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
}
