using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace HerdMe.Windows.Pages;

// Sites list | splitter | details. The list width follows the splitter and is remembered;
// narrow windows switch to master/detail with a Back button in the details header.
public sealed partial class SitesPage
{
    // Below this width of the split area only one pane is shown.
    internal const double NarrowSplitWidth = 820;
    private bool splitLayoutInitialized;
    private bool narrowLayout;
    private bool narrowShowsDetail;
    private double listWidth = WindowsSiteSettings.SitesListWidthDefault;
    private double dragStartWidth;

    private void InitializeSplitLayout()
    {
        listWidth = SiteConfigurationStore.ClampSitesListWidth(settingsStore.Load().SitesListWidth);
        if (!splitLayoutInitialized)
        {
            splitLayoutInitialized = true;
            SitesSplitter.DragStarted += SitesSplitter_DragStarted;
            SitesSplitter.DragDelta += SitesSplitter_DragDelta;
            SitesSplitter.DragCompleted += SitesSplitter_DragCompleted;
            SitesSplitter.ResetRequested += SitesSplitter_ResetRequested;
            var back = new KeyboardAccelerator
            {
                Key = global::Windows.System.VirtualKey.Left,
                Modifiers = global::Windows.System.VirtualKeyModifiers.Menu
            };
            back.Invoked += (_, args) =>
            {
                if (!narrowLayout || !narrowShowsDetail) return;
                args.Handled = true;
                SetNarrowDetailVisible(false);
            };
            BackToListButton.KeyboardAccelerators.Add(back);
        }
        ApplySplitLayout();
    }

    private void SitesSplitGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < NarrowSplitWidth;
        if (narrow == narrowLayout && !narrow)
        {
            // Keep the details pane usable when the window shrinks.
            ApplySplitLayout();
            return;
        }
        narrowLayout = narrow;
        if (narrow) narrowShowsDetail = selectedSite is not null && narrowShowsDetail;
        ApplySplitLayout();
    }

    private void SitesSplitter_DragStarted(object? sender, EventArgs e)
    {
        dragStartWidth = SitesListColumn.ActualWidth > 0 ? SitesListColumn.ActualWidth : listWidth;
    }

    private void SitesSplitter_DragDelta(object? sender, double delta)
    {
        listWidth = ClampToAvailable(dragStartWidth + delta);
        SitesListColumn.Width = new GridLength(listWidth);
    }

    private void SitesSplitter_DragCompleted(object? sender, EventArgs e)
    {
        settingsStore.UpdateSitesListWidth(listWidth);
    }

    private void SitesSplitter_ResetRequested(object? sender, EventArgs e)
    {
        listWidth = WindowsSiteSettings.SitesListWidthDefault;
        ApplySplitLayout();
        settingsStore.UpdateSitesListWidth(listWidth);
    }

    private void BackToList_Click(object sender, RoutedEventArgs e)
    {
        SetNarrowDetailVisible(false);
    }

    private void SetNarrowDetailVisible(bool showDetail)
    {
        if (narrowShowsDetail == showDetail) return;
        narrowShowsDetail = showDetail;
        if (!narrowLayout) return;
        ApplySplitLayout();
        if (!showDetail) SiteList.Focus(FocusState.Programmatic);
    }

    private void ApplySplitLayout()
    {
        BackToListButton.Visibility = narrowLayout ? Visibility.Visible : Visibility.Collapsed;
        if (!narrowLayout)
        {
            SitesListPane.Visibility = Visibility.Visible;
            SitesDetailPane.Visibility = Visibility.Visible;
            SitesSplitter.Visibility = Visibility.Visible;
            SitesListColumn.MinWidth = WindowsSiteSettings.SitesListWidthMinimum;
            SitesListColumn.Width = new GridLength(ClampToAvailable(listWidth));
            SitesSplitterColumn.Width = new GridLength(12);
            SitesDetailColumn.Width = new GridLength(1, GridUnitType.Star);
            return;
        }
        var detail = narrowShowsDetail && selectedSite is not null;
        SitesSplitter.Visibility = Visibility.Collapsed;
        SitesSplitterColumn.Width = new GridLength(0);
        SitesListColumn.MinWidth = 0;
        SitesListPane.Visibility = detail ? Visibility.Collapsed : Visibility.Visible;
        SitesDetailPane.Visibility = detail ? Visibility.Visible : Visibility.Collapsed;
        SitesListColumn.Width = detail ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        SitesDetailColumn.Width = detail ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    // The details pane keeps at least 420 px; the list stays within its saved bounds.
    private double ClampToAvailable(double width)
    {
        var total = SitesSplitGrid.ActualWidth;
        var maximum = total > 0
            ? Math.Max(WindowsSiteSettings.SitesListWidthMinimum, total - 12 - 420)
            : WindowsSiteSettings.SitesListWidthMaximum;
        return Math.Clamp(
            SiteConfigurationStore.ClampSitesListWidth(width),
            WindowsSiteSettings.SitesListWidthMinimum,
            Math.Min(WindowsSiteSettings.SitesListWidthMaximum, maximum)
        );
    }
}
