namespace VoxType.Core.Hotkeys;

public readonly record struct KeyEvent(int Key, bool IsDown, bool IsInjected = false);
public readonly record struct KeyDecision(bool Suppress, HotkeyAction? Action = null);

/// <summary>Pure key-sequence state machine. Its owner serializes events on the hook message thread.</summary>
public sealed class HotkeyMatcher
{
    private readonly HashSet<int> down = [];
    private HotkeyBinding binding;
    private HotkeyBinding? pending;
    private HotkeyBinding? sequence;
    private bool active, inhibited, suppressMain, suppressEscape;
    public HotkeyBinding Binding => binding;
    public IReadOnlyCollection<int> DownKeys => down;
    public bool IsActive => active;
    public HotkeyMatcher(HotkeyBinding binding) { binding.Validate(); this.binding = binding; }

    public void Configure(HotkeyBinding value)
    {
        value.Validate();
        pending = value;
        Rearm();
    }

    public KeyDecision Process(KeyEvent key, bool canCancel = false)
    {
        if (key.IsInjected) return default;
        bool fresh = key.IsDown ? down.Add(key.Key) : down.Remove(key.Key);
        if (key.Key == 0x1b)
        {
            if (!key.IsDown && suppressEscape) { suppressEscape = false; Rearm(); return new(true); }
            if (key.IsDown && suppressEscape) return new(true);
            if (key.IsDown && fresh && canCancel)
            {
                suppressEscape = true; active = false; inhibited = true;
                Rearm();
                return new(true, HotkeyAction.Cancelled);
            }
        }

        bool suppressed = suppressMain && sequence?.Key == key.Key;
        HotkeyAction? action = null;
        if (key.IsDown && fresh && key.Key == binding.Key && !active && !inhibited && Modifiers() == binding.Modifiers)
        {
            active = true; inhibited = true; suppressMain = true; sequence = binding;
            suppressed = true; action = HotkeyAction.Pressed;
        }
        else if (!key.IsDown && active && sequence is not null &&
                 (!down.Contains(sequence.Key) || (Modifiers() & sequence.Modifiers) != sequence.Modifiers))
        {
            active = false; action = HotkeyAction.Released;
        }
        if (!key.IsDown && suppressMain && sequence?.Key == key.Key) suppressMain = false;
        Rearm();
        return new(suppressed, action);
    }

    public HotkeyAction? Interrupt(bool canCancel)
    {
        bool hadSession = active || canCancel;
        active = false; inhibited = true;
        Rearm();
        return hadSession ? HotkeyAction.Interrupted : null;
    }

    /// <summary>
    /// A swallowed down event does not update Windows' asynchronous key state.
    /// Poll only observable keys; keep swallowed main/Escape pairs until hook key-up.
    /// Modifier loss, session notifications and the recording limit remain fallbacks.
    /// </summary>
    public HotkeyAction? ReconcileAsyncState(IReadOnlySet<int> asynchronouslyDown)
    {
        var observed = asynchronouslyDown.ToHashSet();
        if (suppressMain && sequence is not null && down.Contains(sequence.Key)) observed.Add(sequence.Key);
        if (suppressEscape && down.Contains(0x1b)) observed.Add(0x1b);
        return Reconcile(observed);
    }

    public HotkeyAction? Reconcile(IReadOnlySet<int> physicallyDown)
    {
        KeyModifiers physicalModifiers = 0;
        foreach (int key in physicallyDown) physicalModifiers |= ModifierFor(key);
        bool lostSequence = active && sequence is not null && (!physicallyDown.Contains(sequence.Key) ||
            (physicalModifiers & sequence.Modifiers) != sequence.Modifiers);
        if (lostSequence) { active = false; inhibited = true; }
        down.RemoveWhere(key => !physicallyDown.Contains(key));
        if (sequence is not null && !down.Contains(sequence.Key)) suppressMain = false;
        if (!down.Contains(0x1b)) suppressEscape = false;
        Rearm();
        return lostSequence ? HotkeyAction.Interrupted : null;
    }

    private void Rearm()
    {
        var relevant = sequence ?? binding;
        if (active || down.Contains(relevant.Key) || (Modifiers() & relevant.Modifiers) != 0) return;
        inhibited = false;
        sequence = null;
        if (pending is not null) { binding = pending; pending = null; }
    }
    private KeyModifiers Modifiers()
    {
        KeyModifiers value = 0;
        foreach (int key in down) value |= ModifierFor(key);
        return value;
    }
    public static KeyModifiers ModifierFor(int key) => key switch
    {
        0x12 or 0xa4 or 0xa5 => KeyModifiers.Alt,
        0x11 or 0xa2 or 0xa3 => KeyModifiers.Control,
        0x10 or 0xa0 or 0xa1 => KeyModifiers.Shift,
        0x5b or 0x5c => KeyModifiers.Windows,
        _ => KeyModifiers.None
    };
}
