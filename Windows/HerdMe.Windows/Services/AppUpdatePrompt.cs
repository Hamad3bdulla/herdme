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
        if (downloadAvailable)
        {
            dialog.PrimaryButtonText = AppLocalization.Get("UpdateDownload");
        }
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || downloadUri is null) return;
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
            XamlRoot = xamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = AppLocalization.Get("CommonOk")
        };
        await dialog.ShowAsync();
    }
}
