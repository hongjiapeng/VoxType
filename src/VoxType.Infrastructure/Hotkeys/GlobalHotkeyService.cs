using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using VoxType.Core.Hotkeys;
using VoxType.Infrastructure.Windows;

namespace VoxType.Infrastructure.Hotkeys;

public sealed class GlobalHotkeyService : IGlobalHotkeyService
{
    private readonly HookProcedure callback;
    private readonly Channel<HotkeyAction> actions = Channel.CreateUnbounded<HotkeyAction>(new() { SingleReader = true, SingleWriter = true });
    private readonly ManualResetEventSlim ready = new();
    private Thread? thread;
    private HotkeyMatcher? matcher;
    private HotkeyBinding? pendingBinding;
    private nint hook, window;
    private Exception? startupError;
    private bool disposed, canCancel;
    public event EventHandler<HotkeyAction>? Action;
    public event EventHandler<string>? Unavailable;
    public bool CanCancel { get => Volatile.Read(ref canCancel); set => Volatile.Write(ref canCancel, value); }
    public GlobalHotkeyService() => callback = KeyboardCallback;

    public void Start(HotkeyBinding binding)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (thread is not null) throw new InvalidOperationException("快捷键服务已启动。");
        matcher = new(binding);
        thread = new Thread(Run) { Name = "VoxType keyboard hook", IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(); // only waits for native installation; no network/audio/UI work on this thread
        if (startupError is not null) throw new InvalidOperationException("无法启用全局快捷键。", startupError);
        _ = Task.Run(DeliverActionsAsync);
    }
    private async Task DeliverActionsAsync()
    {
        await foreach (var action in actions.Reader.ReadAllAsync())
        {
            if (Volatile.Read(ref disposed)) continue;
            try { Action?.Invoke(this, action); }
            catch { /* consumer faults cannot unwind into the keyboard hook */ }
        }
    }
    public void Configure(HotkeyBinding binding)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        binding.Validate();
        Interlocked.Exchange(ref pendingBinding, binding);
        if (window != 0) NativeMessageWindow.PostMessage(window, 0x8002, 0, 0);
    }
    private void ApplyPending()
    {
        if (CanCancel) return;
        var binding = Interlocked.Exchange(ref pendingBinding, null);
        if (binding is not null)
        {
            matcher!.Configure(binding);
            // Reapplying settings also recovers a hook Windows may have silently removed.
            nint replacement = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
            if (replacement == 0) { Unavailable?.Invoke(this, "快捷键未能重新启用，请重新保存或重启应用。"); return; }
            nint previous = hook; hook = replacement;
            if (previous != 0) UnhookWindowsHookEx(previous);
        }
    }
    private void Run()
    {
        bool sessionRegistered = false;
        try
        {
            using var messages = new NativeMessageWindow(HandleMessage);
            window = messages.Handle;
            hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
            if (hook == 0) throw new Win32Exception();
            sessionRegistered = WTSRegisterSessionNotification(window, 0);
            if (!sessionRegistered) throw new Win32Exception("无法监听当前会话的锁屏事件。");
            if (SetTimer(window, 1, 100, 0) == 0) throw new Win32Exception();
            ready.Set();
            NativeMessageWindow.RunLoop();
        }
        catch (Exception ex) { startupError = ex; Queue(matcher?.Interrupt(CanCancel)); }
        finally
        {
            if (hook != 0) { UnhookWindowsHookEx(hook); hook = 0; }
            if (sessionRegistered) WTSUnRegisterSessionNotification(window);
            window = 0;
            ready.Set();
            actions.Writer.TryComplete();
            GC.KeepAlive(callback);
        }
    }
    private nint? HandleMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case 0x8002: ApplyPending(); return 0;
            case 0x113:
                ApplyPending();
                // GetAsyncKeyState is used outside WH_KEYBOARD_LL only.
                var asynchronous = matcher!.DownKeys.Where(key => (GetAsyncKeyState(key) & 0x8000) != 0).ToHashSet();
                Queue(matcher.ReconcileAsyncState(asynchronous));
                return 0;
            case 0x2b1 when wParam == 7: // WTS_SESSION_LOCK
            case 0x218 when wParam == 4: // PBT_APMSUSPEND
                Queue(matcher!.Interrupt(CanCancel)); return 0;
            case 0x10:
                KillTimer(hwnd, 1);
                NativeMessageWindow.PostQuitMessage(0); return 0;
        }
        return null;
    }
    private nint KeyboardCallback(int code, nuint wParam, nint lParam)
    {
        if (code < 0 || matcher is null) return CallNextHookEx(hook, code, wParam, lParam);
        try
        {
            uint message = (uint)wParam;
            if (message is not (0x100 or 0x101 or 0x104 or 0x105)) return CallNextHookEx(hook, code, wParam, lParam);
            var key = Marshal.PtrToStructure<KeyboardData>(lParam);
            var decision = matcher.Process(new((int)key.Key, message is 0x100 or 0x104, (key.Flags & 0x12) != 0), CanCancel);
            Queue(decision.Action);
            if (decision.Suppress) return 1;
        }
        catch { Queue(HotkeyAction.Interrupted); }
        return CallNextHookEx(hook, code, wParam, lParam);
    }
    private void Queue(HotkeyAction? action) { if (action is not null) actions.Writer.TryWrite(action.Value); }
    public void Dispose()
    {
        if (disposed) return;
        Volatile.Write(ref disposed, true);
        if (window != 0) NativeMessageWindow.PostMessage(window, 0x10, 0, 0);
        thread?.Join();
        actions.Writer.TryComplete();
        ready.Dispose();
    }
    private delegate nint HookProcedure(int code, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint Key, ScanCode, Flags, Time; public nuint ExtraInfo; }
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProcedure callback, nint module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern nuint SetTimer(nint hwnd, nuint id, uint milliseconds, nint callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint hwnd, nuint id);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSRegisterSessionNotification(nint hwnd, uint flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(nint hwnd);
}
