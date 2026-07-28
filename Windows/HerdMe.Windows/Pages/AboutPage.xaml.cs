using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace HerdMe.Windows.Pages;

public sealed partial class AboutPage : Page
{
    private readonly SiteConfigurationStore settingsStore;
    private readonly AppUpdateManager updateManager;
    private readonly string versionSummary;

    public AboutPage(
        SiteConfigurationStore settingsStore,
        AppUpdateManager updateManager
    )
    {
        this.settingsStore = settingsStore;
        this.updateManager = updateManager;
        InitializeComponent();
        var version = typeof(AboutPage).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
        versionSummary = AppLocalization.Format(
            "AboutVersionSummary",
            $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}",
            Math.Max(version.Revision, 0)
        );
        VersionText.Text = versionSummary;
        UpdateStatusText.Text = AppLocalization.Format(
            "AboutChannelStatus",
            UpdateChannelDisplayName(settingsStore.Load().UpdateChannel)
        );
    }

    private async void Repository_Click(object sender, RoutedEventArgs e)
    {
        await OpenProductLinkAsync(ProductLinks.Repository);
    }

    private async void Documentation_Click(object sender, RoutedEventArgs e)
    {
        await OpenProductLinkAsync(ProductLinks.Documentation);
    }

    private async void ReleaseNotes_Click(object sender, RoutedEventArgs e)
    {
        await OpenProductLinkAsync(ProductLinks.ReleaseNotes);
    }

    private async void CopyVersion_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(versionSummary);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            CopyVersionStatusText.Text = AppLocalization.Get("AboutCopied");
        }
        catch (Exception error)
        {
            await ShowMessageAsync(AppLocalization.Get("AboutCopyFailedTitle"), error.Message);
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.IsActive = true;
        try
        {
            var channel = settingsStore.Load().UpdateChannel;
            UpdateStatusText.Text = AppLocalization.Format(
                "AboutCheckingReleases",
                UpdateChannelDisplayName(channel)
            );
            var result = await updateManager.CheckAsync(channel);
            if (result.AvailableRelease is { } release)
            {
                UpdateStatusText.Text = AppLocalization.Format(
                    "AboutVersionAvailable",
                    release.Version
                );
                await ShowUpdateAsync(release);
            }
            else
            {
                UpdateStatusText.Text = AppLocalization.Format(
                    "AboutUpToDateStatus",
                    result.CurrentVersion
                );
                await ShowMessageAsync(
                    AppLocalization.Get("AboutUpToDateTitle"),
                    AppLocalization.Format(
                        "AboutUpToDateMessage",
                        result.CurrentVersion,
                        UpdateChannelDisplayName(channel)
                    )
                );
            }
        }
        catch (Exception error)
        {
            UpdateStatusText.Text = AppLocalization.Get("AboutUpdateCheckFailed");
            await ShowMessageAsync(
                AppLocalization.Get("AboutUpdateCheckFailed"),
                error.Message
            );
        }
        finally
        {
            UpdateProgress.IsActive = false;
            UpdateProgress.Visibility = Visibility.Collapsed;
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void License_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await ShowDocumentAsync(AppLocalization.Get("AboutLicenseDialogTitle"), "LICENSE");
    }

    private async void Acknowledgements_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await ShowDocumentAsync(
            AppLocalization.Get("AboutAcknowledgementsDialogTitle"),
            "THIRD_PARTY.md"
        );
    }

    private async Task ShowDocumentAsync(string title, string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        var contents = File.Exists(path)
            ? await File.ReadAllTextAsync(path)
            : AppLocalization.Get("AboutDocumentUnavailable");
        var text = new TextBlock
        {
            Text = contents,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontFamily = new FontFamily("Consolas")
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new ScrollViewer
            {
                Content = text,
                MaxHeight = 520,
                MaxWidth = 760
            },
            CloseButtonText = AppLocalization.Get("AboutDone")
        };
        await dialog.ShowAsync();
    }

    private async Task OpenProductLinkAsync(ProductLink link)
    {
        try
        {
            if (!await Launcher.LaunchUriAsync(link.Uri))
            {
                await ShowMessageAsync(
                    AppLocalization.Get("AboutLinkOpenFailed"),
                    AppLocalization.Format("AboutOpenInBrowser", link.Uri.AbsoluteUri)
                );
            }
        }
        catch (Exception error)
        {
            await ShowMessageAsync(AppLocalization.Get("AboutLinkOpenFailed"), error.Message);
        }
    }

    private async Task ShowUpdateAsync(AppUpdateRelease release)
    {
        var downloadAvailable = Uri.TryCreate(
            release.PlatformDownloadUrl,
            UriKind.Absolute,
            out var downloadUri
        ) && downloadUri.Scheme == Uri.UriSchemeHttps;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("AboutUpdateDialogTitle", release.Version),
            Content = release.Notes,
            CloseButtonText = downloadAvailable
                ? AppLocalization.Get("AboutLater")
                : AppLocalization.Get("CommonOk"),
            DefaultButton = downloadAvailable
                ? ContentDialogButton.Primary
                : ContentDialogButton.Close
        };
        if (downloadAvailable)
        {
            dialog.PrimaryButtonText = AppLocalization.Get("AboutDownload");
        }
        if (await dialog.ShowAsync() == ContentDialogResult.Primary
            && downloadUri is not null
            && !await Launcher.LaunchUriAsync(downloadUri))
        {
            await ShowMessageAsync(
                AppLocalization.Get("AboutDownloadOpenFailed"),
                AppLocalization.Format("AboutOpenInBrowser", downloadUri.AbsoluteUri)
            );
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = AppLocalization.Get("CommonOk")
        };
        await dialog.ShowAsync();
    }

    private static string UpdateChannelDisplayName(string channel)
    {
        return AppLocalization.Get(
            channel.Equals("Beta", StringComparison.OrdinalIgnoreCase)
                ? "AboutChannelBeta"
                : "AboutChannelStable"
        );
    }
}
