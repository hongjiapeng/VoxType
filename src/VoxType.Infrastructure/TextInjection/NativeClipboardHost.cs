using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using VoxType.Core.TextInjection;
using VoxType.Infrastructure.Windows;

namespace VoxType.Infrastructure.TextInjection;

public sealed class NativeClipboardHost : IClipboardExecutor, IDisposable
{
    private readonly ConcurrentQueue<Action<IClipboardAccess>> queue = new();
    private readonly TaskCompletionSource<nint> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private readonly object sync = new();
    private bool disposed;
    public NativeClipboardHost()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "VoxType Clipboard STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Task.GetAwaiter().GetResult();
    }
    public Task<T> ExecuteAsync<T>(Func<IClipboardAccess, T> operation, CancellationToken cancellationToken)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            queue.Enqueue(access =>
            {
                if (cancellationToken.IsCancellationRequested) { result.TrySetCanceled(cancellationToken); return; }
                try { result.TrySetResult(operation(access)); } catch (Exception error) { result.TrySetException(error); }
            });
            if (!NativeMessageWindow.PostMessage(ready.Task.Result, 0x8001, 0, 0)) result.TrySetException(new Win32Exception());
        }
        return result.Task;
    }
    private void Run()
    {
        NativeAccess? access = null;
        try
        {
            using var window = new NativeMessageWindow((_, message, _, _) =>
            {
                if (message == 0x8001) { while (queue.TryDequeue(out var action)) action(access!); return 0; }
                if (message == 0x0010) { NativeMessageWindow.PostQuitMessage(0); return 0; }
                return null;
            });
            access = new(window.Handle);
            int initialized = OleInitialize(0);
            if (initialized < 0) Marshal.ThrowExceptionForHR(initialized);
            try { ready.SetResult(window.Handle); NativeMessageWindow.RunLoop(); }
            finally { OleUninitialize(); }
        }
        catch (Exception error) { ready.TrySetException(error); }
    }
    public void Dispose()
    {
        lock (sync) { if (disposed) return; disposed = true; NativeMessageWindow.PostMessage(ready.Task.Result, 0x0010, 0, 0); }
        thread.Join();
    }
    [DllImport("ole32.dll")] private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();

    private sealed class NativeAccess(nint owner) : IClipboardAccess
    {
        public uint Sequence => GetClipboardSequenceNumber();
        public bool TryOpen() => OpenClipboard(owner);
        public void Close() => CloseClipboard();
        public uint Register(string name)
        {
            uint value = RegisterClipboardFormat(name);
            if (value == 0) throw new Win32Exception();
            return value;
        }
        public IReadOnlyList<uint> Formats()
        {
            var result = new List<uint>();
            uint current = 0;
            while ((current = EnumClipboardFormats(current)) != 0)
            {
                result.Add(current);
                if (result.Count > 128) throw new InvalidOperationException("剪贴板格式过多。");
            }
            if (Marshal.GetLastPInvokeError() != 0) throw new Win32Exception();
            return result;
        }
        public byte[]? Read(uint format)
        {
            nint data = GetClipboardData(format);
            if (data == 0) return null;
            nuint size = GlobalSize(data);
            if (size == 0 || size > 4 * 1024 * 1024) return null;
            nint pointer = GlobalLock(data);
            if (pointer == 0) return null;
            try { byte[] bytes = new byte[(int)size]; Marshal.Copy(pointer, bytes, 0, bytes.Length); return bytes; }
            finally { GlobalUnlock(data); }
        }
        public void Replace(IReadOnlyList<ClipboardFormatData> formats)
        {
            var allocations = new List<(uint Format, nint Handle)>();
            try
            {
                foreach (var format in formats)
                {
                    nint handle = GlobalAlloc(2, (nuint)Math.Max(1, format.Data.Length));
                    if (handle == 0) throw new Win32Exception();
                    allocations.Add((format.Format, handle));
                    nint pointer = GlobalLock(handle);
                    if (pointer == 0) throw new Win32Exception();
                    try { Marshal.Copy(format.Data, 0, pointer, format.Data.Length); }
                    finally { GlobalUnlock(handle); }
                }
                if (!EmptyClipboard()) throw new Win32Exception();
                for (int index = 0; index < allocations.Count; index++)
                {
                    var item = allocations[index];
                    if (SetClipboardData(item.Format, item.Handle) == 0) throw new Win32Exception();
                    allocations[index] = (item.Format, 0); // ownership transferred to Windows
                }
            }
            finally { foreach (var item in allocations) if (item.Handle != 0) GlobalFree(item.Handle); }
        }
        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint owner);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll", SetLastError = true)] private static extern uint EnumClipboardFormats(uint previous);
        [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
        [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint format, nint data);
        [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterClipboardFormat(string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint flags, nuint bytes);
        [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint handle);
        [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint handle);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint handle);
        [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint handle);
    }
}
