using H.NotifyIcon;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace HerdMe.Windows;

public partial class App : Application
{
    public static MainWindow MainWindow { get; private set; } = null!;
    private readonly AppServices services = null!;
    private readonly SingleInstanceCoordinator singleInstance = null!;
    private TaskbarIcon? trayIcon;
    private volatile bool exitRequested;
    private int backgroundServicesStarted;
    private int automaticUpdateCheckStarted;
    private int automaticUpdatePromptStarted;
    private int reportingUnhandledError;
    private bool suppressAutomaticUpdateCheck;
    private Task<AutomaticUpdateCheck>? automaticUpdateCheck;

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
        suppressAutomaticUpdateCheck = acceptanceRun || onboardingAcceptance;
        MainWindow = new MainWindow(
            services,
            skipOnboarding: acceptanceRun,
            forceOnboarding: onboardingAcceptance
        );
        MainWindow.InitialSetupCompleted += MainWindow_InitialSetupCompleted;
        MainWindow.Activated += MainWindow_Activated;
        MainWindow.Closed += MainWindow_Closed;
        if (!MainWindow.RequiresOnboarding
            && Environment.GetCommandLineArgs().Contains("--background", StringComparer.OrdinalIgnoreCase))
        {
            MainWindow.AppWindow.Hide();
        }
        else
        {
            MainWindow.Activate();
        }
        _ = ListenForActivationAsync();
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
            LeftClickCommand = openCommand,
            NoLeftClickDelay = true,
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/HerdMe.ico")),
            ContextFlyout = contextMenu
        };
        trayIcon.ForceCreate();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (exitRequested) return;
        args.Handled = true;
        MainWindow.AppWindow.Hide();
    }

    private async void QuitCommand_ExecuteRequested(object? sender, ExecuteRequestedEventArgs args)
    {
        exitRequested = true;
        singleInstance.WakeListener();
        trayIcon?.Dispose();
        trayIcon = null;
        await services.Dumps.StopAsync();
        await services.Mail.StopAsync();
        await services.SiteProcesses.StopAllAsync();
        await services.Environment.StopAsync();
        await services.Services.StopAllAsync();
        MainWindow.Close();
        singleInstance.Dispose();
    }

    private async void StartCommand_ExecuteRequested(object? sender, ExecuteRequestedEventArgs args)
    {
        services.SiteSettings.UpdateStartAutomatically(true);
        await StartConfiguredEnvironmentAsync();
        await services.Services.StartEnabledAsync();
    }

    private async void StopCommand_ExecuteRequested(object? sender, ExecuteRequestedEventArgs args)
    {
        services.SiteSettings.UpdateStartAutomatically(false);
        await services.Environment.StopAsync();
        await services.Services.StopAllAsync();
    }

    private async Task StartConfiguredEnvironmentAsync()
    {
        try
        {
            await services.Environment.StartConfiguredAsync(services.SiteSettings);
        }
        catch (Exception error)
        {
            await ApplicationDiagnostics.WriteEnvironmentStartupFailureAsync(error);
        }
    }

    private async Task StartBackgroundServicesAsync()
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
        });
        await StartAndLogAsync("mail capture", () => services.Mail.StartAsync());
        await StartAndLogAsync("dump capture", () => services.Dumps.StartAsync());
        await StartAndLogAsync("managed services", () => services.Services.StartEnabledAsync());
        await StartConfiguredEnvironmentAsync();
        await StartAndLogAsync(
            "command-line tools",
            () => services.InitialSetup.EnsureCommandLineToolsAsync()
        );
    }

    private Task StartBackgroundServicesOnceAsync()
    {
        return Interlocked.Exchange(ref backgroundServicesStarted, 1) == 0
            ? StartBackgroundServicesAsync()
            : Task.CompletedTask;
    }

    private static async Task StartAndLogAsync(string component, Func<Task> operation)
    {
        try
        {
            await operation();
        }
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
