using System.Text.Json.Serialization;

namespace HerdMe.Windows.Models;

public sealed class DoctorResponse
{
    [JsonPropertyName("platform")]
    public string Platform { get; init; } = "windows";

    [JsonPropertyName("supportPath")]
    public string SupportPath { get; init; } = string.Empty;

    [JsonPropertyName("runtimes")]
    public List<RuntimeCheck> Runtimes { get; init; } = [];
}

public sealed class RuntimeCheck
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("available")]
    public bool Available { get; init; }

    [JsonPropertyName("detected")]
    public bool Detected { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    public string Status => Available ? "Available" : Detected ? "Unavailable" : "Missing";
}

public sealed class PhpExtensionReport
{
    [JsonPropertyName("required")]
    public List<string> Required { get; init; } = [];

    [JsonPropertyName("loaded")]
    public List<string> Loaded { get; init; } = [];

    [JsonPropertyName("missing")]
    public List<string> Missing { get; init; } = [];

    [JsonPropertyName("compatible")]
    public bool Compatible { get; init; }
}

public sealed class SitesResponse
{
    [JsonPropertyName("sites")]
    public List<SiteRecord> Sites { get; init; } = [];
}

public sealed class SiteRecord
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("domain")]
    public string Domain { get; init; } = string.Empty;

    [JsonPropertyName("framework")]
    public string Framework { get; init; } = string.Empty;

    [JsonPropertyName("linked")]
    public bool Linked { get; init; }

    [JsonPropertyName("phpVersion")]
    public string? PhpVersion { get; init; }

    [JsonPropertyName("nodeVersion")]
    public string? NodeVersion { get; init; }

    public string Runtime => PhpVersion is not null
        ? $"PHP {PhpVersion}"
        : NodeVersion is not null ? $"Node {NodeVersion}" : "Default";
}
