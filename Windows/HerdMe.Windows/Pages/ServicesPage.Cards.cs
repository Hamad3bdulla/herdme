using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace HerdMe.Windows.Pages;

// Service cards: each card shows the newest line of its log (refreshed every few seconds
// while the page is open) and, for a running service, its address with a copy button.
public sealed partial class ServicesPage
{
    private readonly DispatcherTimer logTailTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool logTailSubscribed;
    private bool readingLogTails;

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

    private async void RequestLogTails()
    {
        if (!loaded || readingLogTails || Rows.Count == 0) return;
        readingLogTails = true;
        try
        {
            var targets = Rows.Select(row => (Row: row, Path: manager.LogPath(row.Id))).ToArray();
            var lines = await Task.Run(() => targets.Select(target => ServiceLogTail.LastLine(target.Path)).ToArray());
            if (!loaded) return;
            for (var index = 0; index < targets.Length; index++)
            {
                // A row replaced during the read is filled on the next tick.
                if (Rows.Contains(targets[index].Row)) targets[index].Row.LastLogLine = lines[index];
            }
        }
        finally
        {
            readingLogTails = false;
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
