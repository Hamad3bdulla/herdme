using HerdMe.Windows.Services;

namespace HerdMe.Windows;

// Background integration that is not tied to a page: the parked-folder watcher and the
// repair of opt-in Explorer, herdme:// and Windows Terminal entries after a move or update.
public partial class App
{
    private SiteRootWatcher? siteRootWatcher;

    private void StartSiteRootWatcher()
    {
        if (exitRequested || siteRootWatcher is not null) return;
        var watcher = new SiteRootWatcher(
            () => services.SiteSettings.Load().Roots,
            OnSiteRootsChangedAsync
        );
        siteRootWatcher = watcher;
        watcher.Start();
    }

    private async Task StopSiteRootWatcherAsync()
    {
        if (siteRootWatcher is { } watcher)
        {
            siteRootWatcher = null;
            await watcher.DisposeAsync();
        }
    }

    // General > Domain suffix changed: rescan with the new suffix, move the running web server
    // and proxy sites over, refresh the Jump List and the Sites page.
    internal void ApplyDomainSuffixChange() => _ = backgroundTasks.RunAsync(OnSiteRootsChangedAsync);

    private async Task OnSiteRootsChangedAsync(CancellationToken cancellationToken)
    {
        if (exitRequested || MainWindow is null || MainWindow.RequiresOnboarding) return;
        var sites = await ScanSitesAsync(cancellationToken);
        RequestJumpListRefresh(sites);
        if (services.Environment.IsRunning)
        {
            services.Environment.ProxyTld = services.SiteSettings.Load().Tld;
            await services.Environment.SynchronizeSitesAsync(sites, cancellationToken);
        }
        if (exitRequested) return;
        await RunOnUiAsync(() => MainWindow.NotifySitesChanged());
    }

    private void RepairShellIntegration()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return;
        var startingDirectory = services.SiteSettings.Load().Roots.FirstOrDefault(Directory.Exists);
        services.ShellIntegration.RepairEnabled(executable, startingDirectory);
    }
}
