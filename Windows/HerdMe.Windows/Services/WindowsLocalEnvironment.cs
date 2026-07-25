using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed class WindowsLocalEnvironment : IAsyncDisposable
{
    private readonly CoreClient coreClient = new();
    private readonly PhpRuntimeInstaller runtimeInstaller;
    private readonly PhpRuntimePolicy runtimePolicy;
    private readonly Dictionary<string, PhpFastCgiProcess> phpProcesses = new(StringComparer.Ordinal);
    private readonly LocalHttpSiteServer httpServer = new();
    private readonly LocalHttpSiteServer httpsServer = new();
    private readonly WindowsCertificateManager certificateManager = new();
    private readonly WindowsHostsManager hostsManager = new();
    private readonly SemaphoreSlim operationLock = new(1, 1);

    public WindowsLocalEnvironment()
    {
        runtimeInstaller = new PhpRuntimeInstaller(coreClient);
        runtimePolicy = new PhpRuntimePolicy(coreClient);
    }

    public bool IsRunning => phpProcesses.Count > 0
        && phpProcesses.Values.All(process => process.IsRunning)
        && httpServer.IsRunning
        && httpsServer.IsRunning;

    public int? HttpPort => httpServer.Port;

    public int? HttpsPort => httpsServer.Port;

    public async Task StartConfiguredAsync(
        SiteConfigurationStore store,
        CancellationToken cancellationToken = default
    )
    {
        var settings = store.Load();
        if (!settings.StartAutomatically) return;
        var sites = await coreClient.ScanAsync(
            settings.Roots,
            settings.Tld,
            settings.LinkedSites,
            cancellationToken
        );
        if (sites.Count == 0) return;
        await StartAsync(sites, cancellationToken);
    }

    public async Task<int> StartAsync(
        IEnumerable<SiteRecord> sites,
        CancellationToken cancellationToken = default
    )
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning && HttpPort is not null) return HttpPort.Value;
            var siteList = sites.ToList();
            if (siteList.Count == 0) throw new InvalidOperationException("Scan at least one site before starting.");

            var settings = runtimePolicy.Load();
            var cycles = siteList.Select(site => site.PhpVersion ?? settings.PhpCycle)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var missingCycles = cycles.Where(cycle => !runtimeInstaller.IsInstalled(cycle)).ToList();
            if (missingCycles.Count > 0)
            {
                throw new InvalidOperationException(
                    "Install these HerdMe PHP runtimes before starting sites: "
                    + string.Join(", ", missingCycles)
                );
            }
            try
            {
                var ports = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var cycle in cycles)
                {
                    var php = runtimeInstaller.PhpExecutable(cycle);
                    var phpCgi = runtimeInstaller.PhpCgiExecutable(cycle);
                    var contract = await runtimePolicy.PrepareLaunchAsync(php, cycle, cancellationToken);
                    var process = new PhpFastCgiProcess();
                    ports[cycle] = await process.StartAsync(phpCgi, contract, cancellationToken);
                    phpProcesses[cycle] = process;
                }
                await hostsManager.EnsureMappingsAsync(
                    siteList.Select(site => site.Domain),
                    cancellationToken
                );
                var definitions = siteList
                    .Select(site => new LocalSiteDefinition(
                        site.Domain,
                        site.Path,
                        ports[site.PhpVersion ?? settings.PhpCycle]
                    ))
                    .ToList();
                var httpPort = await httpServer.StartAsync(
                    definitions,
                    phpFastCgiPort: ports.Values.First(),
                    cancellationToken: cancellationToken
                );
                var certificate = certificateManager.PrepareServerCertificate(
                    siteList.Select(site => site.Domain)
                );
                await httpsServer.StartAsync(
                    definitions,
                    phpFastCgiPort: ports.Values.First(),
                    preferredPort: 443,
                    fallbackPort: 8_443,
                    serverCertificate: certificate,
                    cancellationToken: cancellationToken
                );
                return httpPort;
            }
            catch
            {
                await StopCoreAsync();
                throw;
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task StopAsync()
    {
        await operationLock.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        operationLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task StopCoreAsync()
    {
        await httpsServer.StopAsync();
        await httpServer.StopAsync();
        foreach (var process in phpProcesses.Values) await process.StopAsync();
        phpProcesses.Clear();
    }
}
