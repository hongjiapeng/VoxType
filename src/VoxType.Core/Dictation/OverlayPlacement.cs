namespace VoxType.Core.Dictation;

public readonly record struct OverlayRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Intersects(OverlayRect other) => X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;
}

// Fractions of the available travel distance survive DPI and work-area changes.
public sealed record OverlayAnchor(string Monitor, double X, double Y)
{
    public bool IsValid => !string.IsNullOrEmpty(Monitor) && Monitor.Length <= 64 &&
        double.IsFinite(X) && double.IsFinite(Y) && X is >= 0 and <= 1 && Y is >= 0 and <= 1;
}

public sealed record OverlayPreferences(bool Pinned = false, OverlayAnchor? Anchor = null);

public static class OverlayPlacement
{
    public static OverlayRect Place(OverlayRect work, int width, int height, double scale,
        OverlayAnchor? anchor = null, OverlayRect? avoid = null)
    {
        width = Math.Clamp(width, 1, work.Width);
        height = Math.Clamp(height, 1, work.Height);
        var preferred = Clamp(work, new(
            anchor?.IsValid == true ? work.X + (int)Math.Round((work.Width - width) * anchor.X) : work.X + (work.Width - width) / 2,
            anchor?.IsValid == true ? work.Y + (int)Math.Round((work.Height - height) * anchor.Y) : work.Bottom - height - (int)(56 * scale), width, height));
        if (avoid is not { } input) return preferred;
        int gap = Math.Max(8, (int)Math.Round(16 * scale));
        var protectedArea = new OverlayRect(input.X - gap, input.Y - gap, input.Width + gap * 2, input.Height + gap * 2);
        if (!preferred.Intersects(protectedArea)) return preferred;
        OverlayRect[] candidates = [
            Clamp(work, preferred with { Y = protectedArea.Y - height }),
            Clamp(work, preferred with { Y = protectedArea.Bottom }),
            Clamp(work, preferred with { X = protectedArea.X - width }),
            Clamp(work, preferred with { X = protectedArea.Right })];
        // Choose the smallest correction, preserving the user's chosen position when safe.
        return candidates.Where(c => !c.Intersects(protectedArea))
            .OrderBy(c => Math.Abs((long)c.X - preferred.X) + Math.Abs((long)c.Y - preferred.Y))
            .FirstOrDefault(preferred);
    }

    public static OverlayRect Clamp(OverlayRect work, OverlayRect bounds) => new(
        Math.Clamp(bounds.X, work.X, work.Right - Math.Min(bounds.Width, work.Width)),
        Math.Clamp(bounds.Y, work.Y, work.Bottom - Math.Min(bounds.Height, work.Height)),
        Math.Min(bounds.Width, work.Width), Math.Min(bounds.Height, work.Height));

    public static OverlayAnchor Remember(string monitor, OverlayRect work, OverlayRect bounds) => new(monitor,
        Math.Clamp((bounds.X - work.X) / (double)Math.Max(1, work.Width - bounds.Width), 0, 1),
        Math.Clamp((bounds.Y - work.Y) / (double)Math.Max(1, work.Height - bounds.Height), 0, 1));
}
