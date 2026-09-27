using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HerdMe.Windows.Services;

/// <summary>
/// The buttons under the HerdMe thumbnail on the taskbar (ITaskbarList3.ThumbBarAddButtons).
/// The window is subclassed to receive their WM_COMMAND clicks and the "TaskbarButtonCreated"
/// message, after which Windows needs the buttons added again (Explorer restarted, or the
/// window came back from the tray). Everything runs on the window's UI thread and is best
/// effort: without a taskbar nothing happens.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TaskbarThumbnailToolbar : IDisposable
{
    private const uint WindowCommand = 0x0111;
    private const uint MaskIcon = 0x2;
    private const uint MaskTooltip = 0x4;
    private const uint MaskFlags = 0x8;
    private const uint FlagEnabled = 0x0;
    private const uint FlagDisabled = 0x1;
    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x00000010;
    private const int SmallIconWidth = 49;
    private const int SmallIconHeight = 50;

    private static readonly UIntPtr SubclassId = new(0x4845_5244);

    private readonly IntPtr window;
    private readonly uint taskbarButtonCreated;
    private readonly SubclassProcedure procedure;
    private readonly Dictionary<string, IntPtr> icons = new(StringComparer.Ordinal);
    private IReadOnlyList<(ThumbnailButtonState State, string Tooltip)> desired = [];
    private bool added;
    private bool subclassed;
    private bool disposed;

    public TaskbarThumbnailToolbar(IntPtr window)
    {
        this.window = window;
        taskbarButtonCreated = RegisterWindowMessage("TaskbarButtonCreated");
        // Kept in a field so the delegate outlives every call Windows makes through it.
        procedure = WindowProcedure;
        if (window != IntPtr.Zero) subclassed = SetWindowSubclass(window, procedure, SubclassId, UIntPtr.Zero);
    }

    public event EventHandler<ThumbnailButton>? Clicked;

    public void Update(IReadOnlyList<(ThumbnailButtonState State, string Tooltip)> buttons)
    {
        if (disposed) return;
        desired = buttons;
        Apply();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (subclassed) RemoveWindowSubclass(window, procedure, SubclassId);
        subclassed = false;
        foreach (var icon in icons.Values)
        {
            if (icon != IntPtr.Zero) DestroyIcon(icon);
        }
        icons.Clear();
    }

    private IntPtr WindowProcedure(
        IntPtr handle,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr id,
        UIntPtr data
    )
    {
        if (!disposed)
        {
            if (message == WindowCommand
                && TaskbarThumbnailButtons.FromCommand((ulong)wParam.ToInt64()) is { } button)
            {
                Clicked?.Invoke(this, button);
                return IntPtr.Zero;
            }
            if (taskbarButtonCreated != 0 && message == taskbarButtonCreated)
            {
                // A new taskbar button has no thumbnail buttons yet.
                added = false;
                Apply();
            }
        }
        return DefSubclassProc(handle, message, wParam, lParam);
    }

    private void Apply()
    {
        if (disposed || window == IntPtr.Zero || desired.Count == 0) return;
        object? instance = null;
        var size = Marshal.SizeOf<ThumbButton>();
        var buffer = Marshal.AllocHGlobal(size * desired.Count);
        try
        {
            for (var index = 0; index < desired.Count; index++)
            {
                var (state, tooltip) = desired[index];
                var native = new ThumbButton
                {
                    Mask = MaskIcon | MaskTooltip | MaskFlags,
                    Id = (uint)state.Button,
                    Icon = Icon(state.IconAsset),
                    Tip = tooltip.Length > 259 ? tooltip[..259] : tooltip,
                    Flags = state.Enabled ? FlagEnabled : FlagDisabled
                };
                Marshal.StructureToPtr(native, buffer + (index * size), false);
            }
            instance = new TaskbarOverlay.TaskbarList();
            var list = (TaskbarOverlay.ITaskbarList3)instance;
            if (list.HrInit() < 0) return;
            if (!added)
            {
                // Fails until Windows created the taskbar button; TaskbarButtonCreated retries.
                added = list.ThumbBarAddButtons(window, (uint)desired.Count, buffer) >= 0;
                if (added) return;
            }
            _ = list.ThumbBarUpdateButtons(window, (uint)desired.Count, buffer);
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            // No taskbar (for example a server core session) or the shell is restarting.
        }
        finally
        {
            for (var index = 0; index < desired.Count; index++)
            {
                Marshal.DestroyStructure<ThumbButton>(buffer + (index * size));
            }
            Marshal.FreeHGlobal(buffer);
            if (instance is not null) Marshal.ReleaseComObject(instance);
        }
    }

    // The taskbar draws from our icon handles, so they stay alive until Dispose.
    private IntPtr Icon(string asset)
    {
        if (icons.TryGetValue(asset, out var cached)) return cached;
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", asset);
        var icon = IntPtr.Zero;
        if (File.Exists(path))
        {
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
        }
        icons[asset] = icon;
        return icon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ThumbButton
    {
        public uint Mask;
        public uint Id;
        public uint Bitmap;
        public IntPtr Icon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string Tip;

        public uint Flags;
    }

    private delegate IntPtr SubclassProcedure(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr id,
        UIntPtr data
    );

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr window,
        SubclassProcedure procedure,
        UIntPtr id,
        UIntPtr data
    );

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProcedure procedure, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string name);

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
}
