using System.Collections.ObjectModel;
using System.Diagnostics;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace HerdMe.Windows.Pages;

public sealed partial class ServicesPage : Page
{
    private readonly WindowsServiceManager manager = AppServices.Services;
    private readonly CoreClient coreClient = new();
    private readonly SiteConfigurationStore siteSettings = AppServices.SiteSettings;
    private bool refreshing;

    public IReadOnlyList<ManagedServiceDefinition> Definitions { get; } = ManagedServiceCatalog.All;

    public ObservableCollection<ManagedServiceRow> Rows { get; } = [];

    public ServicesPage()
    {
        InitializeComponent();
        ServiceTypeBox.SelectedIndex = 0;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e) => await RefreshRowsAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshRowsAsync();

    private void ServiceType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ServiceTypeBox.SelectedItem is not ManagedServiceDefinition definition) return;
        ServiceNameBox.Text = definition.Name;
        ServicePortBox.Value = WindowsServiceManager.AvailablePort(
            definition.DefaultPort,
            manager.LoadInstances().Select(instance => instance.Port)
        ) ?? definition.DefaultPort;
        AddServiceButton.IsEnabled = definition.IsInstallable;
        ServiceAvailabilityText.Text = definition.UnavailableReason ?? string.Empty;
        ServiceAvailabilityText.Visibility = definition.IsInstallable
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (ServiceTypeBox.SelectedItem is not ManagedServiceDefinition definition) return;
        if (!definition.IsInstallable)
        {
            await ShowErrorAsync(
                definition.UnavailableReason
                    ?? $"{definition.Name} is not available for native Windows installation."
            );
            return;
        }
        var port = double.IsNaN(ServicePortBox.Value) ? definition.DefaultPort : (int)ServicePortBox.Value;
        var instances = manager.LoadInstances().ToList();
        var assignedToHerdMe = instances.Any(instance => instance.Port == port);
        if (assignedToHerdMe || !WindowsServiceManager.IsPortAvailable(port))
        {
            var suggestion = WindowsServiceManager.AvailablePort(
                port == 65_535 ? 1_024 : port + 1,
                instances.Select(instance => instance.Port)
            );
            if (suggestion is not null) ServicePortBox.Value = suggestion.Value;
            var owner = assignedToHerdMe ? "another HerdMe service" : "another application";
            var nextStep = suggestion is null
                ? " No alternative loopback port is currently available."
                : $" Suggested port {suggestion.Value} is now selected; press Add again to use it.";
            await ShowErrorAsync($"Port {port} is already used by {owner}.{nextStep}");
            return;
        }
        var instance = new ManagedServiceInstance
        {
            DefinitionId = definition.Id,
            Name = string.IsNullOrWhiteSpace(ServiceNameBox.Text) ? definition.Name : ServiceNameBox.Text.Trim(),
            Port = port
        };
        instances.Add(instance);
        manager.SaveInstances(instances);
        await RefreshRowsAsync();
        if (manager.IsInstalled(definition.Id)) return;

        SetWorking(true, $"Installing {instance.Name}");
        try
        {
            var release = await manager.InstallAsync(instance.DefinitionId);
            OperationStatusText.Text = $"{instance.Name} {release.Version} installed";
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

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstance(sender, out var instance)) return;
        if (manager.State(instance.Id, instance.DefinitionId) == ManagedServiceState.Running)
        {
            await ShowErrorAsync("Stop this service before updating its runtime.");
            return;
        }
        SetWorking(true, $"Installing {instance.Name}");
        try
        {
            var release = await manager.InstallAsync(instance.DefinitionId);
            OperationStatusText.Text = $"{instance.Name} {release.Version} installed";
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

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstance(sender, out var instance)) return;
        var running = manager.State(instance.Id, instance.DefinitionId) == ManagedServiceState.Running;
        SetWorking(true, running ? $"Stopping {instance.Name}" : $"Starting {instance.Name}");
        try
        {
            if (running) await manager.StopAsync(instance.Id);
            else await manager.StartAsync(instance.Id);
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

    private void AutomaticStart_Toggled(object sender, RoutedEventArgs e)
    {
        if (refreshing || sender is not ToggleSwitch toggle || toggle.Tag is not Guid id) return;
        var instances = manager.LoadInstances().ToList();
        var instance = instances.FirstOrDefault(candidate => candidate.Id == id);
        if (instance is null || instance.StartAutomatically == toggle.IsOn) return;
        instance.StartAutomatically = toggle.IsOn;
        manager.SaveInstances(instances);
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstance(sender, out var instance)) return;
        var path = manager.DataDirectory(instance.Id);
        Directory.CreateDirectory(path);
        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        startInfo.ArgumentList.Add(path);
        Process.Start(startInfo);
    }

    private async void AddToEnvironment_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstance(sender, out var instance)) return;
        try
        {
            var settings = siteSettings.Load();
            var sites = await coreClient.ScanAsync(
                settings.Roots,
                settings.Tld,
                settings.LinkedSites
            );
            if (sites.Count == 0)
            {
                await ShowErrorAsync("Add or link a site before updating a .env file.");
                return;
            }

            var siteBox = new ComboBox
            {
                Header = "Site",
                ItemsSource = sites,
                DisplayMemberPath = nameof(SiteRecord.Name),
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var pathText = new TextBlock
            {
                Text = Path.Combine(sites[0].Path, ".env"),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7
            };
            siteBox.SelectionChanged += (_, _) =>
            {
                if (siteBox.SelectedItem is SiteRecord site)
                {
                    pathText.Text = Path.Combine(site.Path, ".env");
                }
            };
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(siteBox);
            content.Children.Add(pathText);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Add {instance.Name} to .env",
                Content = content,
                PrimaryButtonText = "Add to .env",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary
                || siteBox.SelectedItem is not SiteRecord selectedSite)
            {
                return;
            }

            var update = manager.AddToEnvironment(selectedSite.Path, instance);
            await ShowMessageAsync(
                "Updated .env",
                $"Added {update.AddedKeys} and updated {update.UpdatedKeys} variables in {selectedSite.Name}."
            );
        }
        catch (Exception error)
        {
            await ShowErrorAsync(error.Message);
        }
    }

    private async void OpenConsole_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstance(sender, out var instance)) return;
        var uri = manager.ConsoleUri(instance.Id);
        if (uri is null)
        {
            await ShowErrorAsync("Start this storage service before opening its console.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            await ShowErrorAsync(error.Message);
        }
    }

    private async void OpenTablePlus_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstance(sender, out var instance)) return;
        if (manager.State(instance.Id, instance.DefinitionId) != ManagedServiceState.Running)
        {
            await ShowErrorAsync("Start this database service before opening it in TablePlus.");
            return;
        }
        try
        {
            manager.OpenInTablePlus(instance);
        }
        catch (Exception error) when (
            error is FileNotFoundException
                or InvalidOperationException
                or NotSupportedException
        )
        {
            await ShowErrorAsync(error.Message);
        }
    }

    private async void CopyConnection_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstance(sender, out var instance)) return;
        if (manager.State(instance.Id, instance.DefinitionId) != ManagedServiceState.Running)
        {
            await ShowErrorAsync("Start this database service before copying its connection URL.");
            return;
        }
        try
        {
            var package = new DataPackage();
            package.SetText(manager.ConnectionUri(instance).AbsoluteUri);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            OperationStatusText.Text = $"{instance.Name} connection URL copied";
        }
        catch (Exception error)
        {
            await ShowErrorAsync(error.Message);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstance(sender, out var instance)) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Delete {instance.Name}?",
            Content = "The service will stop and its local data directory will be deleted. The shared runtime stays installed.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await manager.RemoveAsync(instance.Id, deleteData: true);
            await RefreshRowsAsync();
        }
        catch (Exception error)
        {
            await ShowErrorAsync(error.Message);
        }
    }

    private async Task RefreshRowsAsync()
    {
        var instances = manager.LoadInstances();
        var installedDefinitionIds = instances
            .Select(instance => instance.DefinitionId)
            .Where(manager.IsInstalled)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var releaseTasks = installedDefinitionIds.Select(async definitionId =>
        {
            try
            {
                var release = await manager.ResolveReleaseAsync(definitionId);
                return (DefinitionId: definitionId, Version: release.Version);
            }
            catch (Exception)
            {
                return (DefinitionId: definitionId, Version: (string?)null);
            }
        });
        var latestVersions = (await Task.WhenAll(releaseTasks))
            .Where(result => result.Version is not null)
            .ToDictionary(
                result => result.DefinitionId,
                result => result.Version!,
                StringComparer.OrdinalIgnoreCase
            );

        refreshing = true;
        Rows.Clear();
        foreach (var instance in instances)
        {
            var installedVersion = manager.InstalledVersion(instance.DefinitionId);
            latestVersions.TryGetValue(instance.DefinitionId, out var latestVersion);
            var state = manager.State(instance.Id, instance.DefinitionId);
            Rows.Add(new ManagedServiceRow
            {
                Id = instance.Id,
                DefinitionId = instance.DefinitionId,
                Name = instance.Name,
                Port = instance.Port,
                Version = installedVersion,
                State = state,
                StartAutomatically = instance.StartAutomatically,
                IsUpdateAvailable = latestVersion is not null
                    && RuntimeVersionComparison.IsNewer(latestVersion, installedVersion),
                ConsolePort = manager.ConsolePort(instance.Id),
                ConnectionDisplay = state == ManagedServiceState.Running
                    ? TablePlusConnection.DisplayAddress(instance)
                    : null
            });
        }
        ServiceList.Visibility = Rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        refreshing = false;
    }

    private bool TryGetInstance(object sender, out ManagedServiceInstance instance)
    {
        instance = null!;
        if (sender is not FrameworkElement { Tag: Guid id }) return false;
        instance = manager.LoadInstances().FirstOrDefault(candidate => candidate.Id == id)!;
        return instance is not null;
    }

    private void SetWorking(bool working, string status)
    {
        OperationProgress.IsActive = working;
        OperationStatusText.Text = status;
        IsEnabled = !working;
    }

    private async Task ShowErrorAsync(string message)
    {
        await ShowMessageAsync("HerdMe", message);
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
