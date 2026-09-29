using System.ComponentModel;
using System.Diagnostics;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using HerdMe.Windows.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

// Overview / Health tabs, the one-line health strip, quick actions and the session timeline.
public sealed partial class DashboardPage
{
    private const int TimelineVisibleItems = 8;
    private readonly Dictionary<string, SiteRecord> sitesByName = new(StringComparer.OrdinalIgnoreCase);
    private SiteRecord? quickLastSite;
    private StatusTone healthStripTone = StatusTone.Neutral;
    private string healthStripTitle = string.Empty;
    private string healthStripMessage = string.Empty;
    private bool quickPowerBusy;

    private void DashboardTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        ApplyTab(sender.SelectedItem?.Tag as string ?? "overview");
    }

    private void ShowTab(string tag)
    {
        var item = tag switch
        {
            "health" => HealthTab,
            "activity" => ActivityTab,
            _ => OverviewTab
        };
        if (!ReferenceEquals(DashboardTabs.SelectedItem, item)) DashboardTabs.SelectedItem = item;
        ApplyTab(tag);
    }

    private void ApplyTab(string tag)
    {
        var health = tag == "health";
        var activity = tag == "activity";
        HealthPanel.Visibility = health ? Visibility.Visible : Visibility.Collapsed;
        ActivityPanel.Visibility = activity ? Visibility.Visible : Visibility.Collapsed;
        OverviewPanel.Visibility = health || activity ? Visibility.Collapsed : Visibility.Visible;
        DashboardScroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void HealthStrip_Click(object sender, RoutedEventArgs e) => ShowTab("health");

    private void UpdateHealthStrip(StatusTone tone, int issues, bool healthy)
    {
        healthStripTone = tone;
        healthStripTitle = healthy
            ? AppLocalization.Get("DashboardEverythingReady")
            : AppLocalization.Format("DashboardHealthStripIssues", issues);
        healthStripMessage = healthy
            ? string.Empty
            : AppLocalization.Get("DashboardHealthStripHint");
        HealthTab.Text = issues > 0
            ? AppLocalization.Format("DashboardTabHealthCount", issues)
            : AppLocalization.Get("DashboardTabHealthPlain");
        App.MainWindow.SetHealthIssueCount(issues);
        if (activeRepair is null) RestoreHealthStrip();
    }

    private void RestoreHealthStrip()
    {
        ApplyHealthStrip(healthStripTone, healthStripTitle, healthStripMessage);
    }

    private void ShowRepairInHealthStrip(string step)
    {
        ApplyHealthStrip(StatusTone.Neutral, AppLocalization.Get("DashboardHealthStripRepairing"), step);
    }

    private void ApplyHealthStrip(StatusTone tone, string title, string message)
    {
        HealthStripIconTile.Style = StatusStyles.Pill(tone);
        HealthStripGlyph.Style = StatusStyles.Glyph(tone);
        HealthStripGlyph.Glyph = tone switch
        {
            StatusTone.Success => "\uE930",
            StatusTone.Critical => "\uE783",
            StatusTone.Neutral => "\uE946",
            _ => "\uE7BA"
        };
        HealthStripTitle.Text = title;
        HealthStripMessage.Text = message;
        HealthStripMessage.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void RememberSitePaths(IReadOnlyList<SiteRecord> sites)
    {
        sitesByName.Clear();
        foreach (var site in sites) sitesByName.TryAdd(site.Name, site);
    }

    // Site warnings read "name: check - detail". They are grouped by site so a project with
    // five problems is one block with one "Open site" button instead of five rows.
    private void RenderSiteWarningGroups(IReadOnlyList<string> siteWarnings)
    {
        var groups = new List<(SiteRecord? Site, List<string> Lines)>();
        var other = new List<string>();
        foreach (var warning in siteWarnings)
        {
            var separator = warning.IndexOf(':');
            if (separator > 0 && sitesByName.TryGetValue(warning[..separator], out var site))
            {
                var group = groups.FirstOrDefault(item => ReferenceEquals(item.Site, site));
                if (group.Lines is null)
                {
                    group = (site, new List<string>());
                    groups.Add(group);
                }
                group.Lines.Add(warning[(separator + 1)..].Trim());
            }
            else
            {
                other.Add(warning);
            }
        }
        foreach (var (site, lines) in groups)
        {
            var sitePath = site!.Path;
            WarningList.Children.Add(HealthGroupHeader(
                AppLocalization.Format("DashboardHealthGroupSite", site.Name, lines.Count),
                AppLocalization.Get("DashboardHealthOpenSite"),
                (_, _) => App.MainWindow.NavigateToSite(sitePath)
            ));
            foreach (var line in lines) WarningList.Children.Add(HealthIssueRow(line, null));
        }
        if (other.Count == 0) return;
        WarningList.Children.Add(HealthGroupHeader(AppLocalization.Get("DashboardHealthGroupOther"), null, null));
        foreach (var line in other) WarningList.Children.Add(HealthIssueRow(line, null));
    }

    private static UIElement HealthGroupHeader(string title, string? actionLabel, RoutedEventHandler? action)
    {
        var row = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 6, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (action is not null && actionLabel is not null)
        {
            var button = new Button { Content = actionLabel, Style = TextStyle("SubtleButtonStyle") };
            button.Click += action;
            Grid.SetColumn(button, 1);
            row.Children.Add(button);
        }
        return row;
    }

    private void PositionQuickActions(bool compact)
    {
        QuickColumn2.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        QuickColumn3.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(QuickLastSiteButton, compact ? 1 : 0);
        Grid.SetColumn(QuickLastSiteButton, compact ? 0 : 2);
        Grid.SetRow(QuickPowerButton, compact ? 1 : 0);
        Grid.SetColumn(QuickPowerButton, compact ? 1 : 3);
    }

    private void UpdateQuickActions(IReadOnlyList<SiteRecord> sites, WindowsSiteSettings settings)
    {
        // The site opened last this session, otherwise the first favorite.
        var lastPath = App.MainWindow.LastOpenedSitePath;
        quickLastSite = sites.FirstOrDefault(site => lastPath is not null
                && string.Equals(site.Path, lastPath, StringComparison.OrdinalIgnoreCase))
            ?? sites.FirstOrDefault(site => (settings.FavoriteSites ?? []).Contains(site.Path, StringComparer.OrdinalIgnoreCase));
        QuickLastSiteButton.IsEnabled = quickLastSite is not null;
        QuickLastSiteText.Text = quickLastSite is { } last
            ? AppLocalization.Format("DashboardQuickOpenSite", last.Name)
            : AppLocalization.Get("DashboardQuickLastSiteDefault");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(QuickLastSiteButton, QuickLastSiteText.Text);
        UpdateQuickPower();
    }

    private void UpdateQuickPower()
    {
        var running = environment.IsRunning || environment.IsDegraded;
        QuickPowerIcon.Symbol = running ? Symbol.Stop : Symbol.Play;
        QuickPowerText.Text = AppLocalization.Get(running ? "DashboardQuickStopAll" : "DashboardQuickStartAll");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(QuickPowerButton, QuickPowerText.Text);
        QuickPowerButton.Style = running ? DangerStyles.Button : null;
        QuickPowerButton.IsEnabled = !quickPowerBusy;
    }

    private void QuickCreate_Click(object sender, RoutedEventArgs e) =>
        App.MainWindow.StartSitesNextStep(OnboardingNextStep.CreateLaravel);

    private void QuickPark_Click(object sender, RoutedEventArgs e) =>
        App.MainWindow.StartSitesNextStep(OnboardingNextStep.ParkFolder);

    private void QuickLastSite_Click(object sender, RoutedEventArgs e)
    {
        if (quickLastSite is not { } site) return;
        try
        {
            var uri = SitePresentation.SiteUri(site, environment.IsRunning, environment.HttpPort, environment.HttpsPort);
            using var browser = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            App.MainWindow.RememberOpenedSite(site.Path);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            App.MainWindow.ShowToast(error.Message);
        }
    }

    private async void QuickPower_Click(object sender, RoutedEventArgs e)
    {
        if (quickPowerBusy || XamlRoot is null) return;
        var app = (App)Application.Current;
        var running = environment.IsRunning || environment.IsDegraded;
        if (running && !await DangerStyles.ConfirmAsync(
                XamlRoot,
                AppLocalization.Get("DashboardQuickStopConfirmTitle"),
                AppLocalization.Get("DashboardQuickStopConfirmMessage"),
                AppLocalization.Get("DashboardQuickStopAll")))
        {
            return;
        }
        quickPowerBusy = true;
        UpdateQuickPower();
        try
        {
            if (running) await app.StopAllAsync();
            else await app.StartAllAsync();
        }
        finally
        {
            quickPowerBusy = false;
            UpdateQuickPower();
        }
        await RefreshAsync();
    }

    private void MainWindow_TimelineChanged(object? sender, EventArgs e) => RenderTimeline();

    private void RenderTimeline()
    {
        var events = App.MainWindow.Timeline.Snapshot(TimelineVisibleItems);
        TimelineItems.Children.Clear();
        TimelineEmptyText.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in events) TimelineItems.Children.Add(TimelineRow(item));
    }

    private static UIElement TimelineRow(HerdMe.Windows.Models.ActivityEvent item)
    {
        var (glyph, tone) = item.Kind switch
        {
            ActivityEventKind.Mail => ("\uE715", StatusTone.Neutral),
            ActivityEventKind.Dump => ("\uE890", StatusTone.Neutral),
            ActivityEventKind.Repair => ("\uE90F", StatusTone.Success),
            ActivityEventKind.EnvironmentStopped => ("\uE71A", StatusTone.Caution),
            _ => ("\uE783", StatusTone.Critical)
        };
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 14,
            Style = StatusStyles.Glyph(tone),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0)
        });
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock { Text = item.Title, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
        if (!string.IsNullOrWhiteSpace(item.Detail))
        {
            text.Children.Add(new TextBlock
            {
                Text = SingleLine(item.Detail),
                Style = TextStyle("CaptionTextStyle"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            });
        }
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var time = new TextBlock
        {
            Text = item.At.LocalDateTime.ToString("t", System.Globalization.CultureInfo.CurrentCulture),
            Style = TextStyle("CaptionTextStyle"),
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(time, 2);
        row.Children.Add(time);
        if (item.PageTag is not { } tag) return row;
        var button = new Button
        {
            Content = row,
            Style = TextStyle("SubtleButtonStyle"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(6, 4, 6, 4)
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, item.Title);
        button.Click += (_, _) => App.MainWindow.NavigateToPage(tag);
        return button;
    }
}
