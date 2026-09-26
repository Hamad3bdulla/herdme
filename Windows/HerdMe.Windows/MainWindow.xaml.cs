using HerdMe.Windows.Pages;
using HerdMe.Windows.Services;
using HerdMe.Windows.Views;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
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
        Navigation.Visibility = RequiresOnboarding ? Visibility.Collapsed : Visibility.Visible;
        Onboarding.Visibility = RequiresOnboarding ? Visibility.Visible : Visibility.Collapsed;
        Navigation.SelectedItem = Navigation.MenuItems[0];
        if (ContentFrame.Content is null) ShowPage("dashboard");
        UpdateTitleBarStatus();
        App.MainWindowVisibilityChanged += App_MainWindowVisibilityChanged;
    }

    public bool RequiresOnboarding { get; private set; }

    public event EventHandler? InitialSetupCompleted;

    internal void PrepareForShutdown()
    {
        shuttingDown = true;
        App.MainWindowVisibilityChanged -= App_MainWindowVisibilityChanged;
        titleBarStatusTimer.Stop();
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
        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarInsets();
        RootLayout.ActualThemeChanged += (_, _) => ApplyCaptionButtonColors();
        titleBarStatusTimer.Tick += (_, _) => UpdateTitleBarStatus();
        // The status poll starts once App reports the window visible and pauses in the tray.
    }

    private void App_MainWindowVisibilityChanged(object? sender, bool visible)
    {
        if (shuttingDown) return;
        if (visible)
        {
            UpdateTitleBarStatus();
            titleBarStatusTimer.Start();
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
            TitleBarStatus.Visibility = Visibility.Collapsed;
            return;
        }

        var environment = services.Environment;
        var current = (Running: environment.IsRunning, Degraded: environment.IsDegraded);
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
        TitleBarStatus.Visibility = Visibility.Visible;
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
    }

    private static string NormalizePageTag(string tag)
    {
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
                    services.GitInstaller
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
                    services.UserPath
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
                    services.Mail
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
                    services.Environment,
                    services.PhpInstaller,
                    services.RuntimePolicy,
                    services.NodeInstaller,
                    services.ComposerTools,
                    services.GitInstaller,
                    services.Xdebug,
                    services.Services,
                    services.UserPath
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

    public void NavigateToPage(string tag)
    {
        if (shuttingDown) return;
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
        InitialSetupCompleted?.Invoke(this, EventArgs.Empty);
    }
}
