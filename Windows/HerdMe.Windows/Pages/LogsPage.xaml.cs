using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

public sealed partial class LogsPage : Page
{
    // The text control stays responsive with a bounded amount of text; search still covers the
    // cached tail of the file (up to LogFileReader.MaximumBytes).
    private const int DisplayCharacterLimit = 256 * 1_024;
    private const int ContentCharacterLimit = LogFileReader.MaximumBytes;
    private const double BottomTolerance = 24;
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly CoreClient coreClient;
    private readonly SiteConfigurationStore siteSettings;
    private string currentContent = string.Empty;
    private string displayedText = string.Empty;
    private LogTailState? tailState;
    private bool contentTruncated;
    private bool displayTrimmed;
    private string? requestedSitePath;
    private string? pendingSitePath;
    private bool loadingSources;
    private bool pageActive;
    private bool updatingLogList;
    private bool sourceDiscoveryFailed;
    private CancellationTokenSource? pageCancellation;
    private CancellationTokenSource? reloadCancellation;
    private CancellationTokenSource? contentCancellation;
    private CancellationTokenSource? searchCancellation;
    private SourceLocation? lastErrorLocation;
    private LaravelLogSummary? laravelSummary;

    private sealed record LevelOption(string Label, LaravelLogLevel? Level)
    {
        public override string ToString() => Label;
    }

    public ObservableCollection<LogSourceRecord> Sources { get; } = [];
    public ObservableCollection<LogFileRecord> Logs { get; } = [];

    private string ApplicationLogRoot => Path.Combine(siteSettings.SupportRoot, "Log");

    private LogSourceRecord? SelectedSource => SourceBox.SelectedItem as LogSourceRecord;

    // Laravel sources get the level filter, counts, and Open last error.
    private bool IsLaravelSource => SelectedSource is { IsApplication: false };

    private LaravelLogLevel? SelectedLevel => IsLaravelSource && LevelBox.SelectedItem is LevelOption option
        ? option.Level
        : null;

    public LogsPage(
        CoreClient coreClient,
        SiteConfigurationStore siteSettings,
        string? requestedSitePath = null
    )
    {
        this.coreClient = coreClient;
        this.siteSettings = siteSettings;
        pendingSitePath = requestedSitePath;
        InitializeComponent();
        TailNoticeText.Text = AppLocalization.Get("LogsTailNoticeLimited");
        LevelBox.ItemsSource = new[]
        {
            new LevelOption(AppLocalization.Get("LogsLevelAll"), null),
            new LevelOption(AppLocalization.Get("LogsLevelInfo"), LaravelLogLevel.Info),
            new LevelOption(AppLocalization.Get("LogsLevelWarning"), LaravelLogLevel.Warning),
            new LevelOption(AppLocalization.Get("LogsLevelError"), LaravelLogLevel.Error)
        };
        LevelBox.SelectedIndex = 0;
        refreshTimer.Tick += RefreshTimer_Tick;
        searchTimer.Tick += SearchTimer_Tick;
    }

    // Selects the logs of a site; used when the cached page is shown again from the Sites page.
    public void ShowSite(string sitePath)
    {
        pendingSitePath = sitePath;
        if (pageActive && !loadingSources && pageCancellation is { } cancellation)
        {
            _ = RefreshPageAsync(cancellation.Token);
        }
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        // The page instance is cached, and Loaded can repeat without Unloaded in between.
        if (pageActive) return;
        pageActive = true;
        App.MainWindowVisibilityChanged -= App_MainWindowVisibilityChanged;
        App.MainWindowVisibilityChanged += App_MainWindowVisibilityChanged;
        pageCancellation?.Cancel();
        pageCancellation?.Dispose();
        pageCancellation = new CancellationTokenSource();
        await RefreshPageAsync(pageCancellation.Token);
    }

    private async Task RefreshPageAsync(CancellationToken cancellationToken)
    {
        try
        {
            do
            {
                var previousRoot = SelectedSource?.RootPath;
                await ReloadSourcesAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var sameSource = previousRoot is not null && SelectedSource is { } source
                    && source.RootPath.Equals(previousRoot, StringComparison.OrdinalIgnoreCase);
                if (!sameSource)
                {
                    reloadCancellation?.Cancel();
                    ClearContent();
                    Logs.Clear();
                }
                await ReloadAsync(force: !sameSource);
                cancellationToken.ThrowIfCancellationRequested();
            }
            while (pendingSitePath is not null);
            if (App.IsMainWindowVisible) refreshTimer.Start();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        App.MainWindowVisibilityChanged -= App_MainWindowVisibilityChanged;
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

    private async void App_MainWindowVisibilityChanged(object? sender, bool visible)
    {
        if (!pageActive) return;
        if (!visible)
        {
            refreshTimer.Stop();
            return;
        }
        refreshTimer.Start();
        if (LiveRefreshToggle.IsOn) await ReloadAsync(force: false);
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

    private void SearchAccelerator_Invoked(
        Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args
    )
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
    }

    private async void SearchTimer_Tick(object? sender, object e)
    {
        searchTimer.Stop();
        await ApplySearchAsync();
    }

    private async void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateLaravelControls();
        if (!pageActive || loadingSources || SourceBox.SelectedItem is null) return;
        reloadCancellation?.Cancel();
        ClearContent();
        Logs.Clear();
        await ReloadAsync(force: true);
    }

    private async void LevelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!pageActive) return;
        await ApplySearchAsync();
    }

    private void UpdateLaravelControls()
    {
        var laravel = IsLaravelSource;
        LevelBox.Visibility = laravel ? Visibility.Visible : Visibility.Collapsed;
        OpenLastErrorButton.Visibility = laravel ? Visibility.Visible : Visibility.Collapsed;
        ShowLaravelSummary(laravel ? laravelSummary : null);
    }

    private void ShowLaravelSummary(LaravelLogSummary? summary)
    {
        laravelSummary = summary;
        lastErrorLocation = summary?.LastError;
        OpenLastErrorButton.IsEnabled = lastErrorLocation is not null;
        if (summary is null || !IsLaravelSource)
        {
            LevelCountsText.Visibility = Visibility.Collapsed;
            return;
        }
        LevelCountsText.Text = AppLocalization.Format("LogsLevelCounts", summary.Errors, summary.Warnings, summary.Entries);
        LevelCountsText.Visibility = Visibility.Visible;
        if (lastErrorLocation is { } location)
        {
            ToolTipService.SetToolTip(OpenLastErrorButton, location.Path + ":" + location.Line);
        }
    }

    private async void OpenLastError_Click(object sender, RoutedEventArgs e)
    {
        if (lastErrorLocation is not { } location) return;
        try
        {
            var opened = await Task.Run(() => EditorLauncher.Open(location.Path, location.Line));
            if (opened == EditorOpenKind.DefaultApp)
            {
                EditorBar.Message = AppLocalization.Format("LogsOpenedWithoutLine", location.Line);
                EditorBar.Severity = InfoBarSeverity.Informational;
                EditorBar.IsOpen = true;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or Win32Exception or InvalidOperationException or ArgumentException)
        {
            EditorBar.Message = AppLocalization.Format("LogsOpenLastErrorFailed", UserErrorPresentation.Describe(error));
            EditorBar.Severity = InfoBarSeverity.Warning;
            EditorBar.IsOpen = true;
            await DiagnosticLog.WriteFailureAsync("logs", "open-editor",
                "The file from the last Laravel error could not be opened.", error.ToString());
        }
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
        await LoadSelectedContentAsync(log, incremental: false);
    }

    private async Task LoadSelectedContentAsync(LogFileRecord log, bool incremental)
    {
        contentCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        contentCancellation = cancellation;
        UpdateReadProgress();
        var state = incremental && tailState is { } known
            && known.Path.Equals(log.Path, StringComparison.OrdinalIgnoreCase)
                ? known
                : null;
        var baseContent = state is null ? string.Empty : currentContent;
        var baseTruncated = state is not null && contentTruncated;
        var query = SearchBox.Text;
        var level = SelectedLevel;
        var laravel = IsLaravelSource;
        try
        {
            var view = await Task.Run(
                () => ReadLogView(log.Path, state, baseContent, baseTruncated, query, level, laravel, cancellation.Token),
                cancellation.Token
            );
            if (!pageActive || cancellation.IsCancellationRequested || !IsSelectedLog(log.Path)) return;
            tailState = view.State;
            if (view.Unchanged) return;
            currentContent = view.Content;
            contentTruncated = view.Truncated;
            displayTrimmed = view.DisplayTrimmed;
            UpdateTailNotice();
            ShowLaravelSummary(view.Summary);
            ShowText(view.Display, scrollToEnd: view.Reset);
            // A search or level picked during the read re-renders from the cached text.
            if (!string.Equals(query, SearchBox.Text, StringComparison.Ordinal) || level != SelectedLevel)
                await ApplySearchAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!pageActive || cancellation.IsCancellationRequested || !IsSelectedLog(log.Path)) return;
            TailNoticeText.Visibility = Visibility.Collapsed;
            currentContent = string.Empty;
            tailState = null;
            contentTruncated = false;
            displayTrimmed = false;
            searchCancellation?.Cancel();
            ShowText(UserErrorPresentation.Describe(error), scrollToEnd: false);
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

    private bool IsSelectedLog(string path)
    {
        return LogList.SelectedItem is LogFileRecord selected
            && selected.Path.Equals(path, StringComparison.OrdinalIgnoreCase);
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
        var level = SelectedLevel;
        try
        {
            var (display, trimmed) = await Task.Run(() => BuildDisplay(text, query, level, cancellation.Token),
                cancellation.Token);
            if (!pageActive || cancellation.IsCancellationRequested) return;
            displayTrimmed = trimmed;
            UpdateTailNotice();
            ShowText(display, scrollToEnd: true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(searchCancellation, cancellation)) searchCancellation = null;
        }
    }

    private void ShowText(string text, bool scrollToEnd)
    {
        if (string.Equals(text, displayedText, StringComparison.Ordinal)) return;
        // Follow the end of the log only when the reader is already there (or Follow is on).
        var atBottom = LogScroll.ScrollableHeight <= 0
            || LogScroll.VerticalOffset >= LogScroll.ScrollableHeight - BottomTolerance;
        displayedText = text;
        LogContentText.Text = text;
        ApplyHighlights(text);
        if (!atBottom && !scrollToEnd && !FollowTail) return;
        ScrollToEnd();
    }

    private void UpdateTailNotice()
    {
        TailNoticeText.Visibility = contentTruncated || displayTrimmed
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ClearContent()
    {
        contentCancellation?.Cancel();
        searchCancellation?.Cancel();
        searchTimer.Stop();
        currentContent = string.Empty;
        displayedText = string.Empty;
        tailState = null;
        contentTruncated = false;
        displayTrimmed = false;
        LogContentText.Text = string.Empty;
        ApplyHighlights(string.Empty);
        LogTitleText.Text = AppLocalization.Get("LogsSelectLog");
        TailNoticeText.Visibility = Visibility.Collapsed;
        ShowLaravelSummary(null);
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
            var samePaths = discovered.Count == Logs.Count
                && discovered.Select((record, index) => record.Path.Equals(
                    Logs[index].Path,
                    StringComparison.OrdinalIgnoreCase
                )).All(same => same);
            var changed = force
                || !samePaths
                || discovered.Where((record, index) => !SameRecord(record, Logs[index])).Any();
            var contentMissing = previous is not null && (tailState is not { } state
                || !state.Path.Equals(previous.Path, StringComparison.OrdinalIgnoreCase));
            if (!changed && !contentMissing) return;

            if (changed)
            {
                updatingLogList = true;
                try
                {
                    if (samePaths)
                    {
                        // Replace only grown files so the list keeps its scroll position.
                        for (var index = 0; index < discovered.Count; index++)
                        {
                            if (!SameRecord(discovered[index], Logs[index])) Logs[index] = discovered[index];
                        }
                    }
                    else
                    {
                        Logs.Clear();
                        foreach (var record in discovered) Logs.Add(record);
                    }
                    var selection = Logs.FirstOrDefault(log => log.Path.Equals(
                        previous?.Path,
                        StringComparison.OrdinalIgnoreCase
                    )) ?? Logs.FirstOrDefault();
                    if (!ReferenceEquals(LogList.SelectedItem, selection)) LogList.SelectedItem = selection;
                }
                finally { updatingLogList = false; }
            }
            if (LogList.SelectedItem is LogFileRecord selected)
            {
                var sameFile = tailState is { } loaded
                    && loaded.Path.Equals(selected.Path, StringComparison.OrdinalIgnoreCase);
                if (!sameFile && previous?.Path != selected.Path) ClearContent();
                LogTitleText.Text = selected.Name;
                if (force || !sameFile || wasReading || previous is null || !SameRecord(previous, selected))
                    await LoadSelectedContentAsync(selected, incremental: !force && sameFile);
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
        try
        {
            sourceDiscoveryFailed = false;
            SourceWarning.IsOpen = false;
            var requested = pendingSitePath;
            pendingSitePath = null;
            if (requested is not null) requestedSitePath = requested;
            // A revisit keeps the source the user picked unless a site asked for its logs.
            var preferredRoot = requested is not null
                ? LogPresentation.SiteLogRoot(requested)
                : SelectedSource?.RootPath ?? ApplicationLogRoot;
            Sources.Clear();
            Sources.Add(new LogSourceRecord
            {
                Id = "application",
                Name = "HerdMe",
                RootPath = ApplicationLogRoot,
                FallbackPath = ApplicationLogRoot,
                IsApplication = true
            });
            if (requestedSitePath is { } sitePath)
            {
                AddSiteSource(
                    Path.GetFileName(Path.TrimEndingDirectorySeparator(sitePath)),
                    sitePath
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
        }
        finally
        {
            loadingSources = false;
        }
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

    private sealed record LogTailState(string Path, Encoding Encoding, int UnitSize, long Position);

    private sealed record LogView(
        LogTailState State,
        string Content,
        bool Truncated,
        string Display,
        bool DisplayTrimmed,
        bool Reset,
        bool Unchanged,
        LaravelLogSummary? Summary
    );

    private sealed record LogChunk(LogTailState State, string Text, bool Truncated);

    private static LogView ReadLogView(
        string path,
        LogTailState? state,
        string baseContent,
        bool baseTruncated,
        string query,
        LaravelLogLevel? level,
        bool laravel,
        CancellationToken cancellationToken
    )
    {
        var appended = state is null ? null : ReadAppended(state, cancellationToken);
        if (appended is not null && appended.Text.Length == 0)
        {
            return new LogView(appended.State, baseContent, baseTruncated, string.Empty, false, false, true, null);
        }
        var reset = appended is null;
        var chunk = appended ?? ReadTail(path, cancellationToken);
        var content = reset ? chunk.Text : baseContent + chunk.Text;
        var truncated = reset ? chunk.Truncated : baseTruncated;
        if (content.Length > ContentCharacterLimit)
        {
            content = TrimToLineStart(content, ContentCharacterLimit);
            truncated = true;
        }
        var (display, displayTrimmed) = BuildDisplay(content, query, level, cancellationToken);
        var summary = laravel ? LaravelLog.Summarize(content, cancellationToken) : null;
        return new LogView(chunk.State, content, truncated, display, displayTrimmed, reset, false, summary);
    }

    // The level filter keeps whole Laravel entries (with their stack traces); search then
    // narrows to matching lines.
    private static (string Display, bool Trimmed) BuildDisplay(
        string content,
        string? query,
        LaravelLogLevel? level,
        CancellationToken cancellationToken
    )
    {
        var leveled = LaravelLog.FilterByMinimumLevel(content, level, cancellationToken);
        var filtered = LogPresentation.FilterLines(leveled, query, cancellationToken);
        return filtered.Length > DisplayCharacterLimit
            ? (TrimToLineStart(filtered, DisplayCharacterLimit), true)
            : (filtered, false);
    }

    private static string TrimToLineStart(string text, int limit)
    {
        var start = text.Length - limit;
        var lineBreak = text.IndexOf('\n', start);
        if (lineBreak >= 0 && lineBreak < text.Length - 1) start = lineBreak + 1;
        return text[start..];
    }

    // Reads the tail of the file once and remembers the byte position for later appends.
    private static LogChunk ReadTail(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = OpenLog(path);
        var length = stream.Length;
        var header = new byte[4];
        var headerLength = ReadFully(stream, header, header.Length, cancellationToken);
        var (encoding, unitSize, bomLength) = EncodingFor(header, headerLength);
        var count = (int)Math.Min(length, LogFileReader.MaximumBytes);
        var start = length - count;
        var bytes = new byte[count];
        stream.Seek(start, SeekOrigin.Begin);
        var read = ReadFully(stream, bytes, count, cancellationToken);
        var offset = 0;
        if (start == 0)
        {
            offset = Math.Min(bomLength, read);
        }
        else if (unitSize == 1)
        {
            // A byte limit can land inside an Arabic or other UTF-8 character.
            while (offset < read && (bytes[offset] & 0xC0) == 0x80) offset++;
        }
        else
        {
            offset = (int)((unitSize - start % unitSize) % unitSize);
            offset = Math.Min(offset, read);
            if (unitSize == 2 && read - offset >= 2 && IsLowSurrogate(bytes, offset, encoding)) offset += 2;
        }
        var complete = CompleteLength(bytes, offset, read, unitSize, encoding);
        var text = encoding.GetString(bytes, offset, complete - offset);
        return new LogChunk(new LogTailState(path, encoding, unitSize, start + complete), text, start > 0);
    }

    // Returns null when the file was truncated, replaced, or grew too much for an append.
    private static LogChunk? ReadAppended(LogTailState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = OpenLog(state.Path);
        var length = stream.Length;
        if (length < state.Position) return null;
        if (length == state.Position) return new LogChunk(state, string.Empty, false);
        var delta = length - state.Position;
        if (delta > LogFileReader.MaximumBytes) return null;
        var count = (int)delta;
        var bytes = new byte[count];
        stream.Seek(state.Position, SeekOrigin.Begin);
        var read = ReadFully(stream, bytes, count, cancellationToken);
        var complete = CompleteLength(bytes, 0, read, state.UnitSize, state.Encoding);
        if (complete == 0) return new LogChunk(state, string.Empty, false);
        var text = state.Encoding.GetString(bytes, 0, complete);
        return new LogChunk(state with { Position = state.Position + complete }, text, false);
    }

    private static FileStream OpenLog(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1_024, FileOptions.SequentialScan);
    }

    private static int ReadFully(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var read = 0;
        // Read only the captured length, even if the writer keeps appending.
        while (read < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var received = stream.Read(buffer, read, count - read);
            if (received == 0) break; // The log may have been truncated during rotation.
            read += received;
        }
        return read;
    }

    // Stops before a character that is still being written so the next append decodes it whole.
    private static int CompleteLength(byte[] bytes, int offset, int end, int unitSize, Encoding encoding)
    {
        if (unitSize > 1)
        {
            var aligned = end - (end - offset) % unitSize;
            if (unitSize == 2 && aligned - offset >= 2 && IsHighSurrogate(bytes, aligned - 2, encoding))
                aligned -= 2;
            return aligned;
        }
        for (var index = end - 1; index >= offset && index >= end - 4; index--)
        {
            var value = bytes[index];
            if ((value & 0xC0) == 0x80) continue;
            var needed = value < 0x80 ? 1 : (value & 0xE0) == 0xC0 ? 2 : (value & 0xF0) == 0xE0 ? 3
                : (value & 0xF8) == 0xF0 ? 4 : 1;
            return index + needed > end ? index : end;
        }
        return end;
    }

    private static int CodeUnit(byte[] bytes, int index, Encoding encoding)
    {
        return encoding.CodePage == Encoding.Unicode.CodePage
            ? bytes[index] | bytes[index + 1] << 8
            : bytes[index] << 8 | bytes[index + 1];
    }

    private static bool IsLowSurrogate(byte[] bytes, int index, Encoding encoding)
    {
        return CodeUnit(bytes, index, encoding) is >= 0xDC00 and <= 0xDFFF;
    }

    private static bool IsHighSurrogate(byte[] bytes, int index, Encoding encoding)
    {
        return CodeUnit(bytes, index, encoding) is >= 0xD800 and <= 0xDBFF;
    }

    private static (Encoding Encoding, int UnitSize, int BomLength) EncodingFor(byte[] header, int count)
    {
        if (count >= 4 && header[0] == 0xFF && header[1] == 0xFE && header[2] == 0 && header[3] == 0)
            return (Encoding.UTF32, 4, 4);
        if (count >= 4 && header[0] == 0 && header[1] == 0 && header[2] == 0xFE && header[3] == 0xFF)
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true), 4, 4);
        if (count >= 2 && header[0] == 0xFF && header[1] == 0xFE) return (Encoding.Unicode, 2, 2);
        if (count >= 2 && header[0] == 0xFE && header[1] == 0xFF) return (Encoding.BigEndianUnicode, 2, 2);
        if (count >= 3 && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
            return (Encoding.UTF8, 1, 3);
        return (Encoding.UTF8, 1, 0);
    }
}
