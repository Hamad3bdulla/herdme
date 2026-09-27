namespace HerdMe.Windows.Services;

// Physical pixels, like AppWindow and DisplayArea.
public readonly record struct PanelBounds(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

public enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right
}

/// <summary>
/// Where the tray quick panel opens: against the taskbar, next to the point that was clicked,
/// and always fully inside the work area of that display. Without a click point (keyboard
/// activation) it uses the tray corner, which is on the left in right-to-left Windows.
/// Pure so the contract tests can pin it.
/// </summary>
public static class TrayPanelPlacement
{
    public const int LogicalWidth = 360;
    public const int LogicalHeight = 520;
    public const int LogicalMargin = 12;

    // The taskbar sits on the side where the work area is smaller than the whole display.
    public static TaskbarEdge Edge(PanelBounds display, PanelBounds work)
    {
        if (work.Y > display.Y) return TaskbarEdge.Top;
        if (work.X > display.X) return TaskbarEdge.Left;
        if (work.Right < display.Right) return TaskbarEdge.Right;
        return TaskbarEdge.Bottom;
    }

    public static PanelBounds Place(
        PanelBounds display,
        PanelBounds work,
        (int X, int Y)? anchor,
        int width,
        int height,
        int margin,
        bool rightToLeft
    )
    {
        margin = Math.Max(0, margin);
        width = Math.Max(1, Math.Min(width, work.Width - (margin * 2)));
        height = Math.Max(1, Math.Min(height, work.Height - (margin * 2)));
        var edge = Edge(display, work);
        var cornerX = rightToLeft ? work.X + margin : work.Right - margin - width;
        int x;
        int y;
        switch (edge)
        {
            case TaskbarEdge.Top:
                x = anchor is { } top ? top.X - (width / 2) : cornerX;
                y = work.Y + margin;
                break;
            case TaskbarEdge.Left:
                x = work.X + margin;
                y = anchor is { } left ? left.Y - (height / 2) : work.Bottom - margin - height;
                break;
            case TaskbarEdge.Right:
                x = work.Right - margin - width;
                y = anchor is { } right ? right.Y - (height / 2) : work.Bottom - margin - height;
                break;
            default:
                x = anchor is { } bottom ? bottom.X - (width / 2) : cornerX;
                y = work.Bottom - margin - height;
                break;
        }
        x = Math.Clamp(x, work.X + margin, Math.Max(work.X + margin, work.Right - margin - width));
        y = Math.Clamp(y, work.Y + margin, Math.Max(work.Y + margin, work.Bottom - margin - height));
        return new PanelBounds(x, y, width, height);
    }

    public static int Scale(int logical, double scale) =>
        (int)Math.Round(logical * (scale <= 0 ? 1d : scale));
}
