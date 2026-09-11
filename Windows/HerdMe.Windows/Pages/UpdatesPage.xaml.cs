using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace HerdMe.Windows.Pages;

public sealed partial class UpdatesPage : Page
{
    private readonly SiteConfigurationStore settingsStore;
    private readonly AppUpdateManager appUpdates;
    private readonly ManagedComponentUpdateManager componentUpdates;
    private readonly WindowsLocalEnvironment environment;
    private readonly PhpRuntimeInstaller phpInstaller;
    private readonly PhpRuntimePolicy runtimePolicy;
    private readonly NodeRuntimeInstaller nodeInstaller;
    private readonly ComposerToolManager composerTools;
    private readonly GitRuntimeInstaller gitInstaller;
    private readonly XdebugManager xdebugManager;
    private readonly WindowsServiceManager serviceManager;
    private readonly WindowsUserPathManager userPathManager;
    private CancellationTokenSource? refreshCancellation;
    private ManagedComponentUpdateCheck? latestComponents;
    private AppUpdateCheck? latestApplication;
    private bool loaded;
    private bool busy;
    private readonly Dictionary<string, (Grid Grid, TextBlock Detail, TextBlock Error, ProgressBar Progress, Button Action)> downloadControls = [];

    public UpdatesPage(
        SiteConfigurationStore settingsStore,
        AppUpdateManager appUpdates,
        ManagedComponentUpdateManager componentUpdates,
        WindowsLocalEnvironment environment,
        PhpRuntimeInstaller phpInstaller,
        PhpRuntimePolicy runtimePolicy,
        NodeRuntimeInstaller nodeInstaller,
        ComposerToolManager composerTools,
        GitRuntimeInstaller gitInstaller,
        XdebugManager xdebugManager,
        WindowsServiceManager serviceManager,
        WindowsUserPathManager userPathManager
    )
    {
        this.settingsStore = settingsStore;
        this.appUpdates = appUpdates;
        this.componentUpdates = componentUpdates;
        this.environment = environment;
        this.phpInstaller = phpInstaller;
        this.runtimePolicy = runtimePolicy;
        this.nodeInstaller = nodeInstaller;
        this.composerTools = composerTools;
        this.gitInstaller = gitInstaller;
        this.xdebugManager = xdebugManager;
        this.serviceManager = serviceManager;
        this.userPathManager = userPathManager;
        InitializeComponent();
        RenderApplication();
        RenderComponents();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        loaded = true;
        RuntimeOperations.Shared.Changed += Downloads_Changed;
        RenderDownloads();
        await RefreshAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        loaded = false;
        RuntimeOperations.Shared.Changed -= Downloads_Changed;
        Interlocked.Exchange(ref refreshCancellation, null)?.Cancel();
    }

    private void Downloads_Changed(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(() => { if (loaded) RenderDownloads(); });

    private void ClearDownloads_Click(object sender, RoutedEventArgs e) => RuntimeOperations.Shared.ClearCompleted();

    private void RenderDownloads()
    {
        var snapshot = RuntimeOperations.Shared.Snapshot();
        foreach (var id in downloadControls.Keys.Except(snapshot.Select(item => item.Id)).ToArray())
        {
            DownloadRows.Children.Remove(downloadControls[id].Grid);
            downloadControls.Remove(id);
        }
        foreach (var operation in snapshot)
        {
            var row = ServiceDownloadRow.From(operation.Progress, operation.Name);
            if (downloadControls.TryGetValue(operation.Id, out var controls))
            {
                controls.Detail.Text = row.Detail;
                controls.Error.Text = row.Error;
                controls.Progress.Value = row.Percentage;
                controls.Progress.IsIndeterminate = row.IsIndeterminate;
                controls.Progress.Visibility = row.IsActive;
                controls.Action.Visibility = operation.Progress.Stage == ServiceInstallationStage.Completed ? Visibility.Collapsed : Visibility.Visible;
                controls.Action.Content = new SymbolIcon(operation.Progress.IsActive ? Symbol.Cancel : Symbol.Refresh);
                ToolTipService.SetToolTip(controls.Action, operation.Progress.IsActive ? row.CancelLabel : row.RetryLabel);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(controls.Action, operation.Progress.IsActive ? row.CancelLabel : row.RetryLabel);
                continue;
            }
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Spacing = 4 };
            text.Children.Add(new TextBlock { Text = operation.Name, TextWrapping = TextWrapping.Wrap });
            var detail = new TextBlock { Text = row.Detail, TextWrapping = TextWrapping.Wrap };
            var error = new TextBlock { Text = row.Error, TextWrapping = TextWrapping.Wrap };
            var progress = new ProgressBar { Value = row.Percentage, IsIndeterminate = row.IsIndeterminate, Visibility = row.IsActive };
            text.Children.Add(detail);
            text.Children.Add(progress);
            text.Children.Add(error);
            grid.Children.Add(text);
            {
                var button = new Button { Content = new SymbolIcon(operation.Progress.IsActive ? Symbol.Cancel : Symbol.Refresh),
                    Visibility = operation.Progress.Stage == ServiceInstallationStage.Completed ? Visibility.Collapsed : Visibility.Visible };
                ToolTipService.SetToolTip(button, operation.Progress.IsActive ? row.CancelLabel : row.RetryLabel);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, operation.Progress.IsActive ? row.CancelLabel : row.RetryLabel);
                button.Click += async (_, _) =>
                {
                    if (RuntimeOperations.Shared.Snapshot().FirstOrDefault(item => item.Id == operation.Id)?.Progress.IsActive == true)
                        RuntimeOperations.Shared.Cancel(operation.Id);
                    else
                    {
                        try { await RuntimeOperations.Shared.RetryAsync(operation.Id); }
                        catch (Exception) { RenderDownloads(); }
                    }
                };
                Grid.SetColumn(button, 1);
                grid.Children.Add(button);
                downloadControls[operation.Id] = (grid, detail, error, progress, button);
            }
            DownloadRows.Children.Add(grid);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (busy) return;
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref refreshCancellation, cancellation);
        previous?.Cancel();
        SetBusy(true, AppLocalization.Get("UpdatesChecking"));
        try
        {
            AppUpdateCheck? application = null;
            Exception? applicationError = null;
            var applicationTask = appUpdates.CheckAsync(
                settingsStore.Load().UpdateChannel,
                cancellation.Token
            );
            var componentsTask = componentUpdates.CheckAsync(cancellation.Token);
            try
            {
                application = await applicationTask;
            }
            catch (Exception error) when (
                error is not OperationCanceledException || !cancellation.IsCancellationRequested
            )
            {
                applicationError = error;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (!loaded) return;

            latestApplication = application;
            RenderApplication(applicationError);
            BusyOverlay.Visibility = Visibility.Collapsed;
            ComponentCheckProgress.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;

            var components = await componentsTask;
            cancellation.Token.ThrowIfCancellationRequested();
            if (!loaded) return;

            latestComponents = components;
            RenderComponents();
            LastCheckedText.Text = AppLocalization.Format(
                "UpdatesLastChecked",
                components.CheckedAt.ToLocalTime().ToString("g")
            );
            ShowCheckStatus(applicationError, components);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (loaded)
                ShowStatus(
                    InfoBarSeverity.Error,
                    AppLocalization.Get("UpdatesCheckFailed"),
                    error.Message
                );
        }
        finally
        {
            Interlocked.CompareExchange(ref refreshCancellation, null, cancellation);
            cancellation.Dispose();
            ComponentCheckProgress.Visibility = Visibility.Collapsed;
            SetBusy(false, string.Empty);
        }
    }

    private void RenderApplication(Exception? error = null)
    {
        ApplicationVersionText.Text = AppLocalization.Format(
            "UpdatesInstalledVersion",
            appUpdates.CurrentVersion
        );
        DownloadApplicationButton.Visibility = Visibility.Collapsed;
        if (latestApplication?.AvailableRelease is { } release)
        {
            ApplicationStatusText.Text = AppLocalization.Format(
                "UpdatesVersionAvailable",
                release.Version
            );
            DownloadApplicationButton.Visibility = Visibility.Visible;
        }
        else if (error is not null || latestApplication?.UsedBundledFallback == true)
        {
            ApplicationStatusText.Text = AppLocalization.Get("UpdatesApplicationCheckUnavailable");
        }
        else
        {
            ApplicationStatusText.Text = AppLocalization.Get("UpdatesCurrent");
        }
    }

    private void RenderComponents()
    {
        ComponentRows.Children.Clear();
        var updates = latestComponents?.Updates ?? [];
        foreach (var update in updates)
        {
            ComponentRows.Children.Add(UpdateRow(update));
        }
        EmptyState.Visibility = updates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAllButton.Visibility = updates.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAllButton.IsEnabled = !busy;
    }

    private UIElement UpdateRow(ManagedComponentUpdate update)
    {
        var row = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16)
        };
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var details = new StackPanel { Spacing = 5 };
        details.Children.Add(new TextBlock
        {
            Text = update.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        details.Children.Add(new TextBlock
        {
            Text = AppLocalization.Format(
                "UpdatesVersionChange",
                update.InstalledVersion,
                update.LatestVersion
            ),
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        details.Children.Add(new TextBlock
        {
            Text = ComponentCategory(update),
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        grid.Children.Add(details);

        var button = new Button
        {
            Tag = update,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !busy
        };
        button.Click += UpdateComponent_Click;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new SymbolIcon(Symbol.Download));
        content.Children.Add(new TextBlock { Text = AppLocalization.Get("CommonUpdate") });
        button.Content = content;
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        row.Child = grid;
        return row;
    }

    private async void UpdateComponent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ManagedComponentUpdate update } || busy) return;
        await RunUpdatesAsync([update]);
    }

    private async void UpdateAll_Click(object sender, RoutedEventArgs e)
    {
        if (busy || latestComponents is null) return;
        var uniqueUpdates = latestComponents.Updates
            .GroupBy(OperationKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        await RunUpdatesAsync(uniqueUpdates);
    }

    private async Task RunUpdatesAsync(IReadOnlyList<ManagedComponentUpdate> updates)
    {
        SetBusy(true, AppLocalization.Format("UpdatesInstallingCount", updates.Count));
        var completed = 0;
        var failures = new List<string>();
        foreach (var update in updates)
        {
            BusyText.Text = AppLocalization.Format("UpdatesInstallingComponent", update.Name);
            try
            {
                await InstallAsync(update);
                completed++;
            }
            catch (Exception error)
            {
                failures.Add($"{update.Name}: {error.Message}");
            }
        }
        SetBusy(false, string.Empty);
        if (failures.Count == 0)
        {
            ShowStatus(
                InfoBarSeverity.Success,
                AppLocalization.Get("UpdatesCompletedTitle"),
                AppLocalization.Format("UpdatesCompletedMessage", completed)
            );
        }
        else
        {
            ShowStatus(
                InfoBarSeverity.Error,
                AppLocalization.Get("UpdatesFailedTitle"),
                string.Join(System.Environment.NewLine, failures)
            );
        }
        await RefreshAsync();
    }

    private async Task InstallAsync(ManagedComponentUpdate update)
    {
        if (update.Id.StartsWith("php:", StringComparison.OrdinalIgnoreCase))
        {
            var cycle = update.Id[4..];
            await WithStoppedEnvironmentAsync(async () =>
            {
                await phpInstaller.InstallAsync(cycle);
                await phpInstaller.EnsureManagedConfigurationAsync(cycle);
            });
            SynchronizeUserPath();
            return;
        }
        if (update.Id.StartsWith("node:", StringComparison.OrdinalIgnoreCase))
        {
            await nodeInstaller.InstallAsync(update.Id[5..]);
            SynchronizeUserPath();
            return;
        }
        if (update.Id is "composer" or "laravel-installer")
        {
            await composerTools.InstallOrUpdateAsync(runtimePolicy.Load().PhpCycle);
            SynchronizeUserPath();
            return;
        }
        if (update.Id.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            await gitInstaller.InstallOrUpdateAsync();
            SynchronizeUserPath();
            return;
        }
        if (update.Id.StartsWith("xdebug:", StringComparison.OrdinalIgnoreCase))
        {
            var cycle = update.Id[7..];
            await WithStoppedEnvironmentAsync(() =>
                xdebugManager.InstallAsync(phpInstaller.PhpExecutable(cycle))
            );
            return;
        }
        if (update.Id.StartsWith("service:", StringComparison.OrdinalIgnoreCase))
        {
            await UpdateServiceAsync(update.Id[8..]);
            return;
        }
        throw new InvalidOperationException(
            AppLocalization.Format("UpdatesUnsupportedComponent", update.Name)
        );
    }

    private async Task WithStoppedEnvironmentAsync(Func<Task> update)
    {
        var restart = environment.IsRunning || environment.IsDegraded;
        if (restart) await environment.StopAsync();
        try
        {
            await update();
        }
        finally
        {
            if (restart) await environment.StartConfiguredAsync(settingsStore);
        }
    }

    private async Task UpdateServiceAsync(string definitionId)
    {
        var instances = serviceManager.LoadInstances()
            .Where(instance => instance.DefinitionId.Equals(
                definitionId,
                StringComparison.OrdinalIgnoreCase
            ))
            .ToArray();
        var running = instances.Where(instance => serviceManager.State(
            instance.Id,
            instance.DefinitionId
        ) == ManagedServiceState.Running).ToArray();
        foreach (var instance in running) await serviceManager.StopAsync(instance.Id);
        try
        {
            await serviceManager.InstallAsync(definitionId);
        }
        finally
        {
            foreach (var instance in running) await serviceManager.StartAsync(instance.Id);
        }
    }

    private void SynchronizeUserPath()
    {
        userPathManager.Synchronize(
            composerTools.CommandLineDirectories(runtimePolicy.Load().PhpCycle)
        );
    }

    private async void DownloadApplication_Click(object sender, RoutedEventArgs e)
    {
        var url = latestApplication?.AvailableRelease?.PlatformDownloadUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            ShowStatus(
                InfoBarSeverity.Error,
                AppLocalization.Get("UpdatesDownloadFailedTitle"),
                AppLocalization.Get("UpdatesDownloadUnavailable")
            );
            return;
        }
        if (await Launcher.LaunchUriAsync(uri)) return;
        ShowStatus(
            InfoBarSeverity.Error,
            AppLocalization.Get("UpdatesDownloadFailedTitle"),
            AppLocalization.Format("UpdateOpenInBrowser", uri.AbsoluteUri)
        );
    }

    private void ShowCheckStatus(
        Exception? applicationError,
        ManagedComponentUpdateCheck components
    )
    {
        var unavailable = components.Failures.Select(failure => failure.Component).ToList();
        if (applicationError is not null || latestApplication?.UsedBundledFallback == true)
            unavailable.Insert(0, "HerdMe");
        if (unavailable.Count > 0)
        {
            ShowStatus(
                InfoBarSeverity.Warning,
                AppLocalization.Get("UpdatesPartialTitle"),
                AppLocalization.Format("UpdatesPartialMessage", string.Join(", ", unavailable))
            );
        }
        else if (latestApplication?.AvailableRelease is null && components.Updates.Count == 0)
        {
            ShowStatus(
                InfoBarSeverity.Success,
                AppLocalization.Get("UpdatesCurrentTitle"),
                AppLocalization.Get("ManagedUpdatesUpToDate")
            );
        }
        else
        {
            StatusBar.IsOpen = false;
        }
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private void SetBusy(bool value, string status)
    {
        busy = value;
        BusyText.Text = status;
        BusyOverlay.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = !value;
        UpdateAllButton.IsEnabled = !value;
        RenderComponents();
    }

    internal static string OperationKey(ManagedComponentUpdate update)
    {
        return update.Id is "composer" or "laravel-installer"
            ? "php-tools"
            : update.Id;
    }

    private static string ComponentCategory(ManagedComponentUpdate update)
    {
        return AppLocalization.Get(update.PageTag switch
        {
            "php" => "UpdatesCategoryPhp",
            "node" => "UpdatesCategoryNode",
            "services" => "UpdatesCategoryService",
            "debugger" => "UpdatesCategoryDebugger",
            _ => "UpdatesCategoryTool"
        });
    }
}
