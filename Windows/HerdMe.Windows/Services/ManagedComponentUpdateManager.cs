using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

// Security: the upstream release index marks a release between the installed and the latest
// version as a security fix (only where the index says so; today Node.js).
public sealed record ManagedComponentUpdate(
    string Id,
    string Name,
    string InstalledVersion,
    string LatestVersion,
    string PageTag,
    bool Security = false
);

public sealed record ManagedComponentUpdateFailure(string Component, Exception Error);

// One installed component, listed under "Up to date" on the Updates page.
public sealed record InstalledComponent(string Id, string Name, string Version, string PageTag);

public sealed record ManagedComponentUpdateCheck(
    IReadOnlyList<ManagedComponentUpdate> Updates,
    IReadOnlyList<ManagedComponentUpdateFailure> Failures,
    DateTimeOffset CheckedAt
);

internal sealed record ManagedComponentUpdateProbe(
    string Name,
    Func<CancellationToken, Task<IReadOnlyList<ManagedComponentUpdate>>> Check
);

public sealed class ManagedComponentUpdateManager
{
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(20);
    private readonly object checkSync = new();
    private readonly IReadOnlyList<ManagedComponentUpdateProbe> probes;
    private readonly PhpRuntimeInstaller? phpInstaller;
    private readonly PhpRuntimePolicy? phpPolicy;
    private readonly NodeRuntimeInstaller? nodeInstaller;
    private readonly ComposerToolManager? composerTools;
    private readonly GitRuntimeInstaller? gitInstaller;
    private readonly XdebugManager? xdebugManager;
    private readonly WindowsServiceManager? serviceManager;
    private readonly TimeSpan probeTimeout;
    private Task<ManagedComponentUpdateCheck>? activeCheck;

    public ManagedComponentUpdateManager(
        PhpRuntimeInstaller phpInstaller,
        PhpRuntimePolicy phpPolicy,
        NodeRuntimeInstaller nodeInstaller,
        ComposerToolManager composerTools,
        GitRuntimeInstaller gitInstaller,
        XdebugManager xdebugManager,
        WindowsServiceManager serviceManager
    )
    {
        this.phpInstaller = phpInstaller;
        this.phpPolicy = phpPolicy;
        this.nodeInstaller = nodeInstaller;
        this.composerTools = composerTools;
        this.gitInstaller = gitInstaller;
        this.xdebugManager = xdebugManager;
        this.serviceManager = serviceManager;
        probeTimeout = DefaultProbeTimeout;
        List<ManagedComponentUpdateProbe> configuredProbes =
        [
            new("PHP", CheckPhpAsync),
            new("Node.js and npm", CheckNodeAsync),
            new("Composer", CheckComposerAsync),
            new("Laravel Installer", CheckLaravelInstallerAsync),
            new("Git", CheckGitAsync),
            new("Xdebug", CheckXdebugAsync)
        ];
        configuredProbes.AddRange(ManagedServiceCatalog.All
            .Where(definition => definition.IsInstallable)
            .Select(definition => new ManagedComponentUpdateProbe(
                definition.Name,
                cancellationToken => CheckServiceAsync(definition, cancellationToken)
            )));
        probes = configuredProbes;
    }

    internal ManagedComponentUpdateManager(params ManagedComponentUpdateProbe[] probes)
        : this(DefaultProbeTimeout, probes)
    {
    }

    internal ManagedComponentUpdateManager(
        TimeSpan probeTimeout,
        params ManagedComponentUpdateProbe[] probes
    )
    {
        this.probes = probes;
        this.probeTimeout = probeTimeout;
    }

    public ManagedComponentUpdateCheck? LatestResult { get; private set; }

    public Task<ManagedComponentUpdateCheck> CheckAsync(
        CancellationToken cancellationToken = default
    )
    {
        Task<ManagedComponentUpdateCheck> check;
        lock (checkSync)
        {
            if (activeCheck is null || activeCheck.IsCompleted)
            {
                activeCheck = CheckCoreAsync();
            }
            check = activeCheck;
        }
        return cancellationToken.CanBeCanceled
            ? check.WaitAsync(cancellationToken)
            : check;
    }

    public ManagedComponentUpdate? LatestUpdate(string id)
    {
        return LatestResult?.Updates.FirstOrDefault(update =>
            update.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
        );
    }

    // Everything HerdMe manages that is installed, with the version on disk. Reads local files
    // and asks the installed tools for their version; nothing goes to the network.
    public async Task<IReadOnlyList<InstalledComponent>> InstalledComponentsAsync(
        CancellationToken cancellationToken = default
    )
    {
        var components = new List<InstalledComponent>();
        if (phpInstaller is null) return components;
        var cycles = phpInstaller.InstalledCycles()
            .Where(PhpRuntimeInstaller.IsSupportedCycle)
            .ToArray();
        foreach (var cycle in cycles)
        {
            if (phpInstaller.InstalledVersion(cycle) is { } version)
                components.Add(new($"php:{cycle}", $"PHP {cycle}", version, "php"));
        }
        foreach (var version in nodeInstaller?.InstalledVersions() ?? [])
        {
            var major = version.Split('.', 2)[0];
            if (components.Any(item => item.Id == $"node:{major}")) continue;
            components.Add(new($"node:{major}", $"Node.js {major} / npm", version, "node"));
        }
        if (gitInstaller?.InstalledVersion() is { } git)
            components.Add(new("git", "Git", git, "general"));
        if (serviceManager is not null)
        {
            var configured = serviceManager.LoadInstances()
                .Select(instance => instance.DefinitionId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in ManagedServiceCatalog.All.Where(item => item.IsInstallable))
            {
                if (!configured.Contains(definition.Id) || !serviceManager.IsInstalled(definition.Id)) continue;
                components.Add(new(
                    $"service:{definition.Id}",
                    definition.Name,
                    serviceManager.InstalledVersion(definition.Id),
                    "services"
                ));
            }
        }
        var defaultCycle = phpPolicy?.Load().PhpCycle;
        if (defaultCycle is not null && phpInstaller.IsInstalled(defaultCycle))
        {
            await AddToolAsync("composer", "Composer", "php",
                token => composerTools!.ComposerVersionAsync(defaultCycle, token));
            await AddToolAsync("laravel-installer", "Laravel Installer", "php",
                token => composerTools!.LaravelInstallerVersionAsync(defaultCycle, token));
            await AddToolAsync($"xdebug:{defaultCycle}", $"Xdebug (PHP {defaultCycle})", "debugger",
                async token => (await xdebugManager!.InstalledAsync(
                    phpInstaller.PhpExecutable(defaultCycle), defaultCycle, token))?.Version);
        }
        return components
            .OrderBy(item => PageOrder(item.PageTag))
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        async Task AddToolAsync(string id, string name, string pageTag, Func<CancellationToken, Task<string?>> read)
        {
            if (composerTools is null || xdebugManager is null) return;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                if (await read(timeout.Token) is { Length: > 0 } version)
                    components.Add(new(id, name, version, pageTag));
            }
            catch (Exception error) when (error is not OutOfMemoryException
                && !cancellationToken.IsCancellationRequested)
            {
                // A tool that cannot report its version is left out of the list.
                System.Diagnostics.Debug.WriteLine($"{name} version unavailable: {error.Message}");
            }
        }
    }

    private async Task<ManagedComponentUpdateCheck> CheckCoreAsync()
    {
        var outcomes = await Task.WhenAll(probes.Select(RunProbeAsync));
        var result = new ManagedComponentUpdateCheck(
            outcomes
                .SelectMany(outcome => outcome.Updates)
                .GroupBy(update => update.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(update => PageOrder(update.PageTag))
                .ThenBy(update => update.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            outcomes
                .Where(outcome => outcome.Failure is not null)
                .Select(outcome => outcome.Failure!)
                .ToList(),
            DateTimeOffset.UtcNow
        );
        LatestResult = result;
        return result;
    }

    private async Task<ProbeOutcome> RunProbeAsync(ManagedComponentUpdateProbe probe)
    {
        try
        {
            using var timeout = new CancellationTokenSource(probeTimeout);
            return new ProbeOutcome(
                await probe.Check(timeout.Token).WaitAsync(timeout.Token),
                null
            );
        }
        catch (Exception error)
        {
            return new ProbeOutcome([], new ManagedComponentUpdateFailure(probe.Name, error));
        }
    }

    private async Task<IReadOnlyList<ManagedComponentUpdate>> CheckPhpAsync(
        CancellationToken cancellationToken
    )
    {
        var installer = phpInstaller!;
        var cycles = installer.InstalledCycles()
            .Where(PhpRuntimeInstaller.IsSupportedCycle)
            .ToArray();
        if (cycles.Length == 0) return [];

        var latestVersions = await installer.ResolveLatestVersionsAsync(
            cycles,
            cancellationToken
        );
        return cycles.Select(cycle => CreateUpdate(
                $"php:{cycle}",
                $"PHP {cycle}",
                installer.InstalledVersion(cycle),
                latestVersions.GetValueOrDefault(cycle),
                "php"
            ))
            .OfType<ManagedComponentUpdate>()
            .ToList();
    }

    private async Task<IReadOnlyList<ManagedComponentUpdate>> CheckNodeAsync(
        CancellationToken cancellationToken
    )
    {
        var installer = nodeInstaller!;
        var installed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var major in installer.InstalledVersions()
            .Select(version => version.Split('.', 2)[0])
            .Distinct(StringComparer.Ordinal))
        {
            if (installer.InstalledVersion(major) is { } version) installed[major] = version;
        }
        if (installed.Count == 0) return [];

        var latest = await installer.ResolveLatestReleasesAsync(installed, cancellationToken);
        return installed.Select(item => CreateUpdate(
                $"node:{item.Key}",
                $"Node.js {item.Key} / npm",
                item.Value,
                latest.TryGetValue(item.Key, out var release) ? release.Version : null,
                "node"
            ) is { } update
                ? update with { Security = latest[item.Key].Security }
                : null)
            .OfType<ManagedComponentUpdate>()
            .ToList();
    }

    private async Task<IReadOnlyList<ManagedComponentUpdate>> CheckComposerAsync(
        CancellationToken cancellationToken
    )
    {
        var cycle = phpPolicy!.Load().PhpCycle;
        if (!phpInstaller!.IsInstalled(cycle)) return [];

        var installed = await composerTools!.ComposerVersionAsync(cycle, cancellationToken);
        if (installed is null) return [];
        var release = await composerTools.ResolveComposerReleaseAsync(cancellationToken);
        return CreateUpdate(
            "composer",
            "Composer",
            installed,
            release.Version,
            "php"
        ) is { } update
            ? [update]
            : [];
    }

    private async Task<IReadOnlyList<ManagedComponentUpdate>> CheckLaravelInstallerAsync(
        CancellationToken cancellationToken
    )
    {
        var cycle = phpPolicy!.Load().PhpCycle;
        if (!phpInstaller!.IsInstalled(cycle)) return [];

        var installed = await composerTools!.LaravelInstallerVersionAsync(
            cycle,
            cancellationToken
        );
        if (installed is null) return [];
        var latest = await composerTools.LatestLaravelInstallerVersionAsync(cancellationToken);
        return CreateUpdate(
            "laravel-installer",
            "Laravel Installer",
            installed,
            latest,
            "php"
        ) is { } update
            ? [update]
            : [];
    }

    private async Task<IReadOnlyList<ManagedComponentUpdate>> CheckGitAsync(
        CancellationToken cancellationToken
    )
    {
        var installed = gitInstaller!.InstalledVersion();
        if (installed is null) return [];
        var release = await gitInstaller.ResolveReleaseAsync(cancellationToken);
        return CreateUpdate("git", "Git", installed, release.Version, "general") is { } update
            ? [update]
            : [];
    }

    private async Task<IReadOnlyList<ManagedComponentUpdate>> CheckXdebugAsync(
        CancellationToken cancellationToken
    )
    {
        var cycle = phpPolicy!.Load().PhpCycle;
        if (!phpInstaller!.IsInstalled(cycle)) return [];
        var php = phpInstaller.PhpExecutable(cycle);
        var installed = await xdebugManager!.InstalledAsync(php, cycle, cancellationToken);
        if (installed is null) return [];
        XdebugWindowsRelease release;
        try
        {
            release = await xdebugManager.ResolveReleaseAsync(php, cancellationToken);
        }
        catch (XdebugBuildUnavailableException)
        {
            // Xdebug publishes new releases before every PHP line has a Windows build; the
            // installed build stays current until one appears.
            return [];
        }
        return CreateUpdate(
            $"xdebug:{cycle}",
            $"Xdebug (PHP {cycle})",
            installed.Version,
            release.Version,
            "debugger"
        ) is { } update
            ? [update]
            : [];
    }

    private async Task<IReadOnlyList<ManagedComponentUpdate>> CheckServiceAsync(
        ManagedServiceDefinition definition,
        CancellationToken cancellationToken
    )
    {
        var manager = serviceManager!;
        var configured = manager.LoadInstances().Any(instance =>
            instance.DefinitionId.Equals(definition.Id, StringComparison.OrdinalIgnoreCase)
        );
        if (!configured || !manager.IsInstalled(definition.Id)) return [];
        var release = await manager.ResolveReleaseAsync(definition.Id, cancellationToken);
        return CreateUpdate(
            $"service:{definition.Id}",
            definition.Name,
            manager.InstalledVersion(definition.Id),
            release.Version,
            "services"
        ) is { } update
            ? [update]
            : [];
    }

    private static ManagedComponentUpdate? CreateUpdate(
        string id,
        string name,
        string? installedVersion,
        string? latestVersion,
        string pageTag
    )
    {
        return !string.IsNullOrWhiteSpace(installedVersion)
            && !string.IsNullOrWhiteSpace(latestVersion)
            && RuntimeVersionComparison.IsNewer(latestVersion, installedVersion)
                ? new ManagedComponentUpdate(
                    id,
                    name,
                    installedVersion,
                    latestVersion,
                    pageTag
                )
                : null;
    }

    private static int PageOrder(string pageTag) => pageTag switch
    {
        "php" => 0,
        "node" => 1,
        "services" => 2,
        "debugger" => 3,
        _ => 4
    };

    private sealed record ProbeOutcome(
        IReadOnlyList<ManagedComponentUpdate> Updates,
        ManagedComponentUpdateFailure? Failure
    );
}
