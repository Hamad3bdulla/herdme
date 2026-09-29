using System.ComponentModel;

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

    public bool StartAutomatically { get; set; } = true;
}

public enum ManagedServiceState
{
    NotInstalled,
    Installing,
    Stopped,
    Running
}

public sealed class ManagedServiceRow : INotifyPropertyChanged
{
    private string lastLogLine = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    // The newest line of the service log. Refreshed in place so an open card menu stays open.
    public string LastLogLine
    {
        get => lastLogLine;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(lastLogLine, next, StringComparison.Ordinal)) return;
            lastLogLine = next;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastLogLine)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasLastLogLine)));
        }
    }

    public bool HasLastLogLine => lastLogLine.Length > 0;

    public string PortText { get; set; } = string.Empty;

    // Second line of a row in the Services list: "version, Port N" (or the state when
    // the runtime is not installed yet).
    public string Summary { get; set; } = string.Empty;

    public string CardName { get; set; } = string.Empty;

    public string CopyAddressLabel { get; set; } = string.Empty;

    public bool IsRunning => State == ManagedServiceState.Running;

    public bool IsNotRunning => State != ManagedServiceState.Running;

    // Running services show their address with a copy button (a full URL with credentials
    // for databases, host:port for the rest).
    public bool CanCopyAddress => State == ManagedServiceState.Running && Port > 0;

    public string Address => CanOpenInTablePlus && ConnectionDisplay is { Length: > 0 } display
        ? display
        : $"127.0.0.1:{Port}";

    public Guid Id { get; set; }

    public string DefinitionId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Port { get; set; }

    public string Version { get; set; } = string.Empty;

    public ManagedServiceState State { get; set; }

    public bool StartAutomatically { get; set; }

    public bool IsUpdateAvailable { get; set; }

    public int? ConsolePort { get; set; }

    public string? ConnectionDisplay { get; set; }

    public string Status { get; set; } = string.Empty;

    public string InstallLabel { get; set; } = string.Empty;

    public bool CanInstallOrUpdate => State == ManagedServiceState.NotInstalled
        || State == ManagedServiceState.Stopped && IsUpdateAvailable;

    public string ToggleLabel { get; set; } = string.Empty;

    public bool CanToggle => State is ManagedServiceState.Stopped or ManagedServiceState.Running;

    public bool CanManage => State != ManagedServiceState.Installing;

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

public enum ServiceInstallationStage
{
    BackingUp,
    Resolving,
    Downloading,
    Retrying,
    Verifying,
    Extracting,
    Installing,
    Completed,
    Cancelled,
    Failed
}

public sealed record ServiceInstallationProgress(
    string DefinitionId,
    ServiceInstallationStage Stage,
    long BytesReceived = 0,
    long? TotalBytes = null,
    int Attempt = 1,
    double BytesPerSecond = 0,
    string? Error = null
)
{
    public bool IsActive => Stage is not (ServiceInstallationStage.Completed
        or ServiceInstallationStage.Cancelled or ServiceInstallationStage.Failed);

    public double? Percentage => TotalBytes is > 0
        ? Math.Clamp(100.0 * BytesReceived / TotalBytes.Value, 0, 100)
        : null;
}
