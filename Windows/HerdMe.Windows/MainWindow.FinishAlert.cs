using System.Runtime.InteropServices;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace HerdMe.Windows;

// Long installs and downloads (the shared operations list) and project creation report here
// when they end. If HerdMe is minimized or behind another window, its taskbar button flashes
// until it is brought to the front and a notification says whether the work succeeded.
public sealed partial class MainWindow
{
    private const uint FlashTray = 0x00000002;
    private const uint FlashUntilForeground = 0x0000000C;

    private readonly OperationFinishTracker finishTracker = new();

    private void ObserveFinishedOperations()
    {
        var snapshot = RuntimeOperations.Shared.Snapshot().Select(operation => new OperationObservation(
            operation.Id,
            operation.Name,
            operation.Progress.IsActive,
            operation.Progress.Stage switch
            {
                ServiceInstallationStage.Completed => OperationOutcome.Succeeded,
                ServiceInstallationStage.Cancelled => OperationOutcome.Cancelled,
                ServiceInstallationStage.Failed => OperationOutcome.Failed,
                _ => null
            },
            operation.Progress.Error
        ));
        foreach (var finished in finishTracker.Observe(snapshot, DateTimeOffset.UtcNow))
        {
            ReportLongOperationFinished(
                finished,
                NotificationActions.OpenPage("updates", "NotificationActionOpenUpdates")
            );
        }
    }

    internal void ReportLongOperationFinished(FinishedOperation operation, NotificationAction? primary)
    {
        if (shuttingDown || !OperationFinishAlert.ShouldAlert(operation, IsInForeground())) return;
        FlashTaskbarButton();
        ((App)Application.Current).NotifyOperationFinished(OperationFinishAlert.Notification(operation, primary));
    }

    private bool IsInForeground()
    {
        var handle = WindowNative.GetWindowHandle(this);
        return handle != IntPtr.Zero
            && App.IsMainWindowVisible
            && !IsIconic(handle)
            && GetForegroundWindow() == handle;
    }

    // A window hidden to the tray has no taskbar button; the notification is enough there.
    private void FlashTaskbarButton()
    {
        var handle = WindowNative.GetWindowHandle(this);
        if (handle == IntPtr.Zero || !App.IsMainWindowVisible) return;
        var info = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            Window = handle,
            Flags = FlashTray | FlashUntilForeground,
            Count = 0,
            Timeout = 0
        };
        _ = FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
}
