using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

// Inline per-site PHP picker on each row: the same save path as the runtime dialog,
// without opening it. Only installed cycles are offered, so nothing downloads from here.
public sealed partial class SitesPage
{
    private void RowPhpFlyout_Opening(object? sender, object e)
    {
        if (sender is not MenuFlyout flyout) return;
        flyout.Items.Clear();
        if (flyout.Target is not FrameworkElement { Tag: string path } || SiteFromPath(path) is not { } site) return;
        flyout.Items.Add(PhpChoice(path, null, AppLocalization.Get("SitesRuntimeDefault"), site.PhpVersion is null));
        var cycles = phpInstaller.InstalledCycles().ToArray();
        if (cycles.Length > 0) flyout.Items.Add(new MenuFlyoutSeparator());
        foreach (var cycle in cycles)
        {
            flyout.Items.Add(PhpChoice(path, cycle, $"PHP {cycle}", cycle == site.PhpVersion));
        }
        if (cycles.Length == 0)
        {
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = AppLocalization.Get("SitesRowPhpNoneInstalled"),
                IsEnabled = false
            });
        }
    }

    private MenuFlyoutItem PhpChoice(string path, string? cycle, string text, bool current)
    {
        var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = current };
        item.Click += async (_, _) => await SetRowPhpAsync(path, cycle);
        return item;
    }

    private SiteRecord? SiteFromPath(string path) =>
        Sites.FirstOrDefault(site => site.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    private async Task SetRowPhpAsync(string path, string? cycle)
    {
        if (SiteFromPath(path) is not { } site || site.PhpVersion == cycle) return;
        try
        {
            siteRuntimeStore.SetPhp(path, cycle);
            detectionCache.Invalidate(path);
            await ScanAsync();
            App.MainWindow.ShowToast(AppLocalization.Format(
                "SitesRowPhpChanged",
                site.Domain,
                cycle is null ? AppLocalization.Get("SitesRuntimeDefault") : $"PHP {cycle}"));
        }
        catch (Exception error)
        {
            await ShowErrorAsync(error.Message);
        }
        finally
        {
            UpdateEnvironmentState();
        }
    }
}
