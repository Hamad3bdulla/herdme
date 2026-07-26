using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

public sealed partial class PhpPage : Page
{
    private readonly CoreClient coreClient = new();
    private readonly PhpRuntimePolicy runtimePolicy;
    private readonly PhpRuntimeInstaller runtimeInstaller;
    private readonly ComposerToolManager toolManager = new();
    private PhpRuntimeSettings settings;
    private bool loaded;

    public PhpPage()
    {
        InitializeComponent();
        runtimePolicy = new PhpRuntimePolicy(coreClient);
        runtimeInstaller = new PhpRuntimeInstaller(coreClient);
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
            : "8.4";
        MemoryLimitBox.Value = settings.MemoryLimitMegabytes;
        UploadLimitBox.Value = settings.MaxUploadMegabytes;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        loaded = true;
        await RefreshAsync();
    }

    private async void PhpCycle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loaded) await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        RuntimeProgress.IsActive = true;
        RuntimeStatusText.Text = "Checking";
        ExtensionDetailText.Text = string.Empty;
        InstallPhpButton.Visibility = Visibility.Collapsed;
        InstallToolsButton.Visibility = Visibility.Visible;
        try
        {
            var selectedCycle = PhpCycleBox.SelectedItem?.ToString() ?? settings.PhpCycle;
            var managedPhp = runtimeInstaller.PhpExecutable(selectedCycle);
            var isInstalled = runtimeInstaller.IsInstalled(selectedCycle);
            string? phpPath = isInstalled && File.Exists(managedPhp) ? managedPhp : null;
            string? latestVersion = null;
            if (isInstalled)
            {
                try
                {
                    latestVersion = (await runtimeInstaller.ResolveReleaseAsync(selectedCycle)).Version;
                }
                catch (Exception)
                {
                }
            }
            var action = RuntimeVersionComparison.InstallAction(
                isInstalled,
                runtimeInstaller.InstalledVersion(selectedCycle),
                latestVersion
            );
            InstallPhpButtonText.Text = action.Label;
            InstallPhpButton.Visibility = action.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            if (phpPath is null)
            {
                PhpPathText.Text = "No usable PHP executable was found.";
                RuntimeStatusText.Text = "PHP unavailable";
                ComposerVersionText.Text = "Composer: Not installed";
                LaravelInstallerVersionText.Text = "Laravel Installer: Not installed";
                InstallToolsButton.IsEnabled = false;
                return;
            }

            PhpPathText.Text = phpPath;
            var contract = await runtimePolicy.PrepareLaunchAsync(phpPath);
            RuntimeStatusText.Text = PhpRuntimeInstaller.IsSupportedCycle(selectedCycle)
                ? "Ready for Laravel projects"
                : "Legacy PHP runtime";
            ExtensionDetailText.Text = string.Join(", ", contract.Extensions.Required);
            var toolVersions = await toolManager.InstalledVersionsAsync(selectedCycle);
            ComposerVersionText.Text = "Composer: " + (toolVersions.Composer is null ? "Not installed" : $"v{toolVersions.Composer}");
            LaravelInstallerVersionText.Text = "Laravel Installer: " + (toolVersions.Laravel is null ? "Not installed" : $"v{toolVersions.Laravel}");
            InstallToolsButtonText.Text = toolVersions.Laravel is null ? "Install" : "Update";
            var updateAvailable = toolVersions.Composer is null || toolVersions.Laravel is null;
            if (!updateAvailable)
            {
                try
                {
                    var composerReleaseTask = toolManager.ResolveComposerReleaseAsync();
                    var laravelReleaseTask = toolManager.LatestLaravelInstallerVersionAsync();
                    await Task.WhenAll(composerReleaseTask, laravelReleaseTask);
                    var composerRelease = await composerReleaseTask;
                    var laravelRelease = await laravelReleaseTask;
                    updateAvailable = RuntimeVersionComparison.IsNewer(
                        composerRelease.Version,
                        toolVersions.Composer!
                    ) || RuntimeVersionComparison.IsNewer(
                        laravelRelease,
                        toolVersions.Laravel!
                    );
                }
                catch (Exception)
                {
                    updateAvailable = false;
                }
            }
            InstallToolsButton.Visibility = updateAvailable ? Visibility.Visible : Visibility.Collapsed;
            InstallToolsButton.IsEnabled = updateAvailable;
        }
        catch (Exception error)
        {
            RuntimeStatusText.Text = "Blocked";
            ExtensionDetailText.Text = error.Message;
            var selectedCycle = PhpCycleBox.SelectedItem?.ToString() ?? settings.PhpCycle;
            if (runtimeInstaller.IsInstalled(selectedCycle)
                && PhpRuntimeInstaller.IsSupportedCycle(selectedCycle))
            {
                InstallPhpButtonText.Text = "Repair";
                InstallPhpButton.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            RuntimeProgress.IsActive = false;
        }
    }

    private async void InstallTools_Click(object sender, RoutedEventArgs e)
    {
        var cycle = PhpCycleBox.SelectedItem?.ToString() ?? settings.PhpCycle;
        InstallToolsButton.IsEnabled = false;
        RuntimeProgress.IsActive = true;
        RuntimeStatusText.Text = "Installing Composer and Laravel Installer";
        try
        {
            var versions = await toolManager.InstallOrUpdateAsync(cycle);
            ComposerVersionText.Text = $"Composer: v{versions.Composer}";
            LaravelInstallerVersionText.Text = $"Laravel Installer: v{versions.Laravel}";
            InstallToolsButton.Visibility = Visibility.Collapsed;
            RuntimeStatusText.Text = "Ready for Laravel 13";
        }
        catch (Exception error)
        {
            RuntimeStatusText.Text = "Tool installation failed";
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
        var cycle = PhpCycleBox.SelectedItem?.ToString() ?? "8.4";
        InstallPhpButton.IsEnabled = false;
        RuntimeProgress.IsActive = true;
        RuntimeStatusText.Text = "Installing";
        try
        {
            var release = await runtimeInstaller.InstallAsync(cycle);
            settings.PhpCycle = cycle;
            runtimePolicy.Save(settings);
            RuntimeStatusText.Text = $"PHP {release.Version} installed";
            await RefreshAsync();
        }
        catch (Exception error)
        {
            RuntimeStatusText.Text = "Install failed";
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
        settings.MemoryLimitMegabytes = double.IsNaN(MemoryLimitBox.Value)
            ? 512
            : (int)MemoryLimitBox.Value;
        settings.MaxUploadMegabytes = double.IsNaN(UploadLimitBox.Value)
            ? 100
            : (int)UploadLimitBox.Value;
        settings.PhpCycle = PhpCycleBox.SelectedItem?.ToString() ?? settings.PhpCycle;
        runtimePolicy.Save(settings);
        settings = runtimePolicy.Load();
        MemoryLimitBox.Value = settings.MemoryLimitMegabytes;
        UploadLimitBox.Value = settings.MaxUploadMegabytes;
        SaveStatusText.Text = "Saved for the next local PHP start.";
    }
}
