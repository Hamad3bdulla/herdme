namespace HerdMe.Windows.Services;

// Button ids Windows sends back in WM_COMMAND. They never change once shipped.
public enum ThumbnailButton
{
    StartAll = 1,
    StopAll = 2,
    OpenSite = 3
}

public sealed record ThumbnailButtonState(
    ThumbnailButton Button,
    bool Enabled,
    string TooltipKey,
    string IconAsset
);

/// <summary>
/// Start all / Stop all / Open site under the HerdMe thumbnail when the pointer rests on the
/// taskbar button. This part only decides which buttons are enabled and decodes the click;
/// Services/TaskbarThumbnailToolbar.cs talks to the taskbar. Pure so the contract tests can
/// pin it.
/// </summary>
public static class TaskbarThumbnailButtons
{
    // HIWORD(wParam) of WM_COMMAND for a thumbnail toolbar click.
    public const int ClickedNotification = 0x1800;

    public const string StartIcon = "Thumb-Start.ico";
    public const string StopIcon = "Thumb-Stop.ico";
    public const string OpenIcon = "Thumb-Open.ico";

    // The taskbar keeps the order of the first ThumbBarAddButtons call.
    public static IReadOnlyList<ThumbnailButtonState> For(bool running, bool degraded, bool busy, bool hasSite) =>
    [
        new(ThumbnailButton.StartAll, !busy && !running, "TaskbarThumbStartAll", StartIcon),
        new(ThumbnailButton.StopAll, !busy && (running || degraded), "TaskbarThumbStopAll", StopIcon),
        new(ThumbnailButton.OpenSite, running && hasSite, "TaskbarThumbOpenSite", OpenIcon)
    ];

    public static ThumbnailButton? FromCommand(ulong wParam)
    {
        if (((wParam >> 16) & 0xFFFF) != ClickedNotification) return null;
        var button = (ThumbnailButton)(int)(wParam & 0xFFFF);
        return Enum.IsDefined(button) ? button : null;
    }

    // Only redraw the buttons when something they show changed.
    public static string Signature(IReadOnlyList<ThumbnailButtonState> buttons, string tooltip) =>
        string.Join("|", buttons.Select(button => (int)button.Button + ":" + (button.Enabled ? "1" : "0")))
        + "|" + tooltip;
}
