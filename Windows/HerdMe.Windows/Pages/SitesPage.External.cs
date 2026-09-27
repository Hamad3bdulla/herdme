using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;

namespace HerdMe.Windows.Pages;

// Requests from outside the page: the herdme CLI, Jump List, Explorer "Link with HerdMe",
// herdme:// links, and the parked-folder watcher.
public sealed partial class SitesPage
{
    private string? pendingSelectionPath;
    private bool externalRescanPending;
    private OnboardingNextStep pendingNextStep;

    // From the last onboarding screen. Runs after the first load so the folders are known.
    public void RequestNextStep(OnboardingNextStep step)
    {
        pendingNextStep = step;
        if (loaded && hasLoadedOnce) RunPendingNextStep();
    }

    private void RunPendingNextStep()
    {
        if (!loaded) return;
        var step = pendingNextStep;
        pendingNextStep = OnboardingNextStep.None;
        switch (step)
        {
            case OnboardingNextStep.CreateLaravel:
                CreateLaravel_Click(this, new RoutedEventArgs());
                break;
            case OnboardingNextStep.ParkFolder:
                ParkFolder_Click(this, new RoutedEventArgs());
                break;
        }
    }

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
            siteFilter = SiteListFilterKind.All;
            FilterAllItem.IsChecked = true;
        }
        ApplyFilter(site.Path);
        if (SiteList.SelectedItem is { } selection) SiteList.ScrollIntoView(selection);
    }
}
