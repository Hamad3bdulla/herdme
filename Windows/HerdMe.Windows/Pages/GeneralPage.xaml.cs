using System.Collections.ObjectModel;
using System.Diagnostics;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

public sealed partial class GeneralPage : Page
{
    private readonly CoreClient coreClient = new();
    private readonly PhpRuntimeInstaller runtimeInstaller;
    private readonly PhpRuntimePolicy runtimePolicy;
    private readonly WindowsStartupManager startupManager = new();
    private readonly WindowsHostsManager hostsManager = new();
    private readonly WindowsCertificateManager certificateManager = new();
    private readonly SiteConfigurationStore settingsStore = new();
    private readonly AppUpdateManager updateManager = AppUpdateManager.Configured();
    private bool loadingStartup;
    private bool loadingUpdateSettings;

    public ObservableCollection<RuntimeCheck> Runtimes { get; } = [];

    public GeneralPage()
    {
        InitializeComponent();
        runtimeInstaller = new PhpRuntimeInstaller(coreClient);
        runtimePolicy = new PhpRuntimePolicy(coreClient);
        CoreExecutableText.Text = coreClient.ExecutablePath;
        loadingStartup = true;
        StartupToggle.IsOn = startupManager.IsEnabled;
        loadingStartup = false;
        var settings = settingsStore.Load();
        loadingUpdateSettings = true;
        AutomaticUpdatesToggle.IsOn = settings.AutomaticUpdates;
        UpdateChannelBox.SelectedIndex = settings.UpdateChannel == "Beta" ? 1 : 0;
        UpdateStatusText.Text = $"{settings.UpdateChannel} channel";
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
                CloseButtonText = "OK"
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
        CoreStatusText.Text = "Checking";
        PhpExtensionStatusText.Text = "Checking";
        PhpExtensionDetailText.Text = string.Empty;
        PhpExtensionProgress.IsActive = true;
        OpenDataButton.IsEnabled = false;
        Runtimes.Clear();
        try
        {
            var report = await coreClient.DoctorAsync();
            CoreStatusText.Text = report.Platform == "windows" ? "Ready" : report.Platform;
            SupportPathText.Text = report.SupportPath;
            Directory.CreateDirectory(report.SupportPath);
            OpenDataButton.IsEnabled = true;
            foreach (var runtime in report.Runtimes)
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
                    var extensions = await coreClient.ValidatePhpAsync(phpPath);
                    PhpExtensionStatusText.Text = extensions.Compatible
                        ? "Laravel compatible"
                        : "Missing extensions";
                    PhpExtensionDetailText.Text = extensions.Compatible
                        ? $"{extensions.Required.Count} required extensions loaded"
                        : string.Join(", ", extensions.Missing);
                }
                catch (Exception error)
                {
                    PhpExtensionStatusText.Text = "Check failed";
                    PhpExtensionDetailText.Text = error.Message;
                }
            }
            else
            {
                PhpExtensionStatusText.Text = "Managed PHP unavailable";
                PhpExtensionDetailText.Text = $"Install HerdMe PHP {phpSettings.PhpCycle} from the PHP page.";
            }
        }
        catch (Exception error)
        {
            CoreStatusText.Text = error.Message;
            PhpExtensionStatusText.Text = "Unavailable";
            PhpExtensionDetailText.Text = string.Empty;
        }
        finally
        {
            CoreProgress.IsActive = false;
            PhpExtensionProgress.IsActive = false;
        }
        await RefreshLocalSetupAsync();
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
                    "Add or link at least one site before setting up local domains."
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
            DomainsStatusText.Text = domainsConfigured ? "Configured" : "Not configured";
            RemoveDomainsButton.IsEnabled = domainsConfigured;
            InstallDomainsButton.Content = domainsConfigured ? "Update" : "Set Up";

            var certificateTrusted = certificateManager.IsAuthorityTrusted();
            CertificateStatusText.Text = certificateTrusted ? "Trusted" : "Not trusted";
            TrustCertificateButton.Visibility = certificateTrusted
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        catch (Exception error)
        {
            DomainsStatusText.Text = error.Message;
            CertificateStatusText.Text = "Unavailable";
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
            UpdateStatusText.Text = $"{settings.UpdateChannel} channel";
            return true;
        }
        catch (Exception error)
        {
            _ = ShowMessageAsync("Settings could not be saved", error.Message);
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
            UpdateStatusText.Text = $"Checking {channel.ToLowerInvariant()} releases";
            var result = await updateManager.CheckAsync(channel);
            if (result.AvailableRelease is { } release)
            {
                UpdateStatusText.Text = $"Version {release.Version} is available";
                await ShowUpdateAsync(release);
            }
            else
            {
                UpdateStatusText.Text = $"HerdMe {result.CurrentVersion} is up to date";
                if (userInitiated)
                {
                    await ShowMessageAsync(
                        "HerdMe is up to date",
                        $"Version {result.CurrentVersion} is the newest {channel.ToLowerInvariant()} release."
                    );
                }
            }
        }
        catch (Exception error)
        {
            UpdateStatusText.Text = "Update check failed";
            if (userInitiated)
            {
                await ShowMessageAsync("Update check failed", error.Message);
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
            Title = $"HerdMe {release.Version} is available",
            Content = release.Notes,
            CloseButtonText = downloadAvailable ? "Later" : "OK"
        };
        if (downloadAvailable)
        {
            dialog.PrimaryButtonText = "Download";
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
            CloseButtonText = "OK"
        };
        await dialog.ShowAsync();
    }
}
