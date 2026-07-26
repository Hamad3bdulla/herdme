using HerdMe.Windows.Pages;
using HerdMe.Windows.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace HerdMe.Windows;

public sealed partial class MainWindow : Window
{
    private string? pendingLogSitePath;
    private string? configurationLoadWarning;

    public MainWindow(bool skipOnboarding = false)
    {
        InitializeComponent();
        ResizeWindow();
        var siteSettings = AppServices.SiteSettings.Load();
        _ = AppServices.Services.LoadInstances();
        configurationLoadWarning = string.Join(
            Environment.NewLine + Environment.NewLine,
            new[]
            {
                AppServices.SiteSettings.LastLoadWarning,
                AppServices.Services.LastLoadWarning
            }.Where(message => !string.IsNullOrWhiteSpace(message))
        );
        if (string.IsNullOrWhiteSpace(configurationLoadWarning)) configurationLoadWarning = null;
        if (configurationLoadWarning is not null) Activated += ShowConfigurationLoadWarning;
        RequiresOnboarding = !skipOnboarding
            && !siteSettings.OnboardingCompleted;
        Navigation.Visibility = RequiresOnboarding ? Visibility.Collapsed : Visibility.Visible;
        Onboarding.Visibility = RequiresOnboarding ? Visibility.Visible : Visibility.Collapsed;
        Navigation.SelectedItem = Navigation.MenuItems[0];
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
        AppWindow.GetFromWindowId(windowId).Resize(new SizeInt32(1_080, 720));
    }

    private void Navigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args
    )
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        switch (tag)
        {
            case "general":
                ContentFrame.Navigate(typeof(GeneralPage));
                break;
            case "sites":
                ContentFrame.Navigate(typeof(SitesPage));
                break;
            case "php":
                ContentFrame.Navigate(typeof(PhpPage));
                break;
            case "node":
                ContentFrame.Navigate(typeof(NodePage));
                break;
            case "services":
                ContentFrame.Navigate(typeof(ServicesPage));
                break;
            case "mail":
                ContentFrame.Navigate(typeof(MailPage));
                break;
            case "dumps":
                ContentFrame.Navigate(typeof(DumpsPage));
                break;
            case "logs":
                ContentFrame.Navigate(typeof(LogsPage), pendingLogSitePath);
                pendingLogSitePath = null;
                break;
            case "debugger":
                ContentFrame.Navigate(typeof(DebuggerPage));
                break;
            case "about":
                ContentFrame.Navigate(typeof(AboutPage));
                break;
            default:
                ContentFrame.Navigate(typeof(GeneralPage));
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
            ContentFrame.Navigate(typeof(LogsPage), pendingLogSitePath);
            pendingLogSitePath = null;
        }
        else
        {
            Navigation.SelectedItem = logsItem;
        }
    }

    private void Onboarding_SetupCompleted(object sender, EventArgs e)
    {
        RequiresOnboarding = false;
        Onboarding.Visibility = Visibility.Collapsed;
        Navigation.Visibility = Visibility.Visible;
        InitialSetupCompleted?.Invoke(this, EventArgs.Empty);
    }
}
