using System.Collections.ObjectModel;
using System.Diagnostics;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

public sealed partial class GeneralPage : Page
{
    private readonly CoreClient coreClient;
    private readonly PhpRuntimeInstaller runtimeInstaller;
    private readonly PhpRuntimePolicy runtimePolicy;
    private readonly NodeRuntimeInstaller nodeInstaller;
    private readonly ComposerToolManager composerTools;
    private readonly GitRuntimeInstaller gitInstaller;
    private readonly WindowsStartupManager startupManager;
    private readonly WindowsHostsManager hostsManager;
    private readonly WindowsCertificateManager certificateManager;
    private readonly SiteConfigurationStore settingsStore;
    private readonly AppUpdateManager updateManager;
    private bool loadingStartup;
    private bool loadingUpdateSettings;

    public ObservableCollection<RuntimeCheck> Runtimes { get; } = [];

    public GeneralPage(
        CoreClient coreClient,
        PhpRuntimeInstaller runtimeInstaller,
        PhpRuntimePolicy runtimePolicy,
        NodeRuntimeInstaller nodeInstaller,
        ComposerToolManager composerTools,
        GitRuntimeInstaller gitInstaller,
        WindowsStartupManager startupManager,
        WindowsHostsManager hostsManager,
        WindowsCertificateManager certificateManager,
        SiteConfigurationStore settingsStore,
        AppUpdateManager updateManager
    )
    {
        this.coreClient = coreClient;
        this.runtimeInstaller = runtimeInstaller;
        this.runtimePolicy = runtimePolicy;
        this.nodeInstaller = nodeInstaller;
        this.composerTools = composerTools;
        this.gitInstaller = gitInstaller;
        this.startupManager = startupManager;
        this.hostsManager = hostsManager;
        this.certificateManager = certificateManager;
        this.settingsStore = settingsStore;
        this.updateManager = updateManager;
        InitializeComponent();
        CoreExecutableText.Text = coreClient.ExecutablePath;
        ToolTipService.SetToolTip(
            OpenDataButton,
            AppLocalization.Get("GeneralOpenApplicationData")
        );
        loadingStartup = true;
        StartupToggle.IsOn = startupManager.IsEnabled;
        loadingStartup = false;
        var settings = settingsStore.Load();
        TldTextBox.Text = settings.Tld;
        loadingUpdateSettings = true;
        AutomaticUpdatesToggle.IsOn = settings.AutomaticUpdates;
        UpdateChannelBox.SelectedIndex = settings.UpdateChannel == "Beta" ? 1 : 0;
        UpdateStatusText.Text = AppLocalization.Format(
            "GeneralChannelStatus",
            UpdateChannelDisplayName(settings.UpdateChannel)
        );
        loadingUpdateSettings = false;
    }

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (loadingStartup) return;
        try
        {
            startupManager.SetEnabled(StartupToggle.IsOn);
        }
        catch (Exception error)
        {
            loadingStartup = true;
            StartupToggle.IsOn = !StartupToggle.IsOn;
            loadingStartup = false;
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "HerdMe",
                Content = error.Message,
                CloseButtonText = AppLocalization.Get("CommonOk")
            };
            await dialog.ShowAsync();
        }
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        if (settingsStore.Load().AutomaticUpdates)
        {
            await CheckForUpdatesAsync(userInitiated: false);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        CoreProgress.IsActive = true;
        CoreStatusText.Text = AppLocalization.Get("GeneralChecking");
        PhpExtensionStatusText.Text = AppLocalization.Get("GeneralChecking");
        PhpExtensionDetailText.Text = string.Empty;
        PhpExtensionProgress.IsActive = true;
        OpenDataButton.IsEnabled = false;
        Runtimes.Clear();
        try
        {
            var report = await coreClient.DoctorAsync();
            CoreStatusText.Text = report.Platform == "windows"
                ? AppLocalization.Get("GeneralReady")
                : report.Platform;
            SupportPathText.Text = report.SupportPath;
            Directory.CreateDirectory(report.SupportPath);
            OpenDataButton.IsEnabled = true;
            foreach (var runtime in ManagedRuntimeChecks())
            {
                Runtimes.Add(runtime);
            }
            var phpSettings = runtimePolicy.Load();
            var phpPath = runtimeInstaller.IsInstalled(phpSettings.PhpCycle)
                ? runtimeInstaller.PhpExecutable(phpSettings.PhpCycle)
                : null;
            if (phpPath is not null)
            {
                try
                {
                    runtimeInstaller.EnsureManagedConfiguration(phpSettings.PhpCycle);
                    var extensions = await coreClient.ValidatePhpAsync(phpPath);
                    PhpExtensionStatusText.Text = extensions.Compatible
                        ? AppLocalization.Get("GeneralLaravelCompatible")
                        : AppLocalization.Get("GeneralMissingExtensions");
                    PhpExtensionDetailText.Text = extensions.Compatible
                        ? AppLocalization.Format(
                            "GeneralRequiredExtensionsLoaded",
                            extensions.Required.Count
                        )
                        : string.Join(", ", extensions.Missing);
                }
                catch (Exception error)
                {
                    PhpExtensionStatusText.Text = AppLocalization.Get("GeneralCheckFailed");
                    PhpExtensionDetailText.Text = error.Message;
                }
            }
            else
            {
                PhpExtensionStatusText.Text = AppLocalization.Get("GeneralManagedPhpUnavailable");
                PhpExtensionDetailText.Text = AppLocalization.Format(
                    "GeneralInstallManagedPhp",
                    phpSettings.PhpCycle
                );
            }
        }
        catch (Exception error)
        {
            CoreStatusText.Text = error.Message;
            PhpExtensionStatusText.Text = AppLocalization.Get("GeneralUnavailable");
            PhpExtensionDetailText.Text = string.Empty;
        }
        finally
        {
            CoreProgress.IsActive = false;
            PhpExtensionProgress.IsActive = false;
        }
        await RefreshLocalSetupAsync();
    }

    private IReadOnlyList<RuntimeCheck> ManagedRuntimeChecks()
    {
        var phpCycle = runtimePolicy.Load().PhpCycle;
        var phpPath = runtimeInstaller.PhpExecutable(phpCycle);
        var nodeVersion = nodeInstaller.LoadSettings().ActiveVersion;
        if (string.IsNullOrWhiteSpace(nodeVersion)
            || !File.Exists(Path.Combine(nodeInstaller.RuntimeRoot, nodeVersion, "node.exe")))
        {
            nodeVersion = nodeInstaller.InstalledVersions().FirstOrDefault() ?? string.Empty;
        }
        var nodeDirectory = string.IsNullOrWhiteSpace(nodeVersion)
            ? null
            : Path.Combine(nodeInstaller.RuntimeRoot, nodeVersion);
        var gitPath = gitInstaller.InstalledExecutable();

        return
        [
            ManagedRuntime("php", phpPath, runtimeInstaller.IsInstalled(phpCycle)),
            ManagedRuntime(
                "composer",
                composerTools.ComposerCommandPath,
                File.Exists(composerTools.ComposerPath)
                    && File.Exists(composerTools.ComposerCommandPath)
            ),
            ManagedRuntime(
                "laravel",
                composerTools.LaravelExecutable,
                composerTools.IsLaravelInstallerReady(phpCycle)
            ),
            ManagedRuntime(
                "node",
                nodeDirectory is null ? null : Path.Combine(nodeDirectory, "node.exe"),
                nodeDirectory is not null && File.Exists(Path.Combine(nodeDirectory, "node.exe"))
            ),
            ManagedRuntime(
                "npm",
                nodeDirectory is null ? null : Path.Combine(nodeDirectory, "npm.cmd"),
                nodeDirectory is not null && File.Exists(Path.Combine(nodeDirectory, "npm.cmd"))
            ),
            ManagedRuntime("git", gitPath, gitPath is not null && File.Exists(gitPath))
        ];
    }

    private static RuntimeCheck ManagedRuntime(string name, string? path, bool available)
    {
        return new RuntimeCheck
        {
            Name = name,
            Available = available,
            Detected = available,
            Source = "managed",
            Path = available ? path : null
        };
    }

    private void TldTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var settings = settingsStore.Load();
        settingsStore.UpdateSites(
            settings.Roots,
            TldTextBox.Text,
            startAutomatically: true,
            showPreviews: settings.ShowPreviews
        );
        TldTextBox.Text = settingsStore.Load().Tld;
    }

    private async void InstallDomains_Click(object sender, RoutedEventArgs e)
    {
        await RunLocalSetupAsync(async () =>
        {
            var settings = settingsStore.Load();
            var sites = await coreClient.ScanAsync(
                settings.Roots,
                settings.Tld,
                settings.LinkedSites
            );
            if (sites.Count == 0)
            {
                throw new InvalidOperationException(
                    AppLocalization.Get("GeneralAddSiteBeforeDomains")
                );
            }
            await hostsManager.EnsureMappingsAsync(sites.Select(site => site.Domain));
        });
    }

    private async void RemoveDomains_Click(object sender, RoutedEventArgs e)
    {
        await RunLocalSetupAsync(() => hostsManager.RemoveMappingsAsync());
    }

    private async void TrustCertificate_Click(object sender, RoutedEventArgs e)
    {
        await RunLocalSetupAsync(() =>
        {
            certificateManager.TrustAuthority();
            return Task.CompletedTask;
        });
    }

    private async Task RunLocalSetupAsync(Func<Task> operation)
    {
        SetLocalSetupEnabled(false);
        try
        {
            await operation();
        }
        catch (Exception error)
        {
            await ShowMessageAsync("HerdMe", error.Message);
        }
        finally
        {
            SetLocalSetupEnabled(true);
            await RefreshLocalSetupAsync();
        }
    }

    private async Task RefreshLocalSetupAsync()
    {
        try
        {
            var domainsConfigured = await hostsManager.HasManagedMappingsAsync();
            DomainsStatusText.Text = domainsConfigured
                ? AppLocalization.Get("GeneralConfigured")
                : AppLocalization.Get("GeneralNotConfigured");
            RemoveDomainsButton.IsEnabled = domainsConfigured;
            InstallDomainsButton.Content = domainsConfigured
                ? AppLocalization.Get("GeneralUpdate")
                : AppLocalization.Get("GeneralSetUp");

            var certificateTrusted = certificateManager.IsAuthorityTrusted();
            CertificateStatusText.Text = certificateTrusted
                ? AppLocalization.Get("GeneralTrusted")
                : AppLocalization.Get("GeneralNotTrusted");
            TrustCertificateButton.Visibility = certificateTrusted
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        catch (Exception error)
        {
            DomainsStatusText.Text = error.Message;
            CertificateStatusText.Text = AppLocalization.Get("GeneralUnavailable");
        }
    }

    private void SetLocalSetupEnabled(bool enabled)
    {
        InstallDomainsButton.IsEnabled = enabled;
        RemoveDomainsButton.IsEnabled = enabled;
        TrustCertificateButton.IsEnabled = enabled;
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(SupportPathText.Text))
        {
            var startInfo = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add(SupportPathText.Text);
            Process.Start(startInfo);
        }
    }

    private async void AutomaticUpdatesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (loadingUpdateSettings) return;
        if (!SaveUpdateSettings()) return;
        if (AutomaticUpdatesToggle.IsOn)
        {
            await CheckForUpdatesAsync(userInitiated: false);
        }
    }

    private async void UpdateChannelBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e
    )
    {
        if (loadingUpdateSettings) return;
        if (!SaveUpdateSettings()) return;
        if (AutomaticUpdatesToggle.IsOn)
        {
            await CheckForUpdatesAsync(userInitiated: false);
        }
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(userInitiated: true);
    }

    private bool SaveUpdateSettings()
    {
        try
        {
            var settings = settingsStore.Load();
            settings.AutomaticUpdates = AutomaticUpdatesToggle.IsOn;
            settings.UpdateChannel = SelectedUpdateChannel();
            settingsStore.Save(settings);
            UpdateStatusText.Text = AppLocalization.Format(
                "GeneralChannelStatus",
                UpdateChannelDisplayName(settings.UpdateChannel)
            );
            return true;
        }
        catch (Exception error)
        {
            _ = ShowMessageAsync(
                AppLocalization.Get("GeneralSettingsSaveFailed"),
                error.Message
            );
            return false;
        }
    }

    private string SelectedUpdateChannel()
    {
        return (UpdateChannelBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Stable";
    }

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        UpdateProgress.IsActive = true;
        CheckNowButton.IsEnabled = false;
        try
        {
            var channel = SelectedUpdateChannel();
            var channelName = UpdateChannelDisplayName(channel);
            UpdateStatusText.Text = AppLocalization.Format(
                "GeneralCheckingReleases",
                channelName
            );
            var result = await updateManager.CheckAsync(channel);
            if (result.AvailableRelease is { } release)
            {
                UpdateStatusText.Text = AppLocalization.Format(
                    "GeneralVersionAvailable",
                    release.Version
                );
                await ShowUpdateAsync(release);
            }
            else
            {
                UpdateStatusText.Text = AppLocalization.Format(
                    "GeneralUpToDateStatus",
                    result.CurrentVersion
                );
                if (userInitiated)
                {
                    await ShowMessageAsync(
                        AppLocalization.Get("GeneralUpToDateTitle"),
                        AppLocalization.Format(
                            "GeneralNewestRelease",
                            result.CurrentVersion,
                            channelName
                        )
                    );
                }
            }
        }
        catch (Exception error)
        {
            UpdateStatusText.Text = AppLocalization.Get("GeneralUpdateCheckFailed");
            if (userInitiated)
            {
                await ShowMessageAsync(
                    AppLocalization.Get("GeneralUpdateCheckFailed"),
                    error.Message
                );
            }
        }
        finally
        {
            UpdateProgress.IsActive = false;
            CheckNowButton.IsEnabled = true;
        }
    }

    private async Task ShowUpdateAsync(AppUpdateRelease release)
    {
        var downloadAvailable = Uri.TryCreate(
            release.PlatformDownloadUrl,
            UriKind.Absolute,
            out var downloadUri
        );
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("GeneralUpdateDialogTitle", release.Version),
            Content = release.Notes,
            CloseButtonText = downloadAvailable
                ? AppLocalization.Get("GeneralLater")
                : AppLocalization.Get("CommonOk")
        };
        if (downloadAvailable)
        {
            dialog.PrimaryButtonText = AppLocalization.Get("GeneralDownload");
        }
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.Primary && downloadUri is not null)
        {
            Process.Start(new ProcessStartInfo(downloadUri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = AppLocalization.Get("CommonOk")
        };
        await dialog.ShowAsync();
    }

    private static string UpdateChannelDisplayName(string channel)
    {
        return AppLocalization.Get(
            channel.Equals("Beta", StringComparison.OrdinalIgnoreCase)
                ? "GeneralChannelBeta"
                : "GeneralChannelStable"
        );
    }
}
