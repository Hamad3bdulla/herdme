using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private CancellationTokenSource? operationCancellation;
    private string? operationDefinitionId;

    public IReadOnlyList<ManagedServiceDefinition> Definitions { get; } = ManagedServiceCatalog.All;

    public ObservableCollection<ManagedServiceRow> Rows { get; } = [];

    public ObservableCollection<ServiceDownloadRow> Downloads { get; } = [];

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
        manager.InstallationProgress -= Manager_InstallationProgress;
        manager.InstallationProgress += Manager_InstallationProgress;
        await RefreshRowsAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        loaded = false;
        manager.Changed -= Manager_Changed;
        manager.InstallationProgress -= Manager_InstallationProgress;
        Interlocked.Exchange(ref refreshCancellation, null)?.Cancel();
        Interlocked.Exchange(ref operationCancellation, null)?.Cancel();
        operationDefinitionId = null;
        SetWorking(false, string.Empty);
    }

    private void Manager_InstallationProgress(object? sender, ServiceInstallationProgress progress)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!loaded) return;
            var existing = Downloads.FirstOrDefault(item =>
                item.DefinitionId.Equals(progress.DefinitionId, StringComparison.OrdinalIgnoreCase));
            if (existing is null) Downloads.Add(ServiceDownloadRow.From(progress));
            else existing.Update(progress);
            DownloadsPanel.Visibility = Downloads.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        });
    }

    private void ServicesLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 720;
        ServiceTypeColumn.Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(180);
        ServicePortColumn.Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(130);
        Grid.SetColumnSpan(ServiceTypeBox, compact ? 2 : 1);
        Grid.SetRow(ServiceNameBox, compact ? 1 : 0);
        Grid.SetColumn(ServiceNameBox, compact ? 0 : 1);
        Grid.SetColumnSpan(ServiceNameBox, compact ? 4 : 1);
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
        ServiceVersionBox.Items.Clear();
        if (manager.IsInstalled(definition.Id))
            ServiceVersionBox.Items.Add(AppLocalization.Format("ServicesUseInstalled", manager.InstalledVersion(definition.Id)));
        ServiceVersionBox.Items.Add(AppLocalization.Format("ServicesUseLatest", definition.VersionChannel));
        ServiceVersionBox.SelectedIndex = 0;
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
            var owner = assignedToHerdMe
                ? AppLocalization.Get("ServicesPortOwnerHerdMe")
                : PortOwner(PortConflictInspector.Inspect(port));
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
        var installLatest = ServiceVersionBox.SelectedIndex == ServiceVersionBox.Items.Count - 1;
        instances.Add(instance);
        manager.SaveInstances(instances);
        using var cancellation = BeginOperation(
            AppLocalization.Format("ServicesInstalling", instance.Name), instance.DefinitionId
        );
        try
        {
            InstallationPreflight.EnsureStorage(manager.SupportRoot);
            if (!manager.IsInstalled(definition.Id) || installLatest)
            {
                await manager.InstallAsync(instance.DefinitionId, cancellation.Token);
            }
            OperationStatusText.Text = AppLocalization.Format("ServicesStarting", instance.Name);
            await manager.StartAsync(instance.Id, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            OperationStatusText.Text = AppLocalization.Get("ServicesCancelled");
        }
        catch (Exception error)
        {
            await ShowErrorAsync(error.Message);
        }
        finally
        {
            EndOperation(cancellation);
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
        using var cancellation = BeginOperation(
            AppLocalization.Format("ServicesInstalling", instance.Name), instance.DefinitionId
        );
        try
        {
            var release = await manager.InstallAsync(instance.DefinitionId, cancellation.Token);
            OperationStatusText.Text = AppLocalization.Format(
                "ServicesVersionInstalled",
                instance.Name,
                release.Version
            );
        }
        catch (OperationCanceledException)
        {
            OperationStatusText.Text = AppLocalization.Get("ServicesCancelled");
        }
        catch (Exception error)
        {
            await ShowErrorAsync(error.Message);
        }
        finally
        {
            EndOperation(cancellation);
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
            else if (WindowsServiceManager.IsPortAvailable(instance.Port))
            {
                await manager.StartAsync(instance.Id);
            }
            else
            {
                await RepairPortAsync(instance, startAfterRepair: true);
            }
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

    private async void InspectPort_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstance(sender, out var instance)) return;
        var running = manager.State(instance.Id, instance.DefinitionId)
            == ManagedServiceState.Running;
        if (running)
        {
            await ShowMessageAsync(
                AppLocalization.Get("ServicesPortInspectionTitle"),
                AppLocalization.Format(
                    "ServicesPortInspectionOwned", instance.Port, instance.Name
                )
            );
            return;
        }
        var conflict = PortConflictInspector.Inspect(instance.Port);
        if (!conflict.InUse)
        {
            await ShowMessageAsync(
                AppLocalization.Get("ServicesPortInspectionTitle"),
                AppLocalization.Format("ServicesPortInspectionAvailable", instance.Port)
            );
            return;
        }
        await RepairPortAsync(instance, startAfterRepair: false);
        await RefreshRowsAsync();
    }

    private async void Backups_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstance(sender, out var instance)) return;
        var backups = manager.Backups.List(instance.DefinitionId).Where(item => item.Instances.Contains(instance.Id)).ToArray();
        var picker = new ComboBox { ItemsSource = backups.Select(item => $"{item.CreatedAt.LocalDateTime:g} - {item.RuntimeVersion}").ToArray(),
            SelectedIndex = backups.Length > 0 ? 0 : -1, HorizontalAlignment = HorizontalAlignment.Stretch };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = AppLocalization.Get("ServicesRestoreNotice"), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(picker);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = AppLocalization.Get("ServicesBackupsTitle"), Content = content,
            PrimaryButtonText = AppLocalization.Get("ServicesRestoreData"), CloseButtonText = AppLocalization.Get("CommonCancel"),
            IsPrimaryButtonEnabled = backups.Length > 0, DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || picker.SelectedIndex < 0) return;
        using var cancellation = BeginOperation(AppLocalization.Get("ServicesRestoreData"));
        try { await manager.RestoreDataAsync(instance.Id, backups[picker.SelectedIndex], cancellation.Token); }
        catch (Exception error) { await ShowErrorAsync(UserErrorPresentation.Describe(error)); }
        finally { EndOperation(cancellation); await RefreshRowsAsync(); }
    }

    private async Task RepairPortAsync(
        ManagedServiceInstance instance,
        bool startAfterRepair
    )
    {
        var instances = manager.LoadInstances().ToList();
        var conflict = PortConflictInspector.Inspect(instance.Port);
        var suggestion = WindowsServiceManager.AvailablePort(
            instance.Port == 65_535 ? 1_024 : instance.Port + 1,
            instances.Where(candidate => candidate.Id != instance.Id)
                .Select(candidate => candidate.Port)
        );
        if (!conflict.InUse || suggestion is null)
        {
            await ShowErrorAsync(
                suggestion is null
                    ? AppLocalization.Format(
                        "ServicesPortConflictNoAlternative", instance.Port, PortOwner(conflict)
                    )
                    : AppLocalization.Format("ServicesPortInspectionAvailable", instance.Port)
            );
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = AppLocalization.Get("ServicesPortRepairTitle"),
            Content = AppLocalization.Format(
                "ServicesPortRepairMessage",
                instance.Port,
                PortOwner(conflict),
                suggestion.Value
            ),
            PrimaryButtonText = startAfterRepair
                ? AppLocalization.Get("ServicesPortRepairAndStart")
                : AppLocalization.Get("ServicesPortRepair"),
            CloseButtonText = AppLocalization.Get("CommonCancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var configured = instances.First(candidate => candidate.Id == instance.Id);
        configured.Port = suggestion.Value;
        manager.SaveInstances(instances);
        instance.Port = suggestion.Value;
        if (startAfterRepair) await manager.StartAsync(instance.Id);
        OperationStatusText.Text = AppLocalization.Format(
            "ServicesPortRepaired", instance.Name, suggestion.Value
        );
    }

    private static string PortOwner(PortConflictDetails conflict)
    {
        if (!string.IsNullOrWhiteSpace(conflict.ProcessName) && conflict.ProcessId is { } id)
        {
            return AppLocalization.Format(
                "ServicesPortOwnerProcess", conflict.ProcessName, id
            );
        }
        return conflict.ProcessId is { } processId
            ? AppLocalization.Format("ServicesPortOwnerPid", processId)
            : AppLocalization.Get("ServicesPortOwnerApplication");
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
        foreach (var progress in manager.InstallationStates)
            Manager_InstallationProgress(manager, progress);
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
        foreach (var child in ServiceForm.Children)
        {
            if (child is Control control) control.IsEnabled = !working;
        }
        AddServiceButton.IsEnabled = !working
            && ServiceTypeBox.SelectedItem is ManagedServiceDefinition { IsInstallable: true };
        ServiceList.IsEnabled = !working;
        CancelOperationButton.Visibility = working && operationCancellation is not null
            ? Visibility.Visible : Visibility.Collapsed;
        CancelOperationButton.IsEnabled = working;
    }

    private CancellationTokenSource BeginOperation(string status, string? definitionId = null)
    {
        operationDefinitionId = definitionId;
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref operationCancellation, cancellation);
        previous?.Cancel();
        SetWorking(true, status);
        return cancellation;
    }

    private void EndOperation(CancellationTokenSource cancellation)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref operationCancellation, null, cancellation), cancellation)) return;
        operationDefinitionId = null;
        SetWorking(false, string.Empty);
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        if (operationDefinitionId is { } definitionId) manager.CancelInstallation(definitionId);
        Interlocked.CompareExchange(ref operationCancellation, null, null)?.Cancel();
        OperationStatusText.Text = AppLocalization.Get("ServicesCancelling");
        CancelOperationButton.IsEnabled = false;
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string definitionId })
            manager.CancelInstallation(definitionId);
    }

    private async void RetryDownload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string definitionId }) return;
        if (working || manager.IsInstalling(definitionId)) return;
        using var cancellation = BeginOperation(AppLocalization.Format("ServicesInstalling", definitionId), definitionId);
        try { await manager.InstallAsync(definitionId, cancellation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { await ShowErrorAsync(error.Message); }
        finally { EndOperation(cancellation); await RefreshRowsAsync(); }
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

public sealed class ServiceDownloadRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string DefinitionId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public double Percentage { get; set; }
    public bool IsIndeterminate { get; set; }
    public Visibility IsActive { get; set; }
    public Visibility CanCancel { get; set; }
    public Visibility CanRetry { get; set; }
    public string CancelLabel { get; set; } = string.Empty;
    public string RetryLabel { get; set; } = string.Empty;

    public void Update(ServiceInstallationProgress progress)
    {
        var updated = From(progress);
        Title = updated.Title;
        Detail = updated.Detail;
        Error = updated.Error;
        Percentage = updated.Percentage;
        IsIndeterminate = updated.IsIndeterminate;
        IsActive = updated.IsActive;
        CanCancel = updated.CanCancel;
        CanRetry = updated.CanRetry;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public static ServiceDownloadRow From(ServiceInstallationProgress progress, string? title = null)
    {
        var active = progress.IsActive;
        var label = progress.Stage switch
        {
            ServiceInstallationStage.BackingUp => AppLocalization.Get("ServicesBackingUp"),
            ServiceInstallationStage.Resolving => AppLocalization.Get("ServicesResolving"),
            ServiceInstallationStage.Downloading => AppLocalization.Get("ServicesDownloading"),
            ServiceInstallationStage.Retrying => AppLocalization.Get("ServicesRetrying"),
            ServiceInstallationStage.Verifying => AppLocalization.Get("ServicesVerifying"),
            ServiceInstallationStage.Extracting => AppLocalization.Get("ServicesExtracting"),
            ServiceInstallationStage.Installing => AppLocalization.Get("ServicesInstallingState"),
            ServiceInstallationStage.Completed => AppLocalization.Get("ServicesCompleted"),
            ServiceInstallationStage.Cancelled => AppLocalization.Get("ServicesCancelled"),
            ServiceInstallationStage.Failed => AppLocalization.Get("ServicesFailed"),
            _ => progress.Stage.ToString()
        };
        var detail = progress.Percentage is { } percentage
            ? $"{percentage:0}%" : progress.Stage == ServiceInstallationStage.Downloading ? "..." : string.Empty;
        if (progress.Stage == ServiceInstallationStage.Downloading)
        {
            const double megabyte = 1_048_576;
            var transfer = progress.TotalBytes is { } total
                ? AppLocalization.Format("ServicesTransfer", progress.BytesReceived / megabyte,
                    total / megabyte, progress.BytesPerSecond / megabyte)
                : AppLocalization.Format("ServicesTransferUnknown", progress.BytesReceived / megabyte,
                    progress.BytesPerSecond / megabyte);
            detail = $"{detail} - {transfer}";
        }
        if (progress.Attempt > 1)
            detail = $"{detail} {AppLocalization.Format("ServicesAttempt", progress.Attempt)}".Trim();
        return new ServiceDownloadRow
        {
            DefinitionId = progress.DefinitionId,
            Title = title ?? ManagedServiceCatalog.Get(progress.DefinitionId).Name,
            Detail = detail.Length == 0 ? label : $"{label} - {detail}",
            Error = progress.Stage == ServiceInstallationStage.Failed
                ? AppLocalization.Get("ErrorOperation") + Environment.NewLine + progress.Error : string.Empty,
            CancelLabel = AppLocalization.Get("CommonCancel"),
            RetryLabel = AppLocalization.Get("ServicesRetry"),
            Percentage = progress.Percentage ?? 0,
            IsIndeterminate = progress.Percentage is null && active,
            IsActive = active ? Visibility.Visible : Visibility.Collapsed,
            CanCancel = active ? Visibility.Visible : Visibility.Collapsed,
            CanRetry = progress.Stage is ServiceInstallationStage.Failed or ServiceInstallationStage.Cancelled
                ? Visibility.Visible : Visibility.Collapsed
        };
    }
}
