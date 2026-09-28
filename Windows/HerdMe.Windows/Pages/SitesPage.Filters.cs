using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using HerdMe.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace HerdMe.Windows.Pages;

/// <summary>
/// List chrome for Sites: quick filters and sort, the active-filter chip, empty and
/// loading states, row shortcuts (double-click, Enter, context menu) and detail tabs.
/// </summary>
public sealed partial class SitesPage
{
    private SiteListFilterKind siteFilter = SiteListFilterKind.All;
    private SiteListSortKind siteSort = SiteListSortKind.FavoritesFirst;
    private bool sitesScanning;
    private bool sitesScannedOnce;

    private bool IsListNarrowed =>
        siteFilter != SiteListFilterKind.All || SearchBox.Text.Trim().Length > 0;

    private void FilterOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string value }) return;
        siteFilter = SiteListFilter.ParseFilter(value);
        ApplyFilter(selectedSite?.Path);
    }

    private void SortOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string value }) return;
        siteSort = SiteListFilter.ParseSort(value);
        ApplyFilter(selectedSite?.Path);
    }

    private void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        siteFilter = SiteListFilterKind.All;
        FilterAllItem.IsChecked = true;
        SearchBox.Text = string.Empty;
        ApplyFilter(selectedSite?.Path);
    }

    private static string FilterDisplayName(SiteListFilterKind filter) => filter switch
    {
        SiteListFilterKind.Favorites => AppLocalization.Get("SitesFilterNameFavorites"),
        SiteListFilterKind.Laravel => AppLocalization.Get("SitesFilterNameLaravel"),
        SiteListFilterKind.Running => AppLocalization.Get("SitesFilterNameRunning"),
        SiteListFilterKind.Errors => AppLocalization.Get("SitesFilterNameErrors"),
        SiteListFilterKind.Shared => AppLocalization.Get("SitesFilterNameShared"),
        _ => string.Empty
    };

    private void UpdateSiteCount()
    {
        SiteCountText.Text = IsListNarrowed
            ? AppLocalization.Format("SitesCountFiltered", VisibleSites.Count, Sites.Count)
            : AppLocalization.Format("SitesCount", Sites.Count);
    }

    // Chip, count, skeleton and the empty state all follow the current list.
    private void UpdateListChrome()
    {
        if (siteFilter == SiteListFilterKind.All)
        {
            ActiveFilterChip.Visibility = Visibility.Collapsed;
        }
        else
        {
            ActiveFilterText.Text = FilterDisplayName(siteFilter);
            ActiveFilterChip.Visibility = Visibility.Visible;
        }
        UpdateSiteCount();

        var loading = Sites.Count == 0 && sitesScanning && !sitesScannedOnce;
        SitesSkeleton.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        if (loading || VisibleSites.Count > 0)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            return;
        }

        var nothingAtAll = Sites.Count == 0;
        EmptyStateTitle.Text = AppLocalization.Get(nothingAtAll ? "SitesEmptyNoSitesTitle" : "SitesEmptyNoMatchTitle");
        EmptyStateDescription.Text = AppLocalization.Get(
            nothingAtAll ? "SitesEmptyNoSitesDescription" : "SitesEmptyNoMatchDescription"
        );
        EmptyStateActions.Visibility = nothingAtAll ? Visibility.Visible : Visibility.Collapsed;
        EmptyClearFilterButton.Visibility = nothingAtAll ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = Visibility.Visible;
    }

    private void BeginSitesScan()
    {
        sitesScanning = true;
        UpdateListChrome();
    }

    private void EndSitesScan()
    {
        sitesScanning = false;
        sitesScannedOnce = true;
        UpdateListChrome();
    }

    private void SiteList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (selectedSite is null) return;
        e.Handled = true;
        OpenSite_Click(sender, new RoutedEventArgs());
    }

    private void SiteList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Keys typed in the inline rename box stay with the box.
        if (renamingItem is not null) return;
        if (e.Key == global::Windows.System.VirtualKey.F2 && selectedSite is { } renamed)
        {
            e.Handled = true;
            BeginRename(renamed);
            return;
        }
        if (e.Key != global::Windows.System.VirtualKey.Enter || selectedSite is null) return;
        e.Handled = true;
        OpenSite_Click(sender, e);
    }

    // Row menu actions act on the clicked row, so it becomes the selected site first.
    private SiteRecord? SelectSiteFromMenu(object sender)
    {
        if (SiteFromMenu(sender) is not { } site) return null;
        var item = VisibleSites.FirstOrDefault(row => ReferenceEquals(row.Site, site));
        if (item is not null && !ReferenceEquals(SiteList.SelectedItem, item)) SiteList.SelectedItem = item;
        if (!ReferenceEquals(selectedSite, site)) ShowSite(site);
        return site;
    }

    private void ContextOpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (SelectSiteFromMenu(sender) is not null) OpenSite_Click(sender, e);
    }

    private void ContextOpenEditor_Click(object sender, RoutedEventArgs e)
    {
        if (SelectSiteFromMenu(sender) is not null) OpenEditor_Click(sender, e);
    }

    private void ContextOpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (SelectSiteFromMenu(sender) is not null) OpenTerminal_Click(sender, e);
    }

    private void ContextOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectSiteFromMenu(sender) is not null) OpenFolder_Click(sender, e);
    }

    private void ContextOpenLogs_Click(object sender, RoutedEventArgs e)
    {
        if (SelectSiteFromMenu(sender) is not null) OpenLogs_Click(sender, e);
    }

    private async void OpenEditor_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        try
        {
            var path = site.Path;
            var opened = await Task.Run(() => EditorLauncher.OpenFolder(path));
            if (opened) App.MainWindow.RememberRecentSite(path);
            else await ShowErrorAsync(AppLocalization.Get("SitesEditorMissing"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await ShowErrorAsync(error.Message);
        }
    }

    private void SiteDetailTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (OverviewPanel is null || DatabasePanel is null || ToolsPanel is null || DetailsPanel is null
            || TinkerPanel is null) return;
        var tab = sender.SelectedItem?.Tag as string ?? "overview";
        OverviewPanel.Visibility = tab == "overview" ? Visibility.Visible : Visibility.Collapsed;
        DatabasePanel.Visibility = tab == "database" ? Visibility.Visible : Visibility.Collapsed;
        ToolsPanel.Visibility = tab == "tools" ? Visibility.Visible : Visibility.Collapsed;
        DetailsPanel.Visibility = tab == "details" ? Visibility.Visible : Visibility.Collapsed;
        TinkerPanel.Visibility = tab == "tinker" ? Visibility.Visible : Visibility.Collapsed;
        if (tab == "tinker")
        {
            EnsureTinker().ShowSite(selectedSite);
            if (selectedSite is { } site) App.MainWindow.RememberRecentSite(site.Path);
        }
        SiteDetailScroll.ChangeView(null, 0, null, true);
    }

    private void UpdateHeaderTile(SiteRecord site)
    {
        var monogram = FrameworkVisuals.Monogram(site.Framework);
        SiteHeaderTile.ClearValue(Border.BackgroundProperty);
        SiteHeaderTile.Style = FrameworkVisuals.TileStyle(site.Framework);
        SiteHeaderMonogram.Text = monogram;
        SiteHeaderMonogram.Style = FrameworkVisuals.MonogramStyle(site.Framework);
        SiteHeaderMonogram.Visibility = monogram.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SiteHeaderGlobe.Visibility = monogram.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
