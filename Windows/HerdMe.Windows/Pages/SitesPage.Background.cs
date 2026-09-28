using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using HerdMe.Windows.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace HerdMe.Windows.Pages;

public sealed partial class SitesPage
{
    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is null) return;
        App.MainWindow.NavigateToLogs(selectedSite.Path);
    }

    private async void OpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is null) return;
        try
        {
            var terminal = new ProcessStartInfo("wt.exe")
            {
                UseShellExecute = true,
                WorkingDirectory = selectedSite.Path
            };
            terminal.ArgumentList.Add("-d");
            terminal.ArgumentList.Add(selectedSite.Path);
            Process.Start(terminal);
            App.MainWindow.RememberRecentSite(selectedSite.Path);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            try
            {
                Process.Start(new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = true,
                    WorkingDirectory = selectedSite.Path
                });
            }
            catch (Exception fallbackError) when (fallbackError is Win32Exception or InvalidOperationException)
            {
                await ShowErrorAsync(fallbackError.Message);
            }
        }
    }

    private async void QueueWorker_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        await ShowQueueManagerAsync(site);
    }

    private async Task ShowQueueManagerAsync(SiteRecord site)
    {
        var stateText = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var connectionBox = new TextBox
        {
            Header = AppLocalization.Get("SitesQueueConnection"),
            PlaceholderText = AppLocalization.Get("SitesQueueDefault"),
            MaxLength = 128
        };
        var queueBox = new TextBox
        {
            Header = AppLocalization.Get("SitesQueueNames"),
            PlaceholderText = AppLocalization.Get("SitesQueueDefault"),
            MaxLength = 128
        };
        static NumberBox Number(string header, double value, double minimum, double maximum) => new()
        {
            Header = header,
            Value = value,
            Minimum = minimum,
            Maximum = maximum,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var triesBox = Number(AppLocalization.Get("SitesQueueTries"), 1, 1, 100);
        var timeoutBox = Number(AppLocalization.Get("SitesQueueTimeout"), 60, 0, 86_400);
        var sleepBox = Number(AppLocalization.Get("SitesQueueSleep"), 3, 0, 3_600);
        var maxJobsBox = Number(AppLocalization.Get("SitesQueueMaxJobs"), 0, 0, 1_000_000);
        var maxTimeBox = Number(AppLocalization.Get("SitesQueueMaxTime"), 0, 0, 2_592_000);
        var settingsGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        settingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        settingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        settingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        settingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        settingsGrid.Children.Add(connectionBox);
        Grid.SetColumn(queueBox, 1); settingsGrid.Children.Add(queueBox);
        Grid.SetRow(triesBox, 1); settingsGrid.Children.Add(triesBox);
        Grid.SetRow(timeoutBox, 1); Grid.SetColumn(timeoutBox, 1); settingsGrid.Children.Add(timeoutBox);
        Grid.SetRow(sleepBox, 2); settingsGrid.Children.Add(sleepBox);
        Grid.SetRow(maxJobsBox, 2); Grid.SetColumn(maxJobsBox, 1); settingsGrid.Children.Add(maxJobsBox);
        Grid.SetRow(maxTimeBox, 3); settingsGrid.Children.Add(maxTimeBox);
        var outputBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 210,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        var startStopButton = new Button();
        var failedButton = new Button { Content = AppLocalization.Get("SitesQueueFailedRefresh") };
        var retryButton = new Button { Content = AppLocalization.Get("SitesQueueRetryAll") };
        var flushButton = new Button { Content = AppLocalization.Get("SitesQueueFlushFailed") };
        var confirmFlush = new CheckBox { Content = AppLocalization.Get("SitesQueueFlushConfirm") };
        flushButton.IsEnabled = false;
        var restartButton = new Button { Content = AppLocalization.Get("SitesQueueRestart") };
        var jobIdBox = new TextBox
        {
            Header = AppLocalization.Get("SitesQueueFailedJobId"),
            PlaceholderText = AppLocalization.Get("SitesQueueFailedJobIdPlaceholder"),
            MaxLength = 255
        };
        var retryJobButton = new Button { Content = AppLocalization.Get("SitesQueueRetryJob"), IsEnabled = false };
        var forgetJobButton = new Button { Content = AppLocalization.Get("SitesQueueForgetJob"), IsEnabled = false };
        var progress = new ProgressRing { Width = 18, Height = 18 };
        var workerButtons = new SitesWrapPanel();
        foreach (var control in new Control[] { startStopButton, failedButton, progress })
        {
            workerButtons.Children.Add(control);
        }
        var maintenanceButtons = new SitesWrapPanel();
        foreach (var control in new Control[] { retryButton, restartButton, flushButton })
        {
            maintenanceButtons.Children.Add(control);
        }
        var content = new StackPanel { Width = 500, Spacing = 10 };
        content.Children.Add(stateText);
        content.Children.Add(settingsGrid);
        content.Children.Add(workerButtons);
        content.Children.Add(maintenanceButtons);
        var jobButtons = new SitesWrapPanel();
        jobButtons.Children.Add(retryJobButton);
        jobButtons.Children.Add(forgetJobButton);
        content.Children.Add(jobIdBox);
        content.Children.Add(jobButtons);
        content.Children.Add(confirmFlush);
        content.Children.Add(new TextBlock { Text = AppLocalization.Get("SitesQueueOutput"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(outputBox);
        using var cancellation = new CancellationTokenSource();
        var busy = false;
        var managementOutput = string.Empty;

        void RefreshState()
        {
            var state = siteProcesses.State(site.Path, SiteBackgroundProcessKind.Queue);
            startStopButton.Content = AppLocalization.Get(state.Running ? "SitesStopQueueWorker" : "SitesStartQueueWorker");
            stateText.Text = state.Running
                ? AppLocalization.Format("SitesQueueRunningSince", state.StartedAt?.ToLocalTime().ToString("g") ?? string.Empty)
                : AppLocalization.Get("SitesQueueStopped");
            foreach (var control in new Control[] { connectionBox, queueBox, triesBox, timeoutBox, sleepBox, maxJobsBox, maxTimeBox }) control.IsEnabled = !state.Running && !busy;
            var output = string.IsNullOrWhiteSpace(managementOutput) ? state.Output : managementOutput;
            outputBox.Text = string.IsNullOrWhiteSpace(output) ? AppLocalization.Get("SitesNoCommandOutput") : output;
        }

        async Task RunActionAsync(string action, string? jobId = null)
        {
            if (busy) return;
            busy = true; progress.IsActive = true;
            foreach (var button in new[] { startStopButton, failedButton, retryButton, flushButton, restartButton, retryJobButton, forgetJobButton }) button.IsEnabled = false;
            try
            {
                var cycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
                await runtimePolicy.PrepareLaunchAsync(phpInstaller.PhpExecutable(cycle), cycle, cancellation.Token);
                var result = await QueueManagementService.RunAsync(
                    phpInstaller.PhpExecutable(cycle), site.Path, action, jobId,
                    composerTools.ManagedEnvironment(cycle), cancellation.Token
                );
                managementOutput = string.IsNullOrWhiteSpace(result.Output)
                    ? AppLocalization.Get("SitesQueueActionCompleted") : result.Output;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or InvalidDataException
                or InvalidOperationException or ArgumentException or TimeoutException)
            {
                managementOutput = error.Message;
            }
            finally
            {
                busy = false; progress.IsActive = false;
                foreach (var button in new[] { startStopButton, failedButton, retryButton, restartButton }) button.IsEnabled = true;
                flushButton.IsEnabled = confirmFlush.IsChecked == true;
                retryJobButton.IsEnabled = QueueManagementService.ValidJobId(jobIdBox.Text.Trim());
                forgetJobButton.IsEnabled = retryJobButton.IsEnabled && confirmFlush.IsChecked == true;
                RefreshState();
            }
        }

        startStopButton.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true; startStopButton.IsEnabled = false;
            try
            {
                var state = siteProcesses.State(site.Path, SiteBackgroundProcessKind.Queue);
                managementOutput = string.Empty;
                if (state.Running) await siteProcesses.StopAsync(site.Path, SiteBackgroundProcessKind.Queue);
                else
                {
                    var options = new SiteQueueWorkerOptions(
                        connectionBox.Text.Trim(), queueBox.Text.Trim(), (int)triesBox.Value,
                        (int)timeoutBox.Value, (int)sleepBox.Value, (int)maxJobsBox.Value,
                        (int)maxTimeBox.Value
                    );
                    _ = options.Arguments();
                    var cycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
                    await phpInstaller.EnsureManagedConfigurationAsync(cycle, cancellation.Token);
                    siteProcesses.Start(site.Path, SiteBackgroundProcessKind.Queue,
                        phpInstaller.PhpExecutable(cycle), composerTools.ManagedEnvironment(cycle), options);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
            {
                managementOutput = error.Message;
            }
            finally { busy = false; startStopButton.IsEnabled = true; RefreshState(); }
        };
        failedButton.Click += async (_, _) => await RunActionAsync("failed");
        retryButton.Click += async (_, _) => await RunActionAsync("retry-all");
        restartButton.Click += async (_, _) => await RunActionAsync("restart");
        jobIdBox.TextChanged += (_, _) =>
        {
            retryJobButton.IsEnabled = !busy && QueueManagementService.ValidJobId(jobIdBox.Text.Trim());
            forgetJobButton.IsEnabled = retryJobButton.IsEnabled && confirmFlush.IsChecked == true;
        };
        retryJobButton.Click += async (_, _) => await RunActionAsync("retry", jobIdBox.Text.Trim());
        forgetJobButton.Click += async (_, _) =>
        {
            if (confirmFlush.IsChecked == true) await RunActionAsync("forget", jobIdBox.Text.Trim());
        };
        confirmFlush.Checked += (_, _) =>
        {
            flushButton.IsEnabled = !busy;
            forgetJobButton.IsEnabled = !busy && QueueManagementService.ValidJobId(jobIdBox.Text.Trim());
        };
        confirmFlush.Unchecked += (_, _) =>
        {
            flushButton.IsEnabled = false;
            forgetJobButton.IsEnabled = false;
        };
        flushButton.Click += async (_, _) =>
        {
            if (confirmFlush.IsChecked != true) return;
            await RunActionAsync("flush");
            confirmFlush.IsChecked = false;
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => RefreshState();
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesQueueTitle", site.Name),
            Content = content,
            CloseButtonText = AppLocalization.Get("SitesClose")
        };
        dialog.Opened += async (_, _) => { RefreshState(); StartVisibleTimer(timer); await RunActionAsync("failed"); };
        dialog.Closing += (_, args) => { if (busy) args.Cancel = true; };
        await dialog.ShowAsync();
        StopVisibleTimer(timer); cancellation.Cancel(); UpdateBackgroundProcessState();
    }

    private async void Scheduler_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        await ShowSchedulerManagerAsync(site);
    }

    private async Task ShowSchedulerManagerAsync(SiteRecord site)
    {
        var stateText = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var tasksBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 230,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        var outputBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 150,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        var progress = new ProgressRing { Width = 18, Height = 18 };
        var startStopButton = new Button();
        var refreshButton = new Button { Content = AppLocalization.Get("SitesSchedulerRefresh") };
        var buttons = new SitesWrapPanel();
        buttons.Children.Add(startStopButton);
        buttons.Children.Add(refreshButton);
        buttons.Children.Add(progress);
        var content = new StackPanel { Width = 500, Spacing = 10 };
        content.Children.Add(stateText);
        content.Children.Add(buttons);
        content.Children.Add(new TextBlock
        {
            Text = AppLocalization.Get("SitesSchedulerTasks"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        content.Children.Add(tasksBox);
        content.Children.Add(new TextBlock
        {
            Text = AppLocalization.Get("SitesSchedulerOutput"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        content.Children.Add(outputBox);
        using var cancellation = new CancellationTokenSource();
        var loading = false;
        var schedulerActionRunning = false;
        var loadTask = Task.CompletedTask;

        void RefreshProcessState()
        {
            var state = siteProcesses.State(site.Path, SiteBackgroundProcessKind.Scheduler);
            startStopButton.Content = AppLocalization.Get(
                state.Running ? "SitesStopScheduler" : "SitesStartScheduler"
            );
            stateText.Text = state.Running
                ? AppLocalization.Format(
                    "SitesSchedulerRunningSince",
                    state.StartedAt?.ToLocalTime().ToString("g") ?? string.Empty
                )
                : AppLocalization.Get("SitesSchedulerStopped");
            outputBox.Text = string.IsNullOrWhiteSpace(state.Output)
                ? AppLocalization.Get("SitesNoCommandOutput")
                : state.Output;
        }

        async Task LoadTasksAsync()
        {
            if (loading) return;
            loading = true;
            refreshButton.IsEnabled = false;
            progress.IsActive = true;
            try
            {
                var cycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
                var php = phpInstaller.PhpExecutable(cycle);
                await runtimePolicy.PrepareLaunchAsync(php, cycle, cancellation.Token);
                var result = await ArtisanCommandRunner.RunAsync(
                    php,
                    site.Path,
                    ["schedule:list", "--no-ansi", "--no-interaction"],
                    composerTools.ManagedEnvironment(cycle),
                    TimeSpan.FromMinutes(2),
                    cancellationToken: cancellation.Token
                );
                tasksBox.Text = string.IsNullOrWhiteSpace(result.Output)
                    ? AppLocalization.Get("SitesNoCommandOutput")
                    : result.Output;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception error) when (error is IOException or InvalidDataException
                or InvalidOperationException or ArgumentException or TimeoutException)
            {
                tasksBox.Text = error.Message;
            }
            finally
            {
                loading = false;
                refreshButton.IsEnabled = true;
                progress.IsActive = false;
            }
        }

        startStopButton.Click += async (_, _) =>
        {
            schedulerActionRunning = true;
            startStopButton.IsEnabled = false;
            try
            {
                var state = siteProcesses.State(site.Path, SiteBackgroundProcessKind.Scheduler);
                if (state.Running)
                {
                    await siteProcesses.StopAsync(site.Path, SiteBackgroundProcessKind.Scheduler);
                }
                else
                {
                    var cycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
                    await phpInstaller.EnsureManagedConfigurationAsync(cycle, cancellation.Token);
                    siteProcesses.Start(
                        site.Path,
                        SiteBackgroundProcessKind.Scheduler,
                        phpInstaller.PhpExecutable(cycle),
                        composerTools.ManagedEnvironment(cycle)
                    );
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException
                or InvalidOperationException or ArgumentException)
            {
                outputBox.Text = error.Message;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            finally
            {
                schedulerActionRunning = false;
                startStopButton.IsEnabled = true;
                RefreshProcessState();
            }
        };
        refreshButton.Click += async (_, _) =>
        {
            loadTask = LoadTasksAsync();
            await loadTask;
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => RefreshProcessState();
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesSchedulerTitle", site.Name),
            Content = content,
            CloseButtonText = AppLocalization.Get("SitesClose")
        };
        dialog.Closing += (_, args) =>
        {
            if (schedulerActionRunning) args.Cancel = true;
        };
        dialog.Opened += async (_, _) =>
        {
            RefreshProcessState();
            StartVisibleTimer(timer);
            loadTask = LoadTasksAsync();
            await loadTask;
        };
        await dialog.ShowAsync();
        StopVisibleTimer(timer);
        cancellation.Cancel();
        await loadTask;
        UpdateBackgroundProcessState();
    }

    private async void BackgroundOutput_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        var development = siteProcesses.State(site.Path, SiteBackgroundProcessKind.Development);
        await ShowCommandResultAsync(
            AppLocalization.Get("SitesBackgroundOutputTitle"),
            development.Output,
            true
        );
    }

    private StackPanel CreateCommandFavoritesRow(
        SiteRecord site,
        string tool,
        Func<string?> currentCommand,
        Action<string> applyCommand
    )
    {
        var favoriteBox = new ComboBox
        {
            Header = AppLocalization.Get("SitesCommandFavorites"),
            PlaceholderText = AppLocalization.Get("SitesCommandFavoritesEmpty"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 320
        };
        var saveButton = new Button
        {
            Content = new SymbolIcon(Symbol.Favorite),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        var deleteButton = new Button
        {
            Content = new SymbolIcon(Symbol.Delete),
            VerticalAlignment = VerticalAlignment.Bottom,
            IsEnabled = false
        };
        ToolTipService.SetToolTip(
            saveButton, AppLocalization.Get("SitesCommandFavoriteSaveTooltip")
        );
        ToolTipService.SetToolTip(
            deleteButton, AppLocalization.Get("SitesCommandFavoriteDeleteTooltip")
        );

        void Refresh(string? select = null)
        {
            var commands = commandFavorites.Load(site.Path, tool)
                .Select(item => item.Command)
                .ToArray();
            favoriteBox.ItemsSource = commands;
            favoriteBox.SelectedItem = select is null
                ? null
                : commands.FirstOrDefault(command => command.Equals(
                    select, StringComparison.OrdinalIgnoreCase
                ));
            deleteButton.IsEnabled = favoriteBox.SelectedItem is string;
        }

        favoriteBox.SelectionChanged += (_, _) =>
        {
            deleteButton.IsEnabled = favoriteBox.SelectedItem is string;
            if (favoriteBox.SelectedItem is string command) applyCommand(command);
        };
        saveButton.Click += async (_, _) =>
        {
            var command = currentCommand()?.Trim();
            if (string.IsNullOrWhiteSpace(command))
            {
                await ShowErrorAsync(AppLocalization.Get("SitesCommandFavoriteMissing"));
                return;
            }
            try
            {
                commandFavorites.Add(site.Path, tool, command);
                Refresh(command);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or InvalidDataException or ArgumentException)
            {
                await ShowErrorAsync(error.Message);
            }
        };
        deleteButton.Click += async (_, _) =>
        {
            if (favoriteBox.SelectedItem is not string command) return;
            try
            {
                commandFavorites.Remove(site.Path, tool, command);
                Refresh();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or InvalidDataException or ArgumentException)
            {
                await ShowErrorAsync(error.Message);
            }
        };
        try
        {
            Refresh();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException)
        {
            favoriteBox.IsEnabled = false;
            saveButton.IsEnabled = false;
            deleteButton.IsEnabled = false;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(favoriteBox);
        row.Children.Add(saveButton);
        row.Children.Add(deleteButton);
        return row;
    }

    private async void Composer_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        var options = ComposerCommandRunner.Presets.Select(preset => new DisplayOption(
            preset.Id,
            AppLocalization.Get("SitesComposerPreset" + preset.Id.Replace("-", string.Empty))
        )).Append(new DisplayOption("require", AppLocalization.Get("SitesComposerPresetrequire")))
            .ToArray();
        var commandBox = new ComboBox
        {
            Header = AppLocalization.Get("SitesComposerCommandField"),
            ItemsSource = options,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var packageBox = new TextBox
        {
            Header = AppLocalization.Get("SitesComposerPackageField"),
            PlaceholderText = "vendor/package",
            Visibility = Visibility.Collapsed
        };
        commandBox.SelectionChanged += (_, _) =>
        {
            packageBox.Visibility = commandBox.SelectedItem is DisplayOption { Value: "require" }
                ? Visibility.Visible
                : Visibility.Collapsed;
        };
        var favorites = CreateCommandFavoritesRow(
            site,
            "composer",
            () => commandBox.SelectedItem is not DisplayOption selection
                ? null
                : selection.Value == "require"
                    ? string.IsNullOrWhiteSpace(packageBox.Text)
                        ? null
                        : $"require {packageBox.Text.Trim()}"
                    : selection.Value,
            command =>
            {
                var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var option = options.FirstOrDefault(item => item.Value == parts[0]);
                if (option is null) return;
                commandBox.SelectedItem = option;
                packageBox.Text = parts.Length == 2 ? parts[1] : string.Empty;
            }
        );
        var content = new StackPanel { Width = 430, Spacing = 10 };
        content.Children.Add(favorites);
        content.Children.Add(commandBox);
        content.Children.Add(packageBox);
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesComposerDialogTitle", site.Name),
            Content = content,
            PrimaryButtonText = AppLocalization.Get("SitesRun"),
            CloseButtonText = AppLocalization.Get("SitesCancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || commandBox.SelectedItem is not DisplayOption selection) return;

        IReadOnlyList<string> arguments;
        try
        {
            arguments = selection.Value == "require"
                ? ComposerCommandRunner.RequireArguments(packageBox.Text)
                : ComposerCommandRunner.Presets.Single(item => item.Id == selection.Value).Arguments;
        }
        catch (ArgumentException error)
        {
            await ShowErrorAsync(error.Message);
            return;
        }
        await RunSiteOperationAsync(
            AppLocalization.Get("SitesComposerRunning"),
            async (progress, cancellationToken) =>
            {
                var cycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
                await phpInstaller.EnsureManagedConfigurationAsync(cycle, cancellationToken);
                var result = await ComposerCommandRunner.RunAsync(
                    phpInstaller.PhpExecutable(cycle),
                    composerTools.ComposerPath,
                    site.Path,
                    arguments,
                    composerTools.ManagedEnvironment(cycle),
                    progress,
                    cancellationToken
                );
                await ShowCommandResultAsync(
                    AppLocalization.Get("SitesComposerResultTitle"),
                    result.Output,
                    result.ExitCode == 0
                );
            }
        );
    }

    private async void Performance_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var recent = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 340,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(recent, ScrollBarVisibility.Auto);
        var resetButton = new Button { Content = AppLocalization.Get("SitesPerformanceReset") };
        var content = new StackPanel { Width = 500, Spacing = 12 };
        content.Children.Add(summary);
        content.Children.Add(resetButton);
        content.Children.Add(recent);

        void RefreshPerformance()
        {
            var snapshot = environment.Performance(site.Domain);
            var lastRequest = snapshot.LastRequestAt?.ToLocalTime().ToString("g")
                ?? AppLocalization.Get("SitesPerformanceNever");
            summary.Text = AppLocalization.Format(
                "SitesPerformanceReport",
                snapshot.RequestCount,
                snapshot.ActiveRequests,
                snapshot.ServerErrorCount,
                snapshot.AverageDuration.TotalMilliseconds,
                snapshot.SlowestDuration.TotalMilliseconds,
                lastRequest
            );
            recent.Text = snapshot.RecentRequests.Count == 0
                ? AppLocalization.Get("SitesPerformanceNoRequests")
                : string.Join(Environment.NewLine, snapshot.RecentRequests.Select(request =>
                    $"{request.Timestamp.ToLocalTime():HH:mm:ss}  {request.StatusCode}  "
                    + $"{request.Duration.TotalMilliseconds,8:0.0} ms  {request.Method} {request.Target}"
                ));
            UpdatePerformanceDetails(site);
        }

        resetButton.Click += (_, _) =>
        {
            environment.ResetPerformance(site.Domain);
            RefreshPerformance();
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => RefreshPerformance();
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesPerformanceTitle", site.Name),
            Content = content,
            CloseButtonText = AppLocalization.Get("SitesClose")
        };
        dialog.Opened += (_, _) =>
        {
            RefreshPerformance();
            StartVisibleTimer(timer);
        };
        await dialog.ShowAsync();
        StopVisibleTimer(timer);
    }
}
