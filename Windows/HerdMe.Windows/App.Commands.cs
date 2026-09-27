using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows;

// Commands from the herdme CLI, the taskbar Jump List, Explorer ("Link with HerdMe") and
// herdme:// links. Output is plain English text for the terminal; interactive requests
// report failures in the window instead.
public partial class App
{
    private AppCommandServer? commandServer;
    private AppCommandRequest? launchRequest;

    private static AppCommandRequest? ParseLaunchRequest()
    {
        var arguments = Environment.GetCommandLineArgs();
        return AppCommandProtocol.ParseLaunchArguments(arguments.Length > 1 ? arguments[1..] : []);
    }

    // Runs in a secondary instance before any window exists. Returns false when the primary
    // instance did not accept the request, so the caller falls back to plain activation.
    private static bool ForwardLaunchRequest(AppCommandRequest request)
    {
        try
        {
            AllowSetForegroundWindow(AllowAnyProcess);
            var response = Task.Run(() => AppCommandClient.SendWithRetryAsync(
                request,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)
            )).GetAwaiter().GetResult();
            return response is not null;
        }
        catch (Exception error) when (error is IOException or TimeoutException
            or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsBackgroundLaunchRequest(AppCommandRequest? request)
    {
        return request is { Command: "start" or "stop" or "open" or "status" or "sites" or "ping" };
    }

    private void StartCommandServer()
    {
        commandServer = new AppCommandServer(HandleCommandAsync);
        commandServer.Start();
    }

    private async Task StopCommandServerAsync()
    {
        if (commandServer is { } server)
        {
            commandServer = null;
            await server.DisposeAsync();
        }
    }

    private async Task RunLaunchRequestAsync(AppCommandRequest request)
    {
        try
        {
            if (request.Command is "start" or "stop") await StartBackgroundServicesOnceAsync();
            await HandleCommandAsync(request, CancellationToken.None);
        }
        catch (Exception error)
        {
            await ApplicationDiagnostics.WriteBackgroundServiceStartupFailureAsync(
                $"launch command {request.Command}",
                error
            );
        }
    }

    internal async Task<AppCommandResponse> HandleCommandAsync(
        AppCommandRequest request,
        CancellationToken cancellationToken
    )
    {
        var problem = AppCommandProtocol.Validate(request);
        if (problem is not null) return AppCommandResponse.Failure(problem, AppCommandProtocol.UsageExitCode);
        if (exitRequested) return AppCommandResponse.Failure("HerdMe is shutting down.");
        if (MainWindow is null) return AppCommandResponse.Failure("HerdMe is still starting. Try again.");
        if (MainWindow.RequiresOnboarding && request.Command is not ("ping" or "show"))
        {
            await RunOnUiAsync(ShowMainWindow);
            return AppCommandResponse.Failure("Finish the HerdMe setup in the window first.");
        }

        var arguments = request.Arguments ?? [];
        var argument = arguments.Count > 0 ? arguments[0] : null;
        var response = request.Command switch
        {
            "ping" => AppCommandResponse.Success($"HerdMe {services.Updates.CurrentVersion}"),
            "status" => AppCommandResponse.Success(await DescribeStatusAsync(cancellationToken)),
            "sites" => AppCommandResponse.Success(await DescribeSitesAsync(cancellationToken)),
            "open" => await OpenSiteAsync(argument!, cancellationToken),
            "show" => await ShowPageAsync(argument ?? "dashboard"),
            "site" => await ShowSiteAsync(argument!, null, cancellationToken),
            "share" => await ShowSiteAsync(
                argument!,
                "Confirm sharing in the HerdMe window. Sharing makes the site public until you stop it.",
                cancellationToken
            ),
            "logs" => await ShowLogsAsync(argument, cancellationToken),
            "tinker" => await ShowPageAsync("tinker"),
            "start" => await StartAllFromCommandAsync(cancellationToken),
            "stop" => await StopAllFromCommandAsync(),
            "link" => await LinkFolderAsync(argument!, request.Interactive, cancellationToken),
            "unlink" => await UnlinkAsync(argument!, cancellationToken),
            _ => AppCommandResponse.Failure("Unknown command.", AppCommandProtocol.UsageExitCode)
        };
        if (!response.Ok && request.Interactive) await ShowCommandFailureAsync(response.Output);
        return response;
    }

    private async Task<IReadOnlyList<SiteRecord>> ScanSitesAsync(CancellationToken cancellationToken)
    {
        var settings = services.SiteSettings.Load();
        var sites = await services.Core.ScanAsync(
            settings.Roots,
            settings.Tld,
            settings.LinkedSites,
            cancellationToken
        );
        foreach (var site in sites)
        {
            site.IsFavorite = settings.FavoriteSites.Contains(site.Path, StringComparer.OrdinalIgnoreCase);
        }
        return sites;
    }

    private static readonly SemaphoreSlim JumpListGate = new(1, 1);
    private static IReadOnlyList<SiteRecord>? pendingJumpListSites;

    // Rebuilds the taskbar Jump List off the UI thread. Rapid calls coalesce: only the latest
    // site list is written.
    internal static void RequestJumpListRefresh(IEnumerable<SiteRecord> sites)
    {
        var list = sites.ToList();
        RememberKnownSites(list);
        Volatile.Write(ref pendingJumpListSites, list);
        _ = Task.Run(async () =>
        {
            await JumpListGate.WaitAsync();
            try
            {
                var next = Interlocked.Exchange(ref pendingJumpListSites, null);
                if (next is null) return;
                StartupSnapshot?.SaveSites(next);
                var executable = Environment.ProcessPath
                    ?? Path.Combine(AppContext.BaseDirectory, "HerdMe.Windows.exe");
                JumpListManager.Apply(executable, next);
            }
            finally
            {
                JumpListGate.Release();
            }
        });
    }

    private async Task RefreshJumpListAsync(CancellationToken cancellationToken)
    {
        RequestJumpListRefresh(await ScanSitesAsync(cancellationToken));
    }

    private static SiteRecord? FindSite(IReadOnlyList<SiteRecord> sites, string nameOrPath)
    {
        return sites.FirstOrDefault(site =>
                string.Equals(site.Name, nameOrPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(site.Domain, nameOrPath, StringComparison.OrdinalIgnoreCase))
            ?? sites.FirstOrDefault(site => PathsEqual(site.Path, nameOrPath))
            ?? sites.FirstOrDefault(site => site.Domain.StartsWith(
                nameOrPath + ".",
                StringComparison.OrdinalIgnoreCase
            ));
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd('\\'),
                Path.GetFullPath(right).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase
            );
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private string SiteAddress(SiteRecord site)
    {
        var environment = services.Environment;
        return SitePresentation.SiteUri(
            site,
            environment.IsRunning,
            environment.HttpPort,
            environment.HttpsPort
        ).AbsoluteUri;
    }

    private async Task<string> DescribeStatusAsync(CancellationToken cancellationToken)
    {
        var environment = services.Environment;
        var builder = new StringBuilder();
        builder.AppendLine($"HerdMe {services.Updates.CurrentVersion}");
        var state = environment.IsRunning
            ? environment.HttpsPort is not null ? "running (HTTPS)" : "running (HTTP)"
            : environment.IsDegraded ? "recovering" : "stopped";
        builder.AppendLine($"Environment: {state}");
        try
        {
            var sites = await ScanSitesAsync(cancellationToken);
            builder.AppendLine($"Sites: {sites.Count}");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            builder.AppendLine($"Sites: unavailable ({error.Message})");
        }
        var instances = services.Services.LoadInstances();
        if (instances.Count == 0)
        {
            builder.AppendLine("Services: none");
        }
        else
        {
            builder.AppendLine("Services:");
            foreach (var instance in instances)
            {
                var serviceState = services.Services.State(instance.Id, instance.DefinitionId) switch
                {
                    ManagedServiceState.Running => "running",
                    ManagedServiceState.Installing => "installing",
                    ManagedServiceState.NotInstalled => "not installed",
                    _ => "stopped"
                };
                builder.AppendLine($"  {instance.Name,-20} {serviceState,-14} 127.0.0.1:{instance.Port}");
            }
        }
        return builder.ToString().TrimEnd();
    }

    private async Task<string> DescribeSitesAsync(CancellationToken cancellationToken)
    {
        var sites = await ScanSitesAsync(cancellationToken);
        if (sites.Count == 0) return "No sites yet. Run 'herdme link' in a project folder.";
        var nameWidth = Math.Min(32, sites.Max(site => site.Name.Length)) + 2;
        var rows = sites
            .OrderBy(site => site.Name, StringComparer.OrdinalIgnoreCase)
            .Select(site =>
            {
                var address = SiteAddress(site);
                return $"{site.Name.PadRight(nameWidth)}{address.PadRight(36)} {site.Path}";
            });
        return string.Join(Environment.NewLine, rows);
    }

    private async Task<AppCommandResponse> OpenSiteAsync(string name, CancellationToken cancellationToken)
    {
        var site = FindSite(await ScanSitesAsync(cancellationToken), name);
        if (site is null) return AppCommandResponse.Failure($"No site named '{name}'. Run 'herdme sites'.");
        var address = SiteAddress(site);
        Process.Start(new ProcessStartInfo(address) { UseShellExecute = true })?.Dispose();
        return AppCommandResponse.Success($"Opened {address}");
    }

    private async Task<AppCommandResponse> ShowPageAsync(string page)
    {
        await RunOnUiAsync(() =>
        {
            ShowMainWindow();
            MainWindow.NavigateToPage(page);
        });
        return AppCommandResponse.Success($"Showing {page}.");
    }

    private async Task<AppCommandResponse> ShowSiteAsync(
        string name,
        string? message,
        CancellationToken cancellationToken
    )
    {
        var site = FindSite(await ScanSitesAsync(cancellationToken), name);
        if (site is null) return AppCommandResponse.Failure($"No site named '{name}'. Run 'herdme sites'.");
        await RunOnUiAsync(() =>
        {
            ShowMainWindow();
            MainWindow.NavigateToSite(site.Path);
        });
        return AppCommandResponse.Success(message ?? $"Showing {site.Name}.");
    }

    private async Task<AppCommandResponse> ShowLogsAsync(string? name, CancellationToken cancellationToken)
    {
        if (name is null) return await ShowPageAsync("logs");
        var site = FindSite(await ScanSitesAsync(cancellationToken), name);
        if (site is null) return AppCommandResponse.Failure($"No site named '{name}'. Run 'herdme sites'.");
        await RunOnUiAsync(() =>
        {
            ShowMainWindow();
            MainWindow.NavigateToLogs(site.Path);
        });
        return AppCommandResponse.Success($"Showing logs for {site.Name}.");
    }

    private async Task<AppCommandResponse> StartAllFromCommandAsync(CancellationToken cancellationToken)
    {
        await StartAllAsync();
        return AppCommandResponse.Success(await DescribeStatusAsync(cancellationToken));
    }

    private async Task<AppCommandResponse> StopAllFromCommandAsync()
    {
        await StopAllAsync();
        return AppCommandResponse.Success("HerdMe stopped the environment and all services.");
    }

    private async Task<AppCommandResponse> LinkFolderAsync(
        string path,
        bool interactive,
        CancellationToken cancellationToken
    )
    {
        var fullPath = Path.GetFullPath(AppCommandProtocol.NormalizeFolderArgument(path));
        if (!Directory.Exists(fullPath)) return AppCommandResponse.Failure($"The folder {fullPath} does not exist.");
        if (SiteConfigurationStore.BelongsToOtherHerd(fullPath))
        {
            return AppCommandResponse.Failure(
                "That folder belongs to another Herd installation, so HerdMe will not link it."
            );
        }
        var added = services.SiteSettings.AddLinkedSite(fullPath);
        var sites = await ScanSitesAsync(cancellationToken);
        if (services.Environment.IsRunning)
        {
            services.Environment.ProxyTld = services.SiteSettings.Load().Tld;
            await services.Environment.SynchronizeSitesAsync(sites, cancellationToken);
        }
        var site = sites.FirstOrDefault(candidate => PathsEqual(candidate.Path, fullPath));
        RequestJumpListRefresh(sites);
        await RunOnUiAsync(() =>
        {
            MainWindow.NotifySitesChanged();
            if (!interactive) return;
            ShowMainWindow();
            MainWindow.NavigateToSite(fullPath);
        });
        var address = site is null ? "a new site" : SiteAddress(site);
        return AppCommandResponse.Success(added
            ? $"Linked {fullPath} as {address}"
            : $"{fullPath} is already linked ({address}).");
    }

    private async Task<AppCommandResponse> UnlinkAsync(string nameOrPath, CancellationToken cancellationToken)
    {
        var settings = services.SiteSettings.Load();
        var linked = settings.LinkedSites.FirstOrDefault(path => PathsEqual(path, nameOrPath));
        if (linked is null)
        {
            var site = FindSite(await ScanSitesAsync(cancellationToken), nameOrPath);
            if (site is { Linked: true }) linked = site.Path;
            else if (site is not null)
            {
                return AppCommandResponse.Failure(
                    $"{site.Name} is in a parked folder, not linked. Remove the folder from Sites instead."
                );
            }
        }
        if (linked is null) return AppCommandResponse.Failure($"No linked site matches '{nameOrPath}'.");
        services.SiteSettings.RemoveLinkedSite(linked);
        var remaining = await ScanSitesAsync(cancellationToken);
        RequestJumpListRefresh(remaining);
        if (services.Environment.IsRunning)
        {
            await services.Environment.SynchronizeSitesAsync(remaining, cancellationToken);
        }
        await RunOnUiAsync(() => MainWindow.NotifySitesChanged());
        return AppCommandResponse.Success($"Unlinked {linked}");
    }

    private async Task ShowCommandFailureAsync(string message)
    {
        try
        {
            await RunOnUiAsync(async () =>
            {
                ShowMainWindow();
                var xamlRoot = await WaitForMainWindowXamlRootAsync();
                if (xamlRoot is null || exitRequested) return;
                var dialog = new ContentDialog
                {
                    XamlRoot = xamlRoot,
                    FlowDirection = AppLocalization.LayoutDirection,
                    Title = AppLocalization.Get("AppCommandFailedTitle"),
                    Content = new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true
                    },
                    CloseButtonText = AppLocalization.Get("CommonOk")
                };
                await dialog.ShowAsync();
            });
        }
        catch (Exception error) when (error is InvalidOperationException or COMException)
        {
        }
    }

    private static Task RunOnUiAsync(Action action)
    {
        return RunOnUiAsync(() =>
        {
            action();
            return Task.CompletedTask;
        });
    }

    private static Task RunOnUiAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = MainWindow.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await action();
                completion.TrySetResult();
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        });
        if (!queued) completion.TrySetException(new InvalidOperationException("HerdMe is shutting down."));
        return completion.Task;
    }

    private const int AllowAnyProcess = -1;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
