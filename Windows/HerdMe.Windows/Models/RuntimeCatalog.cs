using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HerdMe.Windows.Models;

public sealed class RuntimeCatalogService
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("defaultPort")]
    public int DefaultPort { get; init; }

    [JsonPropertyName("macOS")]
    public required RuntimeCatalogMacOSService MacOS { get; init; }

    [JsonPropertyName("windows")]
    public required RuntimeCatalogWindowsService Windows { get; init; }
}

public sealed class RuntimeCatalogMacOSService
{
    [JsonPropertyName("versionLabel")]
    public required string VersionLabel { get; init; }

    [JsonPropertyName("symbol")]
    public required string Symbol { get; init; }

    [JsonPropertyName("architectures")]
    public required IReadOnlyList<string> Architectures { get; init; }
}

public sealed class RuntimeCatalogWindowsService
{
    [JsonPropertyName("versionLabel")]
    public required string VersionLabel { get; init; }

    [JsonPropertyName("installable")]
    public bool Installable { get; init; }

    [JsonPropertyName("unavailableReason")]
    public string? UnavailableReason { get; init; }
}

public static partial class RuntimeCatalog
{
    private const string ResourceName = "HerdMe.runtime-catalog.json";
    private static readonly CatalogLoadState State = Load();

    public static string DefaultPhpCycle => State.Document?.Defaults.PhpCycle ?? "8.4";

    public static string DefaultNodeMajor => State.Document?.Defaults.NodeMajor ?? "22";

    public static IReadOnlyList<string> InstallablePhpCycles =>
        State.Document?.Php.InstallableCycles ?? [];

    public static IReadOnlyList<string> MacOSNodeMajors =>
        State.Document?.Node.MacOSMajors ?? [];

    public static IReadOnlyList<string> WindowsNodeMajors =>
        State.Document?.Node.WindowsMajors ?? [];

    public static IReadOnlyList<RuntimeCatalogService> Services =>
        State.Document?.Services ?? [];

    public static string? LoadIssue => State.Error?.Message;

    public static void ValidateData(ReadOnlySpan<byte> data)
    {
        var document = JsonSerializer.Deserialize<RuntimeCatalogDocument>(data)
            ?? throw new InvalidDataException("The HerdMe runtime catalog is empty.");
        Validate(document);
    }

    private static CatalogLoadState Load()
    {
        try
        {
            var assembly = typeof(RuntimeCatalog).Assembly;
            using var stream = assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidDataException("The bundled HerdMe runtime catalog is missing.");
            var document = JsonSerializer.Deserialize<RuntimeCatalogDocument>(stream)
                ?? throw new InvalidDataException("The bundled HerdMe runtime catalog is empty.");
            Validate(document);
            return new(document, null);
        }
        catch (Exception error)
        {
            return new(null, error);
        }
    }

    private static void Validate(RuntimeCatalogDocument document)
    {
        if (document.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"The bundled HerdMe runtime catalog uses unsupported schema version {document.SchemaVersion}."
            );
        }
        ValidateVersions(document);
        ValidateServices(document.Services);
    }

    private static void ValidateVersions(RuntimeCatalogDocument document)
    {
        if (!HasUniqueValues(document.Php.InstallableCycles)
            || document.Php.InstallableCycles.Any(cycle => !PhpCycleRegex().IsMatch(cycle)))
        {
            throw InvalidCatalog("PHP cycles must be unique major.minor values.");
        }
        if (!document.Php.InstallableCycles.Contains(document.Defaults.PhpCycle, StringComparer.Ordinal))
        {
            throw InvalidCatalog("the default PHP cycle is not installable.");
        }

        foreach (var (platform, majors) in new[]
        {
            ("macOS", document.Node.MacOSMajors),
            ("Windows", document.Node.WindowsMajors)
        })
        {
            if (!HasUniqueValues(majors)
                || majors.Any(major => !int.TryParse(major, out var value) || value < 1))
            {
                throw InvalidCatalog($"{platform} Node.js majors must be unique positive integers.");
            }
            if (!majors.Contains(document.Defaults.NodeMajor, StringComparer.Ordinal))
            {
                throw InvalidCatalog($"the default Node.js major is unavailable on {platform}.");
            }
        }
    }

    private static void ValidateServices(IReadOnlyList<RuntimeCatalogService> services)
    {
        if (services.Count == 0) throw InvalidCatalog("the service list is empty.");
        if (!HasUniqueValues(services.Select(service => service.Id).ToList()))
        {
            throw InvalidCatalog("service identifiers must be unique.");
        }

        var categories = new HashSet<string>(
            ["database", "cache", "search", "storage", "realtime"],
            StringComparer.Ordinal
        );
        var architectures = new HashSet<string>(["arm64", "x86_64"], StringComparer.Ordinal);
        foreach (var service in services)
        {
            if (!ServiceIdRegex().IsMatch(service.Id))
            {
                throw InvalidCatalog($"service identifier {service.Id} is malformed.");
            }
            if (string.IsNullOrWhiteSpace(service.Name)
                || !categories.Contains(service.Category)
                || service.DefaultPort is < 1 or > 65_535
                || string.IsNullOrWhiteSpace(service.MacOS.VersionLabel)
                || string.IsNullOrWhiteSpace(service.MacOS.Symbol)
                || !HasUniqueValues(service.MacOS.Architectures)
                || service.MacOS.Architectures.Any(architecture => !architectures.Contains(architecture))
                || string.IsNullOrWhiteSpace(service.Windows.VersionLabel))
            {
                throw InvalidCatalog($"service {service.Id} has incomplete platform metadata.");
            }

            var reason = service.Windows.UnavailableReason?.Trim();
            if (service.Windows.Installable ? reason is not null : string.IsNullOrEmpty(reason))
            {
                throw InvalidCatalog(
                    $"service {service.Id} has an inconsistent Windows availability reason."
                );
            }
        }
    }

    private static bool HasUniqueValues(IReadOnlyList<string> values) =>
        values.Count > 0 && values.Distinct(StringComparer.Ordinal).Count() == values.Count;

    private static InvalidDataException InvalidCatalog(string detail) =>
        new($"The bundled HerdMe runtime catalog is invalid: {detail}");

    [GeneratedRegex("^[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PhpCycleRegex();

    [GeneratedRegex("^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceIdRegex();

    private sealed record CatalogLoadState(RuntimeCatalogDocument? Document, Exception? Error);

    private sealed class RuntimeCatalogDocument
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("defaults")]
        public required RuntimeCatalogDefaults Defaults { get; init; }

        [JsonPropertyName("php")]
        public required RuntimeCatalogPhpPolicy Php { get; init; }

        [JsonPropertyName("node")]
        public required RuntimeCatalogNodePolicy Node { get; init; }

        [JsonPropertyName("services")]
        public required IReadOnlyList<RuntimeCatalogService> Services { get; init; }
    }

    private sealed class RuntimeCatalogDefaults
    {
        [JsonPropertyName("phpCycle")]
        public required string PhpCycle { get; init; }

        [JsonPropertyName("nodeMajor")]
        public required string NodeMajor { get; init; }
    }

    private sealed class RuntimeCatalogPhpPolicy
    {
        [JsonPropertyName("installableCycles")]
        public required IReadOnlyList<string> InstallableCycles { get; init; }
    }

    private sealed class RuntimeCatalogNodePolicy
    {
        [JsonPropertyName("macOSMajors")]
        public required IReadOnlyList<string> MacOSMajors { get; init; }

        [JsonPropertyName("windowsMajors")]
        public required IReadOnlyList<string> WindowsMajors { get; init; }
    }
}
