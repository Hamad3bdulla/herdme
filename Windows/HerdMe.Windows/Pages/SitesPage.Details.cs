using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using HerdMe.Windows.Views;
using HerdMe.Windows.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace HerdMe.Windows.Pages;

public sealed partial class SitesPage
{
    private async void StartLaravel_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site || !ArtisanButton.IsEnabled) return;

        StartLaravelButton.IsEnabled = false;
        try
        {
            var allSites = Sites.ToArray();
            var development = siteProcesses.State(
                site.Path,
                SiteBackgroundProcessKind.Development
            );
            if (development.Running)
            {
                await siteProcesses.StopAsync(
                    site.Path,
                    SiteBackgroundProcessKind.Development
                );
                return;
            }

            if (!environment.IsRunning) await environment.StartAsync(allSites);
            var cycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
            var php = phpInstaller.PhpExecutable(cycle);
            await runtimePolicy.PrepareLaunchAsync(php, cycle);
            var managedEnvironment = composerTools.ManagedEnvironment(cycle);
            siteProcesses.Start(
                site.Path,
                SiteBackgroundProcessKind.Development,
                php,
                managedEnvironment
            );
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            await ShowErrorAsync(error.Message);
        }
        finally
        {
            StartLaravelButton.IsEnabled = ArtisanButton.IsEnabled;
            UpdateBackgroundProcessState();
        }
    }

    private void UpdatePerformanceDetails(SiteRecord site)
    {
        var snapshot = environment.Performance(site.Domain);
        PerformanceDetailsText.Text = AppLocalization.Format(
            "SitesPerformanceSummary",
            snapshot.RequestCount,
            snapshot.AverageDuration.TotalMilliseconds,
            snapshot.ServerErrorCount
        );
    }

    private async Task RefreshSiteDetailsAsync(SiteRecord site)
    {
        siteDetailsCancellation?.Cancel();
        siteDetailsCancellation?.Dispose();
        using var cancellation = new CancellationTokenSource();
        siteDetailsCancellation = cancellation;
        EnvironmentFileText.Text = AppLocalization.Get("SitesDetailsChecking");
        LogsText.Text = AppLocalization.Get("SitesDetailsChecking");
        RoutesText.Text = AppLocalization.Get("SitesDetailsChecking");
        GitText.Text = AppLocalization.Get("SitesDetailsChecking");
        AssociatedServicesText.Text = AppLocalization.Get("SitesDetailsChecking");
        try
        {
            var detection = await Task.Run(() => detectionCache.Detect(site.Path), cancellation.Token);
            if (cancellation.IsCancellationRequested || !IsSelected(site)) return;
            ApplySiteDetection(site, detection);
            var services = serviceManager.LoadInstances();
            var details = await Task.Run(
                () => InspectSiteDetails(site.Path, services, cancellation.Token),
                cancellation.Token
            );
            if (cancellation.IsCancellationRequested || !IsSelected(site)) return;
            EnvironmentFileText.Text = details.EnvironmentUnreadable
                ? AppLocalization.Get("SitesDetailsUnreadable")
                : AppLocalization.Get(
                    details.EnvironmentExists ? "SitesDetailsPresent" : "SitesDetailsMissing"
                );
            LogsText.Text = details.LogFileCount == 0
                ? AppLocalization.Get("SitesDetailsNoLogs")
                : details.LatestLogName is { } latest
                    ? AppLocalization.Format("SitesDetailsLogsLatest", details.LogFileCount, latest)
                    : AppLocalization.Format("SitesDetailsLogsCount", details.LogFileCount);
            RoutesText.Text = details.RouteFileNames.Count == 0
                ? AppLocalization.Get("SitesDetailsNoRoutes")
                : string.Join(", ", details.RouteFileNames);
            GitText.Text = !details.IsGitRepository
                ? AppLocalization.Get("SitesDetailsNotGit")
                : details.GitChangeCount == 0
                    ? AppLocalization.Format(
                        "SitesDetailsGitClean",
                        details.GitBranch ?? AppLocalization.Get("SitesDetailsDetached")
                    )
                    : AppLocalization.Format(
                        "SitesDetailsGitChanges",
                        details.GitBranch ?? AppLocalization.Get("SitesDetailsDetached"),
                        details.GitChangeCount
                    );
            AssociatedServicesText.Text = details.AssociatedServices.Count == 0
                ? AppLocalization.Get("SitesDetailsNoServices")
                : string.Join(", ", details.AssociatedServices);
            DatabaseDetailsText.Text = details.DatabaseSummary
                ?? AppLocalization.Get("SitesDatabaseNotConfigured");
            site.GitSummary = GitSummary(details.Git);
            detectionCache.StoreGitSummary(site.Path, details.GitStamp, site.GitSummary);
            RefreshListItem(site);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or Win32Exception)
        {
            if (!cancellation.IsCancellationRequested && IsSelected(site))
            {
                GitText.Text = AppLocalization.Get("SitesDetailsUnavailable");
                DatabaseDetailsText.Text = AppLocalization.Get("SitesDetailsUnavailable");
                await DiagnosticLog.WriteFailureAsync(
                    "site-details",
                    "inspect",
                    $"The details for {site.Name} could not be inspected.",
                    error.ToString()
                );
            }
        }
        finally
        {
            if (ReferenceEquals(siteDetailsCancellation, cancellation))
            {
                siteDetailsCancellation = null;
            }
        }
    }

    private sealed record SiteDetailsResult(
        bool EnvironmentExists,
        bool EnvironmentUnreadable,
        int LogFileCount,
        string? LatestLogName,
        IReadOnlyList<string> RouteFileNames,
        bool IsGitRepository,
        string? GitBranch,
        int GitChangeCount,
        IReadOnlyList<string> AssociatedServices,
        string? DatabaseSummary,
        SiteGitStatus Git,
        SitesGitStamp GitStamp
    );

    private static SiteDetailsResult InspectSiteDetails(
        string sitePath,
        IReadOnlyList<ManagedServiceInstance> services,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var environmentPath = Path.Combine(sitePath, ".env");
        var environmentExists = File.Exists(environmentPath);
        var environmentUnreadable = false;
        IReadOnlyDictionary<string, string> environmentValues = new Dictionary<string, string>();
        if (environmentExists)
        {
            try
            {
                var attributes = File.GetAttributes(environmentPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0
                    || new FileInfo(environmentPath).Length > 4 * 1_024 * 1_024)
                {
                    environmentUnreadable = true;
                }
                else
                {
                    environmentValues = ParseEnvironment(File.ReadAllText(environmentPath, new UTF8Encoding(false, true)));
                }
            }
            catch (Exception error) when (error is IOException
                or UnauthorizedAccessException
                or DecoderFallbackException)
            {
                environmentUnreadable = true;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var logsPath = Path.Combine(sitePath, "storage", "logs");
        var logs = Directory.Exists(logsPath)
            ? Directory.EnumerateFiles(logsPath, "*", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray()
            : [];
        var routesPath = Path.Combine(sitePath, "routes");
        var routes = Directory.Exists(routesPath)
            ? Directory.EnumerateFiles(routesPath, "*.php", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Select(name => name!)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        cancellationToken.ThrowIfCancellationRequested();
        var git = SitePresentation.InspectGitAsync(sitePath, cancellationToken)
            .GetAwaiter()
            .GetResult();
        var gitStamp = SitesDetectionCache.CaptureGitStamp(sitePath);
        var associated = services.Where(service => MatchesService(service, environmentValues))
            .Select(service => $"{service.Name} ({service.Port})")
            .ToArray();
        var databaseSummary = TryResolveSiteDatabase(
            environmentValues,
            services,
            out var databaseService,
            out var database,
            out _
        )
            ? $"{databaseService.Name}: {database.DatabaseName} ({database.Username})"
            : null;
        return new SiteDetailsResult(
            environmentExists,
            environmentUnreadable,
            logs.Length,
            logs.FirstOrDefault()?.Name,
            routes,
            git.IsRepository,
            git.Branch,
            git.ChangeCount,
            associated,
            databaseSummary,
            git,
            gitStamp
        );
    }

    private static IReadOnlyDictionary<string, string> ParseEnvironment(string contents)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in contents.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var value = line.TrimStart();
            if (value.Length == 0 || value.StartsWith('#')) continue;
            var separator = value.IndexOf('=');
            if (separator <= 0) continue;
            var key = value[..separator].Trim();
            var item = value[(separator + 1)..].Trim();
            if (item.Length >= 2
                && (item[0] == '"' && item[^1] == '"' || item[0] == '\'' && item[^1] == '\''))
            {
                item = item[1..^1];
            }
            if (key.Length > 0) values[key] = item;
        }
        return values;
    }

    private static string? GitSummary(SiteGitStatus status)
    {
        if (!status.IsRepository) return null;
        var branch = status.Branch ?? AppLocalization.Get("SitesDetailsDetached");
        return status.ChangeCount == 0
            ? AppLocalization.Format("SitesDetailsGitClean", branch)
            : AppLocalization.Format("SitesDetailsGitChanges", branch, status.ChangeCount);
    }

    private static bool MatchesService(
        ManagedServiceInstance service,
        IReadOnlyDictionary<string, string> environment
    )
    {
        var port = service.Port.ToString();
        return service.DefinitionId switch
        {
            "mysql" or "mariadb" => Value("DB_PORT") == port && Value("DB_CONNECTION") == "mysql",
            "postgresql" => Value("DB_PORT") == port && Value("DB_CONNECTION") == "pgsql",
            "mongodb" => Value("MONGODB_URI")?.Contains($":{port}", StringComparison.Ordinal) == true,
            "redis" or "valkey" => Value("REDIS_PORT") == port,
            "meilisearch" => Value("MEILISEARCH_HOST")?.Contains($":{port}", StringComparison.Ordinal) == true,
            "typesense" => Value("TYPESENSE_PORT") == port,
            "minio" or "rustfs" => Value("AWS_ENDPOINT")?.Contains($":{port}", StringComparison.Ordinal) == true,
            _ => false
        };

        string? Value(string key) => environment.TryGetValue(key, out var value) ? value : null;
    }

    private Uri SiteUri(SiteRecord site)
    {
        return SitePresentation.SiteUri(
            site,
            environment.IsRunning,
            environment.HttpPort,
            environment.HttpsPort
        );
    }

    private async Task RefreshPreviewAsync()
    {
        var site = selectedSite;
        if (site is null)
        {
            PreviewBorder.Visibility = Visibility.Collapsed;
            return;
        }
        PreviewBorder.Visibility = PreviewToggle.IsOn
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!PreviewToggle.IsOn) return;
        PreviewFailureState.Visibility = Visibility.Collapsed;
        SitePreview.Visibility = Visibility.Visible;
        try
        {
            await SitePreview.EnsureCoreWebView2Async();
            await ApplyDesktopPreviewMetricsAsync();
            if (!IsSelected(site)) return;
            SitePreview.Source = SiteUri(site);
        }
        catch (Exception error) when (error is InvalidOperationException or COMException)
        {
            await ReportPreviewFailureAsync("initialization", site, error.ToString());
            if (IsSelected(site)) ShowPreviewFailure();
        }
    }

    private async Task ApplyDesktopPreviewMetricsAsync()
    {
        if (SitePreview.CoreWebView2 is null || SitePreview.ActualWidth <= 0) return;
        try
        {
            await SitePreview.CoreWebView2.CallDevToolsProtocolMethodAsync(
                "Emulation.setDeviceMetricsOverride",
                SitePresentation.DesktopPreviewMetricsJson(SitePreview.ActualWidth)
            );
        }
        catch (Exception error) when (error is InvalidOperationException or COMException)
        {
            if (selectedSite is not null)
            {
                await ReportPreviewFailureAsync("desktop metrics", selectedSite, error.ToString());
            }
        }
    }

    private async void SitePreview_NavigationCompleted(
        WebView2 sender,
        CoreWebView2NavigationCompletedEventArgs args
    )
    {
        if (!loaded || IsExpectedNavigationCancellation(args.WebErrorStatus)) return;
        if (!args.IsSuccess)
        {
            if (selectedSite is not null)
            {
                await ReportPreviewFailureAsync(
                    "navigation",
                    selectedSite,
                    $"WebView2 status: {args.WebErrorStatus}"
                );
                ShowPreviewFailure();
            }
            return;
        }
        PreviewFailureState.Visibility = Visibility.Collapsed;
        SitePreview.Visibility = Visibility.Visible;
        await ApplyDesktopPreviewMetricsAsync();
    }

    private static bool IsExpectedNavigationCancellation(CoreWebView2WebErrorStatus status)
    {
        return status is CoreWebView2WebErrorStatus.OperationCanceled
            or CoreWebView2WebErrorStatus.ConnectionAborted;
    }

    private async void SitePreview_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PreviewBorder.Visibility == Visibility.Visible)
        {
            await ApplyDesktopPreviewMetricsAsync();
        }
    }

    private void PreviewBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var height = SitePresentation.DesktopPreviewDisplayHeight(e.NewSize.Width);
        if (Math.Abs(PreviewBorder.Height - height) > 1) PreviewBorder.Height = height;
    }

    private void PreviewToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!suppressPreviewToggle)
        {
            settingsStore.UpdateShowPreviews(PreviewToggle.IsOn);
        }
        _ = RefreshPreviewAsync();
    }

    private void RetryPreview_Click(object sender, RoutedEventArgs e)
    {
        _ = RefreshPreviewAsync();
    }

    private bool IsSelected(SiteRecord site)
    {
        return selectedSite?.Path.Equals(site.Path, StringComparison.OrdinalIgnoreCase) == true;
    }

    private void ShowPreviewFailure()
    {
        SitePreview.Visibility = Visibility.Collapsed;
        PreviewFailureState.Visibility = Visibility.Visible;
    }

    private async Task ReportPreviewFailureAsync(string stage, SiteRecord site, string details)
    {
        await DiagnosticLog.WriteFailureAsync(
            "site-preview",
            stage,
            $"The preview for {site.Name} at {site.Path} failed.",
            details
        );
    }

    private async void OpenSite_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(SiteUri(selectedSite).AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            await ShowErrorAsync(error.Message);
        }
    }

    private void CopySelectedSiteLink_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not null) CopyText(SiteUri(selectedSite).AbsoluteUri);
    }

    private void CopySelectedSitePath_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not null) CopyText(selectedSite.Path);
    }

    private void CopySiteLinkFromMenu_Click(object sender, RoutedEventArgs e)
    {
        if (SiteFromMenu(sender) is { } site) CopyText(SiteUri(site).AbsoluteUri);
    }

    private void CopySitePathFromMenu_Click(object sender, RoutedEventArgs e)
    {
        if (SiteFromMenu(sender) is { } site) CopyText(site.Path);
    }

    private SiteRecord? SiteFromMenu(object sender)
    {
        if (sender is not FrameworkElement { Tag: string path }) return null;
        return Sites.FirstOrDefault(site =>
            site.Path.Equals(path, StringComparison.OrdinalIgnoreCase)
        );
    }

    private static void CopyText(string value)
    {
        var package = new DataPackage();
        package.SetText(value);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        App.MainWindow.ShowToast(AppLocalization.Get("CommonCopiedToast"));
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is null) return;
        try
        {
            var explorer = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true
            };
            explorer.ArgumentList.Add(selectedSite.Path);
            Process.Start(explorer);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            await ShowErrorAsync(error.Message);
        }
    }

    private async void EditEnvironment_Click(object sender, RoutedEventArgs e)
    {
        var site = selectedSite;
        if (site is null) return;

        ProjectEnvironmentDocument document;
        try
        {
            document = await Task.Run(() => ProjectEnvironmentFile.Load(site.Path));
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            await ShowErrorAsync(error.Message);
            return;
        }
        if (!IsSelected(site)) return;

        var pathText = new TextBlock
        {
            Text = Path.Combine(site.Path, ".env"),
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            Style = StatusStyles.Text(StatusTone.Neutral),
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = true
        };
        var statusText = new TextBlock
        {
            Text = EnvironmentDocumentStatus(document),
            TextWrapping = TextWrapping.Wrap,
            Style = StatusStyles.Text(StatusTone.Neutral)
        };
        var editor = new TextBox
        {
            Header = AppLocalization.Get("SitesEnvironmentContentsField"),
            Text = document.Contents,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            IsSpellCheckEnabled = false,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 420
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        var content = new StackPanel { Width = 500, Spacing = 10 };
        content.Children.Add(pathText);
        content.Children.Add(statusText);
        content.Children.Add(editor);

        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesEnvironmentDialogTitle", site.Name),
            Content = content,
            PrimaryButtonText = AppLocalization.Get("SitesSave"),
            CloseButtonText = AppLocalization.Get("SitesClose"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false
        };
        var isDirty = false;
        var discardArmed = false;
        editor.TextChanged += (_, _) =>
        {
            isDirty = !string.Equals(editor.Text, document.Contents, StringComparison.Ordinal);
            discardArmed = false;
            dialog.IsPrimaryButtonEnabled = isDirty;
            statusText.Text = isDirty
                ? AppLocalization.Get("SitesEnvironmentStatusUnsaved")
                : EnvironmentDocumentStatus(document);
            statusText.Style = StatusStyles.Text(StatusTone.Neutral);
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (!isDirty) return;
            var deferral = args.GetDeferral();
            editor.IsEnabled = false;
            dialog.IsPrimaryButtonEnabled = false;
            try
            {
                var editedContents = editor.Text;
                var expectedRevision = document.Revision;
                document = await Task.Run(() => ProjectEnvironmentFile.Save(
                    site.Path,
                    editedContents,
                    expectedRevision
                ));
                isDirty = false;
                discardArmed = false;
                statusText.Text = AppLocalization.Get("SitesEnvironmentStatusSaved");
                statusText.Style = StatusStyles.Text(StatusTone.Success);
            }
            catch (ProjectEnvironmentChangedException)
            {
                statusText.Text = AppLocalization.Get("SitesEnvironmentExternalChange");
                statusText.Style = StatusStyles.Text(StatusTone.Critical);
            }
            catch (Exception error) when (error is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException)
            {
                statusText.Text = error.Message;
                statusText.Style = StatusStyles.Text(StatusTone.Critical);
            }
            finally
            {
                editor.IsEnabled = true;
                dialog.IsPrimaryButtonEnabled = isDirty;
                deferral.Complete();
            }
        };
        dialog.CloseButtonClick += (_, args) =>
        {
            if (!isDirty || discardArmed) return;
            args.Cancel = true;
            discardArmed = true;
            statusText.Text = AppLocalization.Get("SitesEnvironmentConfirmDiscard");
            statusText.Style = StatusStyles.Text(StatusTone.Caution);
        };
        await dialog.ShowAsync();
    }
}
