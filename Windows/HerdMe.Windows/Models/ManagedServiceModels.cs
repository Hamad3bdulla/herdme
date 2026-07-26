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
    [
        new("mariadb", "MariaDB", "Database", 3_306, "11.8"),
        new("mysql", "MySQL", "Database", 3_306, "Innovation"),
        new("postgresql", "PostgreSQL", "Database", 5_432, "18"),
        new("mongodb", "MongoDB", "Database", 27_017, "8.0 LTS"),
        new("redis", "Redis", "Cache", 6_379, "Latest"),
        new(
            "valkey",
            "Valkey",
            "Cache",
            6_379,
            "Latest",
            false,
            "Valkey upstream does not currently support or publish an official native Windows x64 package."
        ),
        new("meilisearch", "Meilisearch", "Search", 7_700, "Latest"),
        new(
            "typesense",
            "Typesense",
            "Search",
            8_108,
            "Latest",
            false,
            "Typesense upstream does not currently support or publish an official native Windows x64 package."
        ),
        new("minio", "MinIO", "Storage", 9_000, "Latest"),
        new("rustfs", "RustFS", "Storage", 9_000, "Beta")
    ];

    public static ManagedServiceDefinition Get(string id)
    {
        return All.FirstOrDefault(definition =>
            definition.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
        ) ?? throw new ArgumentOutOfRangeException(nameof(id), id, "Unsupported managed service.");
    }
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

    public string Status => State switch
    {
        ManagedServiceState.NotInstalled => "Not installed",
        ManagedServiceState.Stopped => "Stopped",
        ManagedServiceState.Running => "Running",
        _ => "Unknown"
    };

    public string InstallLabel => State == ManagedServiceState.NotInstalled ? "Install" : "Update";

    public bool CanInstallOrUpdate => State == ManagedServiceState.NotInstalled
        || State == ManagedServiceState.Stopped && IsUpdateAvailable;

    public string ToggleLabel => State == ManagedServiceState.Running ? "Stop" : "Start";

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
