using H.NotifyIcon;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace HerdMe.Windows;

public partial class App : Application
{
    public static MainWindow MainWindow { get; private set; } = null!;
    private readonly AppServices services = null!;
    private readonly SingleInstanceCoordinator singleInstance = null!;
    private TaskbarIcon? trayIcon;
    private volatile bool exitRequested;
    private int backgroundServicesStarted;
    private int reportingUnhandledError;

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
        var acceptanceRun = Environment.GetCommandLineArgs().Contains(
            "--acceptance",
            StringComparer.OrdinalIgnoreCase
        );
        MainWindow = new MainWindow(services, skipOnboarding: acceptanceRun);
        MainWindow.InitialSetupCompleted += (_, _) => _ = StartBackgroundServicesOnceAsync();
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
        if (!MainWindow.RequiresOnboarding) _ = StartBackgroundServicesOnceAsync();
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
            Label = AppLocalization.Get("TrayOpenCommand.Label"),
            IconSource = new SymbolIconSource { Symbol = Symbol.OpenPane }
        };
        openCommand.ExecuteRequested += (_, _) =>
        {
            MainWindow.AppWindow.Show();
            MainWindow.Activate();
        };
        var quitCommand = new XamlUICommand
        {
            Label = AppLocalization.Get("TrayQuitCommand.Label"),
            IconSource = new SymbolIconSource { Symbol = Symbol.ClosePane }
        };
        quitCommand.ExecuteRequested += QuitCommand_ExecuteRequested;
        var startCommand = new XamlUICommand
        {
            Label = AppLocalization.Get("TrayStartAllCommand.Label"),
            IconSource = new SymbolIconSource { Symbol = Symbol.Play }
        };
        startCommand.ExecuteRequested += StartCommand_ExecuteRequested;
        var stopCommand = new XamlUICommand
        {
            Label = AppLocalization.Get("TrayStopAllCommand.Label"),
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
            ContextMenuMode = ContextMenuMode.SecondWindow,
            LeftClickCommand = openCommand,
            NoLeftClickDelay = true,
            IconSource = new GeneratedIconSource
            {
                Text = "H",
                Foreground = new SolidColorBrush(
                    global::Windows.UI.Color.FromArgb(255, 227, 27, 35)
                )
            },
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
        await services.Environment.StopAsync();
        await services.Services.StopAllAsync();
        MainWindow.Close();
        singleInstance.Dispose();
    }

    private async void StartCommand_ExecuteRequested(object? sender, ExecuteRequestedEventArgs args)
    {
        var settings = services.SiteSettings.Load();
        settings.StartAutomatically = true;
        services.SiteSettings.Save(settings);
        await StartConfiguredEnvironmentAsync();
        await services.Services.StartEnabledAsync();
    }

    private async void StopCommand_ExecuteRequested(object? sender, ExecuteRequestedEventArgs args)
    {
        var settings = services.SiteSettings.Load();
        settings.StartAutomatically = false;
        services.SiteSettings.Save(settings);
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
        await StartAndLogAsync("mail capture", () => services.Mail.StartAsync());
        await StartAndLogAsync("dump capture", () => services.Dumps.StartAsync());
        await StartAndLogAsync("managed services", () => services.Services.StartEnabledAsync());
        await StartConfiguredEnvironmentAsync();
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
