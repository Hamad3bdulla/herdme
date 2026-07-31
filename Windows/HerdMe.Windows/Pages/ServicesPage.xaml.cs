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
    private readonly WindowsServiceManager manager;
    private readonly CoreClient coreClient;
    private readonly SiteConfigurationStore siteSettings;
    private bool refreshing;
    private bool loaded;
    private bool working;
    private CancellationTokenSource? refreshCancellation;

    public IReadOnlyList<ManagedServiceDefinition> Definitions { get; } = ManagedServiceCatalog.All;

    public ObservableCollection<ManagedServiceRow> Rows { get; } = [];

    public ServicesPage(
        WindowsServiceManager manager,
        CoreClient coreClient,
        SiteConfigurationStore siteSettings
    )
    {
        this.manager = manager;
        this.coreClient = coreClient;
        this.siteSettings = siteSettings;
        InitializeComponent();
        if (Definitions.Count > 0)
        {
            ServiceTypeBox.SelectedIndex = 0;
        }
        else
        {
            ServiceTypeBox.IsEnabled = false;
            ServiceNameBox.IsEnabled = false;
            ServicePortBox.IsEnabled = false;
            AddServiceButton.IsEnabled = false;
            ServiceAvailabilityText.Text = RuntimeCatalog.LoadIssue
                ?? "The bundled service catalog could not be loaded.";
            ServiceAvailabilityText.Visibility = Visibility.Visible;
        }
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        loaded = true;
        manager.Changed -= Manager_Changed;
        manager.Changed += Manager_Changed;
        await RefreshRowsAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        loaded = false;
        manager.Changed -= Manager_Changed;
        Interlocked.Exchange(ref refreshCancellation, null)?.Cancel();
    }

    private void Manager_Changed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (loaded) await RefreshRowsAsync();
        });
    }

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
        ServiceAvailabilityText.Text = UnavailableReasonFor(definition);
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
                UnavailableReasonFor(definition)
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
            var owner = AppLocalization.Get(
                assignedToHerdMe ? "ServicesPortOwnerHerdMe" : "ServicesPortOwnerApplication"
            );
            await ShowErrorAsync(
                suggestion is null
                    ? AppLocalization.Format("ServicesPortConflictNoAlternative", port, owner)
                    : AppLocalization.Format(
                        "ServicesPortConflictSuggested",
                        port,
                        owner,
                        suggestion.Value
                    )
            );
            return;
        }
        var instance = new ManagedServiceInstance
        {
            DefinitionId = definition.Id,
            Name = string.IsNullOrWhiteSpace(ServiceNameBox.Text) ? definition.Name : ServiceNameBox.Text.Trim(),
            Port = port,
            StartAutomatically = true
        };
        instances.Add(instance);
        manager.SaveInstances(instances);
        SetWorking(true, AppLocalization.Format("ServicesInstalling", instance.Name));
        try
        {
            if (!manager.IsInstalled(definition.Id))
            {
                await manager.InstallAsync(instance.DefinitionId);
            }
            OperationStatusText.Text = AppLocalization.Format("ServicesStarting", instance.Name);
            await manager.StartAsync(instance.Id);
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
            await ShowErrorAsync(AppLocalization.Get("ServicesStopBeforeUpdate"));
            return;
        }
        SetWorking(true, AppLocalization.Format("ServicesInstalling", instance.Name));
        try
        {
            var release = await manager.InstallAsync(instance.DefinitionId);
            OperationStatusText.Text = AppLocalization.Format(
                "ServicesVersionInstalled",
                instance.Name,
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

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstance(sender, out var instance)) return;
        var running = manager.State(instance.Id, instance.DefinitionId) == ManagedServiceState.Running;
        SetWorking(
            true,
            AppLocalization.Format(
                running ? "ServicesStopping" : "ServicesStarting",
                instance.Name
            )
        );
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
        if (refreshing
            || sender is not ToggleMenuFlyoutItem toggle
            || toggle.Tag is not Guid id) return;
        var instances = manager.LoadInstances().ToList();
        var instance = instances.FirstOrDefault(candidate => candidate.Id == id);
        if (instance is null || instance.StartAutomatically == toggle.IsChecked) return;
        instance.StartAutomatically = toggle.IsChecked;
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
                await ShowErrorAsync(AppLocalization.Get("ServicesAddSiteBeforeEnvironment"));
                return;
            }

            var siteBox = new ComboBox
            {
                Header = AppLocalization.Get("ServicesSiteField"),
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
                Title = AppLocalization.Format("ServicesEnvironmentDialogTitle", instance.Name),
                Content = content,
                PrimaryButtonText = AppLocalization.Get("ServicesAddToEnvironment"),
                CloseButtonText = AppLocalization.Get("CommonCancel"),
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary
                || siteBox.SelectedItem is not SiteRecord selectedSite)
            {
                return;
            }

            var update = manager.AddToEnvironment(selectedSite.Path, instance);
            await ShowMessageAsync(
                AppLocalization.Get("ServicesEnvironmentUpdatedTitle"),
                AppLocalization.Format(
                    "ServicesEnvironmentUpdatedMessage",
                    update.AddedKeys,
                    update.UpdatedKeys,
                    selectedSite.Name
                )
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
            await ShowErrorAsync(AppLocalization.Get("ServicesStartBeforeConsole"));
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
            await ShowErrorAsync(AppLocalization.Get("ServicesStartBeforeTablePlus"));
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
            await ShowErrorAsync(AppLocalization.Get("ServicesStartBeforeCopyConnection"));
            return;
        }
        try
        {
            var package = new DataPackage();
            package.SetText(manager.ConnectionUri(instance).AbsoluteUri);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            OperationStatusText.Text = AppLocalization.Format(
                "ServicesConnectionCopied",
                instance.Name
            );
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
            Title = AppLocalization.Format("ServicesDeleteTitle", instance.Name),
            Content = AppLocalization.Get("ServicesDeleteMessage"),
            PrimaryButtonText = AppLocalization.Get("CommonDelete"),
            CloseButtonText = AppLocalization.Get("CommonCancel"),
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
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref refreshCancellation, cancellation);
        previous?.Cancel();
        var instances = manager.LoadInstances();
        if (!loaded)
        {
            Interlocked.CompareExchange(ref refreshCancellation, null, cancellation);
            cancellation.Dispose();
            return;
        }
        RenderRows(
            instances,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        );
        var installedDefinitionIds = instances
            .Select(instance => instance.DefinitionId)
            .Where(manager.IsInstalled)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var releaseTasks = installedDefinitionIds.Select(async definitionId =>
        {
            try
            {
                var release = await manager.ResolveReleaseAsync(
                    definitionId,
                    cancellation.Token
                );
                return (DefinitionId: definitionId, Version: release.Version);
            }
            catch (Exception error) when (
                error is not OperationCanceledException || !cancellation.IsCancellationRequested
            )
            {
                return (DefinitionId: definitionId, Version: (string?)null);
            }
        });
        try
        {
            var latestVersions = (await Task.WhenAll(releaseTasks))
                .Where(result => result.Version is not null)
                .ToDictionary(
                    result => result.DefinitionId,
                    result => result.Version!,
                    StringComparer.OrdinalIgnoreCase
                );
            cancellation.Token.ThrowIfCancellationRequested();
            if (!loaded) return;
            RenderRows(instances, latestVersions);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.CompareExchange(ref refreshCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private void RenderRows(
        IReadOnlyList<ManagedServiceInstance> instances,
        IReadOnlyDictionary<string, string> latestVersions
    )
    {
        refreshing = true;
        try
        {
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
                    Status = StateLabel(state),
                    InstallLabel = AppLocalization.Get(
                        state == ManagedServiceState.NotInstalled ? "CommonInstall" : "CommonUpdate"
                    ),
                    ToggleLabel = AppLocalization.Get(
                        state == ManagedServiceState.Running ? "ServicesStop" : "ServicesStart"
                    ),
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
            if (!working)
            {
                var installing = instances.FirstOrDefault(instance =>
                    manager.IsInstalling(instance.DefinitionId)
                );
                OperationProgress.IsActive = installing is not null;
                OperationStatusText.Text = installing is null
                    ? string.Empty
                    : AppLocalization.Format("ServicesInstalling", installing.Name);
            }
        }
        finally
        {
            refreshing = false;
        }
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
        this.working = working;
        OperationProgress.IsActive = working;
        OperationStatusText.Text = status;
        IsEnabled = !working;
    }

    private static string StateLabel(ManagedServiceState state)
    {
        return AppLocalization.Get(state switch
        {
            ManagedServiceState.NotInstalled => "CommonNotInstalled",
            ManagedServiceState.Installing => "ServicesInstallingState",
            ManagedServiceState.Stopped => "ServicesStopped",
            ManagedServiceState.Running => "ServicesRunning",
            _ => "ServicesUnknown"
        });
    }

    private static string UnavailableReasonFor(ManagedServiceDefinition definition)
    {
        return definition.Id switch
        {
            "valkey" => AppLocalization.Get("ServicesValkeyUnavailable"),
            "typesense" => AppLocalization.Get("ServicesTypesenseUnavailable"),
            _ => definition.UnavailableReason
                ?? AppLocalization.Format("ServicesUnavailableNative", definition.Name)
        };
    }

    private async Task ShowErrorAsync(string message)
    {
        await ShowMessageAsync("HerdMe", message);
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        if (!loaded || XamlRoot is not { } xamlRoot)
        {
            OperationStatusText.Text = message;
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = AppLocalization.Get("CommonOk")
        };
        await dialog.ShowAsync();
    }
}
