using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed class WindowsLocalEnvironment : IAsyncDisposable
{
    private sealed record PreparedPhpLaunch(
        string PhpCgiExecutable,
        PhpRuntimeLaunchContract Contract
    );

    private readonly CoreClient coreClient;
    private readonly PhpRuntimeInstaller runtimeInstaller;
    private readonly PhpRuntimePolicy runtimePolicy;
    private readonly Dictionary<string, PhpFastCgiProcess> phpProcesses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SiteDevelopmentServer> developmentServers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LocalHttpSiteServer httpServer = new();
    private readonly LocalHttpSiteServer httpsServer = new();
    private readonly WindowsCertificateManager certificateManager;
    private readonly WindowsHostsManager hostsManager;
    private readonly XdebugManager xdebugManager;
    private readonly NodeRuntimeInstaller nodeInstaller;
    private readonly ProxySiteStore? proxyStore;
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly object healthMonitorLock = new();
    private readonly object disposalSync = new();
    private volatile IReadOnlyList<PhpFastCgiProcess> phpProcessSnapshot = [];
    private volatile IReadOnlyList<SiteRecord> configuredSites = [];
    private volatile IReadOnlyList<ProxySite> activeProxies = [];
    private volatile bool proxiesConfigured;
    private volatile string proxyTld = "test";
    private string? activeConfigurationKey;
    private CancellationTokenSource? healthMonitorCancellation;
    private Task? healthMonitorTask;
    private int recoveryEnabled;
    private int resumeRecoveryRequested;
    private int disposalRequested;
    private Task? disposalTask;

    public WindowsLocalEnvironment(
        CoreClient? coreClient = null,
        PhpRuntimeInstaller? runtimeInstaller = null,
        PhpRuntimePolicy? runtimePolicy = null,
        WindowsCertificateManager? certificateManager = null,
        WindowsHostsManager? hostsManager = null,
        XdebugManager? xdebugManager = null,
        NodeRuntimeInstaller? nodeInstaller = null,
        ProxySiteStore? proxyStore = null
    )
    {
        this.proxyStore = proxyStore;
        this.coreClient = coreClient ?? new CoreClient();
        this.runtimeInstaller = runtimeInstaller ?? new PhpRuntimeInstaller(this.coreClient);
        this.runtimePolicy = runtimePolicy ?? new PhpRuntimePolicy(this.coreClient);
        this.certificateManager = certificateManager ?? new WindowsCertificateManager();
        this.hostsManager = hostsManager ?? new WindowsHostsManager();
        this.xdebugManager = xdebugManager ?? new XdebugManager();
        this.nodeInstaller = nodeInstaller ?? new NodeRuntimeInstaller();
    }

    public bool IsRunning => (phpProcessSnapshot.Count > 0 || activeProxies.Count > 0)
        && phpProcessSnapshot.All(process => process.IsRunning)
        && httpServer.IsRunning
        && httpsServer.IsRunning;

    public bool IsDegraded => Volatile.Read(ref recoveryEnabled) == 1
        && (configuredSites.Count > 0 || proxiesConfigured)
        && !IsRunning;

    // The PHP lines the running sites use. Updating any other line leaves the sites running.
    public IReadOnlyList<string> PhpCyclesInUse()
    {
        if (!IsRunning && !IsDegraded) return [];
        var fallback = runtimePolicy.Load().PhpCycle;
        return configuredSites
            .Select(site => site.PhpVersion ?? fallback)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public int RunningSiteCount => IsRunning || IsDegraded ? configuredSites.Count : 0;

    // How many running sites use this PHP line (the Updates page names them before it stops them).
    public int SitesUsingPhp(string cycle)
    {
        if (!IsRunning && !IsDegraded) return 0;
        var fallback = runtimePolicy.Load().PhpCycle;
        return configuredSites.Count(site => string.Equals(site.PhpVersion ?? fallback, cycle, StringComparison.Ordinal));
    }

    /// <summary>Proxy sites currently routed by the HTTP and HTTPS listeners.</summary>
    public IReadOnlyList<ProxySite> ActiveProxySites => activeProxies;

    public int? HttpPort => httpServer.Port;

    public int? HttpsPort => httpsServer.Port;

    public void RequestResumeRecovery()
    {
        // A listening socket/process can survive sleep without remaining usable.
        // Let the existing monitor serialize and retry the restart, including
        // when Windows sends more than one resume notification.
        if (Volatile.Read(ref disposalRequested) == 0
            && Volatile.Read(ref recoveryEnabled) == 1)
            Interlocked.Exchange(ref resumeRecoveryRequested, 1);
    }

    public SitePerformanceSnapshot Performance(string domain)
    {
        return MergePerformance(httpServer.Performance(domain), httpsServer.Performance(domain));
    }

    public void ResetPerformance(string domain)
    {
        httpServer.ResetPerformance(domain);
        httpsServer.ResetPerformance(domain);
    }

    public async Task StartConfiguredAsync(
        SiteConfigurationStore store,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposing();
        cancellationToken.ThrowIfCancellationRequested();
        var settings = store.Load();
        ProxyTld = settings.Tld;
        if (!settings.StartAutomatically)
        {
            store.UpdateStartAutomatically(true);
            settings.StartAutomatically = true;
        }
        var sites = await coreClient.ScanAsync(
            settings.Roots,
            settings.Tld,
            settings.LinkedSites,
            cancellationToken
        );
        if (sites.Count == 0 && LoadProxies(sites).Count == 0) return;
        await StartAsync(sites, cancellationToken);
    }

    internal static SitePerformanceSnapshot MergePerformance(
        SitePerformanceSnapshot http,
        SitePerformanceSnapshot https
    )
    {
        var requestCount = http.RequestCount + https.RequestCount;
        var averageTicks = requestCount == 0
            ? 0
            : (long)(
                ((decimal)http.AverageDuration.Ticks * http.RequestCount
                    + (decimal)https.AverageDuration.Ticks * https.RequestCount)
                / requestCount
            );
        return new SitePerformanceSnapshot(
            requestCount,
            http.ServerErrorCount + https.ServerErrorCount,
            http.ActiveRequests + https.ActiveRequests,
            TimeSpan.FromTicks(averageTicks),
            http.SlowestDuration >= https.SlowestDuration
                ? http.SlowestDuration
                : https.SlowestDuration,
            LatestRequest(http.LastRequestAt, https.LastRequestAt),
            http.RecentRequests.Concat(https.RecentRequests)
                .OrderByDescending(request => request.Timestamp)
                .Take(50)
                .ToArray()
        );
    }

    private static DateTimeOffset? LatestRequest(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return left >= right ? left : right;
    }

    public async Task<int> StartAsync(
        IEnumerable<SiteRecord> sites,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposing();
        var siteList = sites.ToArray();
        if (siteList.Length == 0 && LoadProxies(siteList).Count == 0)
        {
            throw new InvalidOperationException("Scan at least one site before starting.");
        }
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposing();
            var settings = runtimePolicy.Load();
            var configurationKey = ConfigurationKey(siteList, settings.PhpCycle)
                + ProxyConfigurationKey(LoadProxies(siteList));
            if (IsRunning
                && HttpPort is not null
                && activeConfigurationKey == configurationKey
                && DevelopmentSitesHealthy(siteList))
            {
                return HttpPort.Value;
            }
            var launches = await PreparePhpLaunchesAsync(siteList, settings, cancellationToken);
            configuredSites = siteList;
            await StopCoreAsync();
            var httpPort = await StartCoreAsync(
                siteList,
                settings,
                launches,
                cancellationToken
            );
            Volatile.Write(ref recoveryEnabled, 1);
            Interlocked.Exchange(ref resumeRecoveryRequested, 0);
            EnsureHealthMonitorStarted();
            return httpPort;
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task SynchronizeSitesAsync(
        IEnumerable<SiteRecord> sites,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposing();
        var siteList = sites.ToArray();
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposing();
            if (siteList.Length == 0 && LoadProxies(siteList).Count == 0)
            {
                Volatile.Write(ref recoveryEnabled, 0);
                configuredSites = [];
                Interlocked.Exchange(ref resumeRecoveryRequested, 0);
                await StopCoreAsync();
                return;
            }
            configuredSites = siteList;
            Volatile.Write(ref recoveryEnabled, 1);
            EnsureHealthMonitorStarted();
            var settings = runtimePolicy.Load();
            var configurationKey = ConfigurationKey(siteList, settings.PhpCycle)
                + ProxyConfigurationKey(LoadProxies(siteList));
            if (IsRunning && activeConfigurationKey == configurationKey
                && DevelopmentSitesHealthy(siteList))
            {
                return;
            }
            var launches = await PreparePhpLaunchesAsync(siteList, settings, cancellationToken);
            await StopCoreAsync();
            await StartCoreAsync(siteList, settings, launches, cancellationToken);
            Interlocked.Exchange(ref resumeRecoveryRequested, 0);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task StopAsync()
    {
        Volatile.Write(ref recoveryEnabled, 0);
        configuredSites = [];
        proxiesConfigured = false;
        await operationLock.WaitAsync();
        try
        {
            // A StartAsync that already owned the lock may have re-enabled
            // recovery and created a new monitor while StopAsync was waiting.
            Volatile.Write(ref recoveryEnabled, 0);
            configuredSites = [];
            proxiesConfigured = false;
            Interlocked.Exchange(ref resumeRecoveryRequested, 0);
            await CancelHealthMonitorAsync();
            await StopCoreAsync();
        }
        finally
        {
            operationLock.Release();
        }
        Stopped?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after the user stopped local sites; public shares close with them.</summary>
    public event EventHandler? Stopped;

    public ValueTask DisposeAsync()
    {
        lock (disposalSync)
        {
            Volatile.Write(ref disposalRequested, 1);
            return new ValueTask(disposalTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await StopAsync();
        // Pending UI continuations must still be able to acquire the gate,
        // observe disposal, and release it without racing SemaphoreSlim.Dispose.
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposing() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalRequested) != 0, this);

    private async Task StopCoreAsync()
    {
        phpProcessSnapshot = [];
        activeProxies = [];
        activeConfigurationKey = null;
        await httpsServer.StopAsync();
        await httpServer.StopAsync();
        foreach (var process in phpProcesses.Values) await process.StopAsync();
        phpProcesses.Clear();
        foreach (var server in developmentServers.Values) await server.StopAsync();
        developmentServers.Clear();
    }

    private async Task<int> StartCoreAsync(
        IReadOnlyList<SiteRecord> siteList,
        PhpRuntimeSettings settings,
        IReadOnlyDictionary<string, PreparedPhpLaunch> launches,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var ports = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (cycle, launch) in launches.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                var process = new PhpFastCgiProcess();
                var cycleSites = siteList
                    .Where(site => (site.PhpVersion ?? settings.PhpCycle) == cycle)
                    .ToDictionary(
                        site => site.Domain.Trim().TrimEnd('.').ToLowerInvariant(),
                        site => site.Path,
                        StringComparer.OrdinalIgnoreCase
                    );
                ports[cycle] = await process.StartAsync(
                    launch.PhpCgiExecutable,
                    launch.Contract,
                    cycleSites,
                    cancellationToken
                );
                phpProcesses[cycle] = process;
            }
            phpProcessSnapshot = phpProcesses.Values.ToArray();
            var developmentPorts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var site in siteList.Where(SiteDevelopmentServer.IsDevelopmentSite))
            {
                var server = new SiteDevelopmentServer();
                try
                {
                    var developmentPort = await server.StartAsync(
                        site,
                        nodeInstaller,
                        cancellationToken
                    );
                    if (server.ProxiesSiteTraffic)
                        developmentPorts[site.Path] = developmentPort;
                    developmentServers[site.Path] = server;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    await server.DisposeAsync();
                    await DiagnosticLog.WriteFailureAsync(
                        "development-server",
                        "automatic-start",
                        $"The development server for {site.Name} could not start automatically.",
                        error.ToString(),
                        deduplicationScope: site.Path,
                        context: new Dictionary<string, string?>
                        {
                            ["site"] = site.Name,
                            ["path"] = site.Path
                        }
                    );
                }
            }
            var proxies = LoadProxies(siteList);
            var proxyDomains = proxies.Select(proxy => proxy.Domain(proxyTld)).ToArray();
            await hostsManager.EnsureMappingsAsync(
                siteList.Select(site => site.Domain).Concat(proxyDomains),
                cancellationToken
            );
            var definitions = siteList
                .Select(site => new LocalSiteDefinition(
                    site.Domain,
                    site.Path,
                    ports[site.PhpVersion ?? settings.PhpCycle],
                    phpProcesses[site.PhpVersion ?? settings.PhpCycle].UsesHttpFallback,
                    developmentPorts.GetValueOrDefault(site.Path) is var developmentPort
                        && developmentPort > 0 ? developmentPort : null
                ))
                .Concat(proxies.Select(proxy => new LocalSiteDefinition(
                    proxy.Domain(proxyTld),
                    string.Empty,
                    DevelopmentServerPort: proxy.Port
                )))
                .ToList();
            var httpPort = await httpServer.StartAsync(
                definitions,
                phpFastCgiPort: ports.Values.FirstOrDefault(),
                fallbackPort: null,
                cancellationToken: cancellationToken
            );
            var certificate = certificateManager.PrepareServerCertificate(
                siteList.Select(site => site.Domain).Concat(proxyDomains)
            );
            await httpsServer.StartAsync(
                definitions,
                phpFastCgiPort: ports.Values.FirstOrDefault(),
                preferredPort: 443,
                fallbackPort: null,
                serverCertificate: certificate,
                cancellationToken: cancellationToken
            );
            activeProxies = proxies;
            activeConfigurationKey = ConfigurationKey(siteList, settings.PhpCycle)
                + ProxyConfigurationKey(proxies);
            return httpPort;
        }
        catch
        {
            await StopCoreAsync();
            throw;
        }
    }

    private async Task<IReadOnlyDictionary<string, PreparedPhpLaunch>> PreparePhpLaunchesAsync(
        IReadOnlyList<SiteRecord> siteList,
        PhpRuntimeSettings settings,
        CancellationToken cancellationToken
    )
    {
        var cycles = siteList.Select(site => site.PhpVersion ?? settings.PhpCycle)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var missingCycles = cycles.Where(cycle => !runtimeInstaller.IsInstalled(cycle)).ToArray();
        if (missingCycles.Length > 0)
        {
            throw new InvalidOperationException(
                "Install these HerdMe PHP runtimes before starting sites: "
                + string.Join(", ", missingCycles)
            );
        }
        var launches = new Dictionary<string, PreparedPhpLaunch>(StringComparer.Ordinal);
        foreach (var cycle in cycles)
        {
            await runtimeInstaller.EnsureManagedConfigurationAsync(cycle, cancellationToken);
            var php = runtimeInstaller.PhpExecutable(cycle);
            if (settings.Debugger.Enabled
                && await xdebugManager.InstalledAsync(php, cycle, cancellationToken) is null)
            {
                try
                {
                    await xdebugManager.InstallAsync(php, cancellationToken);
                }
                catch (Exception error) when (error is HttpRequestException or IOException
                    or InvalidDataException or InvalidOperationException)
                {
                    await DiagnosticLog.WriteFailureAsync(
                        "xdebug",
                        "automatic-install",
                        $"Xdebug for PHP {cycle} could not be installed automatically. Sites will start without the debugger.",
                        error.ToString(),
                        context: new Dictionary<string, string?> { ["phpCycle"] = cycle }
                    );
                }
            }
            var contract = await runtimePolicy.PrepareLaunchAsync(php, cycle, cancellationToken);
            launches[cycle] = new PreparedPhpLaunch(
                runtimeInstaller.PhpCgiExecutable(cycle),
                contract
            );
        }
        return launches;
    }

    private void EnsureHealthMonitorStarted()
    {
        lock (healthMonitorLock)
        {
            if (healthMonitorTask is { IsCompleted: false }) return;
            healthMonitorCancellation?.Dispose();
            healthMonitorCancellation = new CancellationTokenSource();
            healthMonitorTask = MonitorHealthAsync(healthMonitorCancellation.Token);
        }
    }

    private bool DevelopmentSitesHealthy(IEnumerable<SiteRecord> sites)
    {
        var expected = sites.Where(SiteDevelopmentServer.IsDevelopmentSite)
            .Select(site => Path.GetFullPath(site.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return expected.Count == developmentServers.Count
            && expected.All(path => developmentServers.TryGetValue(path, out var server)
                && server.IsRunning);
    }

    private async Task CancelHealthMonitorAsync()
    {
        CancellationTokenSource? source;
        Task? task;
        lock (healthMonitorLock)
        {
            source = healthMonitorCancellation;
            task = healthMonitorTask;
            healthMonitorCancellation = null;
            healthMonitorTask = null;
        }
        source?.Cancel();
        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException) when (source?.IsCancellationRequested == true)
            {
            }
        }
        source?.Dispose();
    }

    private async Task MonitorHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (Volatile.Read(ref recoveryEnabled) == 0
                    || (configuredSites.Count == 0 && !proxiesConfigured)
                    || (IsRunning && Volatile.Read(ref resumeRecoveryRequested) == 0))
                {
                    continue;
                }
                Exception? recoveryError = null;
                await operationLock.WaitAsync(cancellationToken);
                try
                {
                    if (Volatile.Read(ref recoveryEnabled) == 0
                        || (configuredSites.Count == 0 && !proxiesConfigured)
                        || (IsRunning && Volatile.Read(ref resumeRecoveryRequested) == 0))
                    {
                        continue;
                    }
                    var sites = configuredSites;
                    var settings = runtimePolicy.Load();
                    var launches = await PreparePhpLaunchesAsync(sites, settings, cancellationToken);
                    await StopCoreAsync();
                    await StartCoreAsync(sites, settings, launches, cancellationToken);
                    Interlocked.Exchange(ref resumeRecoveryRequested, 0);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    recoveryError = error;
                }
                finally
                {
                    operationLock.Release();
                }
                if (recoveryError is not null)
                {
                    await ApplicationDiagnostics.WriteEnvironmentRecoveryFailureAsync(recoveryError);
                    await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Lets a tunnel host name reach a local site through the loopback HTTP listener.</summary>
    public void SetShareAlias(string publicHost, string domain) => httpServer.SetShareAlias(publicHost, domain);

    public void RemoveShareAlias(string publicHost) => httpServer.RemoveShareAlias(publicHost);

    /// <summary>
    /// Applies added, edited, or removed proxy sites without waiting for the next site scan.
    /// </summary>
    public Task ReloadProxySitesAsync(string tld, CancellationToken cancellationToken = default)
    {
        ProxyTld = tld;
        return SynchronizeSitesAsync(configuredSites, cancellationToken);
    }

    /// <summary>The top-level domain used for proxy sites (the same one used for folder sites).</summary>
    public string ProxyTld
    {
        get => proxyTld;
        set
        {
            if (!string.IsNullOrWhiteSpace(value)) proxyTld = value.Trim().TrimStart('.').ToLowerInvariant();
        }
    }

    /// <summary>
    /// Proxy names that collide with a folder site are skipped so a scanned project always wins.
    /// </summary>
    private IReadOnlyList<ProxySite> LoadProxies(IEnumerable<SiteRecord> sites)
    {
        if (proxyStore is null)
        {
            proxiesConfigured = false;
            return [];
        }
        var taken = sites.Select(site => site.Domain.Trim().TrimEnd('.'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var proxies = proxyStore.Load()
            .Where(proxy => !taken.Contains(proxy.Domain(proxyTld)))
            .ToArray();
        proxiesConfigured = proxies.Length > 0;
        return proxies;
    }

    internal static string ProxyConfigurationKey(IEnumerable<ProxySite> proxies)
    {
        var entries = proxies
            .Select(proxy => proxy.Name.ToLowerInvariant() + ":" + proxy.Port.ToString(
                System.Globalization.CultureInfo.InvariantCulture))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return entries.Length == 0 ? string.Empty : "\nproxy\n" + string.Join("\n", entries);
    }

    internal static string ConfigurationKey(
        IEnumerable<SiteRecord> sites,
        string defaultPhpCycle
    )
    {
        var entries = sites.Select(site => string.Join('\0',
            site.Domain.Trim().TrimEnd('.').ToLowerInvariant(),
            Path.GetFullPath(site.Path).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            ).ToUpperInvariant(),
            site.PhpVersion ?? defaultPhpCycle
        )).Order(StringComparer.Ordinal);
        return defaultPhpCycle + "\n" + string.Join("\n", entries);
    }
}
