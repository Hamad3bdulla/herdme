using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed record ManagedComponentUpdate(
    string Id,
    string Name,
    string InstalledVersion,
    string LatestVersion,
    string PageTag
);

public sealed record ManagedComponentUpdateFailure(string Component, Exception Error);

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
    private readonly object checkSync = new();
    private readonly IReadOnlyList<ManagedComponentUpdateProbe> probes;
    private readonly PhpRuntimeInstaller? phpInstaller;
    private readonly PhpRuntimePolicy? phpPolicy;
    private readonly NodeRuntimeInstaller? nodeInstaller;
    private readonly ComposerToolManager? composerTools;
    private readonly GitRuntimeInstaller? gitInstaller;
    private readonly XdebugManager? xdebugManager;
    private readonly WindowsServiceManager? serviceManager;
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
    {
        this.probes = probes;
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

    private static async Task<ProbeOutcome> RunProbeAsync(ManagedComponentUpdateProbe probe)
    {
        try
        {
            return new ProbeOutcome(await probe.Check(CancellationToken.None), null);
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
        var majors = installer.InstalledVersions()
            .Select(version => version.Split('.', 2)[0])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (majors.Length == 0) return [];

        var latestVersions = await installer.ResolveLatestVersionsAsync(
            majors,
            cancellationToken
        );
        return majors.Select(major => CreateUpdate(
                $"node:{major}",
                $"Node.js {major} / npm",
                installer.InstalledVersion(major),
                latestVersions.GetValueOrDefault(major),
                "node"
            ))
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
        var release = await xdebugManager.ResolveReleaseAsync(php, cancellationToken);
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
