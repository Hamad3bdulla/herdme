using System.Text;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed record ServiceEnvironmentVariable(string Key, string Value);

public sealed record ServiceEnvironmentUpdate(
    string EnvironmentPath,
    int AddedKeys,
    int UpdatedKeys,
    bool CreatedFile
);

public static class ServiceEnvironmentConfiguration
{
    public static IReadOnlyList<ServiceEnvironmentVariable> Variables(
        ManagedServiceInstance instance,
        ServiceCredentials credentials
    )
    {
        const string host = "127.0.0.1";
        var port = instance.Port.ToString();
        return instance.DefinitionId switch
        {
            "mysql" or "mariadb" => Values(
                ("DB_CONNECTION", "mysql"),
                ("DB_HOST", host),
                ("DB_PORT", port),
                ("DB_DATABASE", "laravel"),
                ("DB_USERNAME", credentials.Username),
                ("DB_PASSWORD", credentials.Secret)
            ),
            "postgresql" => Values(
                ("DB_CONNECTION", "pgsql"),
                ("DB_HOST", host),
                ("DB_PORT", port),
                ("DB_DATABASE", "postgres"),
                ("DB_USERNAME", credentials.Username),
                ("DB_PASSWORD", credentials.Secret)
            ),
            "mongodb" => Values(
                ("MONGODB_URI", $"mongodb://{host}:{port}/admin"),
                ("MONGODB_DATABASE", "admin")
            ),
            "redis" or "valkey" => Values(
                ("REDIS_CLIENT", "phpredis"),
                ("REDIS_HOST", host),
                ("REDIS_PASSWORD", "null"),
                ("REDIS_PORT", port)
            ),
            "meilisearch" => Values(
                ("MEILISEARCH_HOST", $"http://{host}:{port}"),
                ("MEILISEARCH_KEY", "")
            ),
            "typesense" => Values(
                ("SCOUT_DRIVER", "typesense"),
                ("TYPESENSE_API_KEY", credentials.Secret),
                ("TYPESENSE_SEARCH_ONLY_KEY", credentials.Secret),
                ("TYPESENSE_HOST", host),
                ("TYPESENSE_PORT", port),
                ("TYPESENSE_PROTOCOL", "http")
            ),
            "minio" or "rustfs" => Values(
                ("AWS_ACCESS_KEY_ID", credentials.Username),
                ("AWS_SECRET_ACCESS_KEY", credentials.Secret),
                ("AWS_DEFAULT_REGION", "us-east-1"),
                ("AWS_ENDPOINT", $"http://{host}:{port}"),
                ("AWS_USE_PATH_STYLE_ENDPOINT", "true")
            ),
            _ => []
        };
    }

    private static IReadOnlyList<ServiceEnvironmentVariable> Values(
        params (string Key, string Value)[] values
    )
    {
        return values.Select(value => new ServiceEnvironmentVariable(value.Key, value.Value)).ToArray();
    }
}

public static class ServiceEnvironmentFile
{
    private const long MaximumEnvironmentFileBytes = 4 * 1_024 * 1_024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static ServiceEnvironmentUpdate Update(
        string projectPath,
        ManagedServiceInstance instance,
        ServiceCredentials credentials
    )
    {
        var fullProjectPath = Path.GetFullPath(projectPath);
        if (!Directory.Exists(fullProjectPath))
        {
            throw new DirectoryNotFoundException("The selected project directory is no longer available.");
        }

        var variables = ServiceEnvironmentConfiguration.Variables(instance, credentials);
        if (variables.Count == 0)
        {
            throw new NotSupportedException($"HerdMe does not have .env variables for {instance.Name}.");
        }

        var environmentPath = Path.Combine(fullProjectPath, ".env");
        var examplePath = Path.Combine(fullProjectPath, ".env.example");
        RejectReparsePoint(environmentPath);
        var environmentExists = File.Exists(environmentPath);
        string initialContents;
        if (environmentExists)
        {
            initialContents = ReadUtf8(environmentPath);
        }
        else if (File.Exists(examplePath))
        {
            RejectReparsePoint(examplePath);
            initialContents = ReadUtf8(examplePath);
        }
        else
        {
            initialContents = string.Empty;
        }

        var merged = Merge(initialContents, variables, instance.Name);
        var temporaryPath = Path.Combine(
            fullProjectPath,
            $".env.herdme-{Guid.NewGuid():N}.tmp"
        );
        try
        {
            File.WriteAllText(temporaryPath, merged.Contents, Utf8);
            File.Move(temporaryPath, environmentPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        return new ServiceEnvironmentUpdate(
            environmentPath,
            merged.AddedKeys,
            merged.UpdatedKeys,
            !environmentExists
        );
    }

    public static (string Contents, int AddedKeys, int UpdatedKeys) Merge(
        string contents,
        IReadOnlyList<ServiceEnvironmentVariable> variables,
        string serviceName
    )
    {
        var newline = contents.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = contents.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        var values = variables.ToDictionary(variable => variable.Key, variable => variable.Value);
        var foundKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Count; index++)
        {
            var key = EnvironmentKey(lines[index]);
            if (key is null || !values.TryGetValue(key, out var value)) continue;
            lines[index] = $"{key}={Encode(value)}";
            foundKeys.Add(key);
        }

        var missing = variables.Where(variable => !foundKeys.Contains(variable.Key)).ToArray();
        if (missing.Length > 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0) lines.Add(string.Empty);
            var safeName = serviceName.Replace('\r', ' ').Replace('\n', ' ');
            if (safeName.Length > 80) safeName = safeName[..80];
            lines.Add($"# Added by HerdMe for {safeName}");
            lines.AddRange(missing.Select(variable => $"{variable.Key}={Encode(variable.Value)}"));
        }

        return (
            string.Join(newline, lines) + newline,
            missing.Length,
            foundKeys.Count
        );
    }

    private static string ReadUtf8(string path)
    {
        if (new FileInfo(path).Length > MaximumEnvironmentFileBytes)
        {
            throw new InvalidDataException(
                "The project's .env file is larger than the supported 4 MB limit."
            );
        }
        try
        {
            return Utf8.GetString(File.ReadAllBytes(path)).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("The project's .env file must be UTF-8 text.", error);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "HerdMe will not modify a symbolic .env file. Replace it with a project-owned file first."
            );
        }
        if (Directory.Exists(path))
        {
            throw new InvalidDataException("The project's .env path must be a regular file.");
        }
    }

    private static string? EnvironmentKey(string line)
    {
        var candidate = line.TrimStart();
        if (candidate.StartsWith("export ", StringComparison.Ordinal))
        {
            candidate = candidate["export ".Length..].TrimStart();
        }
        if (candidate.StartsWith('#')) return null;
        var separator = candidate.IndexOf('=');
        if (separator < 1) return null;
        var key = candidate[..separator].Trim();
        if (!(char.IsLetter(key[0]) || key[0] == '_')
            || key.Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
        {
            return null;
        }
        return key;
    }

    private static string Encode(string value)
    {
        if (value.Length == 0) return string.Empty;
        if (value.All(character => char.IsLetterOrDigit(character)
            || "_./:@+-".Contains(character)))
        {
            return value;
        }
        return "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
    }
}
