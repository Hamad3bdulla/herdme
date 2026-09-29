using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace HerdMe.Windows.Services;

internal static class AppUpdatePrompt
{
    internal static async Task ShowAsync(XamlRoot xamlRoot, AppUpdateRelease release)
    {
        var downloadAvailable = Uri.TryCreate(
            release.PlatformDownloadUrl,
            UriKind.Absolute,
            out var downloadUri
        ) && downloadUri.Scheme == Uri.UriSchemeHttps;
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = xamlRoot,
            Title = AppLocalization.Format("UpdateDialogTitle", release.Version),
            Content = release.Notes,
            CloseButtonText = downloadAvailable
                ? AppLocalization.Get("UpdateLater")
                : AppLocalization.Get("CommonOk"),
            DefaultButton = downloadAvailable
                ? ContentDialogButton.Primary
                : ContentDialogButton.Close
        };
        // The official GitHub setup can be downloaded, checked and installed from the Updates
        // page ("Restart to update"); the browser download stays as the second choice.
        var installInApp = AppSelfUpdater.CanInstall(release);
        if (installInApp)
        {
            dialog.PrimaryButtonText = AppLocalization.Get("UpdateInstallInApp");
            dialog.SecondaryButtonText = AppLocalization.Get("UpdateDownload");
        }
        else if (downloadAvailable)
        {
            dialog.PrimaryButtonText = AppLocalization.Get("UpdateDownload");
        }
        var choice = await dialog.ShowAsync();
        if (installInApp && choice == ContentDialogResult.Primary)
        {
            App.MainWindow.NavigateToPage("updates");
            return;
        }
        var browser = installInApp ? ContentDialogResult.Secondary : ContentDialogResult.Primary;
        if (choice != browser || downloadUri is null) return;
        if (await Launcher.LaunchUriAsync(downloadUri)) return;

        await ShowMessageAsync(
            xamlRoot,
            AppLocalization.Get("UpdateDownloadOpenFailed"),
            AppLocalization.Format("UpdateOpenInBrowser", downloadUri.AbsoluteUri)
        );
    }

    private static async Task ShowMessageAsync(
        XamlRoot xamlRoot,
        string title,
        string message
    )
    {
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = xamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = AppLocalization.Get("CommonOk")
        };
        await dialog.ShowAsync();
    }
}
