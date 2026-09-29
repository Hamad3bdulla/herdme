using System.Collections.ObjectModel;
using System.Diagnostics;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

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
    private readonly ManagedComponentUpdateManager componentUpdateManager;
    private readonly WindowsUserPathManager userPathManager;
    private readonly WindowsShellIntegration shellIntegration;
    private bool loadingStartup;
    private bool loadingShellIntegration;
    private bool loadingUpdateSettings;
    private bool loadingCompactMode;
    private bool loadingNotifications;
    private bool exportingDiagnostics;
    private bool updateEventSubscribed;
    private bool pageActive;

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
        AppUpdateManager updateManager,
        ManagedComponentUpdateManager componentUpdateManager,
        WindowsUserPathManager userPathManager,
        WindowsShellIntegration shellIntegration
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
        this.componentUpdateManager = componentUpdateManager;
        this.userPathManager = userPathManager;
        this.shellIntegration = shellIntegration;
        InitializeComponent();
        CoreExecutableText.Text = coreClient.ExecutablePath;
        ToolTipService.SetToolTip(
            OpenDataButton,
            AppLocalization.Get("GeneralOpenApplicationData")
        );
        loadingStartup = true;
        StartupToggle.IsOn = startupManager.IsEnabled;
        loadingStartup = false;
        LoadShellIntegration();
        var settings = settingsStore.Load();
        LoadDefenderExclusion(settings);
        loadingCompactMode = true;
        CompactModeToggle.IsOn = settings.CompactMode;
        loadingCompactMode = false;
        loadingNotifications = true;
        NotificationsToggle.IsOn = settings.ShowNotifications;
        ActionNotificationsToggle.IsOn = settings.ActionNotifications;
        ActionNotificationsToggle.IsEnabled = settings.ShowNotifications;
        MailNotificationsToggle.IsOn = settings.MailNotifications;
        MailNotificationsToggle.IsEnabled = settings.ShowNotifications;
        loadingNotifications = false;
        LoadAppearance(settings);
        TldTextBox.Text = settings.Tld;
        loadingUpdateSettings = true;
        AutomaticUpdatesToggle.IsOn = settings.AutomaticUpdates;
        UpdateChannelBox.SelectedIndex = settings.UpdateChannel == "Beta" ? 1 : 0;
        UpdateStatusText.Text = AppLocalization.Format(
            "GeneralChannelStatus",
            UpdateChannelDisplayName(settings.UpdateChannel)
        );
        CurrentVersionText.Text = AppLocalization.Format(
            "GeneralCurrentVersion",
            updateManager.CurrentVersion,
            updateManager.CurrentBuild
        );
        loadingUpdateSettings = false;
        PopulateLocalSnapshot();
    }

    private void CompactModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (loadingCompactMode) return;
        settingsStore.UpdateCompactMode(CompactModeToggle.IsOn);
        App.MainWindow.ApplyCompactPreference(CompactModeToggle.IsOn);
        App.MainWindow.ShowToast(AppLocalization.Get("CommonSavedToast"));
    }

    private void NotificationsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (loadingNotifications) return;
        settingsStore.UpdateShowNotifications(NotificationsToggle.IsOn);
        ActionNotificationsToggle.IsEnabled = NotificationsToggle.IsOn;
        MailNotificationsToggle.IsEnabled = NotificationsToggle.IsOn;
    }

    private void MailNotificationsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (loadingNotifications) return;
        settingsStore.UpdateMailNotifications(MailNotificationsToggle.IsOn);
    }

    // Registers or removes HerdMe's Windows notification registration right away; a refusal
    // puts the switch back and says why.
    private async void ActionNotificationsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (loadingNotifications) return;
        var wanted = ActionNotificationsToggle.IsOn;
        if (((App)Application.Current).SetActionNotifications(wanted, out var problem)) return;
        loadingNotifications = true;
        ActionNotificationsToggle.IsOn = !wanted;
        loadingNotifications = false;
        if (XamlRoot is null) return;
        await Views.ErrorDialog.ShowAsync(XamlRoot, AppLocalization.Get("GeneralActionNotificationsFailed"), problem);
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (exportingDiagnostics) return;
        exportingDiagnostics = true;
        ExportDiagnosticsButton.IsEnabled = false;
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = Path.GetFileNameWithoutExtension(DiagnosticsExporter.DefaultFileName(DateTimeOffset.Now))
            };
            picker.FileTypeChoices.Add(AppLocalization.Get("GeneralDiagnosticsZipType"), [".zip"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            var includeDumps = IncludeCrashDumpsCheckBox.IsChecked == true;
            var exporter = new DiagnosticsExporter(settingsStore.SupportRoot, updateManager.CurrentVersion);
            var result = await Task.Run(() => exporter.ExportAsync(file.Path, includeDumps));
            DiagnosticsStatusText.Text = AppLocalization.Format("GeneralDiagnosticsSaved", result.Files, result.Path);
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            DiagnosticsStatusText.Text = AppLocalization.Format("GeneralDiagnosticsFailed", error.Message);
        }
        finally
        {
            exportingDiagnostics = false;
            ExportDiagnosticsButton.IsEnabled = true;
        }
    }

    private void LoadShellIntegration()
    {
        loadingShellIntegration = true;
        ExplorerLinkToggle.IsOn = shellIntegration.IsEnabled(ShellIntegrationFeature.ExplorerLink);
        UriProtocolToggle.IsOn = shellIntegration.IsEnabled(ShellIntegrationFeature.UriProtocol);
        TerminalProfileToggle.IsOn = shellIntegration.IsEnabled(ShellIntegrationFeature.TerminalProfile);
        loadingShellIntegration = false;
    }

    private async void ShellIntegrationToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (loadingShellIntegration || sender is not ToggleSwitch toggle) return;
        var feature = toggle == ExplorerLinkToggle
            ? ShellIntegrationFeature.ExplorerLink
            : toggle == UriProtocolToggle
                ? ShellIntegrationFeature.UriProtocol
                : ShellIntegrationFeature.TerminalProfile;
        var enabled = toggle.IsOn;
        try
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("HerdMe could not determine its executable path.");
            var startingDirectory = settingsStore.Load().Roots.FirstOrDefault(Directory.Exists);
            await Task.Run(() => shellIntegration.SetEnabled(feature, enabled, executable, startingDirectory));
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or InvalidOperationException)
        {
            LoadShellIntegration();
            var dialog = new ContentDialog
            {
                FlowDirection = AppLocalization.LayoutDirection,
                XamlRoot = XamlRoot,
                Title = AppLocalization.Get("GeneralWindowsIntegrationFailedTitle"),
                Content = error.Message,
                CloseButtonText = AppLocalization.Get("CommonOk")
            };
            await dialog.ShowAsync();
        }
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
                FlowDirection = AppLocalization.LayoutDirection,
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
        pageActive = true;
        UpdateProgress.IsActive = false;
        CheckNowButton.IsEnabled = true;
        if (!updateEventSubscribed)
        {
            updateManager.CheckCompleted += UpdateManager_CheckCompleted;
            updateEventSubscribed = true;
        }
        RenderLatestUpdateResult();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => App.MainWindow.OfferTip(LanguageTip, MainWindow.LanguageTipId, automatic: false));
        await RefreshAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        pageActive = false;
        if (!updateEventSubscribed) return;
        updateManager.CheckCompleted -= UpdateManager_CheckCompleted;
        updateEventSubscribed = false;
    }

    private void PopulateLocalSnapshot()
    {
        CoreStatusText.Text = File.Exists(coreClient.ExecutablePath)
            ? AppLocalization.Get("GeneralReady")
            : AppLocalization.Get("GeneralUnavailable");
        SupportPathText.Text = settingsStore.SupportRoot;
        OpenDataButton.IsEnabled = Directory.Exists(settingsStore.SupportRoot);
        Runtimes.Clear();
        foreach (var runtime in ManagedRuntimeChecks()) Runtimes.Add(runtime);
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
        try
        {
            var report = await coreClient.DoctorAsync();
            CoreStatusText.Text = report.Platform == "windows"
                ? AppLocalization.Get("GeneralReady")
                : report.Platform;
            SupportPathText.Text = report.SupportPath;
            Directory.CreateDirectory(report.SupportPath);
            OpenDataButton.IsEnabled = true;
            Runtimes.Clear();
            foreach (var runtime in ManagedRuntimeChecks()) Runtimes.Add(runtime);
            var phpSettings = runtimePolicy.Load();
            var phpPath = runtimeInstaller.IsInstalled(phpSettings.PhpCycle)
                ? runtimeInstaller.PhpExecutable(phpSettings.PhpCycle)
                : null;
            if (phpPath is not null)
            {
                try
                {
                    await runtimeInstaller.EnsureManagedConfigurationAsync(phpSettings.PhpCycle);
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
        ApplyManagedUpdateState();
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

    private void TldTextBox_LostFocus(object sender, RoutedEventArgs e) => CommitDomainSuffix();

    private void TldTextBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != global::Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        CommitDomainSuffix();
    }

    // A bad value keeps the current suffix (instead of silently falling back to "test"); a new
    // one rescans the sites and moves the running web server over right away.
    private async void CommitDomainSuffix()
    {
        var previous = settingsStore.Load().Tld;
        var typed = SiteConfigurationStore.NormalizeTld(TldTextBox.Text);
        if (typed == previous)
        {
            TldTextBox.Text = previous;
            return;
        }
        if (!SiteConfigurationStore.IsValidTld(typed))
        {
            TldTextBox.Text = previous;
            App.MainWindow.ShowToast(AppLocalization.Get("GeneralDomainSuffixInvalid"));
            return;
        }
        settingsStore.UpdateTld(typed);
        var saved = settingsStore.Load().Tld;
        TldTextBox.Text = saved;
        if (saved == previous) return;
        ((App)Application.Current).ApplyDomainSuffixChange();
        var hostsConfigured = false;
        try
        {
            hostsConfigured = await hostsManager.HasManagedMappingsAsync();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        App.MainWindow.ShowToast(AppLocalization.Format(
            hostsConfigured ? "GeneralDomainSuffixChangedHosts" : "GeneralDomainSuffixChanged",
            saved
        ));
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

    private void AutomaticUpdatesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (loadingUpdateSettings) return;
        SaveUpdateSettings();
    }

    private void UpdateChannelBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e
    )
    {
        if (loadingUpdateSettings) return;
        SaveUpdateSettings();
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(userInitiated: true);
    }

    private bool SaveUpdateSettings()
    {
        try
        {
            var updateChannel = SelectedUpdateChannel();
            settingsStore.UpdateUpdatePreferences(AutomaticUpdatesToggle.IsOn, updateChannel);
            UpdateStatusText.Text = AppLocalization.Format(
                "GeneralUpdateReadyToCheck",
                UpdateChannelDisplayName(updateChannel),
                updateManager.CurrentVersion
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
        AppUpdateCheck? applicationResult = null;
        Exception? applicationError = null;
        try
        {
            var channel = SelectedUpdateChannel();
            var channelName = UpdateChannelDisplayName(channel);
            UpdateStatusText.Text = AppLocalization.Format(
                "GeneralCheckingReleases",
                channelName
            );
            var applicationTask = updateManager.CheckAsync(channel);
            var componentsTask = componentUpdateManager.CheckAsync();
            try
            {
                applicationResult = await applicationTask;
            }
            catch (Exception error)
            {
                applicationError = error;
            }
            var components = await componentsTask;
            if (!pageActive) return;
            ApplyManagedUpdateState();

            if (applicationResult?.AvailableRelease is { } release)
            {
                UpdateStatusText.Text = AppLocalization.Format(
                    "GeneralVersionAvailable",
                    release.Version
                );
                await AppUpdatePrompt.ShowAsync(XamlRoot, release);
            }
            else if (applicationResult?.UsedBundledFallback == true || applicationError is not null)
            {
                UpdateStatusText.Text = AppLocalization.Get("UpdateServiceUnavailableStatus");
            }
            else
            {
                UpdateStatusText.Text = AppLocalization.Format(
                    "GeneralUpToDateStatus",
                    applicationResult?.CurrentVersion ?? string.Empty
                );
            }

            if (components.Updates.Count > 0)
            {
                var pageTag = await ManagedComponentUpdatePrompt.ShowAsync(XamlRoot, components);
                if (pageTag is not null) App.MainWindow.NavigateToPage(pageTag);
            }
            else if (userInitiated)
            {
                if (applicationError is not null)
                {
                    await ShowMessageAsync(
                        AppLocalization.Get("GeneralUpdateCheckFailed"),
                        applicationError is OperationCanceledException
                            ? AppLocalization.Get("GeneralUpdateCheckTimedOut")
                            : applicationError.Message
                    );
                }
                else if (applicationResult?.UsedBundledFallback == true)
                {
                    await ShowMessageAsync(
                        AppLocalization.Get("UpdateServiceUnavailableTitle"),
                        AppLocalization.Get("UpdateServiceUnavailableMessage")
                    );
                }
                else if (components.Failures.Count > 0)
                {
                    await ShowMessageAsync(
                        AppLocalization.Get("UpdateServiceUnavailableTitle"),
                        AppLocalization.Format(
                            "ManagedUpdatesPartialFailure",
                            string.Join(", ", components.Failures.Select(failure => failure.Component))
                        )
                    );
                }
                else if (applicationResult?.AvailableRelease is null)
                {
                    await ShowMessageAsync(
                        AppLocalization.Get("GeneralUpToDateTitle"),
                        AppLocalization.Get("ManagedUpdatesUpToDate")
                    );
                }
            }
        }
        catch (Exception error)
        {
            if (!pageActive) return;
            UpdateStatusText.Text = AppLocalization.Get("GeneralUpdateCheckFailed");
            if (userInitiated)
            {
                await ShowMessageAsync(
                    AppLocalization.Get("GeneralUpdateCheckFailed"),
                    error is OperationCanceledException
                        ? AppLocalization.Get("GeneralUpdateCheckTimedOut")
                        : error.Message
                );
            }
        }
        finally
        {
            if (pageActive)
            {
                UpdateProgress.IsActive = false;
                CheckNowButton.IsEnabled = true;
            }
        }
    }

    private void UpdateManager_CheckCompleted(AppUpdateCheck result)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (pageActive) RenderUpdateResult(result);
        });
    }

    private void RenderLatestUpdateResult()
    {
        if (updateManager.LatestResult is { } result)
        {
            RenderUpdateResult(result);
            return;
        }
        UpdateStatusText.Text = AppLocalization.Format(
            "GeneralUpdateReadyToCheck",
            UpdateChannelDisplayName(SelectedUpdateChannel()),
            updateManager.CurrentVersion
        );
    }

    private void RenderUpdateResult(AppUpdateCheck result)
    {
        UpdateStatusText.Text = result.AvailableRelease is { } release
            ? AppLocalization.Format("GeneralVersionAvailable", release.Version)
            : result.UsedBundledFallback
                ? AppLocalization.Get("UpdateServiceUnavailableStatus")
                : AppLocalization.Format("GeneralLastBackgroundCheck", result.CurrentVersion);
        ApplyManagedUpdateState();
    }

    private void ApplyManagedUpdateState()
    {
        var gitUpdate = componentUpdateManager.LatestUpdate("git");
        UpdateGitButton.Visibility = gitUpdate is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (gitUpdate is not null)
        {
            UpdateGitButtonText.Text = AppLocalization.Format(
                "GeneralUpdateGit",
                gitUpdate.LatestVersion
            );
        }
    }

    private async void UpdateGit_Click(object sender, RoutedEventArgs e)
    {
        UpdateGitButton.IsEnabled = false;
        UpdateProgress.IsActive = true;
        UpdateStatusText.Text = AppLocalization.Get("GeneralUpdatingGit");
        try
        {
            await gitInstaller.InstallOrUpdateAsync();
            userPathManager.Synchronize(
                composerTools.CommandLineDirectories(runtimePolicy.Load().PhpCycle)
            );
            var version = gitInstaller.InstalledVersion() ?? string.Empty;
            UpdateStatusText.Text = AppLocalization.Format("GeneralGitUpdated", version);
            UpdateGitButton.Visibility = Visibility.Collapsed;
            await componentUpdateManager.CheckAsync();
            await RefreshAsync();
        }
        catch (Exception error)
        {
            await ShowMessageAsync("HerdMe", error.Message);
        }
        finally
        {
            UpdateProgress.IsActive = false;
            UpdateGitButton.IsEnabled = true;
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
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
