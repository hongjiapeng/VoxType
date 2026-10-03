using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VoxType.Infrastructure.Windows;

public static class NativeWindow
{
    public delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("comctl32.dll", SetLastError = true)] public static extern bool SetWindowSubclass(nint hwnd, SubclassProc procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")] public static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc procedure, nuint id);
    [DllImport("comctl32.dll")] public static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, uint size);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }

    public static void ConfigureOverlay(nint hwnd)
    {
        const int exStyle = -20;
        long style = GetWindowLongPtr(hwnd, exStyle).ToInt64();
        style = (style | 0x08000000L | 0x80L) & ~0x40000L; // NOACTIVATE, TOOLWINDOW; no APPWINDOW
        Marshal.SetLastPInvokeError(0);
        if (SetWindowLongPtr(hwnd, exStyle, (nint)style) == 0 && Marshal.GetLastPInvokeError() != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x10 | 0x20 | 0x01 | 0x02))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        int roundCorners = 2;
        DwmSetWindowAttribute(hwnd, 33, ref roundCorners, sizeof(int));
    }

    public static void PositionOverlay(nint hwnd, nint target)
    {
        nint monitor = MonitorFromWindow(target, 2);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new Win32Exception();
        double scale = GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96d : 1;
        int width = (int)Math.Round(360 * scale), height = (int)Math.Round(84 * scale);
        int x = info.Work.Left + (info.Work.Right - info.Work.Left - width) / 2;
        int y = info.Work.Bottom - height - (int)(24 * scale);
        if (!SetWindowPos(hwnd, -1, x, y, width, height, 0x10)) throw new Win32Exception();
    }
}
