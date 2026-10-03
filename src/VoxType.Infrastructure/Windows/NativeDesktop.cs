using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace VoxType.Infrastructure.Windows;

public static class NativeDesktop
{
    public static bool IsInteractive
    {
        get
        {
            if (NativeWindow.GetForegroundWindow() == 0 || !IsSessionUnlocked()) return false;
            nint desktop = OpenInputDesktop(0, false, 1);
            if (desktop == 0) return false;
            try
            {
                var name = new StringBuilder(256);
                return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) && name.ToString() == "Default";
            }
            finally { CloseDesktop(desktop); }
        }
    }
    public static bool IsUnlockedSessionHeader(ReadOnlySpan<byte> header) => header.Length >= 20 &&
        BinaryPrimitives.ReadInt32LittleEndian(header) == 1 &&
        BinaryPrimitives.ReadInt32LittleEndian(header[12..]) == 0 &&
        BinaryPrimitives.ReadInt32LittleEndian(header[16..]) == 1;
    private static bool IsSessionUnlocked()
    {
        if (!WTSQuerySessionInformation(0, 0xffffffff, 25, out nint data, out uint size)) return false;
        try
        {
            if (size < 20) return false;
            // Windows x64 WTSINFOEX: DWORD Level, 4-byte padding, LEVEL1 data.
            byte[] header = new byte[20]; Marshal.Copy(data, header, 0, header.Length);
            return IsUnlockedSessionHeader(header);
        }
        finally { WTSFreeMemory(data); }
    }
    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW")] private static extern bool WTSQuerySessionInformation(nint server, uint session, int information, out nint buffer, out uint bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint buffer);
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder name, int size, out int needed);
}
