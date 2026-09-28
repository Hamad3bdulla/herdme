using System.Collections.Concurrent;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using HerdMe.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

/// <summary>
/// Warning badge on each site row: the PHP version is not installed, .env or APP_KEY is
/// missing, or Composer dependencies were never installed. The badge flyout lists each
/// problem with its fix; Site Doctor stays the full check.
/// </summary>
public sealed partial class SitesPage
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<SiteWarning>> siteWarnings =
        new(StringComparer.OrdinalIgnoreCase);

    // Runs off the UI thread, after each scan, next to the detection cache.
    private void InspectSiteWarnings(IReadOnlyList<SiteRecord> sites)
    {
        string defaultCycle;
        IReadOnlyList<string> installed;
        try
        {
            defaultCycle = runtimePolicy.Load().PhpCycle;
            installed = phpInstaller.InstalledCycles();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            return;
        }
        foreach (var site in sites)
        {
            siteWarnings[site.Path] = SiteWarnings.Inspect(site, defaultCycle, installed);
        }
    }

    private IReadOnlyList<SiteWarning> WarningsFor(string path) =>
        siteWarnings.TryGetValue(path, out var warnings) ? warnings : [];

    private async Task RefreshSiteWarningsAsync(string path)
    {
        if (SiteFromPath(path) is not { } site) return;
        await Task.Run(() => InspectSiteWarnings([site]));
        if (loaded) ApplyFilter(selectedSite?.Path);
    }

    private void RowWarningsFlyout_Opening(object? sender, object e)
    {
        if (sender is not MenuFlyout flyout) return;
        flyout.Items.Clear();
        if (flyout.Target is not FrameworkElement { Tag: string path } || SiteFromPath(path) is not { } site) return;
        foreach (var warning in WarningsFor(path))
        {
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = SiteWarningText.Describe(warning),
                IsEnabled = false
            });
            var fix = new MenuFlyoutItem
            {
                Text = SiteWarningText.FixLabel(warning),
                Tag = path,
                Icon = new SymbolIcon(Symbol.Repair)
            };
            fix.Click += async (item, _) =>
            {
                if (SelectSiteFromMenu(item) is not null) await FixSiteWarningAsync(site, warning);
            };
            flyout.Items.Add(fix);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        var doctor = new MenuFlyoutItem
        {
            Text = AppLocalization.Get("SitesWarningOpenDoctor"),
            Tag = path,
            Icon = new SymbolIcon(Symbol.Help)
        };
        doctor.Click += (item, args) =>
        {
            if (SelectSiteFromMenu(item) is not null) SiteDoctor_Click(item, args);
        };
        flyout.Items.Add(doctor);
    }

    private async Task FixSiteWarningAsync(SiteRecord site, SiteWarning warning)
    {
        if (warning.Kind == SiteWarningKind.PhpNotInstalled)
        {
            var cycle = warning.Detail;
            await RunSiteOperationAsync(
                AppLocalization.Format("SitesWarningInstallingPhp", cycle),
                async (_, cancellationToken) => await phpInstaller.InstallAsync(cycle, cancellationToken)
            );
            await RefreshSiteWarningsAsync(site.Path);
            UpdateEnvironmentState();
            return;
        }
        await RepairSiteAsync(site, site.PhpVersion ?? runtimePolicy.Load().PhpCycle);
    }
}
