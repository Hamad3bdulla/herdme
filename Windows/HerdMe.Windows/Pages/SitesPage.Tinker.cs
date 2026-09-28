using HerdMe.Windows.Models;
using Microsoft.UI.Xaml;

namespace HerdMe.Windows.Pages;

/// <summary>
/// Tinker lives in the site details as its own tab so a site is one click away from a REPL.
/// The Tinker page is created on first use and always follows the selected site.
/// </summary>
public sealed partial class SitesPage
{
    private TinkerPage? tinker;

    private TinkerPage EnsureTinker()
    {
        if (tinker is not null) return tinker;
        tinker = new TinkerPage(coreClient, settingsStore, phpInstaller, runtimePolicy, composerTools, embedded: true);
        TinkerPanel.Child = tinker;
        return tinker;
    }

    // Used by the "tinker [site]" command, herdme:// and the jump list.
    public void RequestTinker(string? sitePath)
    {
        if (!string.IsNullOrWhiteSpace(sitePath)) RequestSelectSite(sitePath);
        ShowTinkerTab();
    }

    private void ShowTinkerTab()
    {
        if (ReferenceEquals(SiteDetailTabs.SelectedItem, TinkerTab))
        {
            EnsureTinker().ShowSite(selectedSite);
            if (selectedSite is { } site) App.MainWindow.RememberRecentSite(site.Path);
            return;
        }
        SiteDetailTabs.SelectedItem = TinkerTab;
    }

    private void ContextOpenTinker_Click(object sender, RoutedEventArgs e)
    {
        if (SelectSiteFromMenu(sender) is not null) ShowTinkerTab();
    }

    private void ShowTinkerFor(SiteRecord? site) => tinker?.ShowSite(site);
}
