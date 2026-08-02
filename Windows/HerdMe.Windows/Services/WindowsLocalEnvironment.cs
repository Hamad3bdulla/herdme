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
    private readonly LocalHttpSiteServer httpServer = new();
    private readonly LocalHttpSiteServer httpsServer = new();
    private readonly WindowsCertificateManager certificateManager;
    private readonly WindowsHostsManager hostsManager;
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly object healthMonitorLock = new();
    private volatile IReadOnlyList<PhpFastCgiProcess> phpProcessSnapshot = [];
    private volatile IReadOnlyList<SiteRecord> configuredSites = [];
    private string? activeConfigurationKey;
    private CancellationTokenSource? healthMonitorCancellation;
    private Task? healthMonitorTask;
    private int recoveryEnabled;

    public WindowsLocalEnvironment(
        CoreClient? coreClient = null,
        PhpRuntimeInstaller? runtimeInstaller = null,
        PhpRuntimePolicy? runtimePolicy = null,
        WindowsCertificateManager? certificateManager = null,
        WindowsHostsManager? hostsManager = null
    )
    {
        this.coreClient = coreClient ?? new CoreClient();
        this.runtimeInstaller = runtimeInstaller ?? new PhpRuntimeInstaller(this.coreClient);
        this.runtimePolicy = runtimePolicy ?? new PhpRuntimePolicy(this.coreClient);
        this.certificateManager = certificateManager ?? new WindowsCertificateManager();
        this.hostsManager = hostsManager ?? new WindowsHostsManager();
    }

    public bool IsRunning => phpProcessSnapshot.Count > 0
        && phpProcessSnapshot.All(process => process.IsRunning)
        && httpServer.IsRunning
        && httpsServer.IsRunning;

    public bool IsDegraded => Volatile.Read(ref recoveryEnabled) == 1
        && configuredSites.Count > 0
        && !IsRunning;

    public int? HttpPort => httpServer.Port;

    public int? HttpsPort => httpsServer.Port;

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
        var settings = store.Load();
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
        if (sites.Count == 0) return;
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
        var siteList = sites.ToArray();
        if (siteList.Length == 0)
        {
            throw new InvalidOperationException("Scan at least one site before starting.");
        }
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            var settings = runtimePolicy.Load();
            var configurationKey = ConfigurationKey(siteList, settings.PhpCycle);
            if (IsRunning
                && HttpPort is not null
                && activeConfigurationKey == configurationKey)
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
        var siteList = sites.ToArray();
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            if (siteList.Length == 0)
            {
                Volatile.Write(ref recoveryEnabled, 0);
                configuredSites = [];
                await StopCoreAsync();
                return;
            }
            configuredSites = siteList;
            Volatile.Write(ref recoveryEnabled, 1);
            EnsureHealthMonitorStarted();
            var settings = runtimePolicy.Load();
            var configurationKey = ConfigurationKey(siteList, settings.PhpCycle);
            if (IsRunning && activeConfigurationKey == configurationKey)
            {
                return;
            }
            var launches = await PreparePhpLaunchesAsync(siteList, settings, cancellationToken);
            await StopCoreAsync();
            await StartCoreAsync(siteList, settings, launches, cancellationToken);
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
        await operationLock.WaitAsync();
        try
        {
            // A StartAsync that already owned the lock may have re-enabled
            // recovery and created a new monitor while StopAsync was waiting.
            Volatile.Write(ref recoveryEnabled, 0);
            configuredSites = [];
            await CancelHealthMonitorAsync();
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
        phpProcessSnapshot = [];
        activeConfigurationKey = null;
        await httpsServer.StopAsync();
        await httpServer.StopAsync();
        foreach (var process in phpProcesses.Values) await process.StopAsync();
        phpProcesses.Clear();
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
                ports[cycle] = await process.StartAsync(
                    launch.PhpCgiExecutable,
                    launch.Contract,
                    cancellationToken
                );
                phpProcesses[cycle] = process;
            }
            phpProcessSnapshot = phpProcesses.Values.ToArray();
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
                fallbackPort: null,
                cancellationToken: cancellationToken
            );
            var certificate = certificateManager.PrepareServerCertificate(
                siteList.Select(site => site.Domain)
            );
            await httpsServer.StartAsync(
                definitions,
                phpFastCgiPort: ports.Values.First(),
                preferredPort: 443,
                fallbackPort: null,
                serverCertificate: certificate,
                cancellationToken: cancellationToken
            );
            activeConfigurationKey = ConfigurationKey(siteList, settings.PhpCycle);
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
                    || configuredSites.Count == 0
                    || IsRunning)
                {
                    continue;
                }
                Exception? recoveryError = null;
                await operationLock.WaitAsync(cancellationToken);
                try
                {
                    if (Volatile.Read(ref recoveryEnabled) == 0
                        || configuredSites.Count == 0
                        || IsRunning)
                    {
                        continue;
                    }
                    var sites = configuredSites;
                    var settings = runtimePolicy.Load();
                    var launches = await PreparePhpLaunchesAsync(sites, settings, cancellationToken);
                    await StopCoreAsync();
                    await StartCoreAsync(sites, settings, launches, cancellationToken);
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
