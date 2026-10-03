using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VoxType.Infrastructure.Windows;

internal sealed class NativeMessageWindow : IDisposable
{
    private readonly Procedure procedure;
    private readonly string className = "VoxType.Messages." + Guid.NewGuid().ToString("N");
    private readonly nint module;
    public nint Handle { get; private set; }
    public NativeMessageWindow(Func<nint, uint, nuint, nint, nint?> handler)
    {
        procedure = (hwnd, message, wParam, lParam) =>
        {
            try { return handler(hwnd, message, wParam, lParam) ?? DefWindowProc(hwnd, message, wParam, lParam); }
            catch { return DefWindowProc(hwnd, message, wParam, lParam); }
        };
        module = GetModuleHandle(null);
        var windowClass = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = procedure, Module = module, ClassName = className };
        if (RegisterClassEx(ref windowClass) == 0) throw new Win32Exception();
        Handle = CreateWindowEx(0, className, "VoxType service", 0, 0, 0, 0, 0, 0, 0, module, 0);
        if (Handle == 0) { UnregisterClass(className, module); throw new Win32Exception(); }
    }
    public static void RunLoop()
    {
        int result;
        while ((result = GetMessage(out var message, 0, 0, 0)) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
        if (result == -1) throw new Win32Exception();
    }
    public void Dispose()
    {
        if (Handle == 0) return;
        DestroyWindow(Handle); Handle = 0;
        UnregisterClass(className, module);
        GC.KeepAlive(procedure);
    }
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] public static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int exitCode);
    private delegate nint Procedure(nint hwnd, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size, Style; public Procedure Procedure; public int ClassExtra, WindowExtra;
        public nint Module, Icon, Cursor, Background; public string? MenuName; public string ClassName; public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    {
        public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint ex, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint module, nint param);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint module);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)] private static extern int GetMessage(out Message message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref Message message);
}
