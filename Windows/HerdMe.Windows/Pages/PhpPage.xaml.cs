using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

public sealed partial class PhpPage : Page
{
    private readonly CoreClient coreClient;
    private readonly PhpRuntimePolicy runtimePolicy;
    private readonly PhpRuntimeInstaller runtimeInstaller;
    private readonly ComposerToolManager toolManager;
    private readonly WindowsUserPathManager userPathManager;
    private readonly PhpExtensionManager extensionManager;
    private PhpRuntimeSettings settings;
    private IReadOnlyList<PhpExtensionState> extensions = [];
    private bool loadingExtensions;
    private bool loaded;
    private int refreshGeneration;

    public PhpPage(
        CoreClient coreClient,
        PhpRuntimePolicy runtimePolicy,
        PhpRuntimeInstaller runtimeInstaller,
        ComposerToolManager toolManager,
        WindowsUserPathManager userPathManager,
        PhpExtensionManager extensionManager
    )
    {
        this.coreClient = coreClient;
        this.runtimePolicy = runtimePolicy;
        this.runtimeInstaller = runtimeInstaller;
        this.toolManager = toolManager;
        this.userPathManager = userPathManager;
        this.extensionManager = extensionManager;
        InitializeComponent();
        settings = runtimePolicy.Load();
        var availableCycles = PhpRuntimeInstaller.SupportedCycles
            .Concat(runtimeInstaller.InstalledCycles().Where(cycle =>
                !PhpRuntimeInstaller.IsSupportedCycle(cycle)
            ))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var cycle in availableCycles) PhpCycleBox.Items.Add(cycle);
        PhpCycleBox.SelectedItem = availableCycles.Contains(settings.PhpCycle, StringComparer.Ordinal)
            ? settings.PhpCycle
            : RuntimeCatalog.DefaultPhpCycle;
        LoadVersionSettings(PhpCycleBox.SelectedItem?.ToString() ?? settings.PhpCycle);
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        loaded = true;
        await RefreshAsync();
    }

    private async void PhpCycle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loaded) return;
        LoadVersionSettings(PhpCycleBox.SelectedItem?.ToString() ?? settings.PhpCycle);
        await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var generation = Interlocked.Increment(ref refreshGeneration);
        RuntimeProgress.IsActive = true;
        RuntimeStatusText.Text = AppLocalization.Get("PhpCheckingStatus");
        ExtensionDetailText.Text = string.Empty;
        InstallPhpButton.Visibility = Visibility.Collapsed;
        InstallToolsButton.Visibility = Visibility.Visible;
        BackgroundUpdateStatusText.Text = string.Empty;
        ExtensionsPanel.Children.Clear();
        ExtensionsProgress.IsActive = true;
        try
        {
            var selectedCycle = PhpCycleBox.SelectedItem?.ToString() ?? settings.PhpCycle;
            var managedPhp = runtimeInstaller.PhpExecutable(selectedCycle);
            var isInstalled = runtimeInstaller.IsInstalled(selectedCycle);
            string? phpPath = isInstalled && File.Exists(managedPhp) ? managedPhp : null;
            var installedVersion = runtimeInstaller.InstalledVersion(selectedCycle);
            PhpInstalledVersionText.Text = installedVersion is null
                ? AppLocalization.Get("PhpVersionNotInstalled")
                : AppLocalization.Format("PhpInstalledVersion", installedVersion);
            InstallPhpButtonText.Text = AppLocalization.Get("CommonInstall");
            InstallPhpButton.Visibility = isInstalled ? Visibility.Collapsed : Visibility.Visible;
            if (phpPath is null)
            {
                PhpPathText.Text = AppLocalization.Get("PhpNoUsableExecutable");
                RuntimeStatusText.Text = AppLocalization.Get("PhpUnavailable");
                ComposerVersionText.Text = AppLocalization.Format(
                    "PhpComposerVersion",
                    AppLocalization.Get("CommonNotInstalled")
                );
                LaravelInstallerVersionText.Text = AppLocalization.Format(
                    "PhpLaravelInstallerVersion",
                    AppLocalization.Get("CommonNotInstalled")
                );
                InstallToolsButton.IsEnabled = false;
                extensions = [];
                RenderExtensions();
                return;
            }

            PhpPathText.Text = phpPath;
            RuntimeStatusText.Text = AppLocalization.Get("PhpInspectingLocalRuntime");
            await Task.Yield();
            extensions = await extensionManager.InspectAsync(selectedCycle);
            RenderExtensions();
            var missingRequired = extensions
                .Where(extension => extension.Required && !extension.Loaded)
                .Select(extension => extension.Name)
                .ToArray();
            RuntimeStatusText.Text = missingRequired.Length == 0
                ? PhpRuntimeInstaller.IsSupportedCycle(selectedCycle)
                    ? AppLocalization.Get("PhpReadyForLaravel")
                    : AppLocalization.Get("PhpLegacyRuntime")
                : AppLocalization.Get("PhpBlocked");
            ExtensionDetailText.Text = missingRequired.Length == 0
                ? string.Join(", ", extensions.Where(extension => extension.Required)
                    .Select(extension => extension.Name))
                : AppLocalization.Format("PhpMissingRequiredExtensions", string.Join(", ", missingRequired));
            var toolVersions = await toolManager.InstalledVersionsAsync(selectedCycle);
            ComposerVersionText.Text = AppLocalization.Format(
                "PhpComposerVersion",
                toolVersions.Composer is null
                    ? AppLocalization.Get("CommonNotInstalled")
                    : $"v{toolVersions.Composer}"
            );
            LaravelInstallerVersionText.Text = AppLocalization.Format(
                "PhpLaravelInstallerVersion",
                toolVersions.Laravel is null
                    ? AppLocalization.Get("CommonNotInstalled")
                    : $"v{toolVersions.Laravel}"
            );
            InstallToolsButtonText.Text = toolVersions.Composer is null || toolVersions.Laravel is null
                ? AppLocalization.Get("CommonInstall")
                : AppLocalization.Get("CommonUpdate");
            var updateAvailable = toolVersions.Composer is null || toolVersions.Laravel is null;
            InstallToolsButton.Visibility = updateAvailable ? Visibility.Visible : Visibility.Collapsed;
            InstallToolsButton.IsEnabled = updateAvailable;
            _ = CheckAvailableUpdatesAsync(
                selectedCycle,
                installedVersion,
                toolVersions,
                generation
            );
        }
        catch (Exception error)
        {
            RuntimeStatusText.Text = AppLocalization.Get("PhpBlocked");
            ExtensionDetailText.Text = error.Message;
            var selectedCycle = PhpCycleBox.SelectedItem?.ToString() ?? settings.PhpCycle;
            if (runtimeInstaller.IsInstalled(selectedCycle)
                && PhpRuntimeInstaller.IsSupportedCycle(selectedCycle))
            {
                InstallPhpButtonText.Text = AppLocalization.Get("PhpRepair");
                InstallPhpButton.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            RuntimeProgress.IsActive = false;
            ExtensionsProgress.IsActive = false;
        }
    }

    private async Task CheckAvailableUpdatesAsync(
        string cycle,
        string? installedVersion,
        (string? Composer, string? Laravel) toolVersions,
        int generation
    )
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        BackgroundUpdateStatusText.Text = AppLocalization.Get("PhpCheckingUpdatesInBackground");
        try
        {
            var phpReleaseTask = runtimeInstaller.ResolveReleaseAsync(cycle, timeout.Token);
            var composerReleaseTask = toolManager.ResolveComposerReleaseAsync(timeout.Token);
            var laravelReleaseTask = toolManager.LatestLaravelInstallerVersionAsync(timeout.Token);
            await Task.WhenAll(phpReleaseTask, composerReleaseTask, laravelReleaseTask);
            if (generation != refreshGeneration) return;

            var phpRelease = await phpReleaseTask;
            var phpUpdate = RuntimeVersionComparison.IsNewer(
                phpRelease.Version,
                installedVersion ?? string.Empty
            );
            InstallPhpButtonText.Text = phpUpdate
                ? AppLocalization.Get("CommonUpdate")
                : AppLocalization.Get("CommonInstall");
            InstallPhpButton.Visibility = phpUpdate ? Visibility.Visible : Visibility.Collapsed;

            var composerRelease = await composerReleaseTask;
            var laravelRelease = await laravelReleaseTask;
            var toolsUpdate = toolVersions.Composer is null || toolVersions.Laravel is null
                || RuntimeVersionComparison.IsNewer(composerRelease.Version, toolVersions.Composer)
                || RuntimeVersionComparison.IsNewer(laravelRelease, toolVersions.Laravel);
            InstallToolsButtonText.Text = toolVersions.Composer is null || toolVersions.Laravel is null
                ? AppLocalization.Get("CommonInstall")
                : AppLocalization.Get("CommonUpdate");
            InstallToolsButton.Visibility = toolsUpdate ? Visibility.Visible : Visibility.Collapsed;
            InstallToolsButton.IsEnabled = toolsUpdate;
            BackgroundUpdateStatusText.Text = phpUpdate || toolsUpdate
                ? AppLocalization.Get("PhpUpdatesAvailable")
                : AppLocalization.Get("PhpUpdatesCheckedInBackground");
        }
        catch (Exception)
        {
            if (generation == refreshGeneration)
            {
                BackgroundUpdateStatusText.Text = AppLocalization.Get("PhpBackgroundCheckUnavailable");
            }
        }
    }

    private async void InstallTools_Click(object sender, RoutedEventArgs e)
    {
        var cycle = PhpCycleBox.SelectedItem?.ToString() ?? settings.PhpCycle;
        InstallToolsButton.IsEnabled = false;
        RuntimeProgress.IsActive = true;
        RuntimeStatusText.Text = AppLocalization.Get("PhpInstallingTools");
        try
        {
            var versions = await toolManager.InstallOrUpdateAsync(cycle);
            SynchronizeUserPath(cycle);
            ComposerVersionText.Text = AppLocalization.Format(
                "PhpComposerVersion",
                $"v{versions.Composer}"
            );
            LaravelInstallerVersionText.Text = AppLocalization.Format(
                "PhpLaravelInstallerVersion",
                $"v{versions.Laravel}"
            );
            InstallToolsButton.Visibility = Visibility.Collapsed;
            RuntimeStatusText.Text = AppLocalization.Get("PhpReadyForLaravel13");
        }
        catch (Exception error)
        {
            RuntimeStatusText.Text = AppLocalization.Get("PhpToolInstallationFailed");
            ExtensionDetailText.Text = error.Message;
        }
        finally
        {
            RuntimeProgress.IsActive = false;
            InstallToolsButton.IsEnabled = runtimeInstaller.IsInstalled(cycle);
        }
    }

    private async void InstallPhp_Click(object sender, RoutedEventArgs e)
    {
        var cycle = PhpCycleBox.SelectedItem?.ToString() ?? RuntimeCatalog.DefaultPhpCycle;
        InstallPhpButton.IsEnabled = false;
        RuntimeProgress.IsActive = true;
        RuntimeStatusText.Text = AppLocalization.Get("PhpInstalling");
        try
        {
            var release = await runtimeInstaller.InstallAsync(cycle);
            settings.PhpCycle = cycle;
            runtimePolicy.Save(settings);
            SynchronizeUserPath(cycle);
            RuntimeStatusText.Text = AppLocalization.Format(
                "PhpVersionInstalled",
                release.Version
            );
            await RefreshAsync();
        }
        catch (Exception error)
        {
            RuntimeStatusText.Text = AppLocalization.Get("PhpInstallFailed");
            ExtensionDetailText.Text = error.Message;
        }
        finally
        {
            RuntimeProgress.IsActive = false;
            InstallPhpButton.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var cycle = PhpCycleBox.SelectedItem?.ToString() ?? settings.PhpCycle;
        var version = PhpRuntimePolicy.ResolveVersion(settings, cycle);
        version.MemoryLimitMegabytes = double.IsNaN(MemoryLimitBox.Value)
            ? 512
            : (int)MemoryLimitBox.Value;
        version.MaxUploadMegabytes = double.IsNaN(UploadLimitBox.Value)
            ? 100
            : (int)UploadLimitBox.Value;
        version.MaxExecutionTimeSeconds = NumberValue(ExecutionTimeBox, 120);
        version.MaxInputTimeSeconds = NumberValue(InputTimeBox, 60);
        version.MaxInputVariables = NumberValue(InputVariablesBox, 1_000);
        version.MaxFileUploads = NumberValue(FileUploadsBox, 20);
        version.DisplayErrors = DisplayErrorsToggle.IsOn;
        version.OpcacheEnabled = OpcacheToggle.IsOn;
        version.Timezone = TimezoneBox.Text;
        PhpRuntimePolicy.SetVersion(settings, cycle, version);
        settings.PhpCycle = cycle;
        runtimePolicy.Save(settings);
        SynchronizeUserPath(settings.PhpCycle);
        settings = runtimePolicy.Load();
        LoadVersionSettings(cycle);
        SaveStatusText.Text = AppLocalization.Get("PhpSavedForNextStart");
    }

    private void LoadVersionSettings(string cycle)
    {
        settings = runtimePolicy.Load();
        var version = PhpRuntimePolicy.ResolveVersion(settings, cycle);
        MemoryLimitBox.Value = version.MemoryLimitMegabytes;
        UploadLimitBox.Value = version.MaxUploadMegabytes;
        ExecutionTimeBox.Value = version.MaxExecutionTimeSeconds;
        InputTimeBox.Value = version.MaxInputTimeSeconds;
        InputVariablesBox.Value = version.MaxInputVariables;
        FileUploadsBox.Value = version.MaxFileUploads;
        DisplayErrorsToggle.IsOn = version.DisplayErrors;
        OpcacheToggle.IsOn = version.OpcacheEnabled;
        TimezoneBox.Text = version.Timezone;
        SaveStatusText.Text = string.Empty;
    }

    private static int NumberValue(NumberBox box, int fallback) =>
        double.IsNaN(box.Value) ? fallback : (int)box.Value;

    private void ExtensionSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        RenderExtensions();
    }

    private void RenderExtensions()
    {
        ExtensionsPanel.Children.Clear();
        var query = ExtensionSearchBox.Text.Trim();
        foreach (var extension in extensions.Where(item =>
            query.Length == 0 || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            var row = new Grid { Padding = new Thickness(8, 7, 8, 7), ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var details = new StackPanel { Spacing = 2 };
            details.Children.Add(new TextBlock { Text = extension.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            details.Children.Add(new TextBlock
            {
                Text = ExtensionStatus(extension),
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            row.Children.Add(details);
            var toggle = new ToggleSwitch
            {
                Tag = extension.Name,
                IsOn = extension.Enabled,
                IsEnabled = extension.CanToggle,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(toggle, extension.CanToggle
                ? AppLocalization.Get("PhpExtensionToggleTooltip")
                : AppLocalization.Get("PhpExtensionBuiltInTooltip"));
            toggle.Toggled += ExtensionToggle_Toggled;
            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);
            ExtensionsPanel.Children.Add(row);
        }
    }

    private static string ExtensionStatus(PhpExtensionState extension)
    {
        var state = AppLocalization.Get(extension.Loaded
            ? "PhpExtensionLoaded" : extension.Enabled
                ? "PhpExtensionEnabled" : "PhpExtensionDisabled");
        return extension.Required
            ? AppLocalization.Format("PhpExtensionRequiredStatus", state)
            : state;
    }

    private async void ExtensionToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (loadingExtensions || sender is not ToggleSwitch toggle || toggle.Tag is not string name)
        {
            return;
        }
        var cycle = PhpCycleBox.SelectedItem?.ToString() ?? settings.PhpCycle;
        try
        {
            loadingExtensions = true;
            extensionManager.SetEnabled(cycle, name, toggle.IsOn);
            var stored = runtimePolicy.Load();
            var version = PhpRuntimePolicy.ResolveVersion(stored, cycle);
            PhpRuntimePolicy.SetVersion(stored, cycle, version);
            stored.Versions[cycle].Extensions[name] = toggle.IsOn;
            runtimePolicy.Save(stored);
            await RefreshAsync();
        }
        catch (Exception error)
        {
            SaveStatusText.Text = error.Message;
            toggle.IsOn = !toggle.IsOn;
        }
        finally
        {
            loadingExtensions = false;
        }
    }

    private void SynchronizeUserPath(string phpCycle)
    {
        userPathManager.Synchronize(toolManager.CommandLineDirectories(phpCycle));
    }
}
