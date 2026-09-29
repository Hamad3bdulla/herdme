using HerdMe.Windows.Models;
using HerdMe.Windows.Pages;
using HerdMe.Windows.Services;
using HerdMe.Windows.Views;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace HerdMe.Windows;

public sealed partial class MainWindow : Window
{
    private const int LogicalWindowWidth = 1_240;
    private const int LogicalWindowHeight = 800;
    private const int LogicalWindowMargin = 16;
    private readonly AppServices services;
    // One instance per navigation tag; pages keep their state and guard Loaded/Unloaded.
    private readonly Dictionary<string, Page> cachedPages = new(StringComparer.Ordinal);
    private string? pendingLogSitePath;
    private string? configurationLoadWarning;
    private bool shuttingDown;
    private readonly DispatcherTimer titleBarStatusTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2)
    };
    private (bool Running, bool Degraded)? displayedTitleBarStatus;

    public MainWindow(
        AppServices services,
        bool skipOnboarding = false,
        bool forceOnboarding = false
    )
    {
        this.services = services;
        InitializeComponent();
        Onboarding.Configure(services.InitialSetup);
        RootLayout.Language = AppLocalization.LanguageTag;
        RootLayout.FlowDirection = AppLocalization.LayoutDirection;
        ConfigureTitleBar();
        ResizeWindow();
        var siteSettings = services.SiteSettings.Load();
        InitializeAppearance(siteSettings.CompactMode);
        _ = services.Services.LoadInstances();
        configurationLoadWarning = string.Join(
            Environment.NewLine + Environment.NewLine,
            new[]
            {
                services.SiteSettings.LastLoadWarning,
                services.Services.LastLoadWarning
            }.Where(message => !string.IsNullOrWhiteSpace(message))
        );
        if (string.IsNullOrWhiteSpace(configurationLoadWarning)) configurationLoadWarning = null;
        if (configurationLoadWarning is not null) Activated += ShowConfigurationLoadWarning;
        RequiresOnboarding = forceOnboarding
            || (!skipOnboarding && !siteSettings.OnboardingCompleted);
        InitializeWhatsNew(siteSettings.LastSeenVersion);
        Navigation.Visibility = RequiresOnboarding ? Visibility.Collapsed : Visibility.Visible;
        Onboarding.Visibility = RequiresOnboarding ? Visibility.Visible : Visibility.Collapsed;
        Navigation.SelectedItem = Navigation.MenuItems[0];
        if (ContentFrame.Content is null) ShowPage("dashboard");
        UpdateTitleBarStatus();
        App.MainWindowVisibilityChanged += App_MainWindowVisibilityChanged;
        toastTimer.Tick += ToastTimer_Tick;
        SubscribeActivity();
        InitializeStatusBar();
        InitializeThumbnailToolbar();
        InitializeBell();
    }

    public bool RequiresOnboarding { get; private set; }

    public event EventHandler? InitialSetupCompleted;

    internal void PrepareForShutdown()
    {
        shuttingDown = true;
        App.MainWindowVisibilityChanged -= App_MainWindowVisibilityChanged;
        titleBarStatusTimer.Stop();
        toastTimer.Stop();
        toastTimer.Tick -= ToastTimer_Tick;
        deferredTimer.Stop();
        UnsubscribeActivity();
        ReleaseAppearance();
        ReleaseStatusBar();
        ReleaseThumbnailToolbar();
        ReleaseBell();
        AppWindow.Hide();
        RootLayout.IsHitTestVisible = false;
        ContentFrame.Content = null;
        cachedPages.Clear();
        Content = null;
    }

    private async void ShowConfigurationLoadWarning(object sender, WindowActivatedEventArgs args)
    {
        if (
            configurationLoadWarning is null
            || Content is not FrameworkElement root
            || root.XamlRoot is not { } xamlRoot
        ) return;
        Activated -= ShowConfigurationLoadWarning;
        var warning = configurationLoadWarning;
        configurationLoadWarning = null;
        var dialog = new ContentDialog
        {
            Title = AppLocalization.Get("MainWindowSettingsLoadWarningTitle"),
            Content = warning,
            CloseButtonText = AppLocalization.Get("CommonOk"),
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = xamlRoot
        };
        await dialog.ShowAsync();
        await ShowPendingWhatsNewAsync();
    }

    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        var titleBar = AppWindow.TitleBar;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        if (MicaController.IsSupported())
        {
            // Mica shows through the transparent shell; older systems keep the solid shell brush.
            SystemBackdrop = new MicaBackdrop();
            RootLayout.Background = new SolidColorBrush(Colors.Transparent);
        }

        AppTitleBar.Loaded += (_, _) =>
        {
            UpdateTitleBarInsets();
            ApplyCaptionButtonColors();
        };
        AppTitleBar.SizeChanged += (_, _) =>
        {
            UpdateTitleBarInsets();
            UpdateTitleBarPassthrough();
        };
        InitializeTitleBarSearch();
        RootLayout.ActualThemeChanged += (_, _) => ApplyCaptionButtonColors();
        titleBarStatusTimer.Tick += (_, _) =>
        {
            UpdateTitleBarStatus();
            UpdateStatusBar();
            UpdateThumbnailToolbar();
        };
        // The status poll starts once App reports the window visible and pauses in the tray.
    }

    private void App_MainWindowVisibilityChanged(object? sender, bool visible)
    {
        if (shuttingDown) return;
        if (visible)
        {
            UpdateTitleBarStatus();
            UpdateStatusBar();
            UpdateThumbnailToolbar();
            titleBarStatusTimer.Start();
            MarkPageSeen(currentPageTag);
        }
        else
        {
            titleBarStatusTimer.Stop();
        }
    }

    private void UpdateTitleBarInsets()
    {
        if (shuttingDown || AppTitleBar.XamlRoot is not { } xamlRoot) return;
        var scale = xamlRoot.RasterizationScale;
        if (scale <= 0) return;
        var left = AppWindow.TitleBar.LeftInset / scale;
        var right = AppWindow.TitleBar.RightInset / scale;
        // XAML mirrors in right-to-left layouts, but the caption buttons keep their
        // physical side, so the leading padding must use the opposite inset.
        var rightToLeft = RootLayout.FlowDirection == FlowDirection.RightToLeft;
        AppTitleBar.Padding = new Thickness(
            16 + (rightToLeft ? right : left),
            0,
            12 + (rightToLeft ? left : right),
            0
        );
    }

    private void ApplyCaptionButtonColors()
    {
        if (shuttingDown) return;
        var dark = RootLayout.ActualTheme == ElementTheme.Dark;
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        titleBar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
        titleBar.ButtonPressedForegroundColor = dark ? Colors.White : Colors.Black;
        titleBar.ButtonHoverBackgroundColor = dark
            ? ColorHelper.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x0F, 0x00, 0x00, 0x00);
        titleBar.ButtonPressedBackgroundColor = dark
            ? ColorHelper.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x0A, 0x00, 0x00, 0x00);
        titleBar.ButtonInactiveForegroundColor = dark
            ? ColorHelper.FromArgb(0xFF, 0x78, 0x78, 0x78)
            : ColorHelper.FromArgb(0xFF, 0x9B, 0x9B, 0x9B);
    }

    private void UpdateTitleBarStatus()
    {
        if (shuttingDown) return;
        if (RequiresOnboarding)
        {
            TitleBarStatusButton.Visibility = Visibility.Collapsed;
            TitleBarSearchBox.Visibility = Visibility.Collapsed;
            TitleBarSearchButton.Visibility = Visibility.Collapsed;
            TitleBarBellButton.Visibility = Visibility.Collapsed;
            UpdateTitleBarPassthrough();
            return;
        }

        var environment = services.Environment;
        var current = (Running: environment.IsRunning, Degraded: environment.IsDegraded);
        if (TitleBarBellButton.Visibility != Visibility.Visible)
        {
            TitleBarBellButton.Visibility = Visibility.Visible;
            UpdateTitleBarPassthrough();
        }
        if (displayedTitleBarStatus == current) return;
        displayedTitleBarStatus = current;
        var tone = current.Running
            ? StatusTone.Success
            : current.Degraded ? StatusTone.Caution : StatusTone.Critical;
        TitleBarStatus.Style = StatusStyles.Pill(tone);
        TitleBarStatusDot.Style = StatusStyles.Dot(tone);
        TitleBarStatusText.Text = AppLocalization.Format(
            "TitleBarEnvironmentStatus",
            AppLocalization.Get(
                current.Running
                    ? "DashboardRunning"
                    : current.Degraded ? "DashboardRecovering" : "DashboardStopped"
            )
        );
        AutomationProperties.SetName(TitleBarStatusButton, TitleBarStatusText.Text);
        ToolTipService.SetToolTip(TitleBarStatusButton, AppLocalization.Get("TitleBarStatusTooltip"));
        TitleBarStatusButton.Visibility = Visibility.Visible;
        if (TitleBarSearchBox.Visibility != Visibility.Visible) TitleBarSearchButton.Visibility = Visibility.Visible;
        UpdateTitleBarPassthrough();
    }

    private void ResizeWindow()
    {
        var windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var dpi = GetDpiForWindow(windowHandle);
        var scale = dpi == 0 ? 1d : dpi / 96d;
        var margin = (int)Math.Round(LogicalWindowMargin * scale);
        var width = Math.Min(
            (int)Math.Round(LogicalWindowWidth * scale),
            Math.Max(1, workArea.Width - (margin * 2))
        );
        var height = Math.Min(
            (int)Math.Round(LogicalWindowHeight * scale),
            Math.Max(1, workArea.Height - (margin * 2))
        );
        appWindow.MoveAndResize(new RectInt32(
            workArea.X + ((workArea.Width - width) / 2),
            workArea.Y + ((workArea.Height - height) / 2),
            width,
            height
        ));
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    private void Navigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args
    )
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        ShowPage(tag);
    }

    private void ShowPage(string tag)
    {
        if (shuttingDown) return;
        tag = NormalizePageTag(tag);
        if (!cachedPages.TryGetValue(tag, out var page))
        {
            page = CreatePage(tag);
            cachedPages[tag] = page;
            if (tag == "logs") pendingLogSitePath = null;
        }
        else if (tag == "logs" && pendingLogSitePath is { } sitePath && page is LogsPage logsPage)
        {
            pendingLogSitePath = null;
            logsPage.ShowSite(sitePath);
        }

        if (!ReferenceEquals(ContentFrame.Content, page)) ContentFrame.Content = page;
        MarkPageSeen(tag);
    }

    private static string NormalizePageTag(string tag)
    {
        // Tinker moved into the Sites details pane; old links and saved pages land on Sites.
        if (tag == "tinker") return "sites";
        return tag is "dashboard" or "general" or "sites" or "php" or "node" or "services"
            or "updates" or "mail" or "dumps" or "logs" or "debugger" or "about"
            ? tag
            : "general";
    }

    private Page CreatePage(string tag)
    {
        switch (tag)
        {
            case "dashboard":
                return new DashboardPage(
                    services.Core,
                    services.SiteSettings,
                    services.Environment,
                    services.Services,
                    services.Mail,
                    services.Dumps,
                    services.Hosts,
                    services.Certificates,
                    services.PhpInstaller,
                    services.RuntimePolicy,
                    services.ComposerTools,
                    services.NodeInstaller,
                    services.GitInstaller,
                    services.StartupSnapshot
                );
            case "general":
                return new GeneralPage(
                    services.Core,
                    services.PhpInstaller,
                    services.RuntimePolicy,
                    services.NodeInstaller,
                    services.ComposerTools,
                    services.GitInstaller,
                    services.Startup,
                    services.Hosts,
                    services.Certificates,
                    services.SiteSettings,
                    services.Updates,
                    services.ComponentUpdates,
                    services.UserPath,
                    services.ShellIntegration
                );
            case "sites":
                return new SitesPage(
                    services.Core,
                    services.Environment,
                    services.SiteSettings,
                    services.ProjectCreator,
                    services.SiteRuntimes,
                    services.CommandFavorites,
                    services.PhpInstaller,
                    services.RuntimePolicy,
                    services.NodeInstaller,
                    services.ComposerTools,
                    services.Services,
                    services.SiteProcesses,
                    services.Certificates,
                    services.Mail,
                    services.ProxySites,
                    services.Shares
                );
            case "php":
                return new PhpPage(
                    services.Core,
                    services.RuntimePolicy,
                    services.PhpInstaller,
                    services.ComposerTools,
                    services.UserPath,
                    services.PhpExtensions
                );
            case "node":
                return new NodePage(
                    services.NodeInstaller,
                    services.ComposerTools,
                    services.RuntimePolicy,
                    services.UserPath
                );
            case "services":
                return new ServicesPage(
                    services.Services,
                    services.Core,
                    services.SiteSettings
                );
            case "updates":
                return new UpdatesPage(
                    services.SiteSettings,
                    services.Updates,
                    services.ComponentUpdates,
                    services.ComponentUpdater,
                    services.SelfUpdater
                );
            case "mail":
                return new MailPage(
                    services.Mail,
                    services.Core,
                    services.SiteSettings
                );
            case "dumps":
                return new DumpsPage(services.Dumps);
            case "logs":
                return new LogsPage(
                    services.Core,
                    services.SiteSettings,
                    pendingLogSitePath
                );
            case "debugger":
                return new DebuggerPage(
                    services.Core,
                    services.RuntimePolicy,
                    services.PhpInstaller,
                    services.Xdebug,
                    services.ComponentUpdates,
                    services.SiteSettings,
                    services.Environment
                );
            case "about":
                return new AboutPage(services.SiteSettings, services.Updates);
            default:
                return CreatePage("general");
        }
    }

    public void NavigateToLogs(string sitePath)
    {
        if (shuttingDown) return;
        pendingLogSitePath = sitePath;
        var logsItem = Navigation.MenuItems
            .OfType<NavigationViewItem>()
            .First(item => string.Equals(item.Tag?.ToString(), "logs", StringComparison.Ordinal));
        if (ReferenceEquals(Navigation.SelectedItem, logsItem))
        {
            ShowPage("logs");
        }
        else
        {
            Navigation.SelectedItem = logsItem;
        }
    }

    // Selects a site on the Sites page (from the herdme CLI, Jump List, or a herdme:// link).
    public void NavigateToSite(string sitePath)
    {
        if (shuttingDown) return;
        if (!cachedPages.TryGetValue("sites", out var page))
        {
            page = CreatePage("sites");
            cachedPages["sites"] = page;
        }
        if (page is SitesPage sitesPage) sitesPage.RequestSelectSite(sitePath);
        NavigateToPage("sites");
        if (Navigation.SelectedItem is NavigationViewItem { Tag: "sites" }) ShowPage("sites");
    }

    // Sites changed outside the Sites page (linked from Explorer or the CLI, or a new folder
    // appeared in a parked root). A cached, visible page rescans; a hidden one on next show.
    public void NotifySitesChanged()
    {
        if (shuttingDown) return;
        if (cachedPages.TryGetValue("sites", out var page) && page is SitesPage sitesPage)
        {
            sitesPage.RequestRescan();
        }
    }

    // Opens Sites with the Tinker tab, for the given site or the one already selected.
    public void NavigateToTinker(string? sitePath)
    {
        if (shuttingDown) return;
        if (!cachedPages.TryGetValue("sites", out var page))
        {
            page = CreatePage("sites");
            cachedPages["sites"] = page;
        }
        if (page is SitesPage sitesPage) sitesPage.RequestTinker(sitePath);
        NavigateToPage("sites");
        if (Navigation.SelectedItem is NavigationViewItem { Tag: "sites" }) ShowPage("sites");
    }

    public void NavigateToPage(string tag)
    {
        if (shuttingDown) return;
        if (tag == "tinker")
        {
            NavigateToTinker(null);
            return;
        }
        var item = Navigation.MenuItems
            .Concat(Navigation.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(candidate => string.Equals(
                candidate.Tag?.ToString(),
                tag,
                StringComparison.Ordinal
            ));
        if (item is not null) Navigation.SelectedItem = item;
    }

    private void Onboarding_SetupCompleted(object sender, EventArgs e)
    {
        if (shuttingDown) return;
        RequiresOnboarding = false;
        Onboarding.Visibility = Visibility.Collapsed;
        Navigation.Visibility = Visibility.Visible;
        UpdateTitleBarStatus();
        UpdateStatusBar();
        InitialSetupCompleted?.Invoke(this, EventArgs.Empty);
        if (Onboarding.RequestedNextStep != OnboardingNextStep.None)
        {
            StartSitesNextStep(Onboarding.RequestedNextStep);
        }
    }

    // Opens Sites and, once its folders are loaded, starts Create Laravel or Park a folder.
    internal void StartSitesNextStep(OnboardingNextStep step)
    {
        if (!cachedPages.TryGetValue("sites", out var page))
        {
            page = CreatePage("sites");
            cachedPages["sites"] = page;
        }
        if (page is SitesPage sitesPage) sitesPage.RequestNextStep(step);
        NavigateToPage("sites");
        if (Navigation.SelectedItem is NavigationViewItem { Tag: "sites" }) ShowPage("sites");
    }
}
