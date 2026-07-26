using H.NotifyIcon;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace HerdMe.Windows;

public partial class App : Application
{
    public static MainWindow MainWindow { get; private set; } = null!;
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
        MainWindow = new MainWindow(skipOnboarding: acceptanceRun);
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
        var openCommand = (XamlUICommand)Resources["OpenHerdMeCommand"];
        openCommand.ExecuteRequested += (_, _) =>
        {
            MainWindow.AppWindow.Show();
            MainWindow.Activate();
        };
        var quitCommand = (XamlUICommand)Resources["QuitHerdMeCommand"];
        quitCommand.ExecuteRequested += QuitCommand_ExecuteRequested;
        var startCommand = (XamlUICommand)Resources["StartAllCommand"];
        startCommand.ExecuteRequested += StartCommand_ExecuteRequested;
        var stopCommand = (XamlUICommand)Resources["StopAllCommand"];
        stopCommand.ExecuteRequested += StopCommand_ExecuteRequested;
        trayIcon = (TaskbarIcon)Resources["TrayIcon"];
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
        await AppServices.Dumps.StopAsync();
        await AppServices.Mail.StopAsync();
        await AppServices.Environment.StopAsync();
        await AppServices.Services.StopAllAsync();
        MainWindow.Close();
        singleInstance.Dispose();
    }

    private async void StartCommand_ExecuteRequested(object? sender, ExecuteRequestedEventArgs args)
    {
        var settings = AppServices.SiteSettings.Load();
        settings.StartAutomatically = true;
        AppServices.SiteSettings.Save(settings);
        await StartConfiguredEnvironmentAsync();
        await AppServices.Services.StartEnabledAsync();
    }

    private async void StopCommand_ExecuteRequested(object? sender, ExecuteRequestedEventArgs args)
    {
        var settings = AppServices.SiteSettings.Load();
        settings.StartAutomatically = false;
        AppServices.SiteSettings.Save(settings);
        await AppServices.Environment.StopAsync();
        await AppServices.Services.StopAllAsync();
    }

    private static async Task StartConfiguredEnvironmentAsync()
    {
        try
        {
            await AppServices.Environment.StartConfiguredAsync(AppServices.SiteSettings);
        }
        catch (Exception error)
        {
            try
            {
                var logDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HerdMe",
                    "Log"
                );
                await BoundedLog.AppendLineAsync(
                    Path.Combine(logDirectory, "environment.log"),
                    $"[{DateTimeOffset.Now:O}] Automatic start failed: {error.Message}"
                );
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task StartBackgroundServicesAsync()
    {
        await StartAndLogAsync("mail capture", () => AppServices.Mail.StartAsync());
        await StartAndLogAsync("dump capture", () => AppServices.Dumps.StartAsync());
        await StartAndLogAsync("managed services", () => AppServices.Services.StartEnabledAsync());
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
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HerdMe",
                "Log"
            );
            await BoundedLog.AppendLineAsync(
                Path.Combine(logDirectory, "startup.log"),
                $"[{DateTimeOffset.Now:O}] {component} failed: {error.Message}"
            );
        }
        catch (IOException)
        {
        }
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
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HerdMe",
                "Log"
            );
            await BoundedLog.AppendLineAsync(
                Path.Combine(logDirectory, "unhandled.log"),
                $"[{DateTimeOffset.Now:O}] {error}"
            );
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        {
        }

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
