using System.Globalization;
using HerdMe.Windows.Services;
using HerdMe.Windows.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

// History (what was updated, from which version to which, when, and how it ended) and the
// "install component updates by themselves" choice.
public sealed partial class UpdatesPage
{
    private const int HistoryPreview = 10;
    private bool showAllHistory;
    private bool loadingAutoInstall;

    private void RenderHistory()
    {
        HistoryRows.Children.Clear();
        var entries = history.Entries;
        var now = DateTimeOffset.Now;
        var shown = showAllHistory ? entries : entries.Take(HistoryPreview).ToList();
        for (var index = 0; index < shown.Count; index++)
        {
            if (index > 0) HistoryRows.Children.Add(Divider());
            HistoryRows.Children.Add(HistoryRow(shown[index], now));
        }
        HistoryEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearHistoryButton.IsEnabled = entries.Count > 0;
        HistoryMoreButton.Visibility = entries.Count > HistoryPreview ? Visibility.Visible : Visibility.Collapsed;
        HistoryMoreButton.Content = showAllHistory
            ? AppLocalization.Get("UpdatesHistoryShowLess")
            : AppLocalization.Format("UpdatesHistoryShowAll", entries.Count);
    }

    private static Grid HistoryRow(UpdateHistoryEntry entry, DateTimeOffset now)
    {
        var (glyph, kind, label) = entry.Outcome switch
        {
            UpdateOutcome.Updated => ("\uE73E", "Success", "UpdatesOutcomeUpdated"),
            UpdateOutcome.Failed => ("\uE783", "Critical", "UpdatesOutcomeFailed"),
            UpdateOutcome.Cancelled => ("\uE711", "Neutral", "UpdatesOutcomeCancelled"),
            UpdateOutcome.RolledBack => ("\uE7A7", "Neutral", "UpdatesOutcomeRolledBack"),
            _ => ("\uE7BA", "Caution", "UpdatesOutcomeRestored")
        };
        var grid = new Grid { Style = S("SettingsRowStyle") };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new FontIcon { Glyph = glyph, VerticalAlignment = VerticalAlignment.Center };
        SetGlyph(icon, glyph, kind);
        grid.Children.Add(icon);

        var text = new StackPanel { Style = S("SettingsRowTextStyle") };
        text.Children.Add(new TextBlock { Text = entry.Name, Style = S("SettingsRowTitleStyle") });
        var parts = new List<string>
        {
            AppLocalization.Format("UpdatesVersionChange", entry.From, entry.To),
            AppLocalization.Get(label),
            RelativeTime(entry.At, now)
        };
        text.Children.Add(new TextBlock
        {
            Text = string.Join("  \u00B7  ", parts),
            TextWrapping = TextWrapping.Wrap,
            Style = S("SettingsRowDescriptionStyle")
        });
        if (!string.IsNullOrWhiteSpace(entry.Error))
        {
            text.Children.Add(new TextBlock
            {
                Text = entry.Error,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Style = S("StatusCriticalTextStyle")
            });
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        AutomationProperties.SetName(grid, entry.Name + ", " + AppLocalization.Get(label));
        return grid;
    }

    private static string RelativeTime(DateTimeOffset time, DateTimeOffset now)
    {
        var age = now - time;
        if (age < TimeSpan.FromMinutes(1)) return AppLocalization.Get("TitleBarBellJustNow");
        if (age < TimeSpan.FromHours(1)) return AppLocalization.Format("TitleBarBellMinutesAgo", (int)age.TotalMinutes);
        if (age < TimeSpan.FromHours(24)) return AppLocalization.Format("TitleBarBellHoursAgo", (int)age.TotalHours);
        return time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (history.Entries.Count == 0) return;
        var confirmed = await DangerStyles.ConfirmAsync(
            XamlRoot,
            AppLocalization.Get("UpdatesHistoryClearTitle"),
            AppLocalization.Get("UpdatesHistoryClearMessage"),
            AppLocalization.Get("UpdatesHistoryClearAction")
        );
        if (!confirmed) return;
        showAllHistory = false;
        history.Clear();
    }

    private void HistoryMore_Click(object sender, RoutedEventArgs e)
    {
        showAllHistory = !showAllHistory;
        RenderHistory();
    }

    private void LoadAutoInstall()
    {
        loadingAutoInstall = true;
        try
        {
            var mode = preferences.Load().AutoInstallMode.ToString();
            AutoInstallCombo.SelectedItem = AutoInstallCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, mode, StringComparison.OrdinalIgnoreCase))
                ?? AutoInstallCombo.Items[0];
        }
        finally
        {
            loadingAutoInstall = false;
        }
    }

    private void AutoInstall_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingAutoInstall || !loaded) return;
        if (AutoInstallCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        if (!Enum.TryParse<AutoInstallMode>(tag, ignoreCase: true, out var mode)) return;
        if (preferences.Load().AutoInstallMode == mode) return;
        preferences.SetAutoInstall(mode);
        Toast(AppLocalization.Get(mode switch
        {
            AutoInstallMode.Idle => "UpdatesAutoInstallIdleToast",
            AutoInstallMode.OnExit => "UpdatesAutoInstallOnExitToast",
            _ => "UpdatesAutoInstallOffToast"
        }));
    }
}
