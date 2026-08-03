using HerdMe.Windows.Pages;
using HerdMe.Windows.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace HerdMe.Windows;

public sealed partial class MainWindow : Window
{
    private const int LogicalWindowWidth = 1_080;
    private const int LogicalWindowHeight = 720;
    private const int LogicalWindowMargin = 16;
    private readonly AppServices services;
    private string? pendingLogSitePath;
    private string? configurationLoadWarning;

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
    }

    public bool RequiresOnboarding { get; private set; }

    public event EventHandler? InitialSetupCompleted;

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
            Title = "HerdMe settings could not be loaded",
            Content = warning,
            CloseButtonText = "OK",
            XamlRoot = xamlRoot
        };
        await dialog.ShowAsync();
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
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
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
        switch (tag)
        {
            case "dashboard":
                ContentFrame.Content = new DashboardPage(
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
                    services.ComposerTools
                );
                break;
            case "general":
                ContentFrame.Content = new GeneralPage(
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
                break;
            case "sites":
                ContentFrame.Content = new SitesPage(
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
                break;
            case "php":
                ContentFrame.Content = new PhpPage(
                    services.Core,
                    services.RuntimePolicy,
                    services.PhpInstaller,
                    services.ComposerTools,
                    services.UserPath,
                    services.PhpExtensions
                );
                break;
            case "node":
                ContentFrame.Content = new NodePage(
                    services.NodeInstaller,
                    services.ComposerTools,
                    services.RuntimePolicy,
                    services.UserPath
                );
                break;
            case "services":
                ContentFrame.Content = new ServicesPage(
                    services.Services,
                    services.Core,
                    services.SiteSettings
                );
                break;
            case "mail":
                ContentFrame.Content = new MailPage(
                    services.Mail,
                    services.Core,
                    services.SiteSettings
                );
                break;
            case "dumps":
                ContentFrame.Content = new DumpsPage(services.Dumps);
                break;
            case "logs":
                ContentFrame.Content = new LogsPage(
                    services.Core,
                    services.SiteSettings,
                    pendingLogSitePath
                );
                pendingLogSitePath = null;
                break;
            case "debugger":
                ContentFrame.Content = new DebuggerPage(
                    services.Core,
                    services.RuntimePolicy,
                    services.PhpInstaller,
                    services.Xdebug,
                    services.ComponentUpdates,
                    services.SiteSettings,
                    services.Environment
                );
                break;
            case "about":
                ContentFrame.Content = new AboutPage(services.SiteSettings, services.Updates);
                break;
            default:
                ContentFrame.Content = new GeneralPage(
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
                break;
        }
    }

    public void NavigateToLogs(string sitePath)
    {
        pendingLogSitePath = sitePath;
        var logsItem = Navigation.MenuItems
            .OfType<NavigationViewItem>()
            .First(item => string.Equals(item.Tag?.ToString(), "logs", StringComparison.Ordinal));
        if (ReferenceEquals(Navigation.SelectedItem, logsItem))
        {
            ContentFrame.Content = new LogsPage(
                services.Core,
                services.SiteSettings,
                pendingLogSitePath
            );
            pendingLogSitePath = null;
        }
        else
        {
            Navigation.SelectedItem = logsItem;
        }
    }

    public void NavigateToPage(string tag)
    {
        var item = Navigation.MenuItems
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
        RequiresOnboarding = false;
        Onboarding.Visibility = Visibility.Collapsed;
        Navigation.Visibility = Visibility.Visible;
        InitialSetupCompleted?.Invoke(this, EventArgs.Empty);
    }
}
