namespace HerdMe.Windows.Pages;

// Requests from outside the page: the herdme CLI, Jump List, Explorer "Link with HerdMe",
// herdme:// links, and the parked-folder watcher.
public sealed partial class SitesPage
{
    private string? pendingSelectionPath;
    private bool externalRescanPending;

    public void RequestSelectSite(string sitePath)
    {
        pendingSelectionPath = sitePath;
        if (loaded && Sites.Count > 0) ApplyPendingSelection();
    }

    public void RequestRescan()
    {
        if (loaded && App.IsMainWindowVisible)
        {
            _ = ScanAsync();
            return;
        }
        externalRescanPending = true;
    }

    private bool ConsumeExternalRescan()
    {
        var pending = externalRescanPending;
        externalRescanPending = false;
        return pending;
    }

    private void ApplyPendingSelection()
    {
        if (pendingSelectionPath is not { } path) return;
        var site = Sites.FirstOrDefault(candidate => candidate.Path.Equals(
            path,
            StringComparison.OrdinalIgnoreCase
        ));
        // Keep the request until a scan finds the site (for example right after linking).
        if (site is null) return;
        pendingSelectionPath = null;
        if (!VisibleSites.Any(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            searchDebounce?.Stop();
            SearchBox.Text = string.Empty;
        }
        ApplyFilter(site.Path);
        if (SiteList.SelectedItem is { } selection) SiteList.ScrollIntoView(selection);
    }
}
