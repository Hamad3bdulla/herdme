using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using HerdMe.Windows.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace HerdMe.Windows.Pages;

// Laravel .env settings for a service: copy them as ready-to-paste lines, or preview exactly
// which keys "Add to .env" will add or change in the chosen site before applying.
public sealed partial class ServicesPage
{
    private static readonly FontFamily SnippetFont = new("Consolas");

    private async void CopyEnvironment_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstance(sender, out var instance)) return;
        try
        {
            var variables = manager.EnvironmentVariables(instance);
            if (variables.Count == 0)
            {
                await ShowErrorAsync(AppLocalization.Format("ServicesEnvironmentNoVariables", instance.Name));
                return;
            }
            var package = new DataPackage();
            package.SetText(ServiceEnvironmentFile.FormatLines(variables));
            Clipboard.SetContent(package);
            Clipboard.Flush();
            var secret = variables.Any(variable => EnvironmentEditorModel.IsSecret(variable.Key, variable.Value));
            App.MainWindow.ShowToast(AppLocalization.Format(
                secret ? "ServicesEnvironmentCopiedSecret" : "ServicesEnvironmentCopied",
                variables.Count,
                instance.Name
            ));
        }
        catch (Exception error)
        {
            await ShowErrorAsync(error.Message);
        }
    }

    // Fills panel with the key-level changes applying the service to the site would make.
    // Returns false when the site's .env already matches, so the dialog can say so.
    private static async Task<bool> ShowEnvironmentPreviewAsync(
        StackPanel panel,
        string sitePath,
        IReadOnlyList<ServiceEnvironmentVariable> variables,
        string serviceName
    )
    {
        panel.Children.Clear();
        panel.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left });
        try
        {
            var (changes, creates) = await Task.Run(() =>
            {
                var before = ServiceEnvironmentFile.ReadStartingContents(sitePath, out var exists);
                var after = ServiceEnvironmentFile.Merge(before, variables, serviceName).Contents;
                return (EnvironmentEditorModel.Diff(before, after), !exists);
            });
            panel.Children.Clear();
            panel.Children.Add(new TextBlock
            {
                Text = AppLocalization.Get("ServicesEnvironmentPreviewHeading"),
                Style = StatusStyles.Text(StatusTone.Neutral)
            });
            foreach (var change in changes)
            {
                var (text, tone) = change.Kind switch
                {
                    EnvironmentDiffKind.Added => (AppLocalization.Format("SitesEnvironmentDiffAdded", change.Key, change.After), StatusTone.Success),
                    EnvironmentDiffKind.Removed => (AppLocalization.Format("SitesEnvironmentDiffRemoved", change.Key), StatusTone.Critical),
                    _ => (AppLocalization.Format("SitesEnvironmentDiffChanged", change.Key, change.Before, change.After), StatusTone.Caution)
                };
                panel.Children.Add(new TextBlock
                {
                    Text = text,
                    FontFamily = SnippetFont,
                    FlowDirection = FlowDirection.LeftToRight,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Style = StatusStyles.Text(tone)
                });
            }
            if (changes.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = AppLocalization.Get("ServicesEnvironmentPreviewUpToDate"),
                    TextWrapping = TextWrapping.Wrap,
                    Style = StatusStyles.Text(StatusTone.Success)
                });
            }
            if (creates)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = AppLocalization.Get("SitesEnvironmentDiffCreates"),
                    TextWrapping = TextWrapping.Wrap,
                    Style = StatusStyles.Text(StatusTone.Neutral)
                });
            }
            return changes.Count > 0 || creates;
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            panel.Children.Clear();
            panel.Children.Add(new TextBlock
            {
                Text = error.Message,
                TextWrapping = TextWrapping.Wrap,
                Style = StatusStyles.Text(StatusTone.Critical)
            });
            return false;
        }
    }
}
