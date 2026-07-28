namespace HerdMe.Windows.Models;

public sealed record ManagedServiceDefinition(
    string Id,
    string Name,
    string Category,
    int DefaultPort,
    string VersionChannel,
    bool IsInstallable = true,
    string? UnavailableReason = null
);

public static class ManagedServiceCatalog
{
    public static IReadOnlyList<ManagedServiceDefinition> All { get; } =
        RuntimeCatalog.Services.Select(service => new ManagedServiceDefinition(
            service.Id,
            service.Name,
            CategoryTitle(service.Category),
            service.DefaultPort,
            service.Windows.VersionLabel,
            service.Windows.Installable,
            service.Windows.UnavailableReason
        )).ToList();

    public static ManagedServiceDefinition Get(string id)
    {
        return All.FirstOrDefault(definition =>
            definition.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
        ) ?? throw new ArgumentOutOfRangeException(nameof(id), id, "Unsupported managed service.");
    }

    private static string CategoryTitle(string category) => category switch
    {
        "database" => "Database",
        "cache" => "Cache",
        "search" => "Search",
        "storage" => "Storage",
        "realtime" => "Realtime",
        _ => category
    };
}

public sealed class ManagedServiceInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string DefinitionId { get; set; } = "mariadb";

    public string Name { get; set; } = "MariaDB";

    public int Port { get; set; } = 3_306;

    public bool StartAutomatically { get; set; }
}

public enum ManagedServiceState
{
    NotInstalled,
    Stopped,
    Running
}

public sealed class ManagedServiceRow
{
    public required Guid Id { get; init; }

    public required string DefinitionId { get; init; }

    public required string Name { get; init; }

    public required int Port { get; init; }

    public required string Version { get; init; }

    public required ManagedServiceState State { get; init; }

    public required bool StartAutomatically { get; init; }

    public required bool IsUpdateAvailable { get; init; }

    public int? ConsolePort { get; init; }

    public string? ConnectionDisplay { get; init; }

    public required string Status { get; init; }

    public required string InstallLabel { get; init; }

    public bool CanInstallOrUpdate => State == ManagedServiceState.NotInstalled
        || State == ManagedServiceState.Stopped && IsUpdateAvailable;

    public required string ToggleLabel { get; init; }

    public bool CanToggle => State != ManagedServiceState.NotInstalled;

    public bool CanOpenConsole => State == ManagedServiceState.Running
        && (DefinitionId is "minio" or "rustfs")
        && ConsolePort is > 0;

    public bool CanOpenInTablePlus => State == ManagedServiceState.Running
        && DefinitionId is
            "mysql" or "mariadb" or "postgresql" or "mongodb" or "redis" or "valkey";

    public string Subtitle => ConnectionDisplay ?? DefinitionId;
}

public enum ServicePackageChecksumAlgorithm
{
    Sha256,
    Md5
}

public sealed record ServicePackageRelease(
    string DefinitionId,
    string Version,
    string FileName,
    ServicePackageChecksumAlgorithm ChecksumAlgorithm,
    string Checksum,
    Uri DownloadUri,
    bool IsZipArchive
);

public sealed record ServiceLaunchSpec(
    string Executable,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment
);
