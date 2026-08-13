using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HerdMe.Windows.Pages;

public sealed partial class DashboardPage : Page
{
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
    private CancellationTokenSource? refreshCancellation;
    private bool? usesCompactLayout;
    private readonly HashSet<string> failedSiteNames = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> lastHealthWarnings = [];

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
        GitRuntimeInstaller gitInstaller
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
        repairJournal = new OperationJournal(Path.Combine(settingsStore.SupportRoot, "Repair"));
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 680;
        var compactMode = settingsStore.Load().CompactMode;
        if (usesCompactLayout == compact && !compactMode) return;
        usesCompactLayout = compact;

        DashboardLayout.Padding = compact || compactMode
            ? new Thickness(18, 18, 18, 20)
            : new Thickness(28, 22, 28, 24);
        DashboardLayout.RowSpacing = compactMode ? 10 : 18;
        var summaryHeight = compactMode ? 112 : 148;
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

        EnvironmentLabelColumn.Width = compact
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(170);
        EnvironmentStatusColumn.Width = compact
            ? GridLength.Auto
            : new GridLength(140);
        EnvironmentDetailColumn.Width = compact
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        PositionEnvironmentRow(
            EnvironmentLabelText,
            EnvironmentStatusText,
            EnvironmentDetailText,
            compact,
            wideRow: 0,
            compactRow: 0
        );
        PositionEnvironmentRow(
            DomainsLabelText,
            DomainsStatusText,
            DomainsDetailText,
            compact,
            wideRow: 1,
            compactRow: 2
        );
        PositionEnvironmentRow(
            CertificateLabelText,
            CertificateStatusText,
            CertificateDetailText,
            compact,
            wideRow: 2,
            compactRow: 4
        );

        RecentActivityGrid.RowSpacing = compact ? 18 : 0;
        Grid.SetRow(RecentDumpsPanel, compact ? 1 : 0);
        Grid.SetColumn(RecentDumpsPanel, compact ? 0 : 1);
    }

    private static void PositionSummaryCard(Button card, int row, int column)
    {
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
    }

    private static void PositionEnvironmentRow(
        TextBlock label,
        TextBlock status,
        TextBlock detail,
        bool compact,
        int wideRow,
        int compactRow
    )
    {
        var primaryRow = compact ? compactRow : wideRow;
        Grid.SetRow(label, primaryRow);
        Grid.SetColumn(label, 0);
        Grid.SetRow(status, primaryRow);
        Grid.SetColumn(status, 1);
        Grid.SetRow(detail, compact ? compactRow + 1 : wideRow);
        Grid.SetColumn(detail, compact ? 0 : 2);
        Grid.SetColumnSpan(detail, compact ? 2 : 1);
        detail.Padding = compact
            ? new Thickness(0, 0, 0, 8)
            : new Thickness(0, 8, 0, 8);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        refreshCancellation?.Cancel();
        refreshCancellation?.Dispose();
        refreshCancellation = null;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        refreshCancellation?.Cancel();
        refreshCancellation?.Dispose();
        using var cancellation = new CancellationTokenSource();
        refreshCancellation = cancellation;
        RefreshButton.IsEnabled = false;
        RefreshProgress.IsActive = true;

        try
        {
            var settings = settingsStore.Load();
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
            var instances = await servicesTask;
            var messages = await mailTask;
            var dumps = await dumpsTask;
            var domainsConfigured = await domainsTask;
            var certificateTrusted = await certificateTask;
            var defaultPhpCycle = runtimePolicy.Load().PhpCycle;
            var healthTasks = sites.Select(site => SiteHealthInspector.InspectAsync(
                site.Path,
                site.Domain,
                site.PhpVersion ?? defaultPhpCycle,
                phpInstaller,
                composerTools,
                certificateManager,
                site.NodeVersion,
                cancellation.Token
            ));
            var siteHealth = (await Task.WhenAll(healthTasks))
                .SelectMany((checks, index) => checks
                    .Where(check => !check.Healthy)
                    .Select(check => $"{sites[index].Name}: {check.Name} - {check.Detail}"))
                .ToArray();
            if (environment.IsRunning)
            {
                var endpointResults = await Task.WhenAll(sites.Select(site =>
                    RuntimeHealthInspector.InspectSiteAsync(
                        site.Domain,
                        environment.HttpsPort is not null,
                        cancellation.Token
                    )));
                siteHealth = siteHealth.Concat(endpointResults
                    .Select((result, index) => (result, index))
                    .Where(item => !item.result.Healthy)
                    .Select(item => $"{sites[item.index].Name}: {item.result.Name} - {item.result.Detail}"))
                    .ToArray();
            }
            var certificateExpiry = certificateManager.ServerCertificateExpiresAt();
            if (certificateExpiry is { } expiry && expiry <= DateTimeOffset.UtcNow.AddDays(30))
            {
                siteHealth = siteHealth.Append($"HTTPS certificate expires {expiry:d}").ToArray();
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
                .Select(result => $"{result.Name}: Service health - {result.Detail}"))
                .ToArray();
            var duplicatePorts = instances.GroupBy(instance => instance.Port)
                .Where(group => group.Count() > 1)
                .Select(group => $"Services: Duplicate port {group.Key} - {string.Join(", ", group.Select(instance => instance.Name))}");
            siteHealth = siteHealth.Concat(duplicatePorts).ToArray();
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
            SitesStatusDot.Fill = SummaryStatusBrush(runningSites, sites.Count);
            ServicesCountText.Text = instances.Count.ToString();
            ServicesStatusText.Text = AppLocalization.Format(
                "DashboardRunningCount",
                runningServices,
                instances.Count
            );
            ServicesStatusDot.Fill = SummaryStatusBrush(runningServices, instances.Count);
            MailCountText.Text = messages.Count.ToString();
            MailStatusText.Text = AppLocalization.Get(
                mailCapture.IsRunning ? "DashboardCaptureRunning" : "DashboardCaptureStopped"
            );
            MailStatusDot.Fill = CaptureStatusBrush(mailCapture.IsRunning);
            DumpsCountText.Text = dumps.Count.ToString();
            DumpsStatusText.Text = AppLocalization.Get(
                dumpCapture.IsRunning ? "DashboardCaptureRunning" : "DashboardCaptureStopped"
            );
            DumpsStatusDot.Fill = CaptureStatusBrush(dumpCapture.IsRunning);

            UpdateEnvironmentStatus(domainsConfigured, certificateTrusted, settings.Tld);
            UpdateHealth(domainsConfigured, certificateTrusted, failure: null, siteHealth);
            RenderRecentMail(messages);
            RenderRecentDumps(dumps);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            UpdateHealth(domainsConfigured: false, certificateTrusted: false, failure: error.Message, []);
        }
        finally
        {
            if (ReferenceEquals(refreshCancellation, cancellation))
            {
                refreshCancellation = null;
                RefreshButton.IsEnabled = true;
                RefreshProgress.IsActive = false;
            }
        }
    }

    private static Brush SummaryStatusBrush(int running, int total)
    {
        var resource = total == 0
            ? "SystemFillColorNeutralBrush"
            : running == total
                ? "SystemFillColorSuccessBrush"
                : running > 0
                    ? "SystemFillColorCautionBrush"
                    : "SystemFillColorCriticalBrush";
        return (Brush)Application.Current.Resources[resource];
    }

    private static Brush CaptureStatusBrush(bool running)
    {
        return (Brush)Application.Current.Resources[
            running ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"
        ];
    }

    private void UpdateEnvironmentStatus(
        bool domainsConfigured,
        bool certificateTrusted,
        string tld
    )
    {
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

        var healthy = warnings.Count == 0 && siteWarnings.Count == 0;
        HealthBanner.Severity = healthy ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        HealthBanner.Title = AppLocalization.Get(
            healthy ? "DashboardEverythingReady" : "DashboardNeedsAttention"
        );
        HealthBanner.Message = AppLocalization.Get(
            healthy ? "DashboardHealthyDetail" : "DashboardWarningsDetail"
        );
        WarningList.Children.Clear();
        WarningList.Visibility = healthy ? Visibility.Collapsed : Visibility.Visible;
        foreach (var warning in warnings)
        {
            RoutedEventHandler? repair = warning == AppLocalization.Get("DashboardDomainsWarning")
                ? RepairDomains_Click
                : warning == AppLocalization.Get("DashboardCertificateWarning")
                    ? RepairCertificate_Click
                    : warning == AppLocalization.Get("DashboardEnvironmentRecoveringWarning")
                        ? RepairEnvironment_Click
                        : null;
            WarningList.Children.Add(HealthIssueRow(warning, repair));
        }
        foreach (var warning in siteWarnings)
        {
            WarningList.Children.Add(HealthIssueRow(warning, OpenSites_Click));
        }
        lastHealthWarnings = warnings.Concat(siteWarnings).ToArray();
        failedSiteNames.Clear();
        foreach (var warning in siteWarnings)
        {
            var separator = warning.IndexOf(':');
            if (separator > 0) failedSiteNames.Add(warning[..separator]);
        }
    }

    private UIElement HealthIssueRow(string message, RoutedEventHandler? repair)
    {
        var row = new Grid { ColumnSpacing = 9 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new SymbolIcon(Symbol.Important) { VerticalAlignment = VerticalAlignment.Center });
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        if (repair is not null)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(new SymbolIcon(Symbol.Repair));
            content.Children.Add(new TextBlock { Text = AppLocalization.Get("DashboardRepairAction") });
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

    private async Task RepairAllAsync(bool retryFailedOnly)
    {
        RepairAllButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        RefreshProgress.IsActive = true;
        var repaired = 0;
        var skipped = new List<string>();
        var warningsBeforeRepair = lastHealthWarnings.ToArray();
        repairJournal.Append("dashboard-repair", "started", retryFailedOnly ? "retry-failed" : "all");
        try
        {
            var settings = settingsStore.Load();
            var missingLinkedSites = settings.LinkedSites
                .Where(path => !Directory.Exists(path))
                .ToArray();
            if (missingLinkedSites.Length > 0)
            {
                var cleanupDialog = new ContentDialog
                {
                    Title = AppLocalization.Get("DashboardMissingSitesTitle"),
                    Content = new TextBlock
                    {
                        Text = AppLocalization.Format(
                            "DashboardMissingSitesMessage",
                            string.Join(Environment.NewLine, missingLinkedSites.Select(Path.GetFileName))
                        ),
                        TextWrapping = TextWrapping.Wrap
                    },
                    PrimaryButtonText = AppLocalization.Get("DashboardRemoveMissingSites"),
                    CloseButtonText = AppLocalization.Get("DashboardKeepMissingSites"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot
                };
                if (await cleanupDialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    foreach (var path in missingLinkedSites) settingsStore.RemoveLinkedSite(path);
                    repaired += missingLinkedSites.Length;
                    settings = settingsStore.Load();
                }
                else
                {
                    skipped.Add(AppLocalization.Format(
                        "DashboardMissingSitesKept",
                        missingLinkedSites.Length
                    ));
                }
            }
            var allSites = await coreClient.ScanAsync(settings.Roots, settings.Tld, settings.LinkedSites);
            IReadOnlyList<SiteRecord> sites = allSites;
            if (retryFailedOnly && failedSiteNames.Count == 0 && lastHealthWarnings.Count == 0)
            {
                skipped.Add(AppLocalization.Get("DashboardNoFailedRepairs"));
                sites = [];
            }
            else if (retryFailedOnly)
            {
                sites = sites.Where(site => failedSiteNames.Contains(site.Name)).ToArray();
            }

            try
            {
                await hostsManager.EnsureMappingsAsync(allSites.Select(site => site.Domain));
                repaired++;
            }
            catch (Exception error)
            {
                skipped.Add($"Local domains: {error.Message}");
            }

            try
            {
                certificateManager.TrustAuthority();
                repaired++;
            }
            catch (Exception error)
            {
                skipped.Add($"HTTPS certificate: {error.Message}");
            }

            try
            {
                await environment.StartAsync(allSites);
                repaired++;
            }
            catch (Exception error)
            {
                skipped.Add($"Local environment: {error.Message}");
            }

            foreach (var instance in serviceManager.LoadInstances())
            {
                if (serviceManager.State(instance.Id, instance.DefinitionId) != ManagedServiceState.Stopped)
                    continue;
                if (!serviceManager.IsInstalled(instance.DefinitionId))
                {
                    skipped.Add($"{instance.Name}: service runtime is not installed");
                    continue;
                }
                var conflict = PortConflictInspector.Inspect(instance.Port);
                if (conflict.InUse)
                {
                    var owner = string.IsNullOrWhiteSpace(conflict.ProcessName)
                        ? $"process {conflict.ProcessId?.ToString() ?? "unknown"}"
                        : conflict.ProcessName;
                    skipped.Add($"{instance.Name}: port {instance.Port} is already in use by {owner}");
                    continue;
                }
                try
                {
                    await serviceManager.StartAsync(instance.Id);
                    repaired++;
                }
                catch (Exception error)
                {
                    skipped.Add($"{instance.Name}: could not start ({error.Message})");
                }
            }

            var phpCycle = runtimePolicy.Load().PhpCycle;
            foreach (var site in sites)
            {
                try
                {
                    var path = Path.GetFullPath(site.Path);
                    if (!Directory.Exists(path))
                    {
                        skipped.Add($"{site.Name}: project folder is missing");
                        continue;
                    }

                    var environmentFile = ProjectEnvironmentFile.Load(path);
                    var drive = new DriveInfo(Path.GetPathRoot(path)!);
                    if (drive.AvailableFreeSpace < 1L * 1_024 * 1_024 * 1_024)
                    {
                        skipped.Add($"{site.Name}: less than 1 GB free on {drive.Name}");
                    }
                    if (!environmentFile.Exists && environmentFile.LoadedFromExample)
                    {
                        ProjectEnvironmentFile.Save(path, environmentFile.Contents, environmentFile.Revision);
                        repaired++;
                    }
                    if (SiteHealthInspector.IsLaravelProject(path) && environmentFile.Exists
                        && string.IsNullOrWhiteSpace(SiteHealthInspector.EnvironmentValue(environmentFile.Contents, "APP_URL")))
                    {
                        var updatedEnvironment = environmentFile.Contents.TrimEnd() + Environment.NewLine
                            + $"APP_URL=https://{site.Domain}" + Environment.NewLine;
                        ProjectEnvironmentFile.Save(path, updatedEnvironment, environmentFile.Revision);
                        repaired++;
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
                            repaired++;
                        }
                        var git = gitInstaller.InstalledExecutable();
                        if (git is not null)
                        {
                            if (await RuntimeHealthInspector.GitTracksEnvironmentAsync(git, path))
                                skipped.Add($"{site.Name}: .env is tracked by Git; remove it from the index manually");
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
                                repaired++;
                            }
                        }

                        var storageLink = Path.Combine(path, "public", "storage");
                        if (!phpInstaller.IsInstalled(phpCycle))
                        {
                            skipped.Add($"{site.Name}: PHP {phpCycle} is required for Laravel cache repairs");
                        }
                        foreach (var command in new[] { "config:clear", "cache:clear", "route:clear", "view:clear" })
                        {
                            if (!phpInstaller.IsInstalled(phpCycle)) break;
                            var clearResult = await ArtisanCommandRunner.RunAsync(
                                phpInstaller.PhpExecutable(phpCycle),
                                path,
                                [command, "--no-interaction"],
                                composerTools.ManagedEnvironment(phpCycle),
                                TimeSpan.FromMinutes(2));
                            if (clearResult.ExitCode == 0) repaired++;
                            else skipped.Add($"{site.Name}: {command} failed");
                        }
                        if (!Directory.Exists(storageLink))
                        {
                            if (!phpInstaller.IsInstalled(phpCycle))
                            {
                                skipped.Add($"{site.Name}: PHP {phpCycle} is required to create public/storage");
                            }
                            else
                            {
                                var linkResult = await ArtisanCommandRunner.RunAsync(
                                    phpInstaller.PhpExecutable(phpCycle),
                                    path,
                                    ["storage:link", "--no-interaction"],
                                    composerTools.ManagedEnvironment(phpCycle),
                                    TimeSpan.FromMinutes(2));
                                if (linkResult.ExitCode == 0) repaired++;
                                else skipped.Add($"{site.Name}: storage:link failed");
                            }
                        }

                        if (File.Exists(composerJsonPath)
                            && phpInstaller.IsInstalled(phpCycle)
                            && File.Exists(composerTools.ComposerPath)
                            && !File.Exists(Path.Combine(path, "vendor", "autoload.php")))
                        {
                            var result = await ComposerCommandRunner.RunAsync(
                                phpInstaller.PhpExecutable(phpCycle),
                                composerTools.ComposerPath,
                                path,
                                ["install", "--no-interaction"],
                                composerTools.ManagedEnvironment(phpCycle));
                            if (result.ExitCode == 0) repaired++;
                            else skipped.Add($"{site.Name}: Composer install failed");
                        }
                    }
                    else if (File.Exists(composerJsonPath)
                        && phpInstaller.IsInstalled(phpCycle)
                        && File.Exists(composerTools.ComposerPath)
                        && !File.Exists(Path.Combine(path, "vendor", "autoload.php")))
                    {
                        var result = await ComposerCommandRunner.RunAsync(
                            phpInstaller.PhpExecutable(phpCycle),
                            composerTools.ComposerPath,
                            path,
                            ["install", "--no-interaction"],
                            composerTools.ManagedEnvironment(phpCycle));
                        if (result.ExitCode == 0) repaired++;
                        else skipped.Add($"{site.Name}: Composer install failed");
                    }

                    if (File.Exists(composerJsonPath)
                        && phpInstaller.IsInstalled(phpCycle)
                        && File.Exists(composerTools.ComposerPath))
                    {
                        var composerEnvironment = composerTools.ManagedEnvironment(phpCycle);
                        var validation = await ComposerCommandRunner.RunAsync(
                            phpInstaller.PhpExecutable(phpCycle),
                            composerTools.ComposerPath,
                            path,
                            ["validate", "--no-interaction"],
                            composerEnvironment);
                        if (validation.ExitCode != 0)
                            skipped.Add($"{site.Name}: composer validate reported problems");

                        var platform = await ComposerCommandRunner.RunAsync(
                            phpInstaller.PhpExecutable(phpCycle),
                            composerTools.ComposerPath,
                            path,
                            ["check-platform-reqs", "--no-interaction"],
                            composerEnvironment);
                        if (platform.ExitCode != 0)
                            skipped.Add($"{site.Name}: Composer platform requirements are not satisfied");

                        if (File.Exists(Path.Combine(path, "vendor", "autoload.php")))
                        {
                            var autoload = await ComposerCommandRunner.RunAsync(
                                phpInstaller.PhpExecutable(phpCycle),
                                composerTools.ComposerPath,
                                path,
                                ["dump-autoload", "--optimize", "--no-interaction"],
                                composerEnvironment);
                            if (autoload.ExitCode == 0) repaired++;
                            else skipped.Add($"{site.Name}: composer dump-autoload failed");
                        }
                    }

                    var packageJsonPath = Path.Combine(path, "package.json");
                    var nodeModulesPath = Path.Combine(path, "node_modules");
                    if (File.Exists(packageJsonPath) && !Directory.Exists(nodeModulesPath))
                    {
                        var packageManager = RuntimeHealthInspector.NodePackageManager(path);
                        if (!packageManager.Equals("npm", StringComparison.Ordinal))
                        {
                            skipped.Add($"{site.Name}: uses {packageManager}; install dependencies with that package manager");
                            continue;
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
                            var npmResult = await NpmScriptRunner.RunToolAsync(npmInvocation);
                            if (npmResult.ExitCode == 0) repaired++;
                            else skipped.Add($"{site.Name}: npm {arguments[0]} failed");
                        }
                        catch (Exception error)
                        {
                            skipped.Add($"{site.Name}: npm could not run ({error.Message})");
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
                                var dryRun = await NpmScriptRunner.RunToolAsync(dryRunInvocation);
                                if (dryRun.ExitCode != 0)
                                    skipped.Add($"{site.Name}: package-lock.json is not installable with npm ci");
                            }
                            var auditInvocation = NpmScriptRunner.CreateToolInvocation(
                                nodeInstaller,
                                path,
                                site.NodeVersion,
                                ["audit", "--audit-level=high"],
                                TimeSpan.FromMinutes(10));
                            var audit = await NpmScriptRunner.RunToolAsync(auditInvocation);
                            if (audit.ExitCode != 0)
                                skipped.Add($"{site.Name}: npm audit found high-severity vulnerabilities");
                        }
                        catch (Exception error)
                        {
                            skipped.Add($"{site.Name}: npm audit could not run ({error.Message})");
                        }
                    }

                    if (isLaravel && phpInstaller.IsInstalled(phpCycle))
                    {
                        var migrations = await ArtisanCommandRunner.RunAsync(
                            phpInstaller.PhpExecutable(phpCycle),
                            path,
                            ["migrate:status", "--no-interaction"],
                            composerTools.ManagedEnvironment(phpCycle),
                            TimeSpan.FromMinutes(2));
                        if (migrations.ExitCode != 0)
                            skipped.Add($"{site.Name}: database or migration status could not be read");
                    }
                }
                catch (Exception error)
                {
                    skipped.Add($"{site.Name}: {error.Message}");
                }
            }

            // Dependency repair may make a previously unavailable dev server startable.
            if (sites.Count > 0)
            {
                try
                {
                    await environment.StartAsync(allSites);
                }
                catch (Exception error)
                {
                    skipped.Add($"Development servers: {error.Message}");
                }
            }
        }
        catch (Exception error)
        {
            skipped.Add(error.Message);
        }
        finally
        {
            await RefreshAsync();
            RepairAllButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            RefreshProgress.IsActive = false;
        }

        repaired = warningsBeforeRepair.Count(warning =>
            !lastHealthWarnings.Contains(warning, StringComparer.Ordinal));
        repairJournal.Append(
            "dashboard-repair",
            skipped.Count == 0 ? "completed" : "completed-with-attention",
            $"resolved={repaired};attention={skipped.Count}"
        );

        var summary = AppLocalization.Format("DashboardRepairAllSummary", repaired, skipped.Count);
        if (skipped.Count > 0)
        {
            summary += Environment.NewLine + Environment.NewLine
                + AppLocalization.Get("DashboardRepairAllSkipped") + Environment.NewLine
                + string.Join(Environment.NewLine, skipped.Take(8));
        }
        var dialog = new ContentDialog
        {
            Title = AppLocalization.Get("DashboardRepairAllTitle"),
            Content = new ScrollViewer { Content = new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap } },
            CloseButtonText = AppLocalization.Get("CommonClose"),
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads"
        );
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"HerdMe-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json");
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
            warnings = lastHealthWarnings,
            repairHistory = repairJournal.ReadRecent(20)
        };
        await File.WriteAllTextAsync(
            path,
            System.Text.Json.JsonSerializer.Serialize(
                report,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }
            )
        );
        var dialog = new ContentDialog
        {
            Title = AppLocalization.Get("DashboardDiagnosticsExportedTitle"),
            Content = path,
            CloseButtonText = AppLocalization.Get("CommonClose"),
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async Task RunHealthRepairAsync(Func<WindowsSiteSettings, Task> repair)
    {
        RefreshButton.IsEnabled = false;
        RefreshProgress.IsActive = true;
        try
        {
            await repair(settingsStore.Load());
        }
        catch (Exception error)
        {
            UpdateHealth(false, false, error.Message, []);
        }
        finally
        {
            await RefreshAsync();
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
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Padding = new Thickness(0, 12, 0, 12)
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
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            FontSize = 12
        });
        var date = new TextBlock
        {
            Text = receivedAt.LocalDateTime.ToString("g"),
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            FontSize = 12,
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
}
