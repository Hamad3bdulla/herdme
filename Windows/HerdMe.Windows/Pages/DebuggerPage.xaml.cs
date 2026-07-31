using System.Collections.ObjectModel;
using System.Diagnostics;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

public sealed partial class DebuggerPage : Page
{
    private readonly CoreClient coreClient;
    private readonly PhpRuntimePolicy runtimePolicy;
    private readonly PhpRuntimeInstaller runtimeInstaller;
    private readonly XdebugManager xdebugManager;
    private readonly ManagedComponentUpdateManager componentUpdateManager;
    private readonly SiteConfigurationStore siteSettings;
    private readonly WindowsLocalEnvironment environment;
    private PhpRuntimeSettings settings;
    private string? phpExecutable;

    public ObservableCollection<SiteRecord> Sites { get; } = [];

    public DebuggerPage(
        CoreClient coreClient,
        PhpRuntimePolicy runtimePolicy,
        PhpRuntimeInstaller runtimeInstaller,
        XdebugManager xdebugManager,
        ManagedComponentUpdateManager componentUpdateManager,
        SiteConfigurationStore siteSettings,
        WindowsLocalEnvironment environment
    )
    {
        this.coreClient = coreClient;
        this.runtimePolicy = runtimePolicy;
        this.runtimeInstaller = runtimeInstaller;
        this.xdebugManager = xdebugManager;
        this.componentUpdateManager = componentUpdateManager;
        this.siteSettings = siteSettings;
        this.environment = environment;
        InitializeComponent();
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
        InstallStatusText.Text = AppLocalization.Get("DebuggerChecking");
        InstallButton.IsEnabled = false;
        try
        {
            phpExecutable = runtimeInstaller.IsInstalled(settings.PhpCycle)
                ? runtimeInstaller.PhpExecutable(settings.PhpCycle)
                : null;
            if (phpExecutable is null)
            {
                InstallStatusText.Text = AppLocalization.Get("DebuggerPhpUnavailable");
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
                ? AppLocalization.Get("DebuggerNotInstalled")
                : AppLocalization.Format("DebuggerVersion", installation.Version);
            var update = installation is null
                ? null
                : componentUpdateManager.LatestUpdate($"xdebug:{settings.PhpCycle}");
            if (installation is not null && update is not null
                && !RuntimeVersionComparison.IsNewer(
                    update.LatestVersion,
                    installation.Version
                ))
            {
                update = null;
            }
            if (installation is not null && update is null)
            {
                try
                {
                    var release = await xdebugManager.ResolveReleaseAsync(phpExecutable);
                    if (RuntimeVersionComparison.IsNewer(
                            release.Version,
                            installation.Version
                        ))
                    {
                        update = new ManagedComponentUpdate(
                            $"xdebug:{settings.PhpCycle}",
                            $"Xdebug (PHP {settings.PhpCycle})",
                            installation.Version,
                            release.Version,
                            "debugger"
                        );
                    }
                }
                catch (Exception)
                {
                }
            }
            if (update is not null)
            {
                InstallStatusText.Text = AppLocalization.Format(
                    "DebuggerUpdateAvailable",
                    update.LatestVersion
                );
            }
            InstallButtonText.Text = AppLocalization.Get(
                update is null ? "CommonInstall" : "DebuggerUpdateButton"
            );
            InstallButton.IsEnabled = installation is null || update is not null;
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
        InstallStatusText.Text = AppLocalization.Get("DebuggerInstalling");
        try
        {
            var installation = await xdebugManager.InstallAsync(phpExecutable);
            settings.PhpCycle = await xdebugManager.PhpCycleAsync(phpExecutable);
            ExtensionPathText.Text = installation.ExtensionPath;
            InstallStatusText.Text = AppLocalization.Format(
                "DebuggerVersion",
                installation.Version
            );
            InstallButtonText.Text = AppLocalization.Get("CommonInstall");
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
            SaveStatusText.Text = AppLocalization.Get("DebuggerSavedForNextStart");
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
        EndpointText.Text = AppLocalization.Format(
            "DebuggerIdeEndpoint",
            settings.Debugger.Port
        );
    }

    private async Task RefreshSitesAsync()
    {
        SessionProgress.IsActive = true;
        SessionStatusText.Text = AppLocalization.Get("DebuggerScanningSites");
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
            SessionStatusText.Text = AppLocalization.Get("DebuggerEnableAndSave");
            return;
        }
        if (!environment.IsRunning)
        {
            SessionStatusText.Text = AppLocalization.Get("DebuggerStartEnvironment");
            return;
        }

        try
        {
            var uri = SitePresentation.DebugUri(
                site,
                true,
                environment.HttpPort,
                environment.HttpsPort,
                settings.Debugger.IdeKey
            );
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            SessionStatusText.Text = AppLocalization.Format("DebuggerOpenedSite", site.Domain);
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
            ? AppLocalization.Get("DebuggerNoSites")
            : environment.IsRunning
                ? settings.Debugger.Enabled
                    ? AppLocalization.Get("DebuggerReady")
                    : AppLocalization.Get("DebuggerEnableToStart")
                : AppLocalization.Get("DebuggerStartEnvironment");
    }
}
