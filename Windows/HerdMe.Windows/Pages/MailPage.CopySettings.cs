using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace HerdMe.Windows.Pages;

// "Copy mail settings" in the Mail header: the same MAIL_* lines "Add to .env" writes, as
// ready-to-paste .env text for a project HerdMe does not manage (or a teammate). Nothing is
// written anywhere; "Add to .env" stays the way to change a site's file.
public sealed partial class MailPage
{
    internal static string MailSettingsText(int port) =>
        ServiceEnvironmentFile.FormatLines(MailEnvironmentConfiguration.Variables(port));

    private async void CopyMailSettings_Click(object sender, RoutedEventArgs e)
    {
        var port = mail.Port ?? MailCaptureService.DefaultPort;
        try
        {
            var package = new DataPackage();
            package.SetText(MailSettingsText(port));
            Clipboard.SetContent(package);
            Clipboard.Flush();
            App.MainWindow.ShowToast(AppLocalization.Format(
                mail.IsRunning ? "MailSettingsCopied" : "MailSettingsCopiedStopped",
                port
            ));
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException
            or InvalidOperationException or ArgumentOutOfRangeException)
        {
            await ShowMessageAsync(AppLocalization.Get("MailSettingsCopyFailedTitle"), error.Message);
        }
    }
}
