using System.ComponentModel;
using System.Runtime.InteropServices;
using VoxType.Core.Dictation;

namespace VoxType.Infrastructure.Windows;

public sealed record OverlayDisplay(string Device, OverlayRect Work, double Scale);

public static partial class NativeWindow
{
    public static OverlayDisplay OverlayDisplayFor(nint target, string? preferredDevice = null)
    {
        nint monitor = MonitorFromWindow(target, 2);
        if (preferredDevice is not null)
        {
            MonitorEnum callback = (nint candidate, nint dc, ref Rect rectangle, nint data) =>
            {
                var info = new OverlayMonitorInfo { Size = (uint)Marshal.SizeOf<OverlayMonitorInfo>(), Device = "" };
                if (GetOverlayMonitorInfo(candidate, ref info) && info.Device == preferredDevice) { monitor = candidate; return false; }
                return true;
            };
            EnumDisplayMonitors(0, 0, callback, 0);
            GC.KeepAlive(callback);
        }
        return DescribeMonitor(monitor);
    }

    private static OverlayDisplay DescribeMonitor(nint monitor)
    {
        var info = new OverlayMonitorInfo { Size = (uint)Marshal.SizeOf<OverlayMonitorInfo>(), Device = "" };
        if (!GetOverlayMonitorInfo(monitor, ref info)) throw new Win32Exception();
        double scale = GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96d : 1;
        return new(info.Device, new(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top), scale);
    }

    public static OverlayRect OverlayBounds(nint window)
    {
        if (!GetWindowRect(window, out var rect)) throw new Win32Exception();
        return new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public static OverlayRect? OverlayInputArea(nint target)
    {
        if (target == 0 || target != GetForegroundWindow()) return null;
        uint thread = GetWindowThreadProcessId(target, out _);
        var gui = new OverlayGuiInfo { Size = (uint)Marshal.SizeOf<OverlayGuiInfo>() };
        if (!GetGUIThreadInfo(thread, ref gui)) return null;
        // Prefer a bounded edit field so multiple lines remain readable, not just
        // the insertion point. Full-window renderers fall back to the caret below.
        if (gui.Focus != 0 && gui.Focus != target && GetWindowRect(gui.Focus, out var input) && GetWindowRect(target, out var window))
        {
            int height = input.Bottom - input.Top;
            if (height > 0 && height < (window.Bottom - window.Top) / 2)
                return new(input.Left, input.Top, input.Right - input.Left, height);
        }
        if (gui.Caret != 0 && gui.Bounds.Bottom > gui.Bounds.Top)
        {
            var point = new OverlayPoint(gui.Bounds.Left, gui.Bounds.Top);
            if (ClientToScreen(gui.Caret, ref point))
            {
                int lineMargin = (int)Math.Round(24 * Math.Max(1, GetDpiForWindow(target) / 96d));
                return new(point.X - lineMargin * 3, point.Y - lineMargin,
                    Math.Max(2, gui.Bounds.Right - gui.Bounds.Left) + lineMargin * 6,
                    gui.Bounds.Bottom - gui.Bounds.Top + lineMargin * 2);
            }
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct OverlayPoint(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct OverlayMonitorInfo
    {
        public uint Size; public Rect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential)] private struct OverlayGuiInfo
    {
        public uint Size, Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rect Bounds;
    }
    private delegate bool MonitorEnum(nint monitor, nint dc, ref Rect bounds, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnum callback, nint data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] private static extern bool GetOverlayMonitorInfo(nint monitor, ref OverlayMonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref OverlayGuiInfo info);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref OverlayPoint point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
}
