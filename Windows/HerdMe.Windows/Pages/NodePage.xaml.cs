using System.Collections.ObjectModel;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

public sealed partial class NodePage : Page
{
    private static IReadOnlyList<string> SupportedMajors => RuntimeCatalog.WindowsNodeMajors;
    private readonly NodeRuntimeInstaller installer;
    private readonly ComposerToolManager toolManager;
    private readonly PhpRuntimePolicy runtimePolicy;
    private readonly WindowsUserPathManager userPathManager;
    private int refreshGeneration;

    public ObservableCollection<NodeRuntimeRow> Rows { get; } = [];

    public NodePage(
        NodeRuntimeInstaller installer,
        ComposerToolManager toolManager,
        PhpRuntimePolicy runtimePolicy,
        WindowsUserPathManager userPathManager
    )
    {
        this.installer = installer;
        this.toolManager = toolManager;
        this.runtimePolicy = runtimePolicy;
        this.userPathManager = userPathManager;
        InitializeComponent();
        RenderRows(new Dictionary<string, string>());
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshRowsAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshRowsAsync();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string major) return;
        SetWorking(true, AppLocalization.Format("NodeInstallingMajor", major));
        try
        {
            var release = await installer.InstallAsync(major);
            SynchronizeUserPath();
            OperationStatusText.Text = AppLocalization.Format(
                "NodeVersionInstalled",
                release.Version
            );
        }
        catch (Exception error)
        {
            await ShowErrorAsync(error.Message);
        }
        finally
        {
            SetWorking(false, string.Empty);
            await RefreshRowsAsync();
        }
    }

    private async void Use_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string major) return;
        try
        {
            var version = installer.InstalledVersion(major)
                ?? throw new InvalidOperationException(
                    AppLocalization.Format("NodeMajorNotInstalled", major)
                );
            installer.SetActive(version);
            SynchronizeUserPath();
            await RefreshRowsAsync();
        }
        catch (Exception error)
        {
            await ShowErrorAsync(error.Message);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string major) return;
        var version = installer.InstalledVersion(major);
        if (version is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("NodeDeleteVersionTitle", version),
            Content = AppLocalization.Get("NodeDeleteVersionMessage"),
            PrimaryButtonText = AppLocalization.Get("CommonDelete"),
            CloseButtonText = AppLocalization.Get("CommonCancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            installer.Remove(version);
            SynchronizeUserPath();
            await RefreshRowsAsync();
        }
        catch (Exception error)
        {
            await ShowErrorAsync(error.Message);
        }
    }

    private async Task RefreshRowsAsync()
    {
        var generation = Interlocked.Increment(ref refreshGeneration);
        RenderRows(new Dictionary<string, string>());
        OperationStatusText.Text = AppLocalization.Get("NodeCheckingUpdatesInBackground");
        await Task.Yield();
        _ = RefreshAvailableVersionsAsync(generation);
    }

    private async Task RefreshAvailableVersionsAsync(int generation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var latestVersions = await installer.ResolveLatestVersionsAsync(
                SupportedMajors,
                timeout.Token
            );
            if (generation != refreshGeneration) return;
            RenderRows(latestVersions);
            OperationStatusText.Text = AppLocalization.Get("NodeUpdatesCheckedInBackground");
        }
        catch (Exception)
        {
            if (generation == refreshGeneration)
            {
                OperationStatusText.Text = AppLocalization.Get("NodeBackgroundCheckUnavailable");
            }
        }
    }

    private void RenderRows(IReadOnlyDictionary<string, string> latestVersions)
    {
        var active = installer.LoadSettings().ActiveVersion;
        Rows.Clear();
        foreach (var major in SupportedMajors)
        {
            var installed = installer.InstalledVersion(major);
            latestVersions.TryGetValue(major, out var latest);
            Rows.Add(new NodeRuntimeRow
            {
                Major = major,
                InstalledVersion = installed,
                IsActive = installed is not null && installed == active,
                IsUpdateAvailable = installed is not null && latest is not null
                    && RuntimeVersionComparison.IsNewer(latest, installed),
                Status = installed is null
                    ? AppLocalization.Get("CommonNotInstalled")
                    : installed == active
                        ? AppLocalization.Get("NodeActive")
                        : installed
            });
        }
    }

    private void SetWorking(bool working, string status)
    {
        OperationProgress.IsActive = working;
        OperationProgress.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        OperationStatusText.Text = status;
        IsEnabled = !working;
    }

    private void SynchronizeUserPath()
    {
        userPathManager.Synchronize(
            toolManager.CommandLineDirectories(runtimePolicy.Load().PhpCycle)
        );
    }

    private async Task ShowErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "HerdMe",
            Content = message,
            CloseButtonText = AppLocalization.Get("CommonOk")
        };
        await dialog.ShowAsync();
    }
}
