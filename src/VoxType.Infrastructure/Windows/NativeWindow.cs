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
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("comctl32.dll", SetLastError = true)] public static extern bool SetWindowSubclass(nint hwnd, SubclassProc procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")] public static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc procedure, nuint id);
    [DllImport("comctl32.dll")] public static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, uint size);
    [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(nint hwnd, ref BlurBehind blur);
    [StructLayout(LayoutKind.Sequential)] private struct BlurBehind
    {
        public uint Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Enable;
        public nint Region;
        [MarshalAs(UnmanagedType.Bool)] public bool Transition;
    }
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
        // Let the XAML surface draw the stroke; DWM's default border is bright on dark themes.
        int noBorderColor = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(hwnd, 34, ref noBorderColor, sizeof(int));
        ConfigureOverlayComposition(hwnd);
    }

    public static void ConfigureOverlayComposition(nint hwnd)
    {
        // Keep DWM composition active when the HWND has a custom region. Without this,
        // clipping the top-level WinUI window can discard its child composition surface.
        nint region = CreateRectRgn(-2, -2, -1, -1);
        if (region == 0) throw new InvalidOperationException("无法初始化语音浮窗合成区域。");
        try
        {
            var blur = new BlurBehind { Flags = 3, Enable = true, Region = region };
            Marshal.ThrowExceptionForHR(DwmEnableBlurBehindWindow(hwnd, ref blur));
        }
        finally { DeleteObject(region); }
    }

    public static void PositionOverlay(nint hwnd, nint target, double widthDip, double heightDip)
    {
        nint monitor = MonitorFromWindow(target, 2);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new Win32Exception();
        double scale = GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96d : 1;
        int width = Math.Min((int)Math.Round(widthDip * scale), info.Work.Right - info.Work.Left);
        int height = Math.Min((int)Math.Round(heightDip * scale), info.Work.Bottom - info.Work.Top);
        int x = info.Work.Left + (info.Work.Right - info.Work.Left - width) / 2;
        int y = Math.Max(info.Work.Top, info.Work.Bottom - height - (int)(56 * scale));
        if (!SetWindowPos(hwnd, -1, x, y, width, height, 0x10)) throw new Win32Exception();
        // Clip the HWND itself: a XAML radius alone leaves opaque rectangular corners.
        nint region = CreateRoundRectRgn(0, 0, width + 1, height + 1, height, height);
        if (region == 0) throw new InvalidOperationException("无法创建语音浮窗圆角区域。");
        if (SetWindowRgn(hwnd, region, true) == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            DeleteObject(region);
            throw new Win32Exception(error);
        }
        // Windows owns the region after a successful SetWindowRgn.
        ConfigureOverlayComposition(hwnd);
    }

    public static void RefreshOverlayContent(nint hwnd)
    {
        // A non-activating WinUI host needs its render surface primed on first show
        // and after a native size change; otherwise UIA can work while pixels stay black.
        // Notify only this HWND's XAML host, then return it to the inactive state.
        // No native activation, foreground change, or input-queue attachment occurs.
        const uint wmActivate = 0x0006;
        nint foreground = GetForegroundWindow();
        SendMessage(hwnd, wmActivate, 1, foreground);
        SendMessage(hwnd, wmActivate, 0, foreground);
    }
}
