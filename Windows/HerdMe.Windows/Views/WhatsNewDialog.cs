using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace HerdMe.Windows.Views;

// "What's new in HerdMe x.y.z" from the CHANGELOG.md bundled next to the executable.
// When the section is missing (or the file is), the dialog still offers the release page.
public static class WhatsNewDialog
{
    public static IReadOnlyList<ChangelogGroup> Load(string version)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, ChangelogReader.FileName);
            return File.Exists(path)
                ? ChangelogReader.Section(File.ReadAllText(path), version)
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static async Task ShowAsync(XamlRoot xamlRoot, string version)
    {
        var groups = Load(version);
        var body = new StackPanel { Spacing = 10, MaxWidth = 520 };
        if (groups.Count == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = AppLocalization.Get("WhatsNewNoNotes"),
                TextWrapping = TextWrapping.Wrap
            });
        }
        foreach (var group in groups)
        {
            if (!string.IsNullOrWhiteSpace(group.Title))
            {
                body.Children.Add(new TextBlock
                {
                    Text = GroupTitle(group.Title),
                    Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
                });
            }
            var list = new StackPanel { Spacing = 6 };
            foreach (var item in group.Items)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new TextBlock { Text = "\u2022" });
                var text = new TextBlock
                {
                    Text = item.Replace("`", string.Empty, StringComparison.Ordinal),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    // Release notes are written in English; keep them left-to-right.
                    FlowDirection = FlowDirection.LeftToRight
                };
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                list.Children.Add(row);
            }
            body.Children.Add(list);
        }
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = AppLocalization.Format("WhatsNewTitle", version),
            Content = new ScrollViewer { Content = body, MaxHeight = 420 },
            PrimaryButtonText = AppLocalization.Get("WhatsNewReleaseNotes"),
            CloseButtonText = AppLocalization.Get("CommonOk"),
            DefaultButton = ContentDialogButton.Close,
            FlowDirection = AppLocalization.LayoutDirection
        };
        dialog.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.AutomationIdProperty, "WhatsNewDialog");
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _ = await Launcher.LaunchUriAsync(ProductLinks.ReleaseNotes.Uri);
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another dialog is open; What's new is still reachable from About.
        }
    }

    private static string GroupTitle(string title) => title.ToLowerInvariant() switch
    {
        "added" => AppLocalization.Get("WhatsNewGroupAdded"),
        "changed" => AppLocalization.Get("WhatsNewGroupChanged"),
        "fixed" => AppLocalization.Get("WhatsNewGroupFixed"),
        "security" => AppLocalization.Get("WhatsNewGroupSecurity"),
        _ => title
    };
}
