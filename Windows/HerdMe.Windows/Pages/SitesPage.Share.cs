using System.ComponentModel;
using System.Diagnostics;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

/// <summary>
/// Public sharing through a Cloudflare quick tunnel. Always an explicit per-site action
/// behind a warning; the share bar keeps the public link and Stop visible while it runs.
/// </summary>
public sealed partial class SitesPage
{
    private bool shareBusy;

    private void Shares_Changed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateShareState();
            // Rows show a share badge and the Shared filter depends on it.
            if (loaded) RefreshWorkflowStatuses();
        });
    }

    private void UpdateShareState()
    {
        var share = selectedSite is { } site ? shares.Find(site.Domain) : null;
        ShareBar.IsOpen = share is not null;
        ShareButton.IsEnabled = !shareBusy && share is null;
        if (share is null) return;
        ShareBar.Message = AppLocalization.Get("SitesShareBarMessage");
        ShareLinkButton.Content = share.PublicUri.AbsoluteUri;
    }

    private async void ShareSite_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site || shareBusy || XamlRoot is null) return;
        var confirm = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesShareConfirmTitle", site.Name),
            Content = new TextBlock
            {
                Text = AppLocalization.Get("SitesShareConfirmMessage"),
                TextWrapping = TextWrapping.WrapWholeWords
            },
            PrimaryButtonText = AppLocalization.Get("SitesShareConfirmButton"),
            CloseButtonText = AppLocalization.Get("SitesCancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        shareBusy = true;
        ShareButton.IsEnabled = false;
        ShareBar.IsOpen = true;
        ShareBar.Severity = InfoBarSeverity.Informational;
        ShareBar.Message = AppLocalization.Get("SitesShareStarting");
        ShareLinkButton.Content = string.Empty;
        try
        {
            if (!environment.IsRunning) await environment.StartAsync(Sites);
            var share = await shares.StartAsync(site.Domain);
            ShareBar.Severity = InfoBarSeverity.Warning;
            if (ReferenceEquals(selectedSite, site)) CopyText(share.PublicUri.AbsoluteUri);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ShareBar.IsOpen = false;
            ShareBar.Severity = InfoBarSeverity.Warning;
            await ShowErrorAsync(AppLocalization.Format("SitesShareFailed", UserErrorPresentation.Describe(error)));
        }
        finally
        {
            shareBusy = false;
            UpdateShareState();
        }
    }

    private async void StopShare_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        await shares.StopAsync(site.Domain);
        UpdateShareState();
    }

    private void CopyShareLink_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is { } site && shares.Find(site.Domain) is { } share)
        {
            CopyText(share.PublicUri.AbsoluteUri);
        }
    }

    private async void OpenShareLink_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site || shares.Find(site.Domain) is not { } share) return;
        try
        {
            Process.Start(new ProcessStartInfo(share.PublicUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            await ShowErrorAsync(error.Message);
        }
    }
}
