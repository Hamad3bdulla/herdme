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
    private CancellationTokenSource? refreshCancellation;
    private bool? usesCompactLayout;

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
        ComposerToolManager composerTools
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
                cancellation.Token
            ));
            var siteHealth = (await Task.WhenAll(healthTasks))
                .SelectMany((checks, index) => checks
                    .Where(check => !check.Healthy)
                    .Select(check => $"{sites[index].Name}: {check.Name} - {check.Detail}"))
                .ToArray();
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
