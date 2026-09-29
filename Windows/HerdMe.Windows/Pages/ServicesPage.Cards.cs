using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace HerdMe.Windows.Pages;

// Service log tail and address: the details pane shows the newest lines of the selected
// service's log (refreshed every few seconds while the page is open) and, for a running
// service, its address with a copy button.
public sealed partial class ServicesPage
{
    private readonly DispatcherTimer logTailTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool logTailSubscribed;
    private bool readingLogTails;
    private bool logTailRequestedAgain;

    private void StartLogTails()
    {
        if (!logTailSubscribed)
        {
            logTailSubscribed = true;
            logTailTimer.Tick += LogTailTimer_Tick;
        }
        logTailTimer.Start();
    }

    private void StopLogTails() => logTailTimer.Stop();

    private void LogTailTimer_Tick(object? sender, object e) => RequestLogTails();

    // Only the selected service's log is read: its newest lines fill the details pane (and its
    // row keeps the newest line). A selection change during a read triggers one more read.
    private async void RequestLogTails()
    {
        if (!loaded || SelectedRow() is not { } row) return;
        if (readingLogTails)
        {
            logTailRequestedAgain = true;
            return;
        }
        readingLogTails = true;
        try
        {
            var path = manager.LogPath(row.Id);
            var text = await Task.Run(() => ServiceLogTail.LastLines(path));
            if (!loaded) return;
            // A row replaced or deselected during the read is filled on the next tick.
            if (Rows.Contains(row)) row.LastLogLine = ServiceLogTail.FromText(text);
            if (SelectedRow()?.Id == row.Id) ShowDetailLog(text);
        }
        finally
        {
            readingLogTails = false;
        }
        if (logTailRequestedAgain)
        {
            logTailRequestedAgain = false;
            RequestLogTails();
        }
    }

    private async void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstance(sender, out var instance)) return;
        var row = Rows.FirstOrDefault(candidate => candidate.Id == instance.Id);
        if (row is null || !row.CanCopyAddress) return;
        // Databases get the full connection URL (with the local credentials); the rest host:port.
        if (row.CanOpenInTablePlus)
        {
            CopyConnection_Click(sender, e);
            return;
        }
        try
        {
            var package = new DataPackage();
            package.SetText(row.Address);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            App.MainWindow.ShowToast(AppLocalization.Format("ServicesAddressCopied", row.Address));
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            await ShowErrorAsync(error.Message);
        }
    }
}
