using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VoxType.Infrastructure.Windows;

/// <summary>Owns a hidden top-level message window, independent of settings and overlay windows.</summary>
public sealed class TrayService : IDisposable
{
    private const uint Callback = 0x8001;
    private readonly WindowProc procedure;
    private readonly string className = "VoxType.Tray." + Guid.NewGuid().ToString("N");
    private readonly nint instance, hwnd, icon;
    private readonly uint taskbarCreated;
    private bool disposed;
    public bool RecordingAvailable { get; set; }
    public event EventHandler? SettingsRequested;
    public event EventHandler? RecordingRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? MenuOpening;
    public string HotkeyLabel { get; set; } = "Alt + Space";

    public TrayService(string iconPath)
    {
        procedure = HandleMessage;
        instance = GetModuleHandle(null);
        var windowClass = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = procedure, Instance = instance, ClassName = className };
        if (RegisterClassEx(ref windowClass) == 0) throw new Win32Exception();
        hwnd = CreateWindowEx(0, className, "VoxType messages", 0, 0, 0, 0, 0, 0, 0, instance, 0);
        if (hwnd == 0) { UnregisterClass(className, instance); throw new Win32Exception(); }
        icon = LoadImage(0, iconPath, 1, 32, 32, 0x10);
        if (icon == 0) { DestroyWindow(hwnd); UnregisterClass(className, instance); throw new Win32Exception(); }
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        try { AddIcon(); }
        catch { DestroyIcon(icon); DestroyWindow(hwnd); UnregisterClass(className, instance); throw; }
    }

    private NotifyIconData Data() => new() { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = hwnd, Id = 1, Flags = 1 | 2 | 4, CallbackMessage = Callback, Icon = icon, Tip = "VoxType · 按住 " + HotkeyLabel + " 说话", Info = "", InfoTitle = "" };
    public void RefreshTooltip() { var data = Data(); ShellNotifyIcon(1, ref data); }
    private void AddIcon()
    {
        var data = Data();
        if (!ShellNotifyIcon(0, ref data)) { SetTimer(hwnd, 1, 1000, 0); return; }
        KillTimer(hwnd, 1);
        data.Version = 4;
        ShellNotifyIcon(4, ref data);
    }

    private nint HandleMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        // Never let a managed exception unwind across the native callback boundary.
        try
        {
            if (message == taskbarCreated && !disposed) { AddIcon(); return 0; }
            if (message == 0x113 && !disposed) { AddIcon(); return 0; }
            if (message == Callback)
            {
                uint notification = (uint)(lParam.ToInt64() & 0xffff);
                if (notification is 0x203 or 0x400 or 0x401) SettingsRequested?.Invoke(this, EventArgs.Empty);
                else if (notification is 0x205 or 0x7b) ShowMenu();
                return 0;
            }
        }
        catch { /* Shell may be temporarily unavailable during Explorer restart. */ }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        nint menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            MenuOpening?.Invoke(this, EventArgs.Empty);
            AppendMenu(menu, 2, 0, "VoxType");
            AppendMenu(menu, RecordingAvailable ? 0u : 2u, 1, "开始语音输入");
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, 0, 2, "设置");
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, 0, 3, "退出");
            GetCursorPos(out Point point);
            NativeWindow.SetForegroundWindow(hwnd);
            int choice = TrackPopupMenu(menu, 0x100 | 0x02, point.X, point.Y, 0, hwnd, 0);
            PostMessage(hwnd, 0, 0, 0);
            switch (choice)
            {
                case 1: RecordingRequested?.Invoke(this, EventArgs.Empty); break;
                case 2: SettingsRequested?.Invoke(this, EventArgs.Empty); break;
                case 3: ExitRequested?.Invoke(this, EventArgs.Empty); break;
            }
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        KillTimer(hwnd, 1);
        var data = Data();
        ShellNotifyIcon(2, ref data);
        DestroyWindow(hwnd);
        DestroyIcon(icon);
        UnregisterClass(className, instance);
        GC.KeepAlive(procedure);
    }

    private delegate nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size, Style;
        public WindowProc Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id, Flags, CallbackMessage; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint ex, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadImage(nint instance, string path, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nuint SetTimer(nint hwnd, nuint id, uint interval, nint callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint hwnd, nuint id);
}
