using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

public sealed partial class LogsPage : Page
{
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly CoreClient coreClient;
    private readonly SiteConfigurationStore siteSettings;
    private string currentContent = string.Empty;
    private readonly string? requestedSitePath;
    private bool loadingSources;
    private bool pageActive;
    private bool updatingLogList;
    private bool sourceDiscoveryFailed;
    private CancellationTokenSource? pageCancellation;
    private CancellationTokenSource? reloadCancellation;
    private CancellationTokenSource? contentCancellation;
    private CancellationTokenSource? searchCancellation;

    public ObservableCollection<LogSourceRecord> Sources { get; } = [];
    public ObservableCollection<LogFileRecord> Logs { get; } = [];

    private string ApplicationLogRoot => Path.Combine(siteSettings.SupportRoot, "Log");

    private LogSourceRecord? SelectedSource => SourceBox.SelectedItem as LogSourceRecord;

    public LogsPage(
        CoreClient coreClient,
        SiteConfigurationStore siteSettings,
        string? requestedSitePath = null
    )
    {
        this.coreClient = coreClient;
        this.siteSettings = siteSettings;
        this.requestedSitePath = requestedSitePath;
        InitializeComponent();
        refreshTimer.Tick += RefreshTimer_Tick;
        searchTimer.Tick += SearchTimer_Tick;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        pageCancellation?.Cancel();
        pageCancellation?.Dispose();
        pageCancellation = new CancellationTokenSource();
        var cancellationToken = pageCancellation.Token;
        pageActive = true;
        ClearContent();
        try
        {
            await ReloadSourcesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await ReloadAsync(force: true);
            cancellationToken.ThrowIfCancellationRequested();
            refreshTimer.Start();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        refreshTimer.Stop();
        searchTimer.Stop();
        pageActive = false;
        pageCancellation?.Cancel();
        pageCancellation?.Dispose();
        pageCancellation = null;
        reloadCancellation?.Cancel();
        contentCancellation?.Cancel();
        searchCancellation?.Cancel();
        UpdateReadProgress();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await ReloadAsync(force: true);
    }

    private async void RefreshTimer_Tick(object? sender, object e)
    {
        if (LiveRefreshToggle.IsOn) await ReloadAsync(force: false);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!pageActive) return;
        searchCancellation?.Cancel();
        searchTimer.Stop();
        searchTimer.Start();
    }

    private async void SearchTimer_Tick(object? sender, object e)
    {
        searchTimer.Stop();
        await ApplySearchAsync();
    }

    private async void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!pageActive || loadingSources || SourceBox.SelectedItem is null) return;
        reloadCancellation?.Cancel();
        ClearContent();
        Logs.Clear();
        await ReloadAsync(force: true);
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var source = SelectedSource;
        if (source is null) return;
        try
        {
            if (source.IsApplication) Directory.CreateDirectory(source.RootPath);
            var directory = Directory.Exists(source.RootPath) ? source.RootPath : source.FallbackPath;
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(directory);
            Process.Start(startInfo);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or Win32Exception or InvalidOperationException)
        {
            SourceWarning.IsOpen = true;
            await DiagnosticLog.WriteFailureAsync("logs", "open-folder",
                "The log directory could not be opened.", error.ToString());
        }
    }

    private async void LogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!pageActive || updatingLogList) return;
        ClearContent();
        if (LogList.SelectedItem is not LogFileRecord log)
        {
            return;
        }
        LogTitleText.Text = log.Name;
        await LoadSelectedContentAsync(log);
    }

    private async Task LoadSelectedContentAsync(LogFileRecord log)
    {
        contentCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        contentCancellation = cancellation;
        UpdateReadProgress();
        try
        {
            var result = await Task.Run(() => LogFileReader.ReadTailAsync(log.Path, cancellation.Token),
                cancellation.Token);
            if (!pageActive || cancellation.IsCancellationRequested
                || !ReferenceEquals(LogList.SelectedItem, log)) return;
            currentContent = result.Text;
            TailNoticeText.Visibility = result.Truncated ? Visibility.Visible : Visibility.Collapsed;
            await ApplySearchAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!pageActive || cancellation.IsCancellationRequested
                || !ReferenceEquals(LogList.SelectedItem, log)) return;
            TailNoticeText.Visibility = Visibility.Collapsed;
            currentContent = string.Empty;
            searchCancellation?.Cancel();
            LogContentText.Text = UserErrorPresentation.Describe(error);
        }
        finally
        {
            if (ReferenceEquals(contentCancellation, cancellation))
            {
                contentCancellation = null;
                UpdateReadProgress();
            }
        }
    }

    private async Task ApplySearchAsync()
    {
        if (!pageActive) return;
        searchTimer.Stop();
        searchCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        searchCancellation = cancellation;
        var text = currentContent;
        var query = SearchBox.Text;
        try
        {
            var filtered = await Task.Run(() => LogPresentation.FilterLines(text, query, cancellation.Token),
                cancellation.Token);
            if (pageActive && !cancellation.IsCancellationRequested) LogContentText.Text = filtered;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(searchCancellation, cancellation)) searchCancellation = null;
        }
    }

    private void ClearContent()
    {
        contentCancellation?.Cancel();
        searchCancellation?.Cancel();
        searchTimer.Stop();
        currentContent = string.Empty;
        LogContentText.Text = string.Empty;
        LogTitleText.Text = AppLocalization.Get("LogsSelectLog");
        TailNoticeText.Visibility = Visibility.Collapsed;
    }

    private void UpdateReadProgress()
    {
        LogReadProgress.IsActive = pageActive && (reloadCancellation is not null || contentCancellation is not null);
    }

    private async Task ReloadAsync(bool force)
    {
        if (!pageActive || loadingSources || SelectedSource is not { } source) return;
        // Timer ticks skip an in-flight read; manual refresh supersedes it.
        if (!force && (reloadCancellation is not null || contentCancellation is not null)) return;
        reloadCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        reloadCancellation = cancellation;
        var cancellationToken = cancellation.Token;
        UpdateReadProgress();
        try
        {
            var discovered = await Task.Run(() => LogFileReader.Discover(source.RootPath, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!pageActive || !ReferenceEquals(SelectedSource, source)) return;
            SourceWarning.IsOpen = sourceDiscoveryFailed;
            EmptyLogsText.Visibility = discovered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var previous = LogList.SelectedItem as LogFileRecord;
            var wasReading = contentCancellation is not null;
            var changed = force
                || discovered.Count != Logs.Count
                || discovered.Where((record, index) => !SameRecord(record, Logs[index])).Any();
            if (!changed) return;

            updatingLogList = true;
            try
            {
                Logs.Clear();
                foreach (var record in discovered) Logs.Add(record);
                LogList.SelectedItem = Logs.FirstOrDefault(log => log.Path.Equals(
                    previous?.Path,
                    StringComparison.OrdinalIgnoreCase
                )) ?? Logs.FirstOrDefault();
            }
            finally { updatingLogList = false; }
            if (LogList.SelectedItem is LogFileRecord selected)
            {
                if (previous?.Path != selected.Path) ClearContent();
                LogTitleText.Text = selected.Name;
                if (force || wasReading || previous is null || !SameRecord(previous, selected))
                    await LoadSelectedContentAsync(selected);
            }
            else
            {
                ClearContent();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!pageActive || cancellationToken.IsCancellationRequested
                || !ReferenceEquals(SelectedSource, source)) return;
            SourceWarning.IsOpen = true;
            _ = DiagnosticLog.WriteFailureAsync(
                "logs",
                "enumerate-files",
                $"The log directory {source.RootPath} could not be enumerated.",
                error.ToString()
            );
        }
        finally
        {
            if (ReferenceEquals(reloadCancellation, cancellation))
            {
                reloadCancellation = null;
                UpdateReadProgress();
            }
        }
    }

    private static bool SameRecord(LogFileRecord left, LogFileRecord right)
    {
        return left.Path.Equals(right.Path, StringComparison.OrdinalIgnoreCase)
            && left.Size == right.Size
            && left.ModifiedAt == right.ModifiedAt;
    }

    private async Task ReloadSourcesAsync(CancellationToken cancellationToken)
    {
        loadingSources = true;
        sourceDiscoveryFailed = false;
        SourceWarning.IsOpen = false;
        var preferredRoot = requestedSitePath is null
            ? ApplicationLogRoot
            : LogPresentation.SiteLogRoot(requestedSitePath);
        Sources.Clear();
        Sources.Add(new LogSourceRecord
        {
            Id = "application",
            Name = "HerdMe",
            RootPath = ApplicationLogRoot,
            FallbackPath = ApplicationLogRoot,
            IsApplication = true
        });
        if (requestedSitePath is not null)
        {
            AddSiteSource(
                Path.GetFileName(Path.TrimEndingDirectorySeparator(requestedSitePath)),
                requestedSitePath
            );
        }

        try
        {
            var settings = siteSettings.Load();
            var sites = await coreClient.ScanAsync(settings.Roots, settings.Tld, settings.LinkedSites, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var site in sites.Where(site =>
                         site.Framework.Equals("Laravel", StringComparison.OrdinalIgnoreCase)))
            {
                AddSiteSource(site.Name, site.Path);
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException
            or UnauthorizedAccessException or TimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourceDiscoveryFailed = true;
            SourceWarning.IsOpen = true;
            await DiagnosticLog.WriteFailureAsync(
                "logs",
                "discover-sites",
                "Laravel log sources could not be discovered.",
                error.ToString()
            );
        }

        cancellationToken.ThrowIfCancellationRequested();
        SourceBox.SelectedItem = Sources.FirstOrDefault(source => source.RootPath.Equals(
            preferredRoot,
            StringComparison.OrdinalIgnoreCase
        )) ?? Sources.First();
        loadingSources = false;
    }

    private void AddSiteSource(string name, string sitePath)
    {
        var root = LogPresentation.SiteLogRoot(sitePath);
        if (Sources.Any(source => source.RootPath.Equals(root, StringComparison.OrdinalIgnoreCase))) return;
        Sources.Add(new LogSourceRecord
        {
            Id = sitePath,
            Name = string.IsNullOrWhiteSpace(name) ? sitePath : name,
            RootPath = root,
            FallbackPath = sitePath,
            IsApplication = false
        });
    }
}
