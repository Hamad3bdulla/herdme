using System.Collections.ObjectModel;
using System.Diagnostics;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

public sealed partial class LogsPage : Page
{
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CoreClient coreClient;
    private readonly SiteConfigurationStore siteSettings;
    private string currentContent = string.Empty;
    private string? requestedSitePath;
    private bool loadingSources;

    public ObservableCollection<LogSourceRecord> Sources { get; } = [];
    public ObservableCollection<LogFileRecord> Logs { get; } = [];

    private string ApplicationLogRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HerdMe",
        "Log"
    );

    private LogSourceRecord SelectedSource => SourceBox.SelectedItem as LogSourceRecord
        ?? Sources.First();

    private string LogRoot => SelectedSource.RootPath;

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
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await ReloadSourcesAsync();
        Reload(force: true);
        refreshTimer.Start();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        refreshTimer.Stop();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        Reload(force: true);
    }

    private void RefreshTimer_Tick(object? sender, object e)
    {
        if (LiveRefreshToggle.IsOn) Reload(force: false);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplySearch();
    }

    private void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingSources || SourceBox.SelectedItem is null) return;
        currentContent = string.Empty;
        Logs.Clear();
        Reload(force: true);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var source = SelectedSource;
        if (source.IsApplication) Directory.CreateDirectory(source.RootPath);
        var directory = Directory.Exists(source.RootPath) ? source.RootPath : source.FallbackPath;
        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        startInfo.ArgumentList.Add(directory);
        Process.Start(startInfo);
    }

    private void LogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogList.SelectedItem is not LogFileRecord log)
        {
            LogTitleText.Text = AppLocalization.Get("LogsSelectLog");
            currentContent = string.Empty;
            LogContentText.Text = string.Empty;
            return;
        }
        LogTitleText.Text = log.Name;
        LoadSelectedContent(log);
    }

    private void LoadSelectedContent(LogFileRecord log)
    {
        try
        {
            const int maximumBytes = 4 * 1_024 * 1_024;
            using var stream = new FileStream(log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > maximumBytes) stream.Seek(-maximumBytes, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            currentContent = reader.ReadToEnd();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            currentContent = error.Message;
        }
        ApplySearch();
    }

    private void ApplySearch()
    {
        LogContentText.Text = LogPresentation.FilterLines(currentContent, SearchBox.Text);
    }

    private void Reload(bool force)
    {
        if (SelectedSource.IsApplication) Directory.CreateDirectory(LogRoot);
        var selectedPath = (LogList.SelectedItem as LogFileRecord)?.Path;
        IReadOnlyList<string> paths;
        try
        {
            paths = Directory.Exists(LogRoot)
                ? Directory.EnumerateFiles(LogRoot, "*", SearchOption.AllDirectories).ToList()
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            paths = [];
            SourceWarning.IsOpen = true;
            _ = DiagnosticLog.WriteFailureAsync(
                "logs",
                "enumerate-files",
                $"The log directory {LogRoot} could not be enumerated.",
                error.ToString()
            );
        }
        var discovered = paths
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
        {
            var info = new FileInfo(path);
            return new LogFileRecord
            {
                Name = Path.GetRelativePath(LogRoot, path),
                Path = path,
                Size = info.Length,
                ModifiedAt = info.LastWriteTimeUtc
            };
        }).ToList();
        var changed = force
            || discovered.Count != Logs.Count
            || discovered.Where((record, index) => !SameRecord(record, Logs[index])).Any();
        if (!changed) return;

        Logs.Clear();
        foreach (var record in discovered) Logs.Add(record);
        LogList.SelectedItem = Logs.FirstOrDefault(log => log.Path.Equals(
            selectedPath,
            StringComparison.OrdinalIgnoreCase
        )) ?? Logs.FirstOrDefault();
        if (LogList.SelectedItem is LogFileRecord selected)
        {
            LoadSelectedContent(selected);
        }
        else
        {
            LogTitleText.Text = AppLocalization.Get("LogsSelectLog");
            currentContent = string.Empty;
            ApplySearch();
        }
    }

    private static bool SameRecord(LogFileRecord left, LogFileRecord right)
    {
        return left.Path.Equals(right.Path, StringComparison.OrdinalIgnoreCase)
            && left.Size == right.Size
            && left.ModifiedAt == right.ModifiedAt;
    }

    private async Task ReloadSourcesAsync()
    {
        loadingSources = true;
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
            var sites = await coreClient.ScanAsync(settings.Roots, settings.Tld, settings.LinkedSites);
            foreach (var site in sites.Where(site =>
                         site.Framework.Equals("Laravel", StringComparison.OrdinalIgnoreCase)))
            {
                AddSiteSource(site.Name, site.Path);
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            SourceWarning.IsOpen = true;
            await DiagnosticLog.WriteFailureAsync(
                "logs",
                "discover-sites",
                "Laravel log sources could not be discovered.",
                error.ToString()
            );
        }

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
