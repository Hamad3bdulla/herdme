using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace HerdMe.Windows.Views;

// One error dialog for the app: a human title ("A file is in use"), a hint on what to try,
// the original message, and two ways to get help: Copy details and Open diagnostics log.
// Neither button closes the dialog, so the user can do both.
public static class ErrorDialog
{
    public static Task ShowAsync(XamlRoot xamlRoot, Exception error, string? context = null) =>
        ShowCoreAsync(xamlRoot, ErrorPresentation.Classify(error), context ?? error.Message, error.ToString());

    public static Task ShowAsync(XamlRoot xamlRoot, string message, string? details = null) =>
        ShowCoreAsync(xamlRoot, ErrorPresentation.Classify(message), message, details);

    private static async Task ShowCoreAsync(XamlRoot xamlRoot, ErrorKind kind, string message, string? details)
    {
        var title = AppLocalization.Get(ErrorPresentation.TitleKey(kind));
        var supportRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe"
        );
        // The log is what "Open diagnostics log" shows, so the error goes there first.
        _ = DiagnosticLog.WriteFailureAsync("ui", "error-shown", title, details ?? message,
            deduplicationScope: message);

        var status = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextStyle"],
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var body = new StackPanel { Spacing = 10, MaxWidth = 520 };
        body.Children.Add(new TextBlock
        {
            Text = AppLocalization.Get(ErrorPresentation.HintKey(kind)),
            TextWrapping = TextWrapping.Wrap
        });
        var raw = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"]
        };
        AutomationProperties.SetName(raw, AppLocalization.Get("ErrorDialogMessageName"));
        body.Children.Add(new Border
        {
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(4),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            Child = raw
        });
        body.Children.Add(status);

        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = xamlRoot,
            Title = title,
            Content = new ScrollViewer { Content = body, MaxHeight = 420 },
            PrimaryButtonText = AppLocalization.Get("ErrorDialogCopyDetails"),
            SecondaryButtonText = AppLocalization.Get("ErrorDialogOpenLog"),
            CloseButtonText = AppLocalization.Get("CommonOk"),
            DefaultButton = ContentDialogButton.Close
        };
        AutomationProperties.SetAutomationId(dialog, "ErrorDialog");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            try
            {
                var package = new DataPackage();
                package.SetText(ErrorPresentation.Details(title, message, details, Version(), DateTimeOffset.Now));
                Clipboard.SetContent(package);
                Clipboard.Flush();
                status.Text = AppLocalization.Get("ErrorDialogCopied");
            }
            catch (Exception error) when (error is System.Runtime.InteropServices.COMException
                or UnauthorizedAccessException)
            {
                status.Text = error.Message;
            }
        };
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            status.Text = OpenDiagnosticsLog(supportRoot)
                ? AppLocalization.Get("ErrorDialogLogOpened")
                : AppLocalization.Get("ErrorDialogLogMissing");
        };
        try
        {
            await dialog.ShowAsync();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another dialog is already open; the message still reached the diagnostics log.
            App.MainWindow.ShowToast(title);
        }
    }

    // Explorer opens with the log selected (Explorer is one of the allowed shell launches).
    private static bool OpenDiagnosticsLog(string supportRoot)
    {
        try
        {
            var log = ErrorPresentation.DiagnosticsLogPath(supportRoot);
            var folder = Path.GetDirectoryName(log) ?? supportRoot;
            if (!Directory.Exists(folder)) return false;
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            if (File.Exists(log)) startInfo.ArgumentList.Add("/select," + log);
            else startInfo.ArgumentList.Add(folder);
            Process.Start(startInfo);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static string Version() =>
        typeof(ErrorDialog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ErrorDialog).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
