using System.Text;
using System.Text.RegularExpressions;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

/// <summary>
/// The per-project herdme.yml manifest: which PHP and Node.js versions, managed services, and
/// database a project expects. Teams commit it so a fresh checkout can be prepared with one
/// action. Only a strict YAML subset is accepted: top-level scalar keys and one list.
/// </summary>
public sealed record ProjectManifest(
    string? Php,
    string? Node,
    IReadOnlyList<string> Services,
    string? Database
)
{
    public bool IsEmpty => Php is null && Node is null && Services.Count == 0 && Database is null;

    /// <summary>The first listed service that can host the manifest database.</summary>
    public string? DatabaseService => Services.FirstOrDefault(
        SiteDatabaseProvisioner.SupportedDefinitions.Contains
    );
}

public sealed class ProjectManifestException : FormatException
{
    public ProjectManifestException(int line, string message)
        : base(line > 0
            ? ServiceText.Format("ManifestErrorAtLine", "herdme.yml line {0}: {1}", line, message)
            : ServiceText.Format("ManifestError", "herdme.yml: {0}", message))
    {
        Line = line;
    }

    public int Line { get; }
}

public static partial class ProjectManifestFile
{
    public const string FileName = "herdme.yml";
    public const int MaximumFileBytes = 16 * 1_024;
    public const int MaximumServices = 16;
    private static readonly string[] KnownKeys = ["php", "node", "services", "database"];

    public static string PathFor(string projectPath) =>
        Path.Combine(Path.GetFullPath(projectPath), FileName);

    public static bool Exists(string projectPath) => File.Exists(PathFor(projectPath));

    /// <summary>Loads the manifest, or returns null when the project has none.</summary>
    public static ProjectManifest? Load(string projectPath)
    {
        var path = PathFor(projectPath);
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        if (info.Length > MaximumFileBytes)
        {
            throw new ProjectManifestException(0, ServiceText.Get(
                "ManifestTooLarge",
                "the file is larger than 16 KB."
            ));
        }
        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    public static void Save(string projectPath, ProjectManifest manifest)
    {
        var root = Path.GetFullPath(projectPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var text = Serialize(manifest);
        // Round-trip before writing so HerdMe never produces a file it would reject.
        Parse(text);
        var destination = Path.Combine(root, FileName);
        var temporary = destination + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, destination, true);
    }

    public static string Serialize(ProjectManifest manifest)
    {
        var builder = new StringBuilder();
        builder.Append("# HerdMe project settings. Commit this file so teammates get the same setup.\n");
        if (manifest.Php is { } php) builder.Append("php: \"").Append(php).Append("\"\n");
        if (manifest.Node is { } node) builder.Append("node: \"").Append(node).Append("\"\n");
        if (manifest.Services.Count > 0)
        {
            builder.Append("services:\n");
            foreach (var service in manifest.Services) builder.Append("  - ").Append(service).Append('\n');
        }
        if (manifest.Database is { } database) builder.Append("database: ").Append(database).Append('\n');
        return builder.ToString();
    }

    public static ProjectManifest Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumFileBytes)
        {
            throw new ProjectManifestException(0, ServiceText.Get(
                "ManifestTooLarge",
                "the file is larger than 16 KB."
            ));
        }
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var services = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var inServices = false;
        var servicesLine = 0;
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var number = index + 1;
            var line = StripComment(lines[index].TrimEnd('\r'), number).TrimEnd();
            if (line.Length == 0) continue;
            if (line.Contains('\t'))
            {
                throw new ProjectManifestException(number, ServiceText.Get(
                    "ManifestTabs",
                    "use spaces instead of tabs."
                ));
            }
            var indented = line[0] == ' ';
            var content = line.TrimStart();
            if (indented)
            {
                if (!inServices || !content.StartsWith('-'))
                {
                    throw new ProjectManifestException(number, ServiceText.Get(
                        "ManifestUnexpectedIndent",
                        "only list items under services can be indented."
                    ));
                }
                services.Add(Scalar(content[1..].Trim(), number));
                continue;
            }
            if (content.StartsWith('-'))
            {
                if (!inServices)
                {
                    throw new ProjectManifestException(number, ServiceText.Get(
                        "ManifestUnexpectedList",
                        "list items are only allowed under services."
                    ));
                }
                services.Add(Scalar(content[1..].Trim(), number));
                continue;
            }
            inServices = false;
            var separator = content.IndexOf(':');
            if (separator <= 0)
            {
                throw new ProjectManifestException(number, ServiceText.Get(
                    "ManifestExpectedKey",
                    "expected \"key: value\"."
                ));
            }
            var key = content[..separator].Trim();
            var value = content[(separator + 1)..].Trim();
            if (!KnownKeys.Contains(key, StringComparer.Ordinal))
            {
                throw new ProjectManifestException(number, ServiceText.Format(
                    "ManifestUnknownKey",
                    "unknown setting \"{0}\". Supported settings: php, node, services, database.",
                    key.Length > 40 ? key[..40] : key
                ));
            }
            if (!seen.Add(key))
            {
                throw new ProjectManifestException(number, ServiceText.Format(
                    "ManifestDuplicateKey",
                    "\"{0}\" is listed more than once.",
                    key
                ));
            }
            if (key == "services")
            {
                servicesLine = number;
                if (value.Length == 0)
                {
                    inServices = true;
                    continue;
                }
                if (!value.StartsWith('[') || !value.EndsWith(']'))
                {
                    throw new ProjectManifestException(number, ServiceText.Get(
                        "ManifestServicesList",
                        "services must be a list, for example [mariadb, redis]."
                    ));
                }
                var inner = value[1..^1].Trim();
                if (inner.Length > 0)
                {
                    services.AddRange(inner.Split(',').Select(item => Scalar(item.Trim(), number)));
                }
                continue;
            }
            if (value.Length == 0)
            {
                throw new ProjectManifestException(number, ServiceText.Format(
                    "ManifestMissingValue",
                    "\"{0}\" needs a value.",
                    key
                ));
            }
            values[key] = Scalar(value, number);
        }

        var manifest = new ProjectManifest(
            values.GetValueOrDefault("php"),
            values.GetValueOrDefault("node")?.TrimStart('v'),
            services.Select(service => service.ToLowerInvariant()).ToArray(),
            values.GetValueOrDefault("database")
        );
        Validate(manifest, servicesLine);
        return manifest;
    }

    private static void Validate(ProjectManifest manifest, int servicesLine)
    {
        if (manifest.Php is { } php && !PhpCyclePattern().IsMatch(php))
        {
            throw new ProjectManifestException(0, ServiceText.Get(
                "ManifestInvalidPhp",
                "php must be a version such as \"8.3\"."
            ));
        }
        if (manifest.Node is { } node && !NodeVersionPattern().IsMatch(node))
        {
            throw new ProjectManifestException(0, ServiceText.Get(
                "ManifestInvalidNode",
                "node must be a version such as \"22\" or \"22.11.0\"."
            ));
        }
        if (manifest.Services.Count > MaximumServices)
        {
            throw new ProjectManifestException(servicesLine, ServiceText.Get(
                "ManifestTooManyServices",
                "too many services are listed."
            ));
        }
        var known = ManagedServiceCatalog.All.Select(definition => definition.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in manifest.Services)
        {
            if (!known.Contains(service))
            {
                throw new ProjectManifestException(servicesLine, ServiceText.Format(
                    "ManifestUnknownService",
                    "\"{0}\" is not a HerdMe service. Supported services: {1}.",
                    service.Length > 40 ? service[..40] : service,
                    string.Join(", ", known.Order(StringComparer.Ordinal))
                ));
            }
            if (!unique.Add(service))
            {
                throw new ProjectManifestException(servicesLine, ServiceText.Format(
                    "ManifestDuplicateService",
                    "\"{0}\" is listed more than once.",
                    service
                ));
            }
        }
        if (manifest.Database is { } database)
        {
            if (!SiteDatabaseProvisioner.IsValidDatabaseName(database))
            {
                throw new ProjectManifestException(0, ServiceText.Get(
                    "ManifestInvalidDatabase",
                    "database must start with a letter and use only letters, digits, and underscores (up to 63)."
                ));
            }
            if (manifest.DatabaseService is null)
            {
                throw new ProjectManifestException(0, ServiceText.Get(
                    "ManifestDatabaseNeedsService",
                    "database needs mariadb, mysql, or postgresql in services."
                ));
            }
        }
    }

    private static string StripComment(string line, int number)
    {
        char? quote = null;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quote is { } open)
            {
                if (character == open) quote = null;
                continue;
            }
            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }
            if (character == '#' && (index == 0 || char.IsWhiteSpace(line[index - 1])))
            {
                return line[..index];
            }
        }
        if (quote is not null)
        {
            throw new ProjectManifestException(number, ServiceText.Get(
                "ManifestUnclosedQuote",
                "a quoted value is not closed."
            ));
        }
        return line;
    }

    private static string Scalar(string value, int number)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1].Trim();
        }
        if (!PlainScalarPattern().IsMatch(value))
        {
            throw new ProjectManifestException(number, ServiceText.Format(
                "ManifestInvalidValue",
                "\"{0}\" is not a valid value.",
                value.Length > 40 ? value[..40] : value
            ));
        }
        return value;
    }

    /// <summary>
    /// Suggests a manifest for an existing project from its runtime pins and .env file so
    /// export starts from what the project already uses.
    /// </summary>
    public static ProjectManifest Suggest(
        string? phpPin,
        string? nodePin,
        string? environmentContents,
        IEnumerable<string> configuredServices
    )
    {
        var configured = configuredServices.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var environment = ParseEnvironment(environmentContents);
        var services = new List<string>();
        void AddIfConfigured(string id)
        {
            if (configured.Contains(id) && !services.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                services.Add(id);
            }
        }
        var connection = environment.GetValueOrDefault("DB_CONNECTION")?.ToLowerInvariant();
        switch (connection)
        {
            case "mariadb":
                AddIfConfigured("mariadb");
                break;
            case "mysql":
                AddIfConfigured("mysql");
                if (services.Count == 0) AddIfConfigured("mariadb");
                break;
            case "pgsql":
                AddIfConfigured("postgresql");
                break;
        }
        if (environment.ContainsKey("REDIS_HOST")
            || environment.GetValueOrDefault("CACHE_STORE") == "redis"
            || environment.GetValueOrDefault("QUEUE_CONNECTION") == "redis")
        {
            AddIfConfigured("redis");
            if (!services.Contains("redis")) AddIfConfigured("valkey");
        }
        if (environment.GetValueOrDefault("SCOUT_DRIVER") is { } scout)
        {
            if (scout == "meilisearch") AddIfConfigured("meilisearch");
            if (scout == "typesense") AddIfConfigured("typesense");
        }
        if (environment.GetValueOrDefault("MONGODB_URI") is not null
            || connection == "mongodb")
        {
            AddIfConfigured("mongodb");
        }
        var database = environment.GetValueOrDefault("DB_DATABASE");
        var manifestDatabase = database is not null
            && SiteDatabaseProvisioner.IsValidDatabaseName(database)
            && services.Any(SiteDatabaseProvisioner.SupportedDefinitions.Contains)
            ? database
            : null;
        return new ProjectManifest(
            phpPin is not null && PhpCyclePattern().IsMatch(phpPin) ? phpPin : null,
            nodePin?.TrimStart('v') is { } node && NodeVersionPattern().IsMatch(node) ? node : null,
            services,
            manifestDatabase
        );
    }

    private static Dictionary<string, string> ParseEnvironment(string? contents)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(contents)) return values;
        foreach (var rawLine in contents.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var value = line[(separator + 1)..].Trim().Trim('"', '\'');
            if (value.Length > 0) values[line[..separator].Trim()] = value;
        }
        return values;
    }

    [GeneratedRegex(@"^[0-9]{1,2}\.[0-9]{1,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex PhpCyclePattern();

    [GeneratedRegex(@"^[0-9]{1,3}(\.[0-9]{1,3}(\.[0-9]{1,4})?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NodeVersionPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainScalarPattern();
}
