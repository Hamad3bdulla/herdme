using System.Text.Json.Serialization;

namespace HerdMe.Windows.Models;

public sealed class NodeRuntimeSettings
{
    public string ActiveVersion { get; set; } = string.Empty;
}

public sealed class NodeRelease
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("lts")]
    public object? Lts { get; init; }

    [JsonPropertyName("files")]
    public List<string> Files { get; init; } = [];
}

public sealed record NodeWindowsRelease(
    string Major,
    string Version,
    string ArchiveName,
    string Sha256,
    Uri DownloadUri
);

public sealed class NodeRuntimeRow
{
    public string Major { get; set; } = string.Empty;

    public string? InstalledVersion { get; set; }

    public bool IsInstalled => InstalledVersion is not null;

    public bool IsActive { get; set; }

    public bool IsUpdateAvailable { get; set; }

    public bool CanInstallOrUpdate => !IsInstalled || IsUpdateAvailable;

    public string Status { get; set; } = string.Empty;
}
