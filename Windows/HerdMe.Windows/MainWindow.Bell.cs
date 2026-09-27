using System.Globalization;
using HerdMe.Windows.Services;
using HerdMe.Windows.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HerdMe.Windows;

// The bell in the title bar: recent notifications, including ones Windows never showed
// (notifications off, repeated too fast, or HerdMe in the tray). Clicking one runs its action
// through the same allow-list as a notification button. Closing the list marks all read.
public sealed partial class MainWindow
{
    private void InitializeBell()
    {
        services.NotificationHistory.Changed += NotificationHistory_Changed;
        UpdateBellBadge();
    }

    private void ReleaseBell()
    {
        services.NotificationHistory.Changed -= NotificationHistory_Changed;
    }

    private void NotificationHistory_Changed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (shuttingDown) return;
            UpdateBellBadge();
            if (TitleBarBellFlyout.IsOpen) FillBellList();
        });
    }

    private void UpdateBellBadge()
    {
        if (shuttingDown) return;
        var unread = services.NotificationHistory.UnreadCount;
        TitleBarBellBadge.Value = Math.Min(unread, 99);
        TitleBarBellBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        var name = unread > 0
            ? AppLocalization.Format("TitleBarBellUnread", unread)
            : AppLocalization.Get("TitleBarBellName");
        AutomationProperties.SetName(TitleBarBellButton, name);
        ToolTipService.SetToolTip(TitleBarBellButton, name);
    }

    private void TitleBarBellFlyout_Opening(object? sender, object e) => FillBellList();

    private void TitleBarBellFlyout_Closed(object? sender, object e)
    {
        if (shuttingDown) return;
        services.NotificationHistory.MarkAllRead();
    }

    private void TitleBarBellMarkRead_Click(object sender, RoutedEventArgs e)
    {
        services.NotificationHistory.MarkAllRead();
    }

    private void TitleBarBellClear_Click(object sender, RoutedEventArgs e)
    {
        services.NotificationHistory.Clear();
        TitleBarBellFlyout.Hide();
        ShowToast(AppLocalization.Get("TitleBarBellCleared"));
    }

    private void FillBellList()
    {
        var entries = services.NotificationHistory.Entries;
        TitleBarBellList.Children.Clear();
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in entries) TitleBarBellList.Children.Add(BellEntryButton(entry, now));
        var empty = entries.Count == 0;
        TitleBarBellEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        TitleBarBellScroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        TitleBarBellMarkRead.IsEnabled = entries.Any(entry => !entry.Read);
        TitleBarBellClear.IsEnabled = !empty;
    }

    private Button BellEntryButton(NotificationHistoryEntry entry, DateTimeOffset now)
    {
        var tone = entry.Severity switch
        {
            NotificationSeverity.Error => StatusTone.Critical,
            NotificationSeverity.Warning => StatusTone.Caution,
            _ => StatusTone.Neutral
        };
        var glyph = new FontIcon
        {
            Glyph = entry.Severity switch
            {
                NotificationSeverity.Error => "\uEA39",
                NotificationSeverity.Warning => "\uE7BA",
                _ => "\uE946"
            },
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
            Style = StatusStyles.Glyph(tone)
        };
        var title = new TextBlock
        {
            Text = entry.Title,
            FontWeight = entry.Read ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var message = new TextBlock
        {
            Text = entry.Message,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"]
        };
        var details = new List<string> { RelativeTime(entry.Time, now) };
        if (entry.Count > 1) details.Add(AppLocalization.Format("TitleBarBellRepeated", entry.Count));
        if (!entry.Shown) details.Add(AppLocalization.Get("TitleBarBellNotShown"));
        var caption = new TextBlock
        {
            Text = string.Join(" \u00B7 ", details),
            Style = (Style)Application.Current.Resources["CaptionTextStyle"]
        };
        var texts = new StackPanel { Spacing = 2 };
        texts.Children.Add(title);
        texts.Children.Add(message);
        texts.Children.Add(caption);
        var layout = new Grid { ColumnSpacing = 10 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(glyph);
        Grid.SetColumn(texts, 1);
        layout.Children.Add(texts);
        if (!entry.Read)
        {
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
            };
            Grid.SetColumn(dot, 2);
            layout.Children.Add(dot);
        }
        var button = new Button
        {
            Content = layout,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            IsEnabled = true
        };
        var spoken = string.Join(". ", new[] { entry.Title, entry.Message, caption.Text }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        if (!entry.Read) spoken = AppLocalization.Format("TitleBarBellUnreadEntry", spoken);
        AutomationProperties.SetName(button, spoken);
        var id = entry.Id;
        var action = entry.Action;
        button.Click += async (_, _) =>
        {
            TitleBarBellFlyout.Hide();
            services.NotificationHistory.MarkRead(id);
            await ((App)Application.Current).RunHistoryActionAsync(action);
        };
        return button;
    }

    private static string RelativeTime(DateTimeOffset time, DateTimeOffset now)
    {
        var age = now - time;
        if (age < TimeSpan.FromMinutes(1)) return AppLocalization.Get("TitleBarBellJustNow");
        if (age < TimeSpan.FromHours(1)) return AppLocalization.Format("TitleBarBellMinutesAgo", (int)age.TotalMinutes);
        if (age < TimeSpan.FromHours(24)) return AppLocalization.Format("TitleBarBellHoursAgo", (int)age.TotalHours);
        return time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    }
}
