using System.ComponentModel;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

// Code quality: the project's own Pint (check only), PHPStan, and tests, run with the site's
// HerdMe PHP. Each finding opens in the editor at its line.
public sealed partial class SitesPage
{
    private sealed record QualityIssueRow(QualityIssue Issue, string Label)
    {
        public override string ToString() => Label;
    }

    private async void QualityPint_Click(object sender, RoutedEventArgs e) => await RunQualityToolAsync(QualityTool.Pint);

    private async void QualityPhpStan_Click(object sender, RoutedEventArgs e) => await RunQualityToolAsync(QualityTool.PhpStan);

    private async void QualityTests_Click(object sender, RoutedEventArgs e) => await RunQualityToolAsync(QualityTool.Tests);

    private static string QualityToolName(QualityTool tool) => tool switch
    {
        QualityTool.Pint => AppLocalization.Get("SitesQualityToolPint"),
        QualityTool.PhpStan => AppLocalization.Get("SitesQualityToolPhpStan"),
        _ => AppLocalization.Get("SitesQualityToolTests")
    };

    private async Task RunQualityToolAsync(QualityTool tool)
    {
        if (selectedSite is not { } site) return;
        QualityToolResult? result = null;
        var completed = await RunSiteOperationAsync(
            AppLocalization.Format("SitesQualityRunning", QualityToolName(tool), site.Name),
            async (progress, cancellationToken) =>
            {
                try
                {
                    var cycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
                    var php = phpInstaller.PhpExecutable(cycle);
                    await runtimePolicy.PrepareLaunchAsync(php, cycle, cancellationToken);
                    result = await LaravelQualityTools.RunAsync(
                        tool,
                        php,
                        site.Path,
                        composerTools.ManagedEnvironment(cycle),
                        progress,
                        cancellationToken
                    );
                }
                catch (Exception error) when (error is TimeoutException or Win32Exception)
                {
                    // The operation bar reports these like the other site operations.
                    throw new InvalidOperationException(error.Message, error);
                }
            }
        );
        if (!completed || result is null || !loaded) return;
        if (!result.Passed)
        {
            SiteOperationBar.Severity = InfoBarSeverity.Warning;
            SiteOperationBar.Title = QualitySummary(result);
        }
        await ShowQualityResultAsync(site, result);
    }

    private static string QualitySummary(QualityToolResult result)
    {
        var name = QualityToolName(result.Tool);
        if (result.Passed) return AppLocalization.Format("SitesQualityPassed", name);
        return result.Issues.Count > 0
            ? AppLocalization.Format("SitesQualityIssues", name, result.Issues.Count)
            : AppLocalization.Format("SitesQualityFailedNoIssues", name, result.ExitCode);
    }

    private async Task ShowQualityResultAsync(SiteRecord site, QualityToolResult result)
    {
        var content = new StackPanel { Spacing = 12, Width = 560 };
        content.Children.Add(new TextBlock
        {
            Text = QualitySummary(result),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        });
        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Visibility = Visibility.Collapsed
        };
        if (result.Issues.Count > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = AppLocalization.Get("SitesQualityOpenHint"),
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
            });
            var list = new ListView
            {
                ItemsSource = result.Issues.Select(issue => new QualityIssueRow(issue, QualityIssueLabel(site.Path, issue))).ToList(),
                IsItemClickEnabled = true,
                SelectionMode = ListViewSelectionMode.None,
                MaxHeight = 280,
                FlowDirection = FlowDirection.LeftToRight
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, AppLocalization.Get("SitesQualityIssuesList"));
            list.ItemClick += async (_, args) =>
            {
                if (args.ClickedItem is not QualityIssueRow { Issue.Path.Length: > 0 } row) return;
                try
                {
                    var opened = await Task.Run(() => EditorLauncher.Open(row.Issue.Path, row.Issue.Line));
                    status.Visibility = opened == EditorOpenKind.DefaultApp && row.Issue.Line is not null
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    status.Text = AppLocalization.Format("SitesQualityOpenedWithoutLine", row.Issue.Line ?? 0);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException
                    or Win32Exception or InvalidOperationException or ArgumentException)
                {
                    status.Text = AppLocalization.Format("SitesQualityOpenFailed", UserErrorPresentation.Describe(error));
                    status.Visibility = Visibility.Visible;
                }
            };
            content.Children.Add(list);
        }
        content.Children.Add(status);
        var output = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(result.Output) ? AppLocalization.Get("SitesNoCommandOutput") : result.Output,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            Height = 200,
            FlowDirection = FlowDirection.LeftToRight
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(output, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(output, ScrollBarVisibility.Auto);
        content.Children.Add(new Expander
        {
            Header = AppLocalization.Get("SitesQualityOutput"),
            Content = output,
            // Without findings the raw output is the only explanation, so show it.
            IsExpanded = !result.Passed && result.Issues.Count == 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        });
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesQualityDialogTitle", QualityToolName(result.Tool), site.Name),
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = AppLocalization.Get("SitesDone")
        };
        await dialog.ShowAsync();
    }

    private static string QualityIssueLabel(string sitePath, QualityIssue issue)
    {
        var path = issue.Path.Length == 0 ? string.Empty : RelativeToSite(sitePath, issue.Path);
        var location = issue.Line is { } line ? path + ":" + line : path;
        if (location.Length == 0) return issue.Message;
        return issue.Message.Length == 0 ? location : location + "  " + issue.Message;
    }

    private static string RelativeToSite(string sitePath, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(sitePath, path);
            return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}
