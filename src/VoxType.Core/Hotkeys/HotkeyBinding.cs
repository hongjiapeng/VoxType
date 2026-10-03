namespace VoxType.Core.Hotkeys;

[Flags]
public enum KeyModifiers { None = 0, Alt = 1, Control = 2, Shift = 4, Windows = 8 }

public sealed record HotkeyBinding(KeyModifiers Modifiers, int Key)
{
    public static HotkeyBinding Default { get; } = new(KeyModifiers.Control | KeyModifiers.Windows, 0x77);
    public string DisplayName => string.Join(" + ",
        new[] { Modifiers.HasFlag(KeyModifiers.Control) ? "Ctrl" : null,
            Modifiers.HasFlag(KeyModifiers.Alt) ? "Alt" : null,
            Modifiers.HasFlag(KeyModifiers.Shift) ? "Shift" : null,
            Modifiers.HasFlag(KeyModifiers.Windows) ? "Win" : null,
            Key == 0x20 ? "Space" : Key is >= 0x70 and <= 0x7A ? $"F{Key - 0x6F}" : ((char)Key).ToString() }
        .Where(x => x is not null));

    public void Validate()
    {
        if (Modifiers == KeyModifiers.None || (Modifiers & ~(KeyModifiers.Alt | KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Windows)) != 0)
            throw new ArgumentException("快捷键需要 Alt、Ctrl、Shift 或 Win 修饰键。");
        if (Key != 0x20 && Key is not (>= 0x30 and <= 0x39) && Key is not (>= 0x41 and <= 0x5A) && Key is not (>= 0x70 and <= 0x7A))
            throw new ArgumentException("请选择 Space、字母、数字或 F1–F11 主键。");
    }
}

public enum HotkeyAction { Pressed, Released, Cancelled, Interrupted }
public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler<HotkeyAction>? Action;
    void Start(HotkeyBinding binding);
    void Configure(HotkeyBinding binding);
    bool CanCancel { get; set; }
}
