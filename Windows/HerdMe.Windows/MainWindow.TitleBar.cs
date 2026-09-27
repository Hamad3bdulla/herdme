using HerdMe.Windows.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics;

namespace HerdMe.Windows;

// Title bar controls: a search box (pages, sites, Start/Stop all; Ctrl+K) and the
// environment pill as a button with Start all / Stop all / Dashboard. The rest of the strip
// stays a drag region; only these two controls are pass-through.
public sealed partial class MainWindow
{
    private void InitializeTitleBarSearch()
    {
        var focusSearch = new KeyboardAccelerator
        {
            Key = global::Windows.System.VirtualKey.K,
            Modifiers = global::Windows.System.VirtualKeyModifiers.Control
        };
        focusSearch.Invoked += (_, args) =>
        {
            if (TitleBarSearchBox.Visibility != Visibility.Visible) return;
            args.Handled = true;
            TitleBarSearchBox.Focus(FocusState.Keyboard);
        };
        RootLayout.KeyboardAccelerators.Add(focusSearch);
        // Ctrl+K is announced with the box, not as a tooltip on every control.
        RootLayout.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    private void TitleBarInteractive_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleBarPassthrough();

    // Physical-pixel rectangles, relative to the window, for the controls in the title bar.
    private void UpdateTitleBarPassthrough()
    {
        if (shuttingDown || AppTitleBar.XamlRoot is not { } xamlRoot) return;
        var scale = xamlRoot.RasterizationScale;
        if (scale <= 0) return;
        var rects = new List<RectInt32>(3);
        foreach (var element in new FrameworkElement[] { TitleBarSearchBox, TitleBarStatusButton, TitleBarBellButton })
        {
            if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;
            var bounds = element.TransformToVisual(null)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            var left = bounds.X;
            // The window is laid out mirrored in right-to-left languages; the non-client input
            // regions are not, so flip the rectangle back to the physical side.
            if (RootLayout.FlowDirection == FlowDirection.RightToLeft)
            {
                left = RootLayout.ActualWidth - bounds.X - bounds.Width;
            }
            rects.Add(new RectInt32(
                (int)Math.Round(left * scale),
                (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale),
                (int)Math.Round(bounds.Height * scale)
            ));
        }
        try
        {
            InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
                .SetRegionRects(NonClientRegionKind.Passthrough, rects.ToArray());
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            _ = DiagnosticLog.WriteFailureAsync(
                "title-bar",
                "passthrough-failed",
                "The title bar controls could not be made clickable.",
                error.ToString()
            );
        }
    }

    private IEnumerable<TitleBarSearchItem> SearchPages() => Navigation.MenuItems
        .Concat(Navigation.FooterMenuItems)
        .OfType<NavigationViewItem>()
        .Where(item => item.Tag is string && item.Content is string)
        .Select(item => new TitleBarSearchItem(
            TitleBarSearchKind.Page,
            (string)item.Tag,
            (string)item.Content,
            AppLocalization.Get("TitleBarSearchPageDetail")
        ));

    private static IEnumerable<TitleBarSearchItem> SearchSites() => App.KnownSites
        .Where(site => !string.IsNullOrWhiteSpace(site.Path))
        .Select(site => new TitleBarSearchItem(
            TitleBarSearchKind.Site,
            site.Path,
            string.IsNullOrWhiteSpace(site.Domain) ? site.Name : site.Domain,
            site.Path
        ));

    private static IEnumerable<TitleBarSearchItem> SearchActions() =>
    [
        new(TitleBarSearchKind.Action, "start-all", AppLocalization.Get("TitleBarSearchStartAll"), AppLocalization.Get("TitleBarSearchActionDetail")),
        new(TitleBarSearchKind.Action, "stop-all", AppLocalization.Get("TitleBarSearchStopAll"), AppLocalization.Get("TitleBarSearchActionDetail"))
    ];

    private void TitleBarSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var results = TitleBarSearch.Suggest(sender.Text, SearchPages(), SearchSites(), SearchActions());
        sender.ItemsSource = results.Count > 0
            ? results
            : sender.Text.Trim().Length == 0
                ? null
                : new[]
                {
                    new TitleBarSearchItem(TitleBarSearchKind.Page, string.Empty, AppLocalization.Get("TitleBarSearchNoResults"), string.Empty)
                };
    }

    private async void TitleBarSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var item = args.ChosenSuggestion as TitleBarSearchItem
            ?? TitleBarSearch.Suggest(args.QueryText, SearchPages(), SearchSites(), SearchActions(), 1).FirstOrDefault();
        if (item is null || item.Key.Length == 0) return;
        sender.Text = string.Empty;
        sender.ItemsSource = null;
        switch (item.Kind)
        {
            case TitleBarSearchKind.Page:
                NavigateToPage(item.Key);
                break;
            case TitleBarSearchKind.Site:
                NavigateToSite(item.Key);
                break;
            case TitleBarSearchKind.Action when item.Key == "start-all":
                await ((App)Application.Current).StartAllAsync();
                UpdateTitleBarStatus();
                break;
            case TitleBarSearchKind.Action when item.Key == "stop-all":
                await StopAllFromTitleBarAsync();
                break;
        }
    }

    private void TitleBarStatusMenu_Opening(object? sender, object e)
    {
        var environment = services.Environment;
        var running = environment.IsRunning || environment.IsDegraded;
        TitleBarStartAllItem.IsEnabled = !environment.IsRunning;
        TitleBarStopAllItem.IsEnabled = running;
    }

    private async void TitleBarStartAll_Click(object sender, RoutedEventArgs e)
    {
        await ((App)Application.Current).StartAllAsync();
        UpdateTitleBarStatus();
    }

    private async void TitleBarStopAll_Click(object sender, RoutedEventArgs e) => await StopAllFromTitleBarAsync();

    private void TitleBarOpenDashboard_Click(object sender, RoutedEventArgs e) => NavigateToPage("dashboard");

    // Same confirmation as the dashboard's Stop all.
    private async Task StopAllFromTitleBarAsync()
    {
        if (RootLayout.XamlRoot is not { } xamlRoot) return;
        if (!await Views.DangerStyles.ConfirmAsync(
                xamlRoot,
                AppLocalization.Get("DashboardQuickStopConfirmTitle"),
                AppLocalization.Get("DashboardQuickStopConfirmMessage"),
                AppLocalization.Get("DashboardQuickStopAll")))
        {
            return;
        }
        await ((App)Application.Current).StopAllAsync();
        UpdateTitleBarStatus();
    }
}
