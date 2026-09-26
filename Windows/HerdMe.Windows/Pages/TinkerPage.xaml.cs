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
    private CancellationTokenSource? cancellation;
    private bool loadingHistory;

    public ObservableCollection<SiteRecord> Sites { get; } = [];

    public ObservableCollection<TinkerHistoryItem> History { get; } = [];

    public TinkerPage(
        CoreClient coreClient,
        SiteConfigurationStore siteSettings,
        PhpRuntimeInstaller phpInstaller,
        PhpRuntimePolicy runtimePolicy,
        ComposerToolManager composerTools
    )
    {
        this.coreClient = coreClient;
        this.siteSettings = siteSettings;
        this.phpInstaller = phpInstaller;
        this.runtimePolicy = runtimePolicy;
        this.composerTools = composerTools;
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        var history = await Task.Run(() =>
        {
            runner.CleanUp();
            return historyStore.Load();
        });
        ShowHistory(history);
        await RefreshSitesAsync();
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
        var code = CodeBox.Text;
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
        StatusText.Text = AppLocalization.Get("TinkerPreparing");
        try
        {
            var history = await Task.Run(() => historyStore.Add(code));
            ShowHistory(history);
            var cycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
            var php = phpInstaller.PhpExecutable(cycle);
            await runtimePolicy.PrepareLaunchAsync(php, cycle, source.Token);
            var environment = composerTools.ManagedEnvironment(cycle);
            StatusText.Text = AppLocalization.Format("TinkerRunning", site.Domain);
            var result = await runner.RunAsync(
                php,
                site.Path,
                code,
                environment,
                TinkerRunner.DefaultTimeout,
                new Progress<string>(AppendOutput),
                source.Token
            );
            if (OutputBox.Text.Length == 0) OutputBox.Text = result.Output;
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
        var combined = OutputBox.Text + value;
        OutputBox.Text = combined.Length > MaximumOutputCharacters ? combined[^MaximumOutputCharacters..] : combined;
    }

    private void SetRunning(bool running)
    {
        RunProgress.IsActive = running;
        CancelButton.IsEnabled = running;
        SiteBox.IsEnabled = !running;
        HistoryBox.IsEnabled = !running && History.Count > 0;
        ClearHistoryButton.IsEnabled = !running && History.Count > 0;
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
