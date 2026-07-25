using System.Collections.ObjectModel;
using System.Diagnostics;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

public sealed partial class DebuggerPage : Page
{
    private readonly CoreClient coreClient = new();
    private readonly PhpRuntimePolicy runtimePolicy;
    private readonly PhpRuntimeInstaller runtimeInstaller;
    private readonly XdebugManager xdebugManager = new();
    private readonly SiteConfigurationStore siteSettings = AppServices.SiteSettings;
    private PhpRuntimeSettings settings;
    private string? phpExecutable;

    public ObservableCollection<SiteRecord> Sites { get; } = [];

    public DebuggerPage()
    {
        InitializeComponent();
        runtimePolicy = new PhpRuntimePolicy(coreClient);
        runtimeInstaller = new PhpRuntimeInstaller(coreClient);
        settings = runtimePolicy.Load();
        ApplySettings();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        await RefreshSitesAsync();
    }

    private async Task RefreshAsync()
    {
        InstallProgress.IsActive = true;
        InstallStatusText.Text = "Checking";
        InstallButton.IsEnabled = false;
        try
        {
            phpExecutable = runtimeInstaller.IsInstalled(settings.PhpCycle)
                ? runtimeInstaller.PhpExecutable(settings.PhpCycle)
                : null;
            if (phpExecutable is null)
            {
                InstallStatusText.Text = "PHP unavailable";
                EnabledToggle.IsEnabled = false;
                return;
            }

            settings.PhpCycle = await xdebugManager.PhpCycleAsync(phpExecutable);
            var installation = await xdebugManager.InstalledAsync(
                phpExecutable,
                settings.PhpCycle
            );
            ExtensionPathText.Text = xdebugManager.ExtensionPath(settings.PhpCycle);
            InstallStatusText.Text = installation is null
                ? "Not installed"
                : $"Version {installation.Version}";
            InstallButton.IsEnabled = installation is null;
            EnabledToggle.IsEnabled = installation is not null;
            if (installation is null)
            {
                EnabledToggle.IsOn = false;
                settings.Debugger.Enabled = false;
            }
        }
        catch (Exception error)
        {
            InstallStatusText.Text = error.Message;
            EnabledToggle.IsEnabled = false;
        }
        finally
        {
            InstallProgress.IsActive = false;
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (phpExecutable is null) return;
        InstallProgress.IsActive = true;
        InstallButton.IsEnabled = false;
        InstallStatusText.Text = "Installing";
        try
        {
            var installation = await xdebugManager.InstallAsync(phpExecutable);
            settings.PhpCycle = await xdebugManager.PhpCycleAsync(phpExecutable);
            ExtensionPathText.Text = installation.ExtensionPath;
            InstallStatusText.Text = $"Version {installation.Version}";
            EnabledToggle.IsEnabled = true;
        }
        catch (Exception error)
        {
            InstallStatusText.Text = error.Message;
            InstallButton.IsEnabled = true;
        }
        finally
        {
            InstallProgress.IsActive = false;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        settings.Debugger.Enabled = EnabledToggle.IsOn;
        settings.Debugger.DetectBreakpoints = TriggerToggle.IsOn;
        settings.Debugger.Port = double.IsNaN(PortBox.Value) ? 9_003 : (int)PortBox.Value;
        settings.Debugger.IdeKey = IdeKeyBox.Text;
        try
        {
            settings = PhpRuntimePolicy.Normalize(settings);
            _ = PhpRuntimePolicy.BuildPhpOptions(settings);
            runtimePolicy.Save(settings);
            ApplySettings();
            SaveStatusText.Text = "Saved for the next local PHP start.";
            UpdateSessionState();
        }
        catch (Exception error)
        {
            EnabledToggle.IsOn = false;
            settings.Debugger.Enabled = false;
            SaveStatusText.Text = error.Message;
        }
    }

    private void ApplySettings()
    {
        EnabledToggle.IsOn = settings.Debugger.Enabled;
        TriggerToggle.IsOn = settings.Debugger.DetectBreakpoints;
        PortBox.Value = settings.Debugger.Port;
        IdeKeyBox.Text = settings.Debugger.IdeKey;
        EndpointText.Text = $"IDE endpoint: 127.0.0.1:{settings.Debugger.Port}";
    }

    private async Task RefreshSitesAsync()
    {
        SessionProgress.IsActive = true;
        SessionStatusText.Text = "Scanning sites";
        try
        {
            var siteConfiguration = siteSettings.Load();
            var scanned = await coreClient.ScanAsync(
                siteConfiguration.Roots,
                siteConfiguration.Tld,
                siteConfiguration.LinkedSites
            );
            Sites.Clear();
            foreach (var site in scanned) Sites.Add(site);
            SiteBox.SelectedIndex = Sites.Count > 0 ? 0 : -1;
            UpdateSessionState();
        }
        catch (Exception error)
        {
            SessionStatusText.Text = error.Message;
            StartSessionButton.IsEnabled = false;
        }
        finally
        {
            SessionProgress.IsActive = false;
        }
    }

    private void SiteBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSessionState();
    }

    private void StartSession_Click(object sender, RoutedEventArgs e)
    {
        if (SiteBox.SelectedItem is not SiteRecord site) return;
        if (!settings.Debugger.Enabled)
        {
            SessionStatusText.Text = "Enable Xdebug and save the session settings first.";
            return;
        }
        if (!AppServices.Environment.IsRunning)
        {
            SessionStatusText.Text = "Start the local site environment first.";
            return;
        }

        try
        {
            var uri = SitePresentation.DebugUri(
                site,
                true,
                AppServices.Environment.HttpPort,
                AppServices.Environment.HttpsPort,
                settings.Debugger.IdeKey
            );
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            SessionStatusText.Text = $"Opened {site.Domain}";
        }
        catch (Exception error)
        {
            SessionStatusText.Text = error.Message;
        }
    }

    private void UpdateSessionState()
    {
        var hasSite = SiteBox.SelectedItem is SiteRecord;
        StartSessionButton.IsEnabled = hasSite && settings.Debugger.Enabled;
        SessionStatusText.Text = Sites.Count == 0
            ? "No sites available"
            : AppServices.Environment.IsRunning
                ? settings.Debugger.Enabled ? "Ready" : "Enable Xdebug to start a session"
                : "Start the local site environment first";
    }
}
