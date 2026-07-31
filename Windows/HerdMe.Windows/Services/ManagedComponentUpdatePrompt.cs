using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Services;

internal static class ManagedComponentUpdatePrompt
{
    internal static async Task<string?> ShowAsync(
        XamlRoot xamlRoot,
        ManagedComponentUpdateCheck result
    )
    {
        if (result.Updates.Count == 0) return null;

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = AppLocalization.Format(
                "ManagedUpdatesDialogMessage",
                result.Updates.Count
            ),
            TextWrapping = TextWrapping.Wrap
        });
        foreach (var update in result.Updates.Take(10))
        {
            content.Children.Add(new TextBlock
            {
                Text = AppLocalization.Format(
                    "ManagedUpdateVersionLine",
                    update.Name,
                    update.InstalledVersion,
                    update.LatestVersion
                ),
                TextWrapping = TextWrapping.Wrap
            });
        }
        if (result.Updates.Count > 10)
        {
            content.Children.Add(new TextBlock
            {
                Text = AppLocalization.Format(
                    "ManagedUpdatesMore",
                    result.Updates.Count - 10
                ),
                TextWrapping = TextWrapping.Wrap
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = AppLocalization.Get("ManagedUpdatesDialogTitle"),
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = content
            },
            PrimaryButtonText = AppLocalization.Get("ManagedUpdatesView"),
            CloseButtonText = AppLocalization.Get("UpdateLater"),
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? result.Updates[0].PageTag
            : null;
    }
}
