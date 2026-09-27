using System.Runtime.InteropServices;

using System.Runtime.Versioning;

namespace HerdMe.Windows.Services;

public enum TaskbarOverlayKind
{
    None,
    Activity,
    Error
}

// The small badge Windows draws over the HerdMe taskbar button: blue for new mail or dumps,
// red after a service or worker stopped unexpectedly. Icons are packaged .ico files; nothing
// is generated at runtime.
internal static class TaskbarOverlay
{
    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x00000010;
    private const int SmallIconWidth = 49;
    private const int SmallIconHeight = 50;

    public static TaskbarOverlayKind Choose(int unseenMail, int unseenDumps, int unseenErrors) =>
        unseenErrors > 0
            ? TaskbarOverlayKind.Error
            : unseenMail + unseenDumps > 0 ? TaskbarOverlayKind.Activity : TaskbarOverlayKind.None;

    public static string? AssetName(TaskbarOverlayKind kind) => kind switch
    {
        TaskbarOverlayKind.Activity => "Overlay-Activity.ico",
        TaskbarOverlayKind.Error => "Overlay-Error.ico",
        _ => null
    };

    // Must run on the window's UI thread. Failures are ignored: the badge is a hint only and
    // Windows may have taskbar badges turned off.
    [SupportedOSPlatform("windows")]
    public static void Apply(IntPtr window, TaskbarOverlayKind kind, string description)
    {
        if (window == IntPtr.Zero) return;
        var icon = IntPtr.Zero;
        object? instance = null;
        try
        {
            if (AssetName(kind) is { } asset)
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", asset);
                if (!File.Exists(path)) return;
                var dpi = GetDpiForWindow(window);
                if (dpi == 0) dpi = 96;
                icon = LoadImage(
                    IntPtr.Zero,
                    path,
                    ImageIcon,
                    GetSystemMetricsForDpi(SmallIconWidth, dpi),
                    GetSystemMetricsForDpi(SmallIconHeight, dpi),
                    LoadFromFile
                );
                if (icon == IntPtr.Zero) return;
            }
            instance = new TaskbarList();
            var list = (ITaskbarList3)instance;
            if (list.HrInit() < 0) return;
            // The taskbar keeps its own copy of the icon, so ours is freed right after.
            list.SetOverlayIcon(window, icon, icon == IntPtr.Zero ? null : description);
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            // No taskbar (for example a server core session) or the shell is restarting.
        }
        finally
        {
            if (icon != IntPtr.Zero) DestroyIcon(icon);
            if (instance is not null) Marshal.ReleaseComObject(instance);
        }
    }

    // Download progress on the taskbar button. Same rules as the overlay: UI thread, best effort.
    [SupportedOSPlatform("windows")]
    public static void ApplyProgress(IntPtr window, TaskbarProgressState state, ulong completed, ulong total)
    {
        if (window == IntPtr.Zero) return;
        object? instance = null;
        try
        {
            instance = new TaskbarList();
            var list = (ITaskbarList3)instance;
            if (list.HrInit() < 0) return;
            list.SetProgressState(window, (int)state);
            if (state == TaskbarProgressState.Normal && total > 0) list.SetProgressValue(window, completed, total);
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
        }
        finally
        {
            if (instance is not null) Marshal.ReleaseComObject(instance);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(
        IntPtr instance,
        string name,
        uint type,
        int width,
        int height,
        uint load
    );

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    // Also used by TaskbarThumbnailToolbar.
    [ComImport]
    [Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
    internal sealed class TaskbarList;

    // ITaskbarList, ITaskbarList2 and ITaskbarList3 methods in vtable order.
    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ITaskbarList3
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr window);
        [PreserveSig] int DeleteTab(IntPtr window);
        [PreserveSig] int ActivateTab(IntPtr window);
        [PreserveSig] int SetActiveAlt(IntPtr window);
        [PreserveSig] int MarkFullscreenWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        [PreserveSig] int SetProgressValue(IntPtr window, ulong completed, ulong total);
        [PreserveSig] int SetProgressState(IntPtr window, int flags);
        [PreserveSig] int RegisterTab(IntPtr tab, IntPtr window);
        [PreserveSig] int UnregisterTab(IntPtr tab);
        [PreserveSig] int SetTabOrder(IntPtr tab, IntPtr insertBefore);
        [PreserveSig] int SetTabActive(IntPtr tab, IntPtr window, uint reserved);
        [PreserveSig] int ThumbBarAddButtons(IntPtr window, uint count, IntPtr buttons);
        [PreserveSig] int ThumbBarUpdateButtons(IntPtr window, uint count, IntPtr buttons);
        [PreserveSig] int ThumbBarSetImageList(IntPtr window, IntPtr imageList);
        [PreserveSig] int SetOverlayIcon(IntPtr window, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
        [PreserveSig] int SetThumbnailTooltip(IntPtr window, [MarshalAs(UnmanagedType.LPWStr)] string? tip);
        [PreserveSig] int SetThumbnailClip(IntPtr window, IntPtr clip);
    }
}
