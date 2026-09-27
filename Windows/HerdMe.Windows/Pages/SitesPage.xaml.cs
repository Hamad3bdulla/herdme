using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using HerdMe.Windows.ViewModels;
using HerdMe.Windows.Views;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace HerdMe.Windows.Pages;

public sealed partial class SitesPage : Page
{
    private sealed record DisplayOption(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record DatabaseServiceOption(ManagedServiceInstance Instance)
    {
        public override string ToString() => $"{Instance.Name} (127.0.0.1:{Instance.Port})";
    }

    private readonly CoreClient coreClient;
    private readonly WindowsLocalEnvironment environment;
    private readonly SiteConfigurationStore settingsStore;
    private readonly LaravelProjectCreator projectCreator;
    private CancellationTokenSource? projectCreationCancellation;
    private readonly SiteRuntimeStore siteRuntimeStore;
    private readonly SiteCommandFavoritesStore commandFavorites;
    private readonly PhpRuntimeInstaller phpInstaller;
    private readonly PhpRuntimePolicy runtimePolicy;
    private readonly NodeRuntimeInstaller nodeInstaller;
    private readonly ComposerToolManager composerTools;
    private readonly WindowsServiceManager serviceManager;
    private readonly SiteProcessManager siteProcesses;
    private readonly WindowsCertificateManager certificates;
    private readonly MailCaptureService mail;
    private readonly ProxySiteStore proxySites;
    private readonly SiteShareManager shares;
    private readonly SiteScanGeneration siteScanGeneration = new();
    private bool loaded;
    private bool subscribed;
    private bool hasLoadedOnce;
    private bool suppressSiteSelection;
    private bool listRefreshDeferred;
    private int processRefreshPending;
    private readonly SitesDetectionCache detectionCache = new();
    private readonly List<DispatcherTimer> visibilityPausedTimers = [];
    private readonly Dictionary<string, string> lastSiteErrors = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? searchDebounce;
    private bool suppressPreviewToggle = true;
    private SiteRecord? selectedSite;
    private CancellationTokenSource? siteDetailsCancellation;
    private CancellationTokenSource? gitInspectionCancellation;
    private CancellationTokenSource? databaseCancellation;
    private CancellationTokenSource? siteOperationCancellation;

    public ObservableCollection<string> Roots { get; } = [];
    public ObservableCollection<SiteRecord> Sites { get; } = [];
    public ObservableCollection<SitesListItem> VisibleSites { get; } = [];

    public SitesPage(
        CoreClient coreClient,
        WindowsLocalEnvironment environment,
        SiteConfigurationStore settingsStore,
        LaravelProjectCreator projectCreator,
        SiteRuntimeStore siteRuntimeStore,
        SiteCommandFavoritesStore commandFavorites,
        PhpRuntimeInstaller phpInstaller,
        PhpRuntimePolicy runtimePolicy,
        NodeRuntimeInstaller nodeInstaller,
        ComposerToolManager composerTools,
        WindowsServiceManager serviceManager,
        SiteProcessManager siteProcesses,
        WindowsCertificateManager certificates,
        MailCaptureService mail,
        ProxySiteStore proxySites,
        SiteShareManager shares
    )
    {
        this.proxySites = proxySites;
        this.shares = shares;
        this.coreClient = coreClient;
        this.environment = environment;
        this.settingsStore = settingsStore;
        this.projectCreator = projectCreator;
        this.siteRuntimeStore = siteRuntimeStore;
        this.commandFavorites = commandFavorites;
        this.phpInstaller = phpInstaller;
        this.runtimePolicy = runtimePolicy;
        this.nodeInstaller = nodeInstaller;
        this.composerTools = composerTools;
        this.serviceManager = serviceManager;
        this.siteProcesses = siteProcesses;
        this.certificates = certificates;
        this.mail = mail;
        InitializeComponent();
        var settings = settingsStore.Load();
        foreach (var root in settings.Roots) Roots.Add(root);
        Directory.CreateDirectory(Roots[0]);
        RootPathTextBox.Text = Roots[0];
        suppressPreviewToggle = true;
        PreviewToggle.IsOn = settings.ShowPreviews;
        suppressPreviewToggle = false;
        UpdateEnvironmentState();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (loaded) return;
        loaded = true;
        var compactMode = settingsStore.Load().CompactMode;
        SitesLayout.Padding = compactMode
            ? new Thickness(16, 14, 16, 16)
            : new Thickness(28, 24, 28, 24);
        SitesLayout.RowSpacing = compactMode ? 8 : 14;
        SubscribePageEvents();
        var externalRescan = ConsumeExternalRescan();
        if (hasLoadedOnce && Sites.Count > 0 && !externalRescan)
        {
            // The page instance is cached by the main window: reuse the scanned list and
            // the detection cache instead of rescanning every parked folder.
            await RefreshFromCacheAsync();
            RunPendingNextStep();
            return;
        }
        hasLoadedOnce = true;
        await ScanAsync();
        RunPendingNextStep();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        loaded = false;
        searchDebounce?.Stop();
        siteScanGeneration.Invalidate();
        CancelGitInspection();
        projectCreationCancellation?.Cancel();
        activeCommandConsole?.Cancel();
        siteDetailsCancellation?.Cancel();
        databaseCancellation?.Cancel();
        siteOperationCancellation?.Cancel();
        UnsubscribePageEvents();
    }

    private void SubscribePageEvents()
    {
        if (subscribed) return;
        subscribed = true;
        siteProcesses.Changed += SiteProcesses_Changed;
        shares.SharesChanged += Shares_Changed;
        App.MainWindowVisibilityChanged += App_MainWindowVisibilityChanged;
    }

    private void UnsubscribePageEvents()
    {
        if (!subscribed) return;
        subscribed = false;
        siteProcesses.Changed -= SiteProcesses_Changed;
        shares.SharesChanged -= Shares_Changed;
        App.MainWindowVisibilityChanged -= App_MainWindowVisibilityChanged;
    }

    private void App_MainWindowVisibilityChanged(object? sender, bool visible)
    {
        foreach (var timer in visibilityPausedTimers)
        {
            if (visible) timer.Start();
            else timer.Stop();
        }
        if (visible && loaded && ConsumeExternalRescan())
        {
            _ = ScanAsync();
            return;
        }
        if (!visible || !loaded || !listRefreshDeferred) return;
        listRefreshDeferred = false;
        RefreshWorkflowStatuses();
        UpdateBackgroundProcessState();
    }

    // Dialog refresh timers only tick while the main window is visible.
    private void StartVisibleTimer(DispatcherTimer timer)
    {
        if (!visibilityPausedTimers.Contains(timer)) visibilityPausedTimers.Add(timer);
        if (App.IsMainWindowVisible) timer.Start();
    }

    private void StopVisibleTimer(DispatcherTimer timer)
    {
        visibilityPausedTimers.Remove(timer);
        timer.Stop();
    }

    private async Task RefreshFromCacheAsync()
    {
        var generation = siteScanGeneration.Begin();
        CancelGitInspection();
        ApplyFilter(selectedSite?.Path);
        ApplyPendingSelection();
        UpdateSiteCount();
        UpdateEnvironmentState();
        UpdateBackgroundProcessState();
        if (selectedSite is { } site) _ = RefreshSiteDetailsAsync(site);
        var sites = Sites.ToArray();
        var uninspected = await Task.Run(() => ApplyCachedDetection(sites));
        if (!loaded || !siteScanGeneration.IsCurrent(generation)) return;
        ApplyFilter(selectedSite?.Path);
        StartGitInspection(uninspected, generation);
    }

    private void UpdateEnvironmentState()
    {
        var running = environment.IsRunning;
        EnvironmentStatusText.Text = running
            ? AppLocalization.Get("SitesEnvironmentRunning")
            : environment.IsDegraded
                ? AppLocalization.Get("SitesEnvironmentRecovering")
                : AppLocalization.Get("SitesEnvironmentStopped");
        EnvironmentEndpointText.Text = running
            ? environment.HttpsPort is not null ? "HTTPS" : "HTTP"
            : string.Empty;
        EnvironmentStatusDot.Style = Views.StatusStyles.Dot(
            running
                ? Views.StatusTone.Success
                : environment.IsDegraded ? Views.StatusTone.Caution : Views.StatusTone.Neutral
        );
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderPathAsync();
        if (path is not null) RootPathTextBox.Text = path;
    }

    private async void ParkFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderPathAsync();
        if (path is not null) await AddRootAsync(path);
    }

    private static async Task<string?> PickFolderPathAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async void LinkSite_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        if (SiteConfigurationStore.BelongsToOtherHerd(folder.Path))
        {
            await ShowErrorAsync(
                AppLocalization.Get("SitesLinkOtherApplicationRejected")
            );
            return;
        }
        settingsStore.AddLinkedSite(folder.Path);
        await ScanAsync();
    }

    private async void UnlinkSelectedSite_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { Linked: true } site) return;
        await UnlinkSiteAsync(site);
    }

    private async Task UnlinkSiteAsync(SiteRecord site)
    {
        var path = site.Path;
        settingsStore.RemoveLinkedSite(path);
        detectionCache.Invalidate(path);
        await ScanAsync();
        // Unlinking only forgets the registration, so it can be undone for a few seconds.
        App.MainWindow.ShowToast(
            AppLocalization.Format("SitesUnlinkedToast", site.Name),
            AppLocalization.Get("CommonUndo"),
            async () =>
            {
                settingsStore.AddLinkedSite(path);
                RequestSelectSite(path);
                await ScanAsync();
            }
        );
    }

    private async void MoveSelectedSiteToRecycleBin_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { Linked: false } site) return;
        await MoveSiteToRecycleBinAsync(site);
    }

    private async void RemoveSiteFromMenu_Click(object sender, RoutedEventArgs e)
    {
        if (SiteFromMenu(sender) is not { } site) return;
        if (site.Linked)
        {
            await UnlinkSiteAsync(site);
        }
        else
        {
            await MoveSiteToRecycleBinAsync(site);
        }
    }

    private async Task MoveSiteToRecycleBinAsync(SiteRecord site)
    {
        var dialog = DangerStyles.Apply(new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesMoveToRecycleBinTitle", site.Name),
            Content = AppLocalization.Get("SitesMoveToRecycleBinMessage"),
            PrimaryButtonText = AppLocalization.Get("SitesMoveToRecycleBin"),
            CloseButtonText = AppLocalization.Get("CommonCancel"),
            DefaultButton = ContentDialogButton.Close
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await SiteRemovalService.MoveToRecycleBinAsync(site, settingsStore.Load().Roots);
            detectionCache.Invalidate(site.Path);
            await ScanAsync(throwOnError: true);
            UpdateEnvironmentState();
        }
        catch (SiteRemovalException error)
        {
            var key = error.Failure switch
            {
                SiteRemovalFailure.LinkedProject => "SitesRemoveLinkedRejected",
                SiteRemovalFailure.OutsideParkedFolder => "SitesRemoveOutsideRootRejected",
                _ => "SitesRemoveUnavailable"
            };
            await ShowErrorAsync(AppLocalization.Get(key));
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or OperationCanceledException)
        {
            await ShowErrorAsync(error.Message);
        }
    }

    private async void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        await AddRootAsync(RootPathTextBox.Text);
    }

    private async Task AddRootAsync(string value)
    {
        var path = value.Trim();
        if (path.Length == 0) return;
        string normalized;
        try
        {
            normalized = Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            await ShowErrorAsync(error.Message);
            return;
        }
        if (SiteConfigurationStore.BelongsToOtherHerd(normalized))
        {
            await ShowErrorAsync(
                AppLocalization.Get("SitesParkOtherApplicationRejected")
            );
            return;
        }
        if (!Directory.Exists(normalized))
        {
            await ShowErrorAsync(AppLocalization.Format("SitesParkFolderMissing", normalized));
            return;
        }
        RootPathTextBox.Text = normalized;
        if (!Roots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            Roots.Add(normalized);
            SaveRoots();
            await ScanAsync();
        }
    }

    private async void EditRuntimes_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is null) return;
        await ConfigureSiteAsync(selectedSite);
    }

    private async Task ConfigureSiteAsync(SiteRecord site)
    {
        var path = site.Path;
        var phpOptions = new List<DisplayOption>
        {
            new(string.Empty, AppLocalization.Get("SitesRuntimeDefault"))
        };
        phpOptions.AddRange(phpInstaller.InstalledCycles().Select(version => new DisplayOption(version, version)));
        var nodeOptions = new List<DisplayOption>
        {
            new(string.Empty, AppLocalization.Get("SitesRuntimeDefault"))
        };
        nodeOptions.AddRange(nodeInstaller.InstalledVersions().Select(version => new DisplayOption(version, version)));
        var phpBox = new ComboBox
        {
            Header = "PHP",
            ItemsSource = phpOptions,
            SelectedItem = phpOptions.FirstOrDefault(option => option.Value == site.PhpVersion)
                ?? phpOptions[0],
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var nodeBox = new ComboBox
        {
            Header = "Node.js",
            ItemsSource = nodeOptions,
            SelectedItem = nodeOptions.FirstOrDefault(option => option.Value == site.NodeVersion)
                ?? nodeOptions[0],
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var content = new StackPanel { Spacing = 12, MinWidth = 340 };
        content.Children.Add(phpBox);
        content.Children.Add(nodeBox);
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = site.Name,
            Content = content,
            PrimaryButtonText = AppLocalization.Get("SitesSave"),
            CloseButtonText = AppLocalization.Get("SitesCancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var php = (phpBox.SelectedItem as DisplayOption)?.Value;
            var node = (nodeBox.SelectedItem as DisplayOption)?.Value;
            siteRuntimeStore.SetPhp(path, string.IsNullOrEmpty(php) ? null : php);
            siteRuntimeStore.SetNode(path, string.IsNullOrEmpty(node) ? null : node);
            detectionCache.Invalidate(path);
            await ScanAsync();
            App.MainWindow.ShowToast(AppLocalization.Get("CommonSavedToast"));
        }
        catch (Exception error)
        {
            await ShowErrorAsync(error.Message);
        }
        finally
        {
            UpdateEnvironmentState();
        }
    }

    private async void RemoveRoot_Click(object sender, RoutedEventArgs e)
    {
        if (RootList.SelectedItem is string path)
        {
            Roots.Remove(path);
            SaveRoots();
            if (Roots.Count == 0)
            {
                var fallback = settingsStore.Load().Roots[0];
                Roots.Add(fallback);
                RootPathTextBox.Text = fallback;
            }
            await ScanAsync();
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        // An explicit refresh re-detects every project and re-runs Git inspection.
        detectionCache.Clear();
        await ScanAsync();
    }

    private async Task ScanAsync(bool throwOnError = false)
    {
        ScanErrorBar.IsOpen = false;
        var generation = siteScanGeneration.Begin();
        CancelGitInspection();
        var selectedPath = selectedSite?.Path;
        if (Roots.Count == 0)
        {
            var path = RootPathTextBox.Text.Trim();
            if (path.Length > 0) Roots.Add(Path.GetFullPath(path));
        }
        if (Roots.Count == 0)
        {
            return;
        }

        ScanProgress.IsActive = true;
        BeginSitesScan();
        try
        {
            var normalizedSettings = settingsStore.Load();
            var scanned = await coreClient.ScanAsync(
                Roots,
                normalizedSettings.Tld,
                normalizedSettings.LinkedSites
            );
            if (!siteScanGeneration.IsCurrent(generation)) return;
            foreach (var site in scanned)
            {
                site.IsFavorite = normalizedSettings.FavoriteSites.Contains(site.Path, StringComparer.OrdinalIgnoreCase);
                site.LastError = lastSiteErrors.GetValueOrDefault(site.Path);
            }
            // Project probing and cached Git summaries are resolved off the UI thread;
            // the existing rows stay visible until the new list is ready.
            var uninspected = await Task.Run(() => ApplyCachedDetection(scanned));
            if (!siteScanGeneration.IsCurrent(generation)) return;
            Sites.Clear();
            foreach (var site in scanned) Sites.Add(site);
            ApplyFilter(selectedPath);
            ApplyPendingSelection();
            App.RequestJumpListRefresh(scanned);
            UpdateSiteCount();
            if (scanned.Count > 0 && !environment.IsRunning)
            {
                EnvironmentStatusText.Text = AppLocalization.Get("SitesEnvironmentStarting");
            }
            environment.ProxyTld = normalizedSettings.Tld;
            await environment.SynchronizeSitesAsync(scanned);
            if (!siteScanGeneration.IsCurrent(generation)) return;
            UpdateEnvironmentState();
            await RefreshPreviewAsync();
            StartGitInspection(uninspected, generation);
        }
        catch (Exception error)
        {
            if (!siteScanGeneration.IsCurrent(generation)) return;
            if (throwOnError) throw;
            ScanErrorBar.Message = UserErrorPresentation.Describe(error);
            ScanErrorBar.IsOpen = true;
        }
        finally
        {
            if (siteScanGeneration.IsCurrent(generation))
            {
                ScanProgress.IsActive = false;
                EndSitesScan();
                UpdateEnvironmentState();
            }
        }
    }

    private void StartGitInspection(IReadOnlyList<SiteRecord> sites, int generation)
    {
        if (sites.Count == 0) return;
        var cancellation = new CancellationTokenSource();
        gitInspectionCancellation = cancellation;
        _ = InspectGitStatusesInBackgroundAsync(sites, generation, cancellation);
    }

    private async Task InspectGitStatusesInBackgroundAsync(
        IReadOnlyList<SiteRecord> sites,
        int generation,
        CancellationTokenSource cancellation
    )
    {
        try
        {
            var statuses = await SitePresentation.InspectGitStatusesAsync(
                sites,
                cancellation.Token
            );
            if (cancellation.IsCancellationRequested
                || !siteScanGeneration.IsCurrent(generation)) return;

            var summaries = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var site in Sites)
            {
                if (statuses.TryGetValue(site.Path, out var git))
                {
                    site.GitSummary = GitSummary(git);
                    summaries[site.Path] = site.GitSummary;
                }
            }
            ApplyFilter(selectedSite?.Path);
            await Task.Run(() =>
            {
                foreach (var (path, summary) in summaries)
                {
                    detectionCache.StoreGitSummary(path, SitesDetectionCache.CaptureGitStamp(path), summary);
                }
            }, CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!siteScanGeneration.IsCurrent(generation)) return;
            await DiagnosticLog.WriteFailureAsync(
                "site-git",
                "inspect",
                "Git status inspection failed after the site list was loaded.",
                error.ToString()
            );
        }
        finally
        {
            if (ReferenceEquals(gitInspectionCancellation, cancellation))
            {
                gitInspectionCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    // Runs off the UI thread: warms the per-site detection cache and reuses Git summaries
    // whose stamps are unchanged. Returns the sites that still need a Git inspection.
    private IReadOnlyList<SiteRecord> ApplyCachedDetection(IReadOnlyList<SiteRecord> sites)
    {
        var uninspected = new List<SiteRecord>();
        foreach (var site in sites)
        {
            detectionCache.Detect(site.Path);
            var stamp = SitesDetectionCache.CaptureGitStamp(site.Path);
            if (detectionCache.TryGetGitSummary(site.Path, stamp, out var summary))
            {
                site.GitSummary = summary;
            }
            else
            {
                uninspected.Add(site);
            }
        }
        return uninspected;
    }

    private void CancelGitInspection()
    {
        var cancellation = gitInspectionCancellation;
        gitInspectionCancellation = null;
        cancellation?.Cancel();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        searchDebounce ??= CreateSearchTimer();
        searchDebounce.Stop();
        searchDebounce.Start();
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

    private DispatcherTimer CreateSearchTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => { timer.Stop(); if (loaded) ApplyFilter(selectedSite?.Path); };
        return timer;
    }

    private void FavoriteSite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path }) return;
        settingsStore.ToggleFavorite(path);
        var favorites = settingsStore.Load().FavoriteSites;
        foreach (var site in Sites) site.IsFavorite = favorites.Contains(site.Path, StringComparer.OrdinalIgnoreCase);
        ApplyFilter(selectedSite?.Path);
        App.RequestJumpListRefresh(Sites);
    }

    private void SearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != global::Windows.System.VirtualKey.Enter || selectedSite is null) return;
        e.Handled = true;
        OpenSite_Click(sender, e);
    }

    // Incremental: existing rows are kept, moved, or updated in place so the ListView keeps
    // its containers, selection, and scroll offset across searches, scans, and Git updates.
    private void ApplyFilter(string? preferredPath)
    {
        var query = SearchBox.Text.Trim();
        // Running and shared state feed the quick filters, so refresh it for every site.
        foreach (var site in Sites) UpdateWorkflowStatus(site);
        var desired = SiteListFilter.Apply(Sites, query, siteFilter, siteSort);
        var desiredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in desired) desiredPaths.Add(site.Path);

        SitesListItem? selection;
        suppressSiteSelection = true;
        try
        {
            for (var index = VisibleSites.Count - 1; index >= 0; index--)
            {
                if (!desiredPaths.Contains(VisibleSites[index].Path)) VisibleSites.RemoveAt(index);
            }
            var position = 0;
            foreach (var site in desired)
            {
                var current = IndexOfVisibleSite(site.Path, position);
                if (current < 0)
                {
                    VisibleSites.Insert(position, new SitesListItem(site));
                }
                else
                {
                    VisibleSites[current].Update(site);
                    if (current != position) VisibleSites.Move(current, position);
                }
                position++;
            }
            while (VisibleSites.Count > position) VisibleSites.RemoveAt(VisibleSites.Count - 1);

            UpdateListChrome();
            selection = VisibleSites.FirstOrDefault(item => item.Path.Equals(
                preferredPath,
                StringComparison.OrdinalIgnoreCase
            )) ?? VisibleSites.FirstOrDefault();
            if (!ReferenceEquals(SiteList.SelectedItem, selection)) SiteList.SelectedItem = selection;
        }
        finally
        {
            suppressSiteSelection = false;
        }
        // Only a different project (or a rescanned record) rebuilds the details pane.
        if (selection is null || !ReferenceEquals(selectedSite, selection.Site)) ShowSite(selection?.Site);
    }

    private int IndexOfVisibleSite(string path, int start)
    {
        for (var index = start; index < VisibleSites.Count; index++)
        {
            if (VisibleSites[index].Path.Equals(path, StringComparison.OrdinalIgnoreCase)) return index;
        }
        return -1;
    }

    private void UpdateWorkflowStatus(SiteRecord site)
    {
        var process = siteProcesses.State(site.Path, SiteBackgroundProcessKind.Development);
        site.IsRunning = process.Running
            || siteProcesses.State(site.Path, SiteBackgroundProcessKind.Queue).Running
            || siteProcesses.State(site.Path, SiteBackgroundProcessKind.Scheduler).Running;
        site.IsShared = shares.Find(site.Domain) is not null;
        site.WorkflowStatus = process.Running ? AppLocalization.Get("SitesRunning")
            : process.ExitCode is not null and not 0 ? AppLocalization.Get("SitesOperationFailed")
            : AppLocalization.Get("SitesStopped");
        if (process.ExitCode is not null and not 0 && !string.IsNullOrWhiteSpace(process.Output))
            site.LastError = LatestOperationStatus(process.Output);
    }

    private void RefreshWorkflowStatuses()
    {
        // A running or shared filter can gain or lose rows when process state changes.
        if (siteFilter is SiteListFilterKind.Running or SiteListFilterKind.Shared or SiteListFilterKind.Errors)
        {
            ApplyFilter(selectedSite?.Path);
            return;
        }
        foreach (var item in VisibleSites)
        {
            UpdateWorkflowStatus(item.Site);
            item.Update(item.Site);
        }
    }

    private void RefreshListItem(SiteRecord site)
    {
        var index = IndexOfVisibleSite(site.Path, 0);
        if (index >= 0) VisibleSites[index].Update(VisibleSites[index].Site);
    }

    private void SiteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressSiteSelection) return;
        var site = (SiteList.SelectedItem as SitesListItem)?.Site;
        if (!ReferenceEquals(site, selectedSite)) ShowSite(site);
    }

    private void ShowSite(SiteRecord? site)
    {
        selectedSite = site;
        NoSelectionState.Visibility = site is null ? Visibility.Visible : Visibility.Collapsed;
        SiteDetail.Visibility = site is null ? Visibility.Collapsed : Visibility.Visible;
        StartLaravelButton.IsEnabled = false;
        if (site is null) return;

        SiteNameText.Text = site.Name;
        UpdateHeaderTile(site);
        DomainText.Text = site.Domain;
        FrameworkText.Text = site.Framework;
        var phpCycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
        var phpVersion = phpInstaller.InstalledVersion(phpCycle);
        var nodeVersion = site.NodeVersion ?? nodeInstaller.LoadSettings().ActiveVersion;
        RuntimeText.Text = AppLocalization.Format(
            "SitesRuntimeDetails",
            phpVersion is null ? phpCycle : $"{phpCycle} ({phpVersion})",
            string.IsNullOrWhiteSpace(nodeVersion)
                ? AppLocalization.Get("SitesProjectNodeRuntime")
                : nodeVersion
        );
        RegistrationText.Text = AppLocalization.Get(site.Linked ? "SitesLinked" : "SitesParked");
        PathText.Text = site.Path;
        UnlinkButton.Visibility = site.Linked ? Visibility.Visible : Visibility.Collapsed;
        RemoveSiteButton.Visibility = site.Linked ? Visibility.Collapsed : Visibility.Visible;
        UpdateShareState();
        // File probing and .env parsing run in RefreshSiteDetailsAsync off the UI thread;
        // the cached detection keeps the actions usable while it revalidates.
        ApplySiteDetection(site, detectionCache.Peek(site.Path));
        DatabaseDetailsText.Text = AppLocalization.Get("SitesDetailsChecking");
        HealthDetailsText.Text = AppLocalization.Get("SitesHealthCheckAvailable");
        UpdatePerformanceDetails(site);
        UrlButton.Content = SitePresentation.DisplayAddress(
            site,
            environment.IsRunning,
            environment.HttpPort,
            environment.HttpsPort
        );
        _ = RefreshSiteDetailsAsync(site);
        UpdateBackgroundProcessState();
        _ = RefreshPreviewAsync();
    }

    private void ApplySiteDetection(SiteRecord site, SitesDetection? detection)
    {
        ArtisanButton.IsEnabled = site.Framework == "Laravel" && detection?.HasArtisan == true;
        StartLaravelButton.IsEnabled = ArtisanButton.IsEnabled;
        QualityMenu.IsEnabled = ArtisanButton.IsEnabled;
        NpmButton.IsEnabled = detection?.HasPackageJson == true;
        ComposerButton.IsEnabled = detection?.HasComposerJson == true;
    }

    // Process events only refresh row status text; bursts are coalesced into one UI update
    // and deferred entirely while the main window is hidden.
    private void SiteProcesses_Changed(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref processRefreshPending, 1) == 1) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Volatile.Write(ref processRefreshPending, 0);
            if (!loaded) return;
            if (!App.IsMainWindowVisible)
            {
                listRefreshDeferred = true;
                return;
            }
            RefreshWorkflowStatuses();
            UpdateBackgroundProcessState();
        }))
        {
            Volatile.Write(ref processRefreshPending, 0);
        }
    }

    private void UpdateBackgroundProcessState()
    {
        if (selectedSite is not { } site) return;
        var development = siteProcesses.State(site.Path, SiteBackgroundProcessKind.Development);
        BackgroundProcessesText.Text = AppLocalization.Format(
            "SitesDevelopmentStatus",
            development.Running ? AppLocalization.Get("SitesRunning") : AppLocalization.Get("SitesStopped")
        );
        ProcessesDetailsText.Text = BackgroundProcessesText.Text;
        StartLaravelIcon.Symbol = development.Running ? Symbol.Stop : Symbol.Play;
        StartLaravelLabel.Text = AppLocalization.Get(
            development.Running ? "SitesStopLaravelButton" : "SitesStartLaravelButton"
        );
    }

    private void SaveRoots()
    {
        settingsStore.UpdateRoots(Roots);
    }

    private async Task ShowErrorAsync(string message)
    {
        if (selectedSite is { } site)
        {
            lastSiteErrors[site.Path] = message;
            site.LastError = message;
            RefreshListItem(site);
        }
        if (!loaded || XamlRoot is null) return;
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = "HerdMe",
            Content = message,
            CloseButtonText = AppLocalization.Get("SitesOk")
        };
        await dialog.ShowAsync();
    }
}
