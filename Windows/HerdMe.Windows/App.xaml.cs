using H.NotifyIcon;
using H.NotifyIcon.Core;
using HerdMe.Windows.Services;
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
        singleInstance = new SingleInstanceCoordinator();
        if (!singleInstance.IsPrimary)
        {
            singleInstance.Dispose();
            Environment.Exit(0);
            return;
        }
        services = new AppServices();
        InitializeComponent();
        UnhandledException += App_UnhandledException;
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
        MainWindow = new MainWindow(
            services,
            skipOnboarding: acceptanceRun,
            forceOnboarding: onboardingAcceptance || onboardingAfterReinstall
        );
        MainWindow.InitialSetupCompleted += MainWindow_InitialSetupCompleted;
        MainWindow.Activated += MainWindow_Activated;
        MainWindow.Closed += MainWindow_Closed;
        SystemEvents.PowerModeChanged += System_PowerModeChanged;
        if (!MainWindow.RequiresOnboarding
            && Environment.GetCommandLineArgs().Contains("--background", StringComparer.OrdinalIgnoreCase))
        {
            MainWindow.AppWindow.Hide();
        }
        else
        {
            MainWindow.Activate();
        }
        activationListener = ListenForActivationAsync();
        if (!MainWindow.RequiresOnboarding)
        {
            _ = StartBackgroundServicesOnceAsync();
            StartAutomaticUpdateCheckOnce();
        }
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
                MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    if (exitRequested) return;
                    MainWindow.AppWindow.Show();
                    MainWindow.Activate();
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
            MainWindow.AppWindow.Show();
            MainWindow.Activate();
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

        var contextMenu = new MenuFlyout { AreOpenCloseAnimationsEnabled = false };
        contextMenu.Items.Add(new MenuFlyoutItem
        {
            Command = openCommand,
            Text = openCommand.Label
        });
        contextMenu.Items.Add(new MenuFlyoutSeparator());
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
            LeftClickCommand = openCommand,
            NoLeftClickDelay = true,
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/HerdMe.ico")),
            ContextFlyout = contextMenu
        };
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
        MainWindow.PrepareForShutdown();
        trayIcon?.Dispose();
        trayIcon = null;
        await StopAndLogAsync("background operations", backgroundTasks.StopAsync);
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
        await backgroundTasks.RunAsync(async cancellationToken =>
        {
            services.SiteSettings.UpdateStartAutomatically(true);
            await StartConfiguredEnvironmentAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await services.Services.StartEnabledAsync(cancellationToken);
        });
    }

    private async void StopCommand_ExecuteRequested(object? sender, ExecuteRequestedEventArgs args)
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
        await StartAndLogAsync("command-line path", () =>
        {
            services.NodeInstaller.RepairActiveCommandShims();
            services.UserPath.Synchronize(
                services.ComposerTools.CommandLineDirectories(
                    services.RuntimePolicy.Load().PhpCycle
                )
            );
            return Task.CompletedTask;
        }, cancellationToken);
        await StartAndLogAsync(
            "mail capture",
            () => services.Mail.StartAsync(cancellationToken: cancellationToken),
            cancellationToken
        );
        await StartAndLogAsync(
            "dump capture",
            () => services.Dumps.StartAsync(cancellationToken: cancellationToken),
            cancellationToken
        );
        await StartAndLogAsync(
            "managed services",
            () => services.Services.StartEnabledAsync(cancellationToken),
            cancellationToken
        );
        await StartConfiguredEnvironmentAsync(cancellationToken);
        await StartAndLogAsync(
            "command-line tools",
            () => services.InitialSetup.EnsureCommandLineToolsAsync(cancellationToken: cancellationToken),
            cancellationToken
        );
    }

    private Task StartBackgroundServicesOnceAsync()
    {
        return Interlocked.Exchange(ref backgroundServicesStarted, 1) == 0
            ? backgroundTasks.RunAsync(StartBackgroundServicesAsync)
            : Task.CompletedTask;
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
        if (!UnhandledExceptionPolicy.CanRecover(args.Exception)) return;
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
                Title = "HerdMe could not complete the operation",
                Content = UnhandledExceptionPolicy.UserMessage(error),
                CloseButtonText = "OK"
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
