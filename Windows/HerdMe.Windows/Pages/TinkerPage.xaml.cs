using System.Collections.ObjectModel;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace HerdMe.Windows.Pages;

public sealed partial class TinkerPage : Page
{
    private const int MaximumOutputCharacters = 1 * 1_024 * 1_024;
    private readonly CoreClient coreClient;
    private readonly SiteConfigurationStore siteSettings;
    private readonly PhpRuntimeInstaller phpInstaller;
    private readonly PhpRuntimePolicy runtimePolicy;
    private readonly ComposerToolManager composerTools;
    private readonly TinkerRunner runner = new();
    private readonly TinkerHistoryStore historyStore = new();
    private readonly TinkerSnippetStore snippetStore = new();
    private readonly bool embedded;
    private readonly string readyText;
    private CancellationTokenSource? cancellation;
    private bool loadingHistory;
    private bool loadingSnippets;
    // What the last run printed, before the JSON / table view is applied.
    private string rawOutput = string.Empty;
    private TinkerOutputMode lastRunMode = TinkerOutputMode.Dump;

    public ObservableCollection<SiteRecord> Sites { get; } = [];

    public ObservableCollection<TinkerHistoryItem> History { get; } = [];

    public ObservableCollection<TinkerSnippet> Snippets { get; } = [];

    public TinkerPage(
        CoreClient coreClient,
        SiteConfigurationStore siteSettings,
        PhpRuntimeInstaller phpInstaller,
        PhpRuntimePolicy runtimePolicy,
        ComposerToolManager composerTools,
        bool embedded = false
    )
    {
        this.coreClient = coreClient;
        this.siteSettings = siteSettings;
        this.phpInstaller = phpInstaller;
        this.runtimePolicy = runtimePolicy;
        this.composerTools = composerTools;
        this.embedded = embedded;
        InitializeComponent();
        readyText = StatusText.Text;
        ToolTipService.SetToolTip(SaveSnippetButton, AppLocalization.Get("TinkerSaveSnippetTooltip"));
        ToolTipService.SetToolTip(DeleteSnippetButton, AppLocalization.Get("TinkerDeleteSnippetTooltip"));
        if (embedded) UseEmbeddedLayout();
    }

    // Inside the Sites details pane the site is the one selected in the list, and the pane
    // already has a title and scrolls, so only the editor, history and output remain.
    private void UseEmbeddedLayout()
    {
        HeaderPanel.Visibility = Visibility.Collapsed;
        SiteRow.Visibility = Visibility.Collapsed;
        SiteRowDivider.Visibility = Visibility.Collapsed;
        RootScroll.VerticalScrollMode = ScrollMode.Disabled;
        RootScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        ContentPanel.Padding = new Thickness(0);
    }

    // Embedded mode: follow the site selected in the Sites list. Switching to another site
    // cancels code still running in the previous one and clears its output.
    public void ShowSite(SiteRecord? site)
    {
        var current = SiteBox.SelectedItem as SiteRecord;
        if (site is not null && current is not null
            && current.Path.Equals(site.Path, StringComparison.OrdinalIgnoreCase))
        {
            if (!ReferenceEquals(current, site))
            {
                Sites[0] = site;
                SiteBox.SelectedIndex = 0;
            }
            return;
        }
        cancellation?.Cancel();
        Sites.Clear();
        if (site is not null) Sites.Add(site);
        SiteBox.SelectedIndex = Sites.Count > 0 ? 0 : -1;
        OutputBox.Text = string.Empty;
        rawOutput = string.Empty;
        StatusText.Text = site is null ? AppLocalization.Get("TinkerNoSites") : readyText;
        UpdateState();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        var history = await Task.Run(() =>
        {
            runner.CleanUp();
            return historyStore.Load();
        });
        ShowHistory(history);
        if (!embedded) await RefreshSitesAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        cancellation?.Cancel();
    }

    private async Task RefreshSitesAsync()
    {
        RunProgress.IsActive = true;
        try
        {
            var configuration = siteSettings.Load();
            var scanned = await coreClient.ScanAsync(
                configuration.Roots,
                configuration.Tld,
                configuration.LinkedSites
            );
            Sites.Clear();
            foreach (var site in scanned) Sites.Add(site);
            SiteBox.SelectedIndex = Sites.Count > 0 ? 0 : -1;
            if (Sites.Count == 0) StatusText.Text = AppLocalization.Get("TinkerNoSites");
        }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
        }
        finally
        {
            RunProgress.IsActive = false;
            UpdateState();
        }
    }

    private void SiteBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SiteRuntimeText.Text = SiteBox.SelectedItem is SiteRecord site
            ? SitePresentation.RuntimeLabel(
                site,
                AppLocalization.Get("SitesDefaultPhpRuntime"),
                AppLocalization.Get("SitesProjectNodeRuntime")
            )
            : string.Empty;
        _ = LoadSnippetsAsync();
        UpdateState();
    }

    private void HistoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingHistory || HistoryBox.SelectedItem is not TinkerHistoryItem item) return;
        CodeBox.Text = item.Code.Replace("\n", "\r", StringComparison.Ordinal);
        CodeBox.Focus(FocusState.Programmatic);
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Task.Run(historyStore.Clear);
            ShowHistory([]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = error.Message;
        }
    }

    private void RunAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (RunButton.IsEnabled) _ = RunAsync();
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        _ = RunAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (cancellation is null) return;
        StatusText.Text = AppLocalization.Get("TinkerCancelling");
        cancellation.Cancel();
    }

    private async Task RunAsync()
    {
        if (cancellation is not null || SiteBox.SelectedItem is not SiteRecord site) return;
        // With lines selected only the selection runs (like a SQL editor); history keeps it too.
        var selection = CodeBox.SelectionLength > 0 ? CodeBox.SelectedText : string.Empty;
        var runsSelection = selection.Trim().Length > 0;
        var code = runsSelection ? selection : CodeBox.Text;
        var mode = CurrentOutputView() == TinkerOutputView.Dump ? TinkerOutputMode.Dump : TinkerOutputMode.Json;
        try
        {
            _ = TinkerScript.Normalize(code);
        }
        catch (ArgumentException error)
        {
            StatusText.Text = error.Message;
            return;
        }

        using var source = new CancellationTokenSource();
        cancellation = source;
        SetRunning(true);
        OutputBox.Text = string.Empty;
        rawOutput = string.Empty;
        lastRunMode = mode;
        StatusText.Text = AppLocalization.Get("TinkerPreparing");
        try
        {
            var history = await Task.Run(() => historyStore.Add(code));
            ShowHistory(history);
            var cycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
            var php = phpInstaller.PhpExecutable(cycle);
            await runtimePolicy.PrepareLaunchAsync(php, cycle, source.Token);
            var environment = composerTools.ManagedEnvironment(cycle);
            StatusText.Text = AppLocalization.Format(runsSelection ? "TinkerRunningSelection" : "TinkerRunning", site.Domain);
            var result = await runner.RunAsync(
                php,
                site.Path,
                code,
                environment,
                TinkerRunner.DefaultTimeout,
                new Progress<string>(AppendOutput),
                mode,
                source.Token
            );
            if (rawOutput.Length == 0) rawOutput = result.Output;
            ShowOutput();
            var elapsed = result.Elapsed.TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture);
            StatusText.Text = result.ExitCode == 0
                ? AppLocalization.Format("TinkerCompleted", elapsed)
                : AppLocalization.Format("TinkerFailedExit", result.ExitCode, elapsed);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            StatusText.Text = AppLocalization.Get("TinkerCancelled");
        }
        catch (Exception error)
        {
            StatusText.Text = AppLocalization.Get(error is TimeoutException ? "TinkerTimedOut" : "TinkerFailed");
            AppendOutput(error.Message);
            await DiagnosticLog.WriteFailureAsync(
                "tinker",
                "run",
                $"Tinker code for {site.Name} failed.",
                error.ToString()
            );
        }
        finally
        {
            if (ReferenceEquals(cancellation, source)) cancellation = null;
            SetRunning(false);
        }
    }

    private void AppendOutput(string value)
    {
        if (value.Length == 0) return;
        var combined = rawOutput + value;
        rawOutput = combined.Length > MaximumOutputCharacters ? combined[^MaximumOutputCharacters..] : combined;
        OutputBox.Text = rawOutput;
    }

    private TinkerOutputView CurrentOutputView() => ((OutputViewBox?.SelectedItem as ComboBoxItem)?.Tag as string) switch
    {
        "json" => TinkerOutputView.Json,
        "table" => TinkerOutputView.Table,
        _ => TinkerOutputView.Dump
    };

    // JSON and Table read the same json_encode output, so switching between them needs no new
    // run; Dump output is not JSON and stays as printed.
    private void ShowOutput()
    {
        if (rawOutput.Length == 0 || lastRunMode != TinkerOutputMode.Json)
        {
            OutputBox.Text = rawOutput;
            return;
        }
        var view = CurrentOutputView();
        var formatted = view == TinkerOutputView.Table
            ? TinkerOutputFormatter.Table(rawOutput)
            : TinkerOutputFormatter.IndentJson(rawOutput);
        OutputBox.Text = formatted ?? rawOutput;
    }

    private void OutputViewBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OutputBox is null || StatusText is null || OutputViewBox is null) return;
        var wantsJson = CurrentOutputView() != TinkerOutputView.Dump;
        if (rawOutput.Length > 0 && wantsJson != (lastRunMode == TinkerOutputMode.Json))
        {
            StatusText.Text = AppLocalization.Get("TinkerOutputViewRunAgain");
            return;
        }
        ShowOutput();
    }

    private async Task LoadSnippetsAsync()
    {
        if (SiteBox.SelectedItem is not SiteRecord site)
        {
            ShowSnippets([]);
            return;
        }
        var path = site.Path;
        var snippets = await Task.Run(() => snippetStore.Load(path));
        if (SiteBox.SelectedItem is SiteRecord current && current.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            ShowSnippets(snippets);
        }
    }

    private void ShowSnippets(IReadOnlyList<TinkerSnippet> snippets, string? select = null)
    {
        loadingSnippets = true;
        try
        {
            Snippets.Clear();
            foreach (var snippet in snippets) Snippets.Add(snippet);
            var index = select is null
                ? -1
                : Snippets.ToList().FindIndex(snippet => snippet.Name.Equals(select, StringComparison.OrdinalIgnoreCase));
            SnippetBox.SelectedIndex = index;
            SnippetBox.IsEnabled = Snippets.Count > 0;
            DeleteSnippetButton.IsEnabled = index >= 0;
        }
        finally
        {
            loadingSnippets = false;
        }
    }

    private void SnippetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DeleteSnippetButton.IsEnabled = SnippetBox.SelectedItem is TinkerSnippet;
        if (loadingSnippets || SnippetBox.SelectedItem is not TinkerSnippet snippet || cancellation is not null) return;
        CodeBox.Text = snippet.Code.Replace("\n", "\r", StringComparison.Ordinal);
        CodeBox.Focus(FocusState.Programmatic);
    }

    private void SaveSnippetAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = SaveSnippetAsync();
    }

    private void SaveSnippet_Click(object sender, RoutedEventArgs e)
    {
        _ = SaveSnippetAsync();
    }

    private async Task SaveSnippetAsync()
    {
        if (SiteBox.SelectedItem is not SiteRecord site || XamlRoot is null) return;
        var code = CodeBox.Text;
        if (code.Trim().Length == 0)
        {
            StatusText.Text = AppLocalization.Get("TinkerCodeEmpty");
            return;
        }
        var nameBox = new TextBox
        {
            Header = AppLocalization.Get("TinkerSnippetNameHeader"),
            Text = (SnippetBox.SelectedItem as TinkerSnippet)?.Name ?? SuggestedSnippetName(code),
            MaxLength = TinkerSnippetStore.MaximumNameCharacters
        };
        nameBox.SelectAll();
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("TinkerSaveSnippetTitle", site.Domain),
            Content = nameBox,
            PrimaryButtonText = AppLocalization.Get("TinkerSaveSnippetConfirm"),
            CloseButtonText = AppLocalization.Get("TinkerSaveSnippetCancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var name = nameBox.Text;
        var path = site.Path;
        try
        {
            var snippets = await Task.Run(() => snippetStore.Save(path, name, code));
            ShowSnippets(snippets, TinkerSnippetStore.NormalizeName(name));
            StatusText.Text = AppLocalization.Format("TinkerSnippetSaved", TinkerSnippetStore.NormalizeName(name));
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = error.Message;
        }
    }

    private static string SuggestedSnippetName(string code)
    {
        var line = new TinkerHistoryItem(code.Replace('\r', '\n')).Preview;
        return line.Length > TinkerSnippetStore.MaximumNameCharacters
            ? line[..TinkerSnippetStore.MaximumNameCharacters]
            : line;
    }

    private async void DeleteSnippet_Click(object sender, RoutedEventArgs e)
    {
        if (SiteBox.SelectedItem is not SiteRecord site || SnippetBox.SelectedItem is not TinkerSnippet snippet) return;
        var path = site.Path;
        try
        {
            var remaining = await Task.Run(() => snippetStore.Delete(path, snippet.Name));
            ShowSnippets(remaining);
            StatusText.Text = AppLocalization.Format("TinkerSnippetDeleted", snippet.Name);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = error.Message;
        }
    }

    private void SetRunning(bool running)
    {
        RunProgress.IsActive = running;
        CancelButton.IsEnabled = running;
        SiteBox.IsEnabled = !running;
        HistoryBox.IsEnabled = !running && History.Count > 0;
        ClearHistoryButton.IsEnabled = !running && History.Count > 0;
        SnippetBox.IsEnabled = !running && Snippets.Count > 0;
        SaveSnippetButton.IsEnabled = !running;
        CodeBox.IsReadOnly = running;
        UpdateState();
    }

    private void UpdateState()
    {
        RunButton.IsEnabled = cancellation is null && SiteBox.SelectedItem is SiteRecord;
    }

    private void ShowHistory(IReadOnlyList<string> entries)
    {
        loadingHistory = true;
        try
        {
            History.Clear();
            foreach (var entry in entries) History.Add(new TinkerHistoryItem(entry));
            HistoryBox.SelectedIndex = -1;
            HistoryBox.IsEnabled = cancellation is null && History.Count > 0;
            ClearHistoryButton.IsEnabled = cancellation is null && History.Count > 0;
        }
        finally
        {
            loadingHistory = false;
        }
    }
}

public sealed class TinkerHistoryItem(string code)
{
    private const int PreviewCharacters = 60;

    public string Code { get; } = code;

    public string Preview { get; } = PreviewOf(code);

    private static string PreviewOf(string code)
    {
        var line = code.Split('\n').Select(value => value.Trim()).FirstOrDefault(value => value.Length > 0) ?? string.Empty;
        return line.Length > PreviewCharacters ? line[..PreviewCharacters] + "..." : line;
    }
}
