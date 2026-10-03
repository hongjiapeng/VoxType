using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using VoxType.Core.TextInjection;
using VoxType.Infrastructure.Windows;

namespace VoxType.Infrastructure.TextInjection;

public sealed class NativeInputTargetPlatform : IInputTargetPlatform, IWindowTargetAccess
{
    private readonly FocusTargetValidator validator;
    private const nuint InputMarker = 0x56545850;
    public NativeInputTargetPlatform() => validator = new(this);
    public nint ForegroundWindow => NativeWindow.GetForegroundWindow();
    public int? OwnIntegrity => Integrity((uint)Environment.ProcessId);
    public bool AnyPhysicalModifiersDown => new int[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);
    public bool IsInteractiveDesktop => NativeDesktop.IsInteractive;
    public string? ValidateTarget(FocusContext target, bool tryRestore) => IsInteractiveDesktop ? validator.Validate(target, tryRestore) : "当前桌面不可交互。";
    public bool TrySetForeground(nint window) => NativeWindow.SetForegroundWindow(window);
    public TargetWindowInfo? Inspect(nint window)
    {
        if (window == 0 || !IsWindow(window)) return null;
        uint thread = GetWindowThreadProcessId(window, out uint process);
        if (thread == 0 || process == 0) return null;
        var name = new StringBuilder(256); GetClassName(window, name, name.Capacity);
        bool external = process != Environment.ProcessId && name.ToString() is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "#32768");
        long? created = null;
        try { using var target = Process.GetProcessById((int)process); created = target.StartTime.ToUniversalTime().Ticks; } catch { }
        var gui = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        nint child = GetGUIThreadInfo(thread, ref gui) ? gui.Focus : 0;
        if (child != 0 && GetAncestor(child, 2) != window) child = 0;
        return new(process, thread, created, child, IsIconic(window), external, Integrity(process));
    }
    private static int? Integrity(uint processId)
    {
        nint process = OpenProcess(0x1000, false, processId);
        if (process == 0) return null;
        nint token = 0, buffer = 0;
        try
        {
            if (!OpenProcessToken(process, 8, out token)) return null;
            GetTokenInformation(token, 25, 0, 0, out uint size);
            if (size == 0 || size > 65536) return null;
            buffer = Marshal.AllocHGlobal((int)size);
            if (!GetTokenInformation(token, 25, buffer, size, out _)) return null;
            nint sid = Marshal.ReadIntPtr(buffer);
            nint count = GetSidSubAuthorityCount(sid);
            if (count == 0 || Marshal.ReadByte(count) == 0) return null;
            nint last = GetSidSubAuthority(sid, (uint)(Marshal.ReadByte(count) - 1));
            return last == 0 ? null : Marshal.ReadInt32(last);
        }
        finally { if (buffer != 0) Marshal.FreeHGlobal(buffer); if (token != 0) CloseHandle(token); CloseHandle(process); }
    }
    public uint SendPaste() => SendInput(4, [Key(0x11), Key(0x56), Key(0x56, true), Key(0x11, true)], Marshal.SizeOf<NativeInput>());
    public void ReleaseSyntheticKeys(uint sentCount)
    {
        // SendInput reports a prefix. Only release keys this prefix could have held down.
        NativeInput[] releases = sentCount switch { 1 => [Key(0x11, true)], 2 => [Key(0x56, true), Key(0x11, true)], 3 => [Key(0x11, true)], _ => [] };
        if (releases.Length != 0) SendInput((uint)releases.Length, releases, Marshal.SizeOf<NativeInput>());
    }
    private static NativeInput Key(ushort key, bool up = false) => new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = up ? 2u : 0u, ExtraInfo = InputMarker } } };
    public static int InputSize => Marshal.SizeOf<NativeInput>();
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public HardwareInput Hardware;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct HardwareInput { public uint Message; public ushort Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    { public uint Size, Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int size);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(nint token, int type, nint buffer, uint size, out uint needed);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthority(nint sid, uint index);
}
