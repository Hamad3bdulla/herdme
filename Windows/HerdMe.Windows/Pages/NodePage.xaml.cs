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
        var active = installer.LoadSettings().ActiveVersion;
        IReadOnlyDictionary<string, string> latestVersions;
        try
        {
            latestVersions = await installer.ResolveLatestVersionsAsync(SupportedMajors);
        }
        catch (Exception)
        {
            latestVersions = new Dictionary<string, string>();
        }
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
