using System.Collections.Concurrent;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using HerdMe.Windows.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

public sealed partial class DashboardPage : Page
{
    // Endpoint probes send a real GET / through the local server, which boots the whole
    // application. Healthy results are reused for a short time (the page is recreated on every
    // navigation), failures are always re-probed, and the Refresh button bypasses the cache.
    private static readonly TimeSpan EndpointProbeCacheLifetime = TimeSpan.FromSeconds(60);
    private const int MaximumConcurrentEndpointProbes = 2;
    private static readonly ConcurrentDictionary<string, (DateTimeOffset CheckedAt, RuntimeHealthResult Result)>
        EndpointProbeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly CoreClient coreClient;
    private readonly SiteConfigurationStore settingsStore;
    private readonly WindowsLocalEnvironment environment;
    private readonly WindowsServiceManager serviceManager;
    private readonly MailCaptureService mailCapture;
    private readonly DumpCaptureService dumpCapture;
    private readonly WindowsHostsManager hostsManager;
    private readonly WindowsCertificateManager certificateManager;
    private readonly PhpRuntimeInstaller phpInstaller;
    private readonly PhpRuntimePolicy runtimePolicy;
    private readonly ComposerToolManager composerTools;
    private readonly NodeRuntimeInstaller nodeInstaller;
    private readonly GitRuntimeInstaller gitInstaller;
    private readonly OperationJournal repairJournal;
    private readonly StartupSnapshotStore? startupSnapshot;
    // Repair all can run for many minutes (composer/npm). It is shared by every dashboard
    // instance so a second click, a recreated page, or a cached page that is loaded again
    // reattaches to the running repair instead of starting another one.
    private static RepairSession? activeRepair;
    private static DashboardPage? loadedPage;
    private static string? pendingRepairSummary;
    private static bool pendingRepairNeedsAttention;
    // The compact-mode preference is cached so SizeChanged never touches the settings file;
    // it is refreshed with the rest of the dashboard data.
    private static bool compactModeSetting;
    private CancellationTokenSource? refreshCancellation;
    private bool? usesCompactLayout;
    private bool lifecycleAttached;
    private bool summaryCountsShown;
    private RepairSession? observedRepair;
    private readonly HashSet<string> failedSiteNames = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> lastHealthWarnings = [];
    private readonly DispatcherTimer environmentRefreshTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2)
    };
    private (bool Running, bool Degraded, int? Http, int? Https)? displayedEnvironment;

    public DashboardPage(
        CoreClient coreClient,
        SiteConfigurationStore settingsStore,
        WindowsLocalEnvironment environment,
        WindowsServiceManager serviceManager,
        MailCaptureService mailCapture,
        DumpCaptureService dumpCapture,
        WindowsHostsManager hostsManager,
        WindowsCertificateManager certificateManager,
        PhpRuntimeInstaller phpInstaller,
        PhpRuntimePolicy runtimePolicy,
        ComposerToolManager composerTools,
        NodeRuntimeInstaller nodeInstaller,
        GitRuntimeInstaller gitInstaller,
        StartupSnapshotStore? startupSnapshot = null
    )
    {
        this.coreClient = coreClient;
        this.settingsStore = settingsStore;
        this.environment = environment;
        this.serviceManager = serviceManager;
        this.mailCapture = mailCapture;
        this.dumpCapture = dumpCapture;
        this.hostsManager = hostsManager;
        this.certificateManager = certificateManager;
        this.phpInstaller = phpInstaller;
        this.runtimePolicy = runtimePolicy;
        this.composerTools = composerTools;
        this.nodeInstaller = nodeInstaller;
        this.gitInstaller = gitInstaller;
        this.startupSnapshot = startupSnapshot;
        repairJournal = new OperationJournal(Path.Combine(settingsStore.SupportRoot, "Repair"));
        InitializeComponent();
        environmentRefreshTimer.Tick += EnvironmentRefresh_Tick;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        // Pages may be cached and loaded several times (and Loaded can repeat without an
        // Unloaded in between), so subscriptions are guarded by a flag.
        if (!lifecycleAttached)
        {
            lifecycleAttached = true;
            App.MainWindowVisibilityChanged += App_MainWindowVisibilityChanged;
            App.MainWindow.TimelineChanged += MainWindow_TimelineChanged;
            App.MainWindow.GettingStartedChanged += MainWindow_GettingStartedChanged;
        }
        loadedPage = this;
        RenderTimeline();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => App.MainWindow.OfferShellTips(HealthTabTip));
        if (activeRepair is { } repair) ObserveRepair(repair);
        ShowPendingRepairSummary();
        if (App.IsMainWindowVisible) environmentRefreshTimer.Start();
        else environmentRefreshTimer.Stop();
        ShowSnapshotCounts();
        await RefreshAsync();
    }

    private void App_MainWindowVisibilityChanged(object? sender, bool visible)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ApplyMainWindowVisibility(visible));
            return;
        }
        ApplyMainWindowVisibility(visible);
    }

    private async void ApplyMainWindowVisibility(bool visible)
    {
        if (!lifecycleAttached) return;
        if (!visible)
        {
            // Hidden to the tray: nothing is on screen, so stop polling.
            environmentRefreshTimer.Stop();
            return;
        }
        if (environmentRefreshTimer.IsEnabled) return;
        environmentRefreshTimer.Start();
        await RefreshAsync();
    }

    private async void EnvironmentRefresh_Tick(object? sender, object e)
    {
        // A dashboard opened during startup/recovery must not keep showing
        // "0 running" after the environment has recovered in the background.
        var current = (environment.IsRunning, environment.IsDegraded,
            environment.HttpPort, environment.HttpsPort);
        if (displayedEnvironment == current || refreshCancellation is not null
            || activeRepair is not null) return;
        await RefreshAsync();
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // SizeChanged fires continuously while resizing; it only uses cached settings.
        ApplyLayout(e.NewSize.Width < 820, force: false);
    }

    private void ApplyLayout(bool compact, bool force)
    {
        var compactMode = compactModeSetting;
        if (!force && usesCompactLayout == compact) return;
        usesCompactLayout = compact;

        DashboardLayout.Padding = compact || compactMode
            ? new Thickness(18, 18, 18, 20)
            : new Thickness(28, 24, 28, 24);
        DashboardLayout.RowSpacing = compactMode ? 10 : 18;
        var summaryHeight = compactMode ? 112 : 136;
        SitesCard.MinHeight = summaryHeight;
        ServicesCard.MinHeight = summaryHeight;
        MailCard.MinHeight = summaryHeight;
        DumpsCard.MinHeight = summaryHeight;
        SummaryCardsGrid.RowSpacing = compact ? 12 : 0;
        SummaryColumn0.Width = new GridLength(1, GridUnitType.Star);
        SummaryColumn1.Width = new GridLength(1, GridUnitType.Star);
        SummaryColumn2.Width = compact
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        SummaryColumn3.Width = compact
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        PositionSummaryCard(SitesCard, row: 0, column: 0);
        PositionSummaryCard(ServicesCard, row: 0, column: 1);
        PositionSummaryCard(MailCard, row: compact ? 1 : 0, column: compact ? 0 : 2);
        PositionSummaryCard(DumpsCard, row: compact ? 1 : 0, column: compact ? 1 : 3);

        // Narrow windows move the health actions and status pills under their text
        // instead of squeezing the text column.
        Grid.SetRow(HealthActions, compact ? 1 : 0);
        Grid.SetColumn(HealthActions, compact ? 1 : 2);
        HealthActions.Margin = compact ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        PositionEnvironmentRow(EnvironmentStatusPill, compact);
        PositionEnvironmentRow(DomainsStatusPill, compact);
        PositionEnvironmentRow(CertificateStatusPill, compact);

        PositionQuickActions(compact);
        ApplyHomeLayout(compact);
        RecentActivityGrid.RowSpacing = compact ? 12 : 0;
        Grid.SetRow(RecentDumpsPanel, compact ? 1 : 0);
        Grid.SetColumn(RecentDumpsPanel, compact ? 0 : 1);
    }

    private static void PositionSummaryCard(Button card, int row, int column)
    {
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
    }

    private static void PositionEnvironmentRow(Border statusPill, bool compact)
    {
        Grid.SetRow(statusPill, compact ? 1 : 0);
        Grid.SetColumn(statusPill, compact ? 1 : 2);
        statusPill.HorizontalAlignment = compact
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        statusPill.Margin = compact ? new Thickness(0, 8, 0, 0) : new Thickness(0);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if (lifecycleAttached)
        {
            lifecycleAttached = false;
            App.MainWindowVisibilityChanged -= App_MainWindowVisibilityChanged;
            App.MainWindow.TimelineChanged -= MainWindow_TimelineChanged;
            App.MainWindow.GettingStartedChanged -= MainWindow_GettingStartedChanged;
        }
        if (ReferenceEquals(loadedPage, this)) loadedPage = null;
        StopObservingRepair();
        environmentRefreshTimer.Stop();
        refreshCancellation?.Cancel();
        refreshCancellation?.Dispose();
        refreshCancellation = null;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync(forceEndpointProbes: true);
    }

    private async Task RefreshAsync(bool forceEndpointProbes = false)
    {
        refreshCancellation?.Cancel();
        refreshCancellation?.Dispose();
        using var cancellation = new CancellationTokenSource();
        refreshCancellation = cancellation;
        RefreshButton.IsEnabled = false;
        RefreshProgress.IsActive = true;

        try
        {
            var settings = await Task.Run(settingsStore.Load, cancellation.Token);
            if (compactModeSetting != settings.CompactMode)
            {
                compactModeSetting = settings.CompactMode;
                if (usesCompactLayout is { } compact) ApplyLayout(compact, force: true);
            }
            var sitesTask = coreClient.ScanAsync(
                settings.Roots,
                settings.Tld,
                settings.LinkedSites,
                cancellation.Token
            );
            var servicesTask = Task.Run(serviceManager.LoadInstances, cancellation.Token);
            var mailTask = Task.Run(mailCapture.Load, cancellation.Token);
            var dumpsTask = Task.Run(dumpCapture.Load, cancellation.Token);
            var domainsTask = hostsManager.HasManagedMappingsAsync(cancellation.Token);
            var certificateTask = Task.Run(certificateManager.IsAuthorityTrusted, cancellation.Token);

            await Task.WhenAll(
                sitesTask,
                servicesTask,
                mailTask,
                dumpsTask,
                domainsTask,
                certificateTask
            );
            cancellation.Token.ThrowIfCancellationRequested();

            var sites = await sitesTask;
            RememberSitePaths(sites);
            var instances = await servicesTask;
            var messages = await mailTask;
            var dumps = await dumpsTask;
            var domainsConfigured = await domainsTask;
            var certificateTrusted = await certificateTask;
            // Counts, recent items and quick actions come first; the health checks below can
            // take seconds (PHP and endpoint probes) and fill in the rest afterwards.
            var runningSites = environment.IsRunning ? sites.Count : 0;
            var runningServices = instances.Count(instance =>
                serviceManager.State(instance.Id, instance.DefinitionId) == ManagedServiceState.Running
            );

            SitesCountText.Text = sites.Count.ToString();
            SitesStatusText.Text = AppLocalization.Format(
                "DashboardRunningCount",
                runningSites,
                sites.Count
            );
            SitesStatusDot.Style = StatusStyles.Dot(SummaryStatusTone(runningSites, sites.Count));
            ServicesCountText.Text = instances.Count.ToString();
            ServicesStatusText.Text = AppLocalization.Format(
                "DashboardRunningCount",
                runningServices,
                instances.Count
            );
            ServicesStatusDot.Style = StatusStyles.Dot(
                SummaryStatusTone(runningServices, instances.Count)
            );
            MailCountText.Text = messages.Count.ToString();
            MailStatusText.Text = AppLocalization.Get(
                mailCapture.IsRunning ? "DashboardCaptureRunning" : "DashboardCaptureStopped"
            );
            MailStatusDot.Style = StatusStyles.Dot(CaptureStatusTone(mailCapture.IsRunning));
            DumpsCountText.Text = dumps.Count.ToString();
            DumpsStatusText.Text = AppLocalization.Get(
                dumpCapture.IsRunning ? "DashboardCaptureRunning" : "DashboardCaptureStopped"
            );
            DumpsStatusDot.Style = StatusStyles.Dot(CaptureStatusTone(dumpCapture.IsRunning));
            ShowSummaryCounts();
            RenderRecentMail(messages);
            RenderRecentDumps(dumps);
            UpdateQuickActions(sites, settings);
            UpdateGettingStarted(settings, sites, messages.Count, dumps.Count);
            var defaultPhpCycle = (await Task.Run(runtimePolicy.Load, cancellation.Token)).PhpCycle;
            RenderActiveServices(instances, defaultPhpCycle);
            await RenderGlobalPhpAsync(defaultPhpCycle, cancellation.Token);
            var certificateExpiryTask = Task.Run(
                certificateManager.ServerCertificateExpiresAt,
                cancellation.Token
            );
            // Sites usually share one or two PHP versions; validate each php.exe once per
            // refresh instead of spawning php/core for every site.
            var extensionReports = new ConcurrentDictionary<string, Lazy<Task<PhpExtensionReport>>>(
                StringComparer.OrdinalIgnoreCase
            );
            Task<PhpExtensionReport> SharedExtensionReport(string phpExecutable, CancellationToken token)
            {
                return extensionReports.GetOrAdd(
                    phpExecutable,
                    path => new Lazy<Task<PhpExtensionReport>>(
                        () => phpInstaller.ManagedExtensionReportAsync(path, token)
                    )
                ).Value;
            }
            // SiteHealthInspector performs bounded filesystem and JSON reads
            // before its asynchronous PHP checks. Keep that work off the UI
            // thread when several sites are present.
            var healthTasks = sites.Select(site => Task.Run(
                () => SiteHealthInspector.InspectAsync(
                    site.Path,
                    site.Domain,
                    site.PhpVersion ?? defaultPhpCycle,
                    phpInstaller,
                    composerTools,
                    certificateManager,
                    site.NodeVersion,
                    SharedExtensionReport,
                    cancellation.Token
                ),
                cancellation.Token
            ));
            var siteHealth = (await Task.WhenAll(healthTasks))
                .SelectMany((checks, index) => checks
                    .Where(check => !check.Healthy)
                    .Select(check => $"{sites[index].Name}: {HealthCheckNames.Display(check.Name)} - {check.Detail}"))
                .ToArray();
            if (environment.IsRunning)
            {
                using var probeGate = new SemaphoreSlim(MaximumConcurrentEndpointProbes);
                var https = environment.HttpsPort is not null;
                var probeScope = $"{environment.HttpPort}|{environment.HttpsPort}|{https}";
                async Task<RuntimeHealthResult> ProbeEndpointAsync(string domain)
                {
                    var key = $"{probeScope}|{domain}";
                    if (!forceEndpointProbes
                        && EndpointProbeCache.TryGetValue(key, out var cached)
                        && DateTimeOffset.UtcNow - cached.CheckedAt < EndpointProbeCacheLifetime)
                    {
                        return cached.Result;
                    }
                    await probeGate.WaitAsync(cancellation.Token);
                    try
                    {
                        var result = await RuntimeHealthInspector.InspectSiteAsync(
                            domain,
                            https,
                            cancellation.Token
                        );
                        if (result.Healthy) EndpointProbeCache[key] = (DateTimeOffset.UtcNow, result);
                        else EndpointProbeCache.TryRemove(key, out _);
                        return result;
                    }
                    finally
                    {
                        probeGate.Release();
                    }
                }
                var endpointResults = await Task.WhenAll(sites.Select(site =>
                    ProbeEndpointAsync(site.Domain)));
                siteHealth = siteHealth.Concat(endpointResults
                    .Select((result, index) => (result, index))
                    .Where(item => !item.result.Healthy)
                    .Select(item => $"{sites[item.index].Name}: {item.result.Name} - {item.result.Detail}"))
                    .ToArray();
            }
            var certificateExpiry = await certificateExpiryTask;
            if (certificateExpiry is { } expiry && expiry <= DateTimeOffset.UtcNow.AddDays(30))
            {
                siteHealth = siteHealth.Append(AppLocalization.Format(
                    "Dashboard_Health_CertificateExpires",
                    expiry.LocalDateTime.ToString("d", System.Globalization.CultureInfo.CurrentCulture)
                )).ToArray();
            }
            var serviceHealth = await Task.WhenAll(instances
                .Where(instance => serviceManager.State(instance.Id, instance.DefinitionId)
                    == ManagedServiceState.Running)
                .Select(instance => RuntimeHealthInspector.InspectTcpServiceAsync(
                    instance.Name,
                    instance.Port,
                    cancellation.Token)));
            siteHealth = siteHealth.Concat(serviceHealth
                .Where(result => !result.Healthy)
                .Select(result => AppLocalization.Format(
                    "Dashboard_Health_ServiceUnhealthy",
                    result.Name,
                    result.Detail
                )))
                .ToArray();
            var duplicatePorts = instances.GroupBy(instance => instance.Port)
                .Where(group => group.Count() > 1)
                .Select(group => AppLocalization.Format(
                    "Dashboard_Health_DuplicatePort",
                    group.Key,
                    string.Join(", ", group.Select(instance => instance.Name))
                ));
            siteHealth = siteHealth.Concat(duplicatePorts).ToArray();
            UpdateEnvironmentStatus(domainsConfigured, certificateTrusted, settings.Tld);
            UpdateHealth(domainsConfigured, certificateTrusted, failure: null, siteHealth);
            displayedEnvironment = (environment.IsRunning, environment.IsDegraded,
                environment.HttpPort, environment.HttpsPort);
            RenderTimeline();
            SaveStartupCounts(instances.Count, messages.Count, dumps.Count, lastHealthWarnings.Count);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            UpdateHealth(domainsConfigured: false, certificateTrusted: false, failure: error.Message, []);
            ShowSummaryCounts();
            displayedEnvironment = (environment.IsRunning, environment.IsDegraded,
                environment.HttpPort, environment.HttpsPort);
        }
        finally
        {
            if (ReferenceEquals(refreshCancellation, cancellation))
            {
                refreshCancellation = null;
                RefreshButton.IsEnabled = activeRepair is null;
                RefreshProgress.IsActive = activeRepair is not null;
            }
        }
    }

    // The summary numbers stay as placeholders until the first refresh, so "0 sites" is never
    // shown for a moment before the real count.
    private void ShowSummaryCounts()
    {
        if (summaryCountsShown) return;
        summaryCountsShown = true;
        foreach (var (count, skeleton) in new (FrameworkElement, FrameworkElement)[]
        {
            (SitesCountText, SitesCountSkeleton),
            (ServicesCountText, ServicesCountSkeleton),
            (MailCountText, MailCountSkeleton),
            (DumpsCountText, DumpsCountSkeleton)
        })
        {
            skeleton.Visibility = Visibility.Collapsed;
            count.Visibility = Visibility.Visible;
        }
    }

    private static StatusTone SummaryStatusTone(int running, int total)
    {
        return total == 0
            ? StatusTone.Neutral
            : running == total
                ? StatusTone.Success
                : running > 0 ? StatusTone.Caution : StatusTone.Critical;
    }

    private static StatusTone CaptureStatusTone(bool running)
    {
        return running ? StatusTone.Success : StatusTone.Critical;
    }

    private static void ApplyStatusPill(
        Border pill,
        Microsoft.UI.Xaml.Shapes.Ellipse dot,
        StatusTone tone
    )
    {
        pill.Style = StatusStyles.Pill(tone);
        dot.Style = StatusStyles.Dot(tone);
    }

    private static Style TextStyle(string key)
    {
        return (Style)Application.Current.Resources[key];
    }

    private void UpdateEnvironmentStatus(
        bool domainsConfigured,
        bool certificateTrusted,
        string tld
    )
    {
        ApplyStatusPill(
            EnvironmentStatusPill,
            EnvironmentStatusDot,
            environment.IsRunning
                ? StatusTone.Success
                : environment.IsDegraded ? StatusTone.Caution : StatusTone.Critical
        );
        ApplyStatusPill(
            DomainsStatusPill,
            DomainsStatusDot,
            domainsConfigured ? StatusTone.Success : StatusTone.Caution
        );
        ApplyStatusPill(
            CertificateStatusPill,
            CertificateStatusDot,
            certificateTrusted ? StatusTone.Success : StatusTone.Caution
        );
        EnvironmentStatusText.Text = AppLocalization.Get(
            environment.IsRunning
                ? "DashboardRunning"
                : environment.IsDegraded ? "DashboardRecovering" : "DashboardStopped"
        );
        EnvironmentDetailText.Text = AppLocalization.Get(
            environment.IsRunning
                ? environment.HttpsPort is not null ? "DashboardServingHttps" : "DashboardServingHttp"
                : "DashboardNotServing"
        );
        DomainsStatusText.Text = AppLocalization.Get(
            domainsConfigured ? "DashboardConfigured" : "DashboardNotConfigured"
        );
        DomainsDetailText.Text = AppLocalization.Format("DashboardDomainDetail", tld);
        CertificateStatusText.Text = AppLocalization.Get(
            certificateTrusted ? "DashboardTrusted" : "DashboardNotTrusted"
        );
        CertificateDetailText.Text = AppLocalization.Get(
            environment.IsRunning && environment.HttpsPort is not null
                ? "DashboardHttpsActive" : "DashboardConfigureHttps"
        );
    }

    private void UpdateHealth(
        bool domainsConfigured,
        bool certificateTrusted,
        string? failure,
        IReadOnlyList<string> siteWarnings
    )
    {
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(failure))
        {
            warnings.Add(AppLocalization.Format("DashboardRefreshFailed", failure));
        }
        if (environment.IsDegraded)
        {
            warnings.Add(AppLocalization.Get("DashboardEnvironmentRecoveringWarning"));
        }
        if (!domainsConfigured)
        {
            warnings.Add(AppLocalization.Get("DashboardDomainsWarning"));
        }
        if (!certificateTrusted)
        {
            warnings.Add(AppLocalization.Get("DashboardCertificateWarning"));
        }
        var portWarnings = WebPortConflictWarnings();
        warnings.AddRange(portWarnings.Keys);

        var healthy = warnings.Count == 0 && siteWarnings.Count == 0;
        var tone = !string.IsNullOrWhiteSpace(failure)
            ? StatusTone.Critical
            : healthy ? StatusTone.Success : StatusTone.Caution;
        HealthIconTile.Style = StatusStyles.Pill(tone);
        HealthGlyph.Style = StatusStyles.Glyph(tone);
        HealthGlyph.Glyph = tone switch
        {
            StatusTone.Success => "\uE930",
            StatusTone.Critical => "\uE783",
            _ => "\uE7BA"
        };
        HealthTitle.Text = AppLocalization.Get(
            healthy ? "DashboardEverythingReady" : "DashboardNeedsAttention"
        );
        HealthMessage.Text = AppLocalization.Get(
            healthy ? "DashboardHealthyDetail" : "DashboardWarningsDetail"
        );
        WarningList.Children.Clear();
        WarningPanel.Visibility = healthy ? Visibility.Collapsed : Visibility.Visible;
        // Environment-wide problems first, each with its own repair; then one group per site
        // with a way to open it; then everything else.
        if (warnings.Count > 0) WarningList.Children.Add(HealthGroupHeader(AppLocalization.Get("DashboardHealthGroupEnvironment"), null, null));
        foreach (var warning in warnings)
        {
            RoutedEventHandler? repair = warning == AppLocalization.Get("DashboardDomainsWarning")
                ? RepairDomains_Click
                : warning == AppLocalization.Get("DashboardCertificateWarning")
                    ? RepairCertificate_Click
                    : warning == AppLocalization.Get("DashboardEnvironmentRecoveringWarning")
                        ? RepairEnvironment_Click
                        : null;
            if (portWarnings.TryGetValue(warning, out var portDetail))
            {
                WarningList.Children.Add(HealthIssueRow(
                    warning,
                    RepairEnvironment_Click,
                    portDetail,
                    AppLocalization.Get("DashboardFixRetryAction")
                ));
                continue;
            }
            WarningList.Children.Add(repair == RepairCertificate_Click
                ? HealthIssueRow(
                    warning,
                    repair,
                    AppLocalization.Get("DashboardFixCertificateDetail"),
                    AppLocalization.Get("DashboardFixCertificateAction")
                )
                : HealthIssueRow(warning, repair));
        }
        RenderSiteWarningGroups(siteWarnings);
        lastHealthWarnings = warnings.Concat(siteWarnings).ToArray();
        UpdateHealthStrip(tone, lastHealthWarnings.Count, healthy);
        failedSiteNames.Clear();
        foreach (var warning in siteWarnings)
        {
            var separator = warning.IndexOf(':');
            if (separator > 0) failedSiteNames.Add(warning[..separator]);
        }
    }

    private UIElement HealthIssueRow(
        string message,
        RoutedEventHandler? repair,
        string? detail = null,
        string? actionLabel = null
    )
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new FontIcon
        {
            Glyph = "\uE7BA",
            Style = StatusStyles.Glyph(StatusTone.Caution),
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center
        });
        // A fix-it card: what is wrong, then why and what the button will do.
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(detail))
        {
            text.Children.Add(new TextBlock
            {
                Text = detail,
                TextWrapping = TextWrapping.Wrap,
                Style = TextStyle("CaptionTextStyle")
            });
        }
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        if (repair is not null)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(new SymbolIcon(Symbol.Repair));
            content.Children.Add(new TextBlock { Text = actionLabel ?? AppLocalization.Get("DashboardRepairAction") });
            var button = new Button { Content = content };
            button.Click += repair;
            ToolTipService.SetToolTip(button, AppLocalization.Get("DashboardRepairTooltip"));
            Grid.SetColumn(button, 2);
            row.Children.Add(button);
        }
        return row;
    }

    private async void RepairDomains_Click(object sender, RoutedEventArgs e)
    {
        await RunHealthRepairAsync(async settings =>
        {
            var sites = await coreClient.ScanAsync(settings.Roots, settings.Tld, settings.LinkedSites);
            await hostsManager.EnsureMappingsAsync(sites.Select(site => site.Domain));
        });
    }

    private async void RepairCertificate_Click(object sender, RoutedEventArgs e)
    {
        await RunHealthRepairAsync(settings =>
        {
            certificateManager.TrustAuthority();
            return Task.CompletedTask;
        });
    }

    private async void RepairEnvironment_Click(object sender, RoutedEventArgs e)
    {
        await RunHealthRepairAsync(async settings =>
        {
            var sites = await coreClient.ScanAsync(settings.Roots, settings.Tld, settings.LinkedSites);
            await environment.StartAsync(sites);
        });
    }

    private async void RepairAll_Click(object sender, RoutedEventArgs e)
    {
        await RepairAllAsync(retryFailedOnly: false);
    }

    private async void RetryFailed_Click(object sender, RoutedEventArgs e)
    {
        await RepairAllAsync(retryFailedOnly: true);
    }

    private void RepairCancel_Click(object sender, RoutedEventArgs e)
    {
        activeRepair?.Cancel();
    }

    // Domains, certificate, environment and services are fixed steps; every site and the
    // final development-server restart are counted on top of them.
    private const int RepairFixedSteps = 4;

    private readonly record struct RepairProgress(string Message, int Step, int TotalSteps);

    private sealed record RepairPlan(
        IReadOnlyList<SiteRecord> AllSites,
        IReadOnlyList<SiteRecord> Sites,
        int TotalSteps,
        int Actions
    );

    private sealed class RepairSession : IDisposable
    {
        public CancellationTokenSource Cancellation { get; } = new();

        public RepairProgress Progress { get; private set; } = new(string.Empty, 0, 0);

        public bool IsCancelling { get; private set; }

        public bool IsFinished { get; private set; }

        public event EventHandler? Changed;

        public void Report(RepairProgress progress)
        {
            if (IsFinished) return;
            Progress = progress;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Cancel()
        {
            if (IsFinished || IsCancelling) return;
            IsCancelling = true;
            Changed?.Invoke(this, EventArgs.Empty);
            // The command runners kill their process trees when this token is cancelled.
            Cancellation.Cancel();
        }

        public void Finish()
        {
            if (IsFinished) return;
            IsFinished = true;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            Cancellation.Dispose();
        }
    }

    private void ObserveRepair(RepairSession repair)
    {
        if (!ReferenceEquals(observedRepair, repair))
        {
            StopObservingRepair();
            observedRepair = repair;
            repair.Changed += ObservedRepair_Changed;
        }
        RenderRepairProgress(repair);
    }

    private void StopObservingRepair()
    {
        if (observedRepair is null) return;
        observedRepair.Changed -= ObservedRepair_Changed;
        observedRepair = null;
    }

    private void ObservedRepair_Changed(object? sender, EventArgs e)
    {
        if (sender is RepairSession repair) RenderRepairProgress(repair);
    }

    private void RenderRepairProgress(RepairSession repair)
    {
        if (repair.IsFinished)
        {
            StopObservingRepair();
            RepairProgressInfoBar.IsOpen = false;
            RepairAllButton.IsEnabled = true;
            RefreshButton.IsEnabled = refreshCancellation is null;
            RefreshProgress.IsActive = refreshCancellation is not null;
            RestoreHealthStrip();
            return;
        }

        RepairAllButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        RefreshProgress.IsActive = true;
        RepairProgressInfoBar.IsOpen = true;
        RepairCancelButton.IsEnabled = !repair.IsCancelling;
        var progress = repair.Progress;
        RepairProgressText.Text = repair.IsCancelling
            ? AppLocalization.Get("Dashboard_Repair_Cancelling")
            : progress.Message;
        ShowRepairInHealthStrip(RepairProgressText.Text);
        if (progress.TotalSteps > 0 && !repair.IsCancelling)
        {
            RepairProgressIndicator.IsIndeterminate = false;
            RepairProgressIndicator.Maximum = progress.TotalSteps;
            RepairProgressIndicator.Value = Math.Clamp(progress.Step - 1, 0, progress.TotalSteps);
            RepairProgressCountText.Text = AppLocalization.Format(
                "Dashboard_Repair_ProgressCount",
                progress.Step,
                progress.TotalSteps
            );
            RepairProgressCountText.Visibility = Visibility.Visible;
        }
        else
        {
            RepairProgressIndicator.IsIndeterminate = true;
            RepairProgressCountText.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowPendingRepairSummary()
    {
        if (pendingRepairSummary is not { } summary) return;
        pendingRepairSummary = null;
        ShowRepairResultBar(summary, pendingRepairNeedsAttention);
    }

    private void ShowRepairResultBar(string summary, bool needsAttention)
    {
        RepairResultInfoBar.Title = AppLocalization.Get("DashboardRepairAllTitle");
        RepairResultInfoBar.Message = summary;
        RepairResultInfoBar.Severity = needsAttention ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        RepairResultInfoBar.IsOpen = true;
    }

    private async Task RepairAllAsync(bool retryFailedOnly)
    {
        if (activeRepair is { } running)
        {
            // Never start a second repair; bring the running one into view instead.
            ShowTab("health");
            ObserveRepair(running);
            RepairProgressInfoBar.StartBringIntoView();
            return;
        }

        using var session = new RepairSession();
        activeRepair = session;
        ShowTab("health");
        ObserveRepair(session);
        RepairResultInfoBar.IsOpen = false;
        pendingRepairSummary = null;
        var token = session.Cancellation.Token;
        // Created on the UI thread, so reports from background work are marshalled back.
        IProgress<RepairProgress> progress = new Progress<RepairProgress>(session.Report);
        var actions = 0;
        var cancelled = false;
        var skipped = new List<string>();
        var warningsBeforeRepair = lastHealthWarnings.ToArray();
        var failedNames = failedSiteNames.ToArray();
        var hasRetryTargets = failedSiteNames.Count > 0 || lastHealthWarnings.Count > 0;
        try
        {
            progress.Report(new RepairProgress(AppLocalization.Get("Dashboard_Repair_StepPreparing"), 0, 0));
            await Task.Run(() => repairJournal.Append(
                "dashboard-repair",
                "started",
                retryFailedOnly ? "retry-failed" : "all"
            ));
            var settings = await Task.Run(settingsStore.Load, token);
            var missingLinkedSites = await Task.Run(
                () => settings.LinkedSites.Where(path => !Directory.Exists(path)).ToArray(),
                token
            );
            if (missingLinkedSites.Length > 0)
            {
                var removeMissing = false;
                var dialogHost = loadedPage;
                if (dialogHost is { IsLoaded: true, XamlRoot: { } dialogRoot } && App.IsMainWindowVisible)
                {
                    var cleanupDialog = new ContentDialog
                    {
                        FlowDirection = AppLocalization.LayoutDirection,
                        Title = AppLocalization.Get("DashboardMissingSitesTitle"),
                        Content = new ScrollViewer
                        {
                            Content = new TextBlock
                            {
                                Text = AppLocalization.Format(
                                    "DashboardMissingSitesMessage",
                                    string.Join(Environment.NewLine, missingLinkedSites.Select(Path.GetFileName))
                                ),
                                TextWrapping = TextWrapping.Wrap
                            }
                        },
                        PrimaryButtonText = AppLocalization.Get("DashboardRemoveMissingSites"),
                        CloseButtonText = AppLocalization.Get("DashboardKeepMissingSites"),
                        DefaultButton = ContentDialogButton.Close,
                        PrimaryButtonStyle = DangerStyles.Button,
                        XamlRoot = dialogRoot
                    };
                    try
                    {
                        removeMissing = await cleanupDialog.ShowAsync() == ContentDialogResult.Primary;
                    }
                    catch (System.Runtime.InteropServices.COMException)
                    {
                        // Another dialog is already open; keep the linked sites.
                        removeMissing = false;
                    }
                }
                token.ThrowIfCancellationRequested();
                if (removeMissing)
                {
                    settings = await Task.Run(() =>
                    {
                        foreach (var path in missingLinkedSites) settingsStore.RemoveLinkedSite(path);
                        return settingsStore.Load();
                    }, token);
                    actions += missingLinkedSites.Length;
                }
                else
                {
                    skipped.Add(AppLocalization.Format(
                        "DashboardMissingSitesKept",
                        missingLinkedSites.Length
                    ));
                }
            }

            var repairSettings = settings;
            var plan = await Task.Run(
                () => PrepareRepairAsync(
                    repairSettings,
                    retryFailedOnly,
                    hasRetryTargets,
                    failedNames,
                    skipped,
                    progress,
                    token
                ),
                token
            );
            actions += plan.Actions;

            // Trusting the certificate can show a Windows security prompt, so it stays on
            // the UI thread like the single "Repair" action.
            token.ThrowIfCancellationRequested();
            progress.Report(new RepairProgress(
                AppLocalization.Get("Dashboard_Repair_StepCertificate"),
                2,
                plan.TotalSteps
            ));
            try
            {
                certificateManager.TrustAuthority();
                actions++;
            }
            catch (Exception error)
            {
                skipped.Add(AppLocalization.Format("Dashboard_Repair_CertificateFailed", error.Message));
            }

            token.ThrowIfCancellationRequested();
            actions += await Task.Run(
                () => RepairEnvironmentAndSitesAsync(plan, skipped, progress, token),
                token
            );
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception error)
        {
            skipped.Add(error.Message);
        }
        finally
        {
            activeRepair = null;
            session.Finish();
        }

        if (cancelled) skipped.Insert(0, AppLocalization.Get("Dashboard_Repair_Cancelled"));
        var refreshHost = loadedPage ?? this;
        await refreshHost.RefreshAsync(forceEndpointProbes: true);

        var repaired = warningsBeforeRepair.Count(warning =>
            !refreshHost.lastHealthWarnings.Contains(warning, StringComparer.Ordinal));
        var outcome = cancelled
            ? "cancelled"
            : skipped.Count == 0 ? "completed" : "completed-with-attention";
        var attention = skipped.Count;
        await Task.Run(() => repairJournal.Append(
            "dashboard-repair",
            outcome,
            $"resolved={repaired};attention={attention};actions={actions}"
        ));

        var summary = AppLocalization.Format("DashboardRepairAllSummary", repaired, skipped.Count);
        App.MainWindow.RecordActivity(new ActivityEvent(
            ActivityEventKind.Repair,
            AppLocalization.Get(cancelled ? "TimelineRepairCancelled" : "TimelineRepairFinished"),
            summary,
            DateTimeOffset.Now,
            "dashboard"
        ));
        if (skipped.Count > 0)
        {
            summary += Environment.NewLine + Environment.NewLine
                + AppLocalization.Get("DashboardRepairAllSkipped") + Environment.NewLine
                + string.Join(Environment.NewLine, skipped.Take(8));
        }

        // The user may have navigated away or hidden the window while the repair ran.
        // Never show a dialog on a page that is gone: keep the summary for the next visit.
        var summaryHost = loadedPage;
        if (summaryHost is not { IsLoaded: true, XamlRoot: { } summaryRoot } || !App.IsMainWindowVisible)
        {
            pendingRepairSummary = summary;
            pendingRepairNeedsAttention = skipped.Count > 0;
            summaryHost?.ShowPendingRepairSummary();
            return;
        }
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            Title = AppLocalization.Get("DashboardRepairAllTitle"),
            Content = new ScrollViewer { Content = new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap } },
            CloseButtonText = AppLocalization.Get("CommonClose"),
            XamlRoot = summaryRoot
        };
        try
        {
            await dialog.ShowAsync();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another dialog is open; show the summary inline instead.
            summaryHost.ShowRepairResultBar(summary, skipped.Count > 0);
        }
    }

    // Runs on a worker thread.
    private async Task<RepairPlan> PrepareRepairAsync(
        WindowsSiteSettings settings,
        bool retryFailedOnly,
        bool hasRetryTargets,
        IReadOnlyCollection<string> failedNames,
        List<string> skipped,
        IProgress<RepairProgress> progress,
        CancellationToken token
    )
    {
        var actions = 0;
        var allSites = await coreClient.ScanAsync(settings.Roots, settings.Tld, settings.LinkedSites, token);
        IReadOnlyList<SiteRecord> sites = allSites;
        if (retryFailedOnly && !hasRetryTargets)
        {
            skipped.Add(AppLocalization.Get("DashboardNoFailedRepairs"));
            sites = [];
        }
        else if (retryFailedOnly)
        {
            var failed = new HashSet<string>(failedNames, StringComparer.OrdinalIgnoreCase);
            sites = sites.Where(site => failed.Contains(site.Name)).ToArray();
        }
        var totalSteps = RepairFixedSteps + sites.Count + (sites.Count > 0 ? 1 : 0);

        token.ThrowIfCancellationRequested();
        progress.Report(new RepairProgress(AppLocalization.Get("Dashboard_Repair_StepDomains"), 1, totalSteps));
        try
        {
            // Not cancelled midway: a half-written hosts file is worse than waiting.
            await hostsManager.EnsureMappingsAsync(allSites.Select(site => site.Domain));
            actions++;
        }
        catch (Exception error)
        {
            skipped.Add(AppLocalization.Format("Dashboard_Repair_LocalDomainsFailed", error.Message));
        }
        return new RepairPlan(allSites, sites, totalSteps, actions);
    }

    // Runs on a worker thread.
    private async Task<int> RepairEnvironmentAndSitesAsync(
        RepairPlan plan,
        List<string> skipped,
        IProgress<RepairProgress> progress,
        CancellationToken token
    )
    {
        var actions = 0;
        var total = plan.TotalSteps;
        progress.Report(new RepairProgress(AppLocalization.Get("Dashboard_Repair_StepEnvironment"), 3, total));
        try
        {
            // Environment and service starts are not cancelled midway so a cancelled repair
            // never leaves the sites environment half restarted.
            await environment.StartAsync(plan.AllSites);
            actions++;
        }
        catch (Exception error)
        {
            skipped.Add(AppLocalization.Format("Dashboard_Repair_EnvironmentFailed", error.Message));
        }

        token.ThrowIfCancellationRequested();
        progress.Report(new RepairProgress(AppLocalization.Get("Dashboard_Repair_StepServices"), 4, total));
        foreach (var instance in serviceManager.LoadInstances())
        {
            token.ThrowIfCancellationRequested();
            if (serviceManager.State(instance.Id, instance.DefinitionId) != ManagedServiceState.Stopped)
                continue;
            if (!serviceManager.IsInstalled(instance.DefinitionId))
            {
                skipped.Add(AppLocalization.Format("Dashboard_Repair_ServiceNotInstalled", instance.Name));
                continue;
            }
            var conflict = PortConflictInspector.Inspect(instance.Port);
            if (conflict.InUse)
            {
                var owner = string.IsNullOrWhiteSpace(conflict.ProcessName)
                    ? AppLocalization.Format(
                        "Dashboard_Repair_UnknownProcess",
                        conflict.ProcessId?.ToString() ?? AppLocalization.Get("Dashboard_Repair_UnknownProcessId")
                    )
                    : conflict.ProcessName;
                skipped.Add(AppLocalization.Format(
                    "Dashboard_Repair_ServicePortInUse",
                    instance.Name,
                    instance.Port,
                    owner
                ));
                continue;
            }
            try
            {
                await serviceManager.StartAsync(instance.Id);
                actions++;
            }
            catch (Exception error)
            {
                skipped.Add(AppLocalization.Format(
                    "Dashboard_Repair_ServiceStartFailed",
                    instance.Name,
                    error.Message
                ));
            }
        }

        var phpCycle = runtimePolicy.Load().PhpCycle;
        for (var index = 0; index < plan.Sites.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var site = plan.Sites[index];
            var step = RepairFixedSteps + index + 1;
            var label = AppLocalization.Format(
                "Dashboard_Repair_StepSite",
                site.Name,
                index + 1,
                plan.Sites.Count
            );
            progress.Report(new RepairProgress(label, step, total));
            try
            {
                actions += await RepairSiteAsync(site, phpCycle, label, step, total, skipped, progress, token);
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                skipped.Add($"{site.Name}: {error.Message}");
            }
        }

        // Dependency repair may make a previously unavailable dev server startable.
        if (plan.Sites.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            progress.Report(new RepairProgress(
                AppLocalization.Get("Dashboard_Repair_StepDevServers"),
                total,
                total
            ));
            try
            {
                await environment.StartAsync(plan.AllSites);
            }
            catch (Exception error)
            {
                skipped.Add(AppLocalization.Format("Dashboard_Repair_DevServersFailed", error.Message));
            }
        }
        return actions;
    }

    // Runs on a worker thread. Every child process receives the repair token so Cancel
    // stops composer/npm/php/git immediately.
    private async Task<int> RepairSiteAsync(
        SiteRecord site,
        string phpCycle,
        string label,
        int step,
        int total,
        List<string> skipped,
        IProgress<RepairProgress> progress,
        CancellationToken token
    )
    {
        var actions = 0;
        void Running(string command)
        {
            token.ThrowIfCancellationRequested();
            progress.Report(new RepairProgress(
                AppLocalization.Format("Dashboard_Repair_StepSiteCommand", label, command),
                step,
                total
            ));
        }

        var path = Path.GetFullPath(site.Path);
        if (!Directory.Exists(path))
        {
            skipped.Add(AppLocalization.Format("Dashboard_Repair_FolderMissing", site.Name));
            return actions;
        }

        var environmentFile = ProjectEnvironmentFile.Load(path);
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.AvailableFreeSpace < 1L * 1_024 * 1_024 * 1_024)
        {
            skipped.Add(AppLocalization.Format("Dashboard_Repair_LowDiskSpace", site.Name, drive.Name));
        }
        if (!environmentFile.Exists && environmentFile.LoadedFromExample)
        {
            ProjectEnvironmentFile.Save(path, environmentFile.Contents, environmentFile.Revision);
            actions++;
        }
        if (SiteHealthInspector.IsLaravelProject(path) && environmentFile.Exists
            && string.IsNullOrWhiteSpace(SiteHealthInspector.EnvironmentValue(environmentFile.Contents, "APP_URL")))
        {
            var updatedEnvironment = environmentFile.Contents.TrimEnd() + Environment.NewLine
                + $"APP_URL=https://{site.Domain}" + Environment.NewLine;
            ProjectEnvironmentFile.Save(path, updatedEnvironment, environmentFile.Revision);
            actions++;
        }

        if (Directory.Exists(Path.Combine(path, ".git")))
        {
            var gitIgnorePath = Path.Combine(path, ".gitignore");
            var gitIgnore = File.Exists(gitIgnorePath) ? File.ReadAllText(gitIgnorePath) : string.Empty;
            var ignoreEntries = new List<string> { ".env" };
            if (File.Exists(Path.Combine(path, "composer.json"))) ignoreEntries.Add("/vendor/");
            if (File.Exists(Path.Combine(path, "package.json"))) ignoreEntries.Add("/node_modules/");
            if (SiteHealthInspector.IsLaravelProject(path))
            {
                ignoreEntries.Add("/public/storage");
                ignoreEntries.Add("/storage/*.key");
            }
            var existingEntries = gitIgnore.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
            var missingEntries = ignoreEntries
                .Where(entry => !existingEntries.Contains(entry, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (missingEntries.Length > 0)
            {
                var prefix = gitIgnore.Length > 0 && !gitIgnore.EndsWith('\n')
                    ? Environment.NewLine : string.Empty;
                File.AppendAllText(
                    gitIgnorePath,
                    prefix + string.Join(Environment.NewLine, missingEntries) + Environment.NewLine
                );
                actions++;
            }
            var git = gitInstaller.InstalledExecutable();
            if (git is not null)
            {
                Running("git ls-files .env");
                if (await RuntimeHealthInspector.GitTracksEnvironmentAsync(git, path, token))
                    skipped.Add(AppLocalization.Format("Dashboard_Repair_EnvTrackedByGit", site.Name));
            }
        }

        var composerJsonPath = Path.Combine(path, "composer.json");
        var isLaravel = SiteHealthInspector.IsLaravelProject(path);

        if (isLaravel)
        {
            foreach (var directory in new[]
            {
                Path.Combine(path, "storage", "framework", "cache"),
                Path.Combine(path, "storage", "framework", "sessions"),
                Path.Combine(path, "storage", "framework", "views"),
                Path.Combine(path, "storage", "logs"),
                Path.Combine(path, "bootstrap", "cache")
            })
            {
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    actions++;
                }
            }

            var storageLink = Path.Combine(path, "public", "storage");
            if (!phpInstaller.IsInstalled(phpCycle))
            {
                skipped.Add(AppLocalization.Format("Dashboard_Repair_PhpRequiredForCache", site.Name, phpCycle));
            }
            foreach (var command in new[] { "config:clear", "cache:clear", "route:clear", "view:clear" })
            {
                if (!phpInstaller.IsInstalled(phpCycle)) break;
                Running($"php artisan {command}");
                var clearResult = await ArtisanCommandRunner.RunAsync(
                    phpInstaller.PhpExecutable(phpCycle),
                    path,
                    [command, "--no-interaction"],
                    composerTools.ManagedEnvironment(phpCycle),
                    TimeSpan.FromMinutes(2),
                    outputProgress: null,
                    cancellationToken: token);
                if (clearResult.ExitCode == 0) actions++;
                else skipped.Add(AppLocalization.Format("Dashboard_Repair_CommandFailed", site.Name, command));
            }
            if (!Directory.Exists(storageLink))
            {
                if (!phpInstaller.IsInstalled(phpCycle))
                {
                    skipped.Add(AppLocalization.Format(
                        "Dashboard_Repair_PhpRequiredForStorageLink",
                        site.Name,
                        phpCycle
                    ));
                }
                else
                {
                    Running("php artisan storage:link");
                    var linkResult = await ArtisanCommandRunner.RunAsync(
                        phpInstaller.PhpExecutable(phpCycle),
                        path,
                        ["storage:link", "--no-interaction"],
                        composerTools.ManagedEnvironment(phpCycle),
                        TimeSpan.FromMinutes(2),
                        outputProgress: null,
                        cancellationToken: token);
                    if (linkResult.ExitCode == 0) actions++;
                    else skipped.Add(AppLocalization.Format("Dashboard_Repair_CommandFailed", site.Name, "storage:link"));
                }
            }

            if (File.Exists(composerJsonPath)
                && phpInstaller.IsInstalled(phpCycle)
                && File.Exists(composerTools.ComposerPath)
                && !File.Exists(Path.Combine(path, "vendor", "autoload.php")))
            {
                Running("composer install");
                var result = await ComposerCommandRunner.RunAsync(
                    phpInstaller.PhpExecutable(phpCycle),
                    composerTools.ComposerPath,
                    path,
                    ["install", "--no-interaction"],
                    composerTools.ManagedEnvironment(phpCycle),
                    outputProgress: null,
                    cancellationToken: token);
                if (result.ExitCode == 0) actions++;
                else skipped.Add(AppLocalization.Format("Dashboard_Repair_ComposerInstallFailed", site.Name));
            }
        }
        else if (File.Exists(composerJsonPath)
            && phpInstaller.IsInstalled(phpCycle)
            && File.Exists(composerTools.ComposerPath)
            && !File.Exists(Path.Combine(path, "vendor", "autoload.php")))
        {
            Running("composer install");
            var result = await ComposerCommandRunner.RunAsync(
                phpInstaller.PhpExecutable(phpCycle),
                composerTools.ComposerPath,
                path,
                ["install", "--no-interaction"],
                composerTools.ManagedEnvironment(phpCycle),
                outputProgress: null,
                cancellationToken: token);
            if (result.ExitCode == 0) actions++;
            else skipped.Add(AppLocalization.Format("Dashboard_Repair_ComposerInstallFailed", site.Name));
        }

        if (File.Exists(composerJsonPath)
            && phpInstaller.IsInstalled(phpCycle)
            && File.Exists(composerTools.ComposerPath))
        {
            var composerEnvironment = composerTools.ManagedEnvironment(phpCycle);
            Running("composer validate");
            var validation = await ComposerCommandRunner.RunAsync(
                phpInstaller.PhpExecutable(phpCycle),
                composerTools.ComposerPath,
                path,
                ["validate", "--no-interaction"],
                composerEnvironment,
                outputProgress: null,
                cancellationToken: token);
            if (validation.ExitCode != 0)
            {
                skipped.Add(AppLocalization.Format(
                    "Dashboard_Repair_CommandReportedProblems",
                    site.Name,
                    "composer validate"
                ));
            }

            Running("composer check-platform-reqs");
            var platform = await ComposerCommandRunner.RunAsync(
                phpInstaller.PhpExecutable(phpCycle),
                composerTools.ComposerPath,
                path,
                ["check-platform-reqs", "--no-interaction"],
                composerEnvironment,
                outputProgress: null,
                cancellationToken: token);
            if (platform.ExitCode != 0)
                skipped.Add(AppLocalization.Format("Dashboard_Repair_ComposerPlatformRequirements", site.Name));

            if (File.Exists(Path.Combine(path, "vendor", "autoload.php")))
            {
                Running("composer dump-autoload");
                var autoload = await ComposerCommandRunner.RunAsync(
                    phpInstaller.PhpExecutable(phpCycle),
                    composerTools.ComposerPath,
                    path,
                    ["dump-autoload", "--optimize", "--no-interaction"],
                    composerEnvironment,
                    outputProgress: null,
                    cancellationToken: token);
                if (autoload.ExitCode == 0) actions++;
                else skipped.Add(AppLocalization.Format(
                    "Dashboard_Repair_CommandFailed",
                    site.Name,
                    "composer dump-autoload"
                ));
            }
        }

        var packageJsonPath = Path.Combine(path, "package.json");
        var nodeModulesPath = Path.Combine(path, "node_modules");
        if (File.Exists(packageJsonPath) && !Directory.Exists(nodeModulesPath))
        {
            var packageManager = RuntimeHealthInspector.NodePackageManager(path);
            if (!packageManager.Equals("npm", StringComparison.Ordinal))
            {
                skipped.Add(AppLocalization.Format(
                    "Dashboard_Repair_OtherPackageManager",
                    site.Name,
                    packageManager
                ));
                return actions;
            }
            IReadOnlyList<string> arguments = File.Exists(Path.Combine(path, "package-lock.json"))
                ? new[] { "ci" }
                : new[] { "install" };
            try
            {
                var npmInvocation = NpmScriptRunner.CreateToolInvocation(
                    nodeInstaller,
                    path,
                    site.NodeVersion,
                    arguments,
                    TimeSpan.FromMinutes(30));
                Running($"npm {arguments[0]}");
                var npmResult = await NpmScriptRunner.RunToolAsync(npmInvocation, cancellationToken: token);
                if (npmResult.ExitCode == 0) actions++;
                else skipped.Add(AppLocalization.Format("Dashboard_Repair_CommandFailed", site.Name, $"npm {arguments[0]}"));
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                skipped.Add(AppLocalization.Format(
                    "Dashboard_Repair_CommandCouldNotRun",
                    site.Name,
                    "npm",
                    error.Message
                ));
            }
        }
        if (File.Exists(packageJsonPath) && Directory.Exists(nodeModulesPath))
        {
            try
            {
                if (File.Exists(Path.Combine(path, "package-lock.json")))
                {
                    var dryRunInvocation = NpmScriptRunner.CreateToolInvocation(
                        nodeInstaller,
                        path,
                        site.NodeVersion,
                        ["ci", "--dry-run", "--ignore-scripts"],
                        TimeSpan.FromMinutes(10));
                    Running("npm ci --dry-run");
                    var dryRun = await NpmScriptRunner.RunToolAsync(dryRunInvocation, cancellationToken: token);
                    if (dryRun.ExitCode != 0)
                        skipped.Add(AppLocalization.Format("Dashboard_Repair_LockFileNotInstallable", site.Name));
                }
                var auditInvocation = NpmScriptRunner.CreateToolInvocation(
                    nodeInstaller,
                    path,
                    site.NodeVersion,
                    ["audit", "--audit-level=high"],
                    TimeSpan.FromMinutes(10));
                Running("npm audit");
                var audit = await NpmScriptRunner.RunToolAsync(auditInvocation, cancellationToken: token);
                if (audit.ExitCode != 0)
                    skipped.Add(AppLocalization.Format("Dashboard_Repair_NpmAuditVulnerabilities", site.Name));
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                skipped.Add(AppLocalization.Format(
                    "Dashboard_Repair_CommandCouldNotRun",
                    site.Name,
                    "npm audit",
                    error.Message
                ));
            }
        }

        if (isLaravel && phpInstaller.IsInstalled(phpCycle))
        {
            Running("php artisan migrate:status");
            var migrations = await ArtisanCommandRunner.RunAsync(
                phpInstaller.PhpExecutable(phpCycle),
                path,
                ["migrate:status", "--no-interaction"],
                composerTools.ManagedEnvironment(phpCycle),
                TimeSpan.FromMinutes(2),
                outputProgress: null,
                cancellationToken: token);
            if (migrations.ExitCode != 0)
                skipped.Add(AppLocalization.Format("Dashboard_Repair_MigrationStatusFailed", site.Name));
        }
        return actions;
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var healthWarnings = lastHealthWarnings.ToArray();
        // Settings, certificates, runtimes and the journal are all read from disk; keep that
        // work off the UI thread.
        var path = await Task.Run(async () =>
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads"
            );
            Directory.CreateDirectory(directory);
            var reportPath = Path.Combine(directory, $"HerdMe-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            var settings = settingsStore.Load();
            var report = new
            {
                generatedAt = DateTimeOffset.Now,
                environment = new
                {
                    environment.IsRunning,
                    environment.IsDegraded,
                    environment.HttpPort,
                    environment.HttpsPort
                },
                certificateExpiresAt = certificateManager.ServerCertificateExpiresAt(),
                php = new
                {
                    activeCycle = runtimePolicy.Load().PhpCycle,
                    installedCycles = phpInstaller.InstalledCycles()
                },
                node = new { installedVersions = nodeInstaller.InstalledVersions() },
                services = serviceManager.LoadInstances().Select(instance => new
                {
                    instance.Name,
                    instance.DefinitionId,
                    instance.Port,
                    state = serviceManager.State(instance.Id, instance.DefinitionId).ToString()
                }),
                siteRoots = settings.Roots,
                linkedSiteCount = settings.LinkedSites.Count,
                warnings = healthWarnings,
                repairHistory = repairJournal.ReadRecent(20)
            };
            await File.WriteAllTextAsync(
                reportPath,
                System.Text.Json.JsonSerializer.Serialize(
                    report,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }
                )
            );
            return reportPath;
        });
        if (!IsLoaded || XamlRoot is null) return;
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            Title = AppLocalization.Get("DashboardDiagnosticsExportedTitle"),
            Content = new TextBlock
            {
                Text = path,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            },
            CloseButtonText = AppLocalization.Get("CommonClose"),
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async Task RunHealthRepairAsync(Func<WindowsSiteSettings, Task> repair)
    {
        if (activeRepair is not null)
        {
            // Repair all is already fixing this; show its progress instead of racing it.
            ShowTab("health");
            RepairProgressInfoBar.StartBringIntoView();
            return;
        }
        RefreshButton.IsEnabled = false;
        RefreshProgress.IsActive = true;
        try
        {
            await repair(await Task.Run(settingsStore.Load));
        }
        catch (Exception error)
        {
            UpdateHealth(false, false, error.Message, []);
        }
        finally
        {
            await RefreshAsync(forceEndpointProbes: true);
        }
    }

    private void RenderRecentMail(IReadOnlyList<CapturedMail> messages)
    {
        RecentMailItems.Children.Clear();
        if (messages.Count == 0)
        {
            RecentMailItems.Children.Add(EmptyActivityText("DashboardNoRecentMail"));
            return;
        }
        foreach (var message in messages.Take(4))
        {
            RecentMailItems.Children.Add(ActivityRow(
                string.IsNullOrWhiteSpace(message.Subject)
                    ? AppLocalization.Get("DashboardNoSubject") : message.Subject,
                message.Sender,
                message.ReceivedAt
            ));
        }
    }

    private void RenderRecentDumps(IReadOnlyList<CapturedDump> dumps)
    {
        RecentDumpItems.Children.Clear();
        if (dumps.Count == 0)
        {
            RecentDumpItems.Children.Add(EmptyActivityText("DashboardNoRecentDumps"));
            return;
        }
        foreach (var dump in dumps.Take(4))
        {
            RecentDumpItems.Children.Add(ActivityRow(
                string.IsNullOrWhiteSpace(dump.Summary)
                    ? AppLocalization.Get("DashboardEmptyDump") : SingleLine(dump.Summary),
                dump.Source,
                dump.ReceivedAt
            ));
        }
    }

    private static TextBlock EmptyActivityText(string key)
    {
        return new TextBlock
        {
            Text = AppLocalization.Get(key),
            Style = TextStyle("SecondaryTextStyle"),
            Padding = new Thickness(0, 8, 0, 8)
        };
    }

    private static Grid ActivityRow(string title, string subtitle, DateTimeOffset receivedAt)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        });
        text.Children.Add(new TextBlock
        {
            Text = subtitle,
            Style = TextStyle("CaptionTextStyle"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        });
        var date = new TextBlock
        {
            Text = receivedAt.LocalDateTime.ToString("g"),
            Style = TextStyle("CaptionTextStyle"),
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(date, 1);
        row.Children.Add(text);
        row.Children.Add(date);
        return row;
    }

    private static string SingleLine(string value)
    {
        return string.Join(" ", value.Split(
            new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        ));
    }

    private void OpenSites_Click(object sender, RoutedEventArgs e) =>
        App.MainWindow.NavigateToPage("sites");

    private void OpenServices_Click(object sender, RoutedEventArgs e) =>
        App.MainWindow.NavigateToPage("services");

    private void OpenMail_Click(object sender, RoutedEventArgs e) =>
        App.MainWindow.NavigateToPage("mail");

    private void OpenDumps_Click(object sender, RoutedEventArgs e) =>
        App.MainWindow.NavigateToPage("dumps");

    private void OpenLogs_Click(object sender, RoutedEventArgs e) =>
        App.MainWindow.NavigateToPage("logs");
}
