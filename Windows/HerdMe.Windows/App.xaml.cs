using H.NotifyIcon;
using H.NotifyIcon.Core;
using HerdMe.Windows.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Win32;

namespace HerdMe.Windows;

public partial class App : Application
{
    public static MainWindow MainWindow { get; private set; } = null!;

    // True while the main window is shown and not minimized. Pages pause polling while hidden.
    public static bool IsMainWindowVisible { get; private set; }

    // Raised on the UI thread with the new visibility when the window is hidden to the tray,
    // shown again, minimized, or restored.
    public static event EventHandler<bool>? MainWindowVisibilityChanged;

    private readonly AppServices services = null!;
    private readonly SingleInstanceCoordinator singleInstance = null!;
    private readonly ApplicationTaskLifetime backgroundTasks = new();
    private readonly TaskCompletionSource shutdownCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private TaskbarIcon? trayIcon;
    private volatile bool exitRequested;
    private int backgroundServicesStarted;
    private int automaticUpdateCheckStarted;
    private int automaticUpdatePromptStarted;
    private int reportingUnhandledError;
    private int shutdownStarted;
    private bool suppressAutomaticUpdateCheck;
    private Task<AutomaticUpdateCheck>? automaticUpdateCheck;
    private Task activationListener = Task.CompletedTask;
    private Task backgroundServicesStartup = Task.CompletedTask;

    public App()
    {
        if (WindowsHostsManager.TryRunElevatedHelper(
                Environment.GetCommandLineArgs(),
                out var helperExitCode
            ))
        {
            Environment.Exit(helperExitCode);
            return;
        }
        if (DefenderExclusion.TryRunElevatedHelper(Environment.GetCommandLineArgs(), out var defenderExitCode))
        {
            Environment.Exit(defenderExitCode);
            return;
        }
        if (DefenderExclusion.TryRunUninstallCleanup(Environment.GetCommandLineArgs()))
        {
            Environment.Exit(0);
            return;
        }
        if (TryRunUnregisterNotifications(Environment.GetCommandLineArgs()))
        {
            Environment.Exit(0);
            return;
        }
        launchRequest = ParseLaunchRequest();
        singleInstance = new SingleInstanceCoordinator(signalActivation: launchRequest is null);
        if (!singleInstance.IsPrimary)
        {
            // Jump List tasks, Explorer "Link with HerdMe" and herdme:// links forward their
            // request to the running instance; plain launches just bring it forward.
            if (launchRequest is not null && !ForwardLaunchRequest(launchRequest))
            {
                singleInstance.SignalActivation();
            }
            singleInstance.Dispose();
            Environment.Exit(0);
            return;
        }
        ServiceText.Localize = key =>
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            var value = AppLocalization.Get(key);
            return value == key ? null : value;
        };
        services = new AppServices();
        StartupSnapshot = services.StartupSnapshot;
        RecentSites = services.RecentSites;
        SeedKnownSitesFromSnapshot();
        // Before any resource lookup or XAML load, so x:Uid, ResourceLoader and FlowDirection
        // all agree on the language chosen in General.
        ApplyLanguageOverride(services.SiteSettings.Load().UiLanguage);
        InstallCrashReporter();
        InitializeComponent();
        UnhandledException += App_UnhandledException;
    }

    // Shared with the Dashboard and the Jump List refresh; one instance, so writes never race.
    internal static StartupSnapshotStore? StartupSnapshot { get; private set; }

    // Sites opened last (browser, editor, terminal, Tinker); the tray and Jump List list them first.
    internal static RecentSitesStore? RecentSites { get; private set; }

    internal static IReadOnlyList<string> RecentSitePaths => RecentSites?.Paths ?? [];

    // The tray, the quick panel and the Sites page start from the sites the last session saw;
    // the first scan replaces them a moment later. Acceptance runs always start empty.
    private static void SeedKnownSitesFromSnapshot()
    {
        if (KnownSites.Count > 0 || Environment.GetCommandLineArgs().Contains("--acceptance", StringComparer.OrdinalIgnoreCase)) return;
        if (StartupSnapshot?.Load()?.Sites is { Count: > 0 } sites) RememberKnownSites(sites);
    }

    private static void ApplyLanguageOverride(string? language)
    {
        var normalized = UiLanguageSettings.Normalize(language);
        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = normalized;
            if (normalized.Length == 0) return;
            // UI culture only: dates and numbers keep the user's Windows regional format.
            var culture = System.Globalization.CultureInfo.GetCultureInfo(normalized);
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
            System.Globalization.CultureInfo.CurrentUICulture = culture;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException
            or ArgumentException
            or System.Globalization.CultureNotFoundException)
        {
            System.Diagnostics.Debug.WriteLine($"HerdMe could not apply the language override: {error.Message}");
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        InitializeTrayIcon();
        var commandLine = Environment.GetCommandLineArgs();
        var acceptanceRun = commandLine.Contains(
            "--acceptance",
            StringComparer.OrdinalIgnoreCase
        );
        var onboardingAcceptance = commandLine.Contains(
            "--acceptance-onboarding",
            StringComparer.OrdinalIgnoreCase
        );
        var onboardingAfterReinstall = !acceptanceRun && !onboardingAcceptance
            && services.SiteSettings.ApplyOnboardingAfterReinstallRequest();
        suppressAutomaticUpdateCheck = acceptanceRun || onboardingAcceptance;
        suppressNotifications = acceptanceRun || onboardingAcceptance;
        InitializeActionNotifications();
        MainWindow = new MainWindow(
            services,
            skipOnboarding: acceptanceRun,
            forceOnboarding: onboardingAcceptance || onboardingAfterReinstall
        );
        MainWindow.InitialSetupCompleted += MainWindow_InitialSetupCompleted;
        MainWindow.Activated += MainWindow_Activated;
        MainWindow.Closed += MainWindow_Closed;
        MainWindow.VisibilityChanged += MainWindow_VisibilityChanged;
        MainWindow.AppWindow.Changed += MainWindow_AppWindowChanged;
        SystemEvents.PowerModeChanged += System_PowerModeChanged;
        SubscribeNotifications();
        StartTrayStatus();
        if (!MainWindow.RequiresOnboarding
            && (Environment.GetCommandLineArgs().Contains("--background", StringComparer.OrdinalIgnoreCase)
                || IsBackgroundLaunchRequest(launchRequest)))
        {
            MainWindow.AppWindow.Hide();
            SetMainWindowVisible(false);
        }
        else
        {
            MainWindow.Activate();
            SetMainWindowVisible(true);
        }
        activationListener = ListenForActivationAsync();
        StartCommandServer();
        if (!MainWindow.RequiresOnboarding)
        {
            _ = StartBackgroundServicesOnceAsync();
            StartAutomaticUpdateCheckOnce();
            if (launchRequest is { } request) _ = RunLaunchRequestAsync(request);
            NotifyPreviousCrash();
            RunColdStartNotificationAction();
        }
        launchRequest = null;
    }

    private void MainWindow_InitialSetupCompleted(object? sender, EventArgs args)
    {
        _ = StartBackgroundServicesOnceAsync();
        StartAutomaticUpdateCheckOnce();
        StartAutomaticUpdatePromptOnce();
    }

    private void System_PowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Resume && !exitRequested)
            services.Environment.RequestResumeRecovery();
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;
        RefreshMainWindowVisibility();
        StartAutomaticUpdateCheckOnce();
        StartAutomaticUpdatePromptOnce();
    }

    private void StartAutomaticUpdateCheckOnce()
    {
        if (suppressAutomaticUpdateCheck || MainWindow.RequiresOnboarding || exitRequested) return;
        var settings = services.SiteSettings.Load();
        if (!settings.AutomaticUpdates) return;
        if (Interlocked.Exchange(ref automaticUpdateCheckStarted, 1) != 0) return;
        automaticUpdateCheck = CheckForUpdatesInBackgroundAsync(settings.UpdateChannel);
    }

    private void StartAutomaticUpdatePromptOnce()
    {
        if (automaticUpdateCheck is null || exitRequested) return;
        if (Interlocked.Exchange(ref automaticUpdatePromptStarted, 1) != 0) return;
        _ = ShowAutomaticUpdatePromptsAsync(automaticUpdateCheck);
    }

    private async Task<AutomaticUpdateCheck> CheckForUpdatesInBackgroundAsync(string channel)
    {
        var applicationTask = CheckForApplicationUpdateAsync(channel);
        var componentsTask = CheckForManagedComponentUpdatesAsync();
        await Task.WhenAll(applicationTask, componentsTask);
        return new AutomaticUpdateCheck(
            await applicationTask,
            await componentsTask
        );
    }

    private async Task<AppUpdateRelease?> CheckForApplicationUpdateAsync(string channel)
    {
        try
        {
            var result = await services.Updates.CheckAsync(channel);
            return result.UsedBundledFallback ? null : result.AvailableRelease;
        }
        catch (Exception error)
        {
            await ApplicationDiagnostics.WriteAutomaticUpdateCheckFailureAsync(error);
            return null;
        }
    }

    private async Task<ManagedComponentUpdateCheck> CheckForManagedComponentUpdatesAsync()
    {
        try
        {
            var result = await services.ComponentUpdates.CheckAsync();
            if (result.Failures.Count > 0)
            {
                await ApplicationDiagnostics.WriteManagedComponentUpdateCheckFailureAsync(
                    result.Failures
                );
            }
            return result;
        }
        catch (Exception error)
        {
            var failure = new ManagedComponentUpdateFailure("Managed components", error);
            await ApplicationDiagnostics.WriteManagedComponentUpdateCheckFailureAsync([failure]);
            return new ManagedComponentUpdateCheck([], [failure], DateTimeOffset.UtcNow);
        }
    }

    private async Task ShowAutomaticUpdatePromptsAsync(Task<AutomaticUpdateCheck> checkTask)
    {
        var result = await checkTask;
        if (exitRequested) return;
        var xamlRoot = await WaitForMainWindowXamlRootAsync();
        if (xamlRoot is null || exitRequested) return;

        if (result.ApplicationRelease is { } release)
        {
            NotifyUpdateAvailable(release);
            await AppUpdatePrompt.ShowAsync(xamlRoot, release);
        }
        if (exitRequested || result.Components.Updates.Count == 0) return;
        var pageTag = await ManagedComponentUpdatePrompt.ShowAsync(
            xamlRoot,
            result.Components
        );
        if (pageTag is not null) MainWindow.NavigateToPage(pageTag);
    }

    private sealed record AutomaticUpdateCheck(
        AppUpdateRelease? ApplicationRelease,
        ManagedComponentUpdateCheck Components
    );

    private static async Task<XamlRoot?> WaitForMainWindowXamlRootAsync()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (MainWindow.Content is FrameworkElement { XamlRoot: { } xamlRoot })
            {
                return xamlRoot;
            }
            await Task.Delay(100);
        }
        return null;
    }

    private Task ListenForActivationAsync()
    {
        return Task.Run(() =>
        {
            while (!exitRequested)
            {
                if (!singleInstance.WaitForActivation()) return;
                if (exitRequested) return;
                if (singleInstance.ShutdownRequested)
                {
                    MainWindow.DispatcherQueue.TryEnqueue(async () => await RequestExitAsync());
                    return;
                }
                MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    if (exitRequested) return;
                    ShowMainWindow();
                });
            }
        });
    }

    private void InitializeTrayIcon()
    {
        var openCommand = new XamlUICommand
        {
            Label = AppLocalization.Get("TrayOpenCommandLabel"),
            IconSource = new SymbolIconSource { Symbol = Symbol.OpenPane }
        };
        openCommand.ExecuteRequested += (_, _) =>
        {
            if (exitRequested) return;
            ShowMainWindow();
        };
        var quitCommand = new XamlUICommand
        {
            Label = AppLocalization.Get("TrayQuitCommandLabel"),
            IconSource = new SymbolIconSource { Symbol = Symbol.ClosePane }
        };
        quitCommand.ExecuteRequested += QuitCommand_ExecuteRequested;
        var startCommand = new XamlUICommand
        {
            Label = AppLocalization.Get("TrayStartAllCommandLabel"),
            IconSource = new SymbolIconSource { Symbol = Symbol.Play }
        };
        startCommand.ExecuteRequested += StartCommand_ExecuteRequested;
        var stopCommand = new XamlUICommand
        {
            Label = AppLocalization.Get("TrayStopAllCommandLabel"),
            IconSource = new SymbolIconSource { Symbol = Symbol.Stop }
        };
        stopCommand.ExecuteRequested += StopCommand_ExecuteRequested;
        // A left click opens the quick panel (App.TrayPanel.cs); "Open HerdMe" is in it.
        var panelCommand = new XamlUICommand { Label = openCommand.Label };
        panelCommand.ExecuteRequested += (_, _) => TrayIcon_LeftClicked();

        var contextMenu = new MenuFlyout { AreOpenCloseAnimationsEnabled = false };
        contextMenu.Items.Add(new MenuFlyoutItem
        {
            Command = openCommand,
            Text = openCommand.Label
        });
        contextMenu.Items.Add(new MenuFlyoutSeparator());
        AddTrayDynamicItems(contextMenu);
        contextMenu.Items.Add(new MenuFlyoutItem
        {
            Command = startCommand,
            Text = startCommand.Label
        });
        contextMenu.Items.Add(new MenuFlyoutItem
        {
            Command = stopCommand,
            Text = stopCommand.Label
        });
        contextMenu.Items.Add(new MenuFlyoutSeparator());
        contextMenu.Items.Add(new MenuFlyoutItem
        {
            Command = quitCommand,
            Text = quitCommand.Label
        });

        trayIcon = new TaskbarIcon
        {
            Visibility = Visibility.Visible,
            ToolTipText = "HerdMe",
            ContextMenuMode = ContextMenuMode.PopupMenu,
            MenuActivation = PopupActivationMode.RightClick,
            LeftClickCommand = panelCommand,
            NoLeftClickDelay = true,
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/HerdMe.ico")),
            ContextFlyout = contextMenu
        };
        // Clicking a tray balloon runs that notification's main action (App.Notifications.cs).
        trayIcon.ForceCreate();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (exitRequested)
        {
            // Keep the dispatcher alive until asynchronous shutdown finishes.
            args.Handled = !shutdownCompletion.Task.IsCompleted;
            return;
        }
        args.Handled = true;
        MainWindow.AppWindow.Hide();
        SetMainWindowVisible(false);
    }

    private static void ShowMainWindow()
    {
        MainWindow.AppWindow.Show();
        MainWindow.Activate();
        SetMainWindowVisible(true);
    }

    private void MainWindow_VisibilityChanged(object sender, WindowVisibilityChangedEventArgs args)
    {
        if (exitRequested) return;
        RefreshMainWindowVisibility();
    }

    private void MainWindow_AppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (exitRequested) return;
        RefreshMainWindowVisibility();
    }

    private void RefreshMainWindowVisibility()
    {
        if (exitRequested || MainWindow is null) return;
        var appWindow = MainWindow.AppWindow;
        var minimized = appWindow.Presenter is OverlappedPresenter
        {
            State: OverlappedPresenterState.Minimized
        };
        SetMainWindowVisible(appWindow.IsVisible && !minimized);
    }

    private static void SetMainWindowVisible(bool visible)
    {
        if (IsMainWindowVisible == visible) return;
        IsMainWindowVisible = visible;
        MainWindowVisibilityChanged?.Invoke(null, visible);
    }

    private async void QuitCommand_ExecuteRequested(object? sender, ExecuteRequestedEventArgs args)
    {
        await RequestExitAsync();
    }

    internal async Task RequestExitAsync()
    {
        if (Interlocked.Exchange(ref shutdownStarted, 1) != 0)
        {
            await shutdownCompletion.Task;
            return;
        }
        exitRequested = true;
        singleInstance.WakeListener();
        SystemEvents.PowerModeChanged -= System_PowerModeChanged;
        UnsubscribeNotifications();
        UnregisterActionNotifications(removeRegistration: false);
        MainWindow.VisibilityChanged -= MainWindow_VisibilityChanged;
        MainWindow.AppWindow.Changed -= MainWindow_AppWindowChanged;
        // Deletes still showing Undo are carried out before the window goes away.
        await MainWindow.FlushDeferredActionsAsync();
        MainWindow.PrepareForShutdown();
        SetMainWindowVisible(false);
        StopTrayStatus();
        CloseTrayPanel();
        trayIcon?.Dispose();
        trayIcon = null;
        await StopAndLogAsync("command pipe", StopCommandServerAsync);
        await StopAndLogAsync("background operations", backgroundTasks.StopAsync);
        await StopAndLogAsync("site folder watcher", StopSiteRootWatcherAsync);
        await StopAndLogAsync("activation listener", () => activationListener);
        await StopAndLogAsync(
            "application services",
            () => services.DisposeAsync().AsTask()
        );
        shutdownCompletion.TrySetResult();
        MainWindow.Close();
        singleInstance.Dispose();
    }

    private static async Task StopAndLogAsync(string component, Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception error)
        {
            await ApplicationDiagnostics.WriteBackgroundServiceStartupFailureAsync(
                $"shutdown: {component}",
                error
            );
        }
    }

    private async void StartCommand_ExecuteRequested(object? sender, ExecuteRequestedEventArgs args)
    {
        await StartAllAsync();
    }

    private async void StopCommand_ExecuteRequested(object? sender, ExecuteRequestedEventArgs args)
    {
        await StopAllAsync();
    }

    internal async Task StartAllAsync()
    {
        await backgroundTasks.RunAsync(async cancellationToken =>
        {
            services.SiteSettings.UpdateStartAutomatically(true);
            await StartConfiguredEnvironmentAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await services.Services.StartEnabledAsync(cancellationToken);
        });
    }

    internal async Task StopAllAsync()
    {
        await backgroundTasks.RunAsync(async _ =>
        {
            services.SiteSettings.UpdateStartAutomatically(false);
            await services.Environment.StopAsync();
            await services.Services.StopAllAsync();
        });
    }

    private async Task StartConfiguredEnvironmentAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await services.Environment.StartConfiguredAsync(services.SiteSettings, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            await ApplicationDiagnostics.WriteEnvironmentStartupFailureAsync(error);
        }
    }

    private async Task StartBackgroundServicesAsync(CancellationToken cancellationToken)
    {
        // Independent listeners and processes start together off the UI thread. The
        // environment orders its own certificate, hosts, and HTTP server work internally.
        var commandLinePath = StartAndLogAsync("command-line path", () => Task.Run(() =>
        {
            services.NodeInstaller.RepairActiveCommandShims();
            HerdMeCommandShim.Ensure(services.SiteSettings.SupportRoot, AppContext.BaseDirectory);
            RepairShellIntegration();
            services.UserPath.Synchronize(
                services.ComposerTools.CommandLineDirectories(
                    services.RuntimePolicy.Load().PhpCycle
                )
            );
        }, cancellationToken), cancellationToken);
        var mailCapture = StartAndLogAsync(
            "mail capture",
            () => Task.Run(
                () => services.Mail.StartAsync(cancellationToken: cancellationToken),
                cancellationToken
            ),
            cancellationToken
        );
        var dumpCapture = StartAndLogAsync(
            "dump capture",
            () => Task.Run(
                () => services.Dumps.StartAsync(cancellationToken: cancellationToken),
                cancellationToken
            ),
            cancellationToken
        );
        var managedServices = StartAndLogAsync(
            "managed services",
            () => Task.Run(
                () => services.Services.StartEnabledAsync(cancellationToken),
                cancellationToken
            ),
            cancellationToken
        );
        var environment = Task.Run(
            () => StartConfiguredEnvironmentAsync(cancellationToken),
            cancellationToken
        );
        await Task.WhenAll(commandLinePath, mailCapture, dumpCapture, managedServices, environment);
        await StartAndLogAsync("jump list", () => RefreshJumpListAsync(cancellationToken), cancellationToken);
        await StartAndLogAsync("site folder watcher", () => Task.Run(StartSiteRootWatcher, cancellationToken), cancellationToken);
        // Tool installation rewrites PHP configuration and the user PATH, so it waits for the
        // path repair and the running environment above.
        await StartAndLogAsync(
            "command-line tools",
            () => Task.Run(
                () => services.InitialSetup.EnsureCommandLineToolsAsync(cancellationToken: cancellationToken),
                cancellationToken
            ),
            cancellationToken
        );
    }

    private Task StartBackgroundServicesOnceAsync()
    {
        if (Interlocked.Exchange(ref backgroundServicesStarted, 1) == 0)
        {
            backgroundServicesStartup = backgroundTasks.RunAsync(StartBackgroundServicesAsync);
        }
        return backgroundServicesStartup;
    }

    private static async Task StartAndLogAsync(
        string component,
        Func<Task> operation,
        CancellationToken cancellationToken
    )
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await operation();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            await LogStartupFailureAsync(component, error);
        }
    }

    private static async Task LogStartupFailureAsync(string component, Exception error)
    {
        await ApplicationDiagnostics.WriteBackgroundServiceStartupFailureAsync(component, error);
    }

    private void App_UnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs args
    )
    {
        if (!UnhandledExceptionPolicy.CanRecover(args.Exception))
        {
            // HerdMe is about to close: keep a local crash report for the next start.
            crashReporter?.TryWrite(args.Exception, writeDump: true);
            return;
        }
        args.Handled = true;
        _ = ReportUnhandledExceptionAsync(args.Exception);
    }

    private async Task ReportUnhandledExceptionAsync(Exception error)
    {
        await ApplicationDiagnostics.WriteUnhandledExceptionAsync(error);

        if (Interlocked.Exchange(ref reportingUnhandledError, 1) != 0) return;
        try
        {
            if (MainWindow?.Content is not FrameworkElement root || root.XamlRoot is null) return;
            var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                XamlRoot = root.XamlRoot,
                FlowDirection = AppLocalization.LayoutDirection,
                Title = AppLocalization.Get("AppUnhandledErrorTitle"),
                Content = new TextBlock
                {
                    Text = UnhandledExceptionPolicy.UserMessage(error),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                },
                CloseButtonText = AppLocalization.Get("CommonOk")
            };
            await dialog.ShowAsync();
        }
        catch (Exception dialogError) when (
            dialogError is InvalidOperationException or System.Runtime.InteropServices.COMException
        )
        {
        }
        finally
        {
            Interlocked.Exchange(ref reportingUnhandledError, 0);
        }
    }
}
