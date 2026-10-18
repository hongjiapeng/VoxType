using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VoxType.Infrastructure.Windows;

public static partial class NativeWindow
{
    public delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("comctl32.dll", SetLastError = true)] public static extern bool SetWindowSubclass(nint hwnd, SubclassProc procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")] public static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc procedure, nuint id);
    [DllImport("comctl32.dll")] public static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, uint size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref FrameMargins margins);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? name);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out Rect bounds);
    [StructLayout(LayoutKind.Sequential)] private struct FrameMargins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPosition
    {
        public nint Window, InsertAfter; public int X, Y, Width, Height; public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

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
        // HRGN edges are binary pixels; leave the antialiased outline to XAML.
        SetWindowRgn(hwnd, 0, true);
        ConfigureOverlayComposition(hwnd);
    }

    public static void ConfigureOverlayComposition(nint hwnd)
    {
        // Like SlidePace's HUD, extend composition across the client and suppress
        // native chrome. Our transparent backdrop and XAML border draw the capsule.
        long style = GetWindowLongPtr(hwnd, -16).ToInt64();
        long clean = style & ~(0x00C00000L | 0x00040000L); // CAPTION, THICKFRAME
        if (style != clean)
        {
            SetWindowLongPtr(hwnd, -16, (nint)clean);
            SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x10 | 0x20 | 0x01 | 0x02 | 0x04);
        }
        int none = 1, noColor = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(hwnd, 2, ref none, sizeof(int)); // disable native NC rendering/shadow
        DwmSetWindowAttribute(hwnd, 33, ref none, sizeof(int)); // no native corner overlay
        DwmSetWindowAttribute(hwnd, 34, ref noColor, sizeof(int));
        DwmSetWindowAttribute(hwnd, 35, ref noColor, sizeof(int));
        DwmSetWindowAttribute(hwnd, 38, ref none, sizeof(int)); // no system-drawn backdrop
        var margins = new FrameMargins { Left = -1 };
        Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(hwnd, ref margins));
    }

    public static void PositionOverlay(nint hwnd, VoxType.Core.Dictation.OverlayRect bounds)
    {
        int width = bounds.Width, height = bounds.Height;
        if (!SetWindowPos(hwnd, -1, bounds.X, bounds.Y, width, height, 0x10)) throw new Win32Exception();
    }

    public static nint OverlayContentBridge(nint hwnd) => FindWindowEx(hwnd, 0, "Microsoft.UI.Content.DesktopChildSiteBridge", null);

    public static void FillOverlayContent(nint hwnd, nint bridge)
    {
        if (bridge == 0 || !GetClientRect(hwnd, out var client)) return;
        if (GetWindowRect(hwnd, out var parent) && GetWindowRect(bridge, out var child) &&
            child.Left == parent.Left && child.Top == parent.Top && child.Right - child.Left == client.Right && child.Bottom - child.Top == client.Bottom) return;
        SetWindowPos(bridge, 0, 0, 0, client.Right, client.Bottom, 0x04 | 0x10);
    }

    public static void NormalizeOverlayContentPosition(nint pointer)
    {
        if (pointer == 0) return;
        var position = Marshal.PtrToStructure<WindowPosition>(pointer);
        if ((position.Flags & 0x02) == 0 && position.Y > 0)
        {
            int inset = position.Y;
            position.Y = 0;
            if ((position.Flags & 0x01) == 0) position.Height += inset;
            Marshal.StructureToPtr(position, pointer, false);
        }
    }
}
