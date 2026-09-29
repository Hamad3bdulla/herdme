namespace HerdMe.Windows.Services;

public enum ServiceGroupKind
{
    Database,
    CacheAndQueue,
    Search,
    Storage,
    Other
}

/// <summary>
/// How the Services page groups and describes a service: its section in the list (Database,
/// Cache &amp; Queue, Search, Storage) and the vendor's official documentation page. Pure data so
/// the contract tests can check it without WinUI.
/// </summary>
public static class ServiceDirectory
{
    public static ServiceGroupKind GroupOf(string definitionId) => definitionId.ToLowerInvariant() switch
    {
        "mysql" or "mariadb" or "postgresql" or "mongodb" => ServiceGroupKind.Database,
        "redis" or "valkey" => ServiceGroupKind.CacheAndQueue,
        "meilisearch" or "typesense" => ServiceGroupKind.Search,
        "minio" or "rustfs" => ServiceGroupKind.Storage,
        _ => ServiceGroupKind.Other
    };

    public static string GroupTitleKey(ServiceGroupKind kind) => "ServicesGroup" + kind;

    public static string GroupTitleFallback(ServiceGroupKind kind) => kind switch
    {
        ServiceGroupKind.Database => "Database",
        ServiceGroupKind.CacheAndQueue => "Cache & Queue",
        ServiceGroupKind.Search => "Search",
        ServiceGroupKind.Storage => "Storage",
        _ => "Other"
    };

    // Official vendor documentation only; opened in the default browser on request.
    public static Uri? DocumentationUri(string definitionId)
    {
        var address = definitionId.ToLowerInvariant() switch
        {
            "mysql" => "https://dev.mysql.com/doc/",
            "mariadb" => "https://mariadb.com/docs/",
            "postgresql" => "https://www.postgresql.org/docs/",
            "mongodb" => "https://www.mongodb.com/docs/manual/",
            "redis" => "https://redis.io/docs/latest/",
            "valkey" => "https://valkey.io/docs/",
            "meilisearch" => "https://www.meilisearch.com/docs",
            "typesense" => "https://typesense.org/docs/",
            "minio" => "https://min.io/docs/minio/windows/index.html",
            "rustfs" => "https://docs.rustfs.com/",
            _ => null
        };
        return address is null ? null : new Uri(address, UriKind.Absolute);
    }

    // Stable order for the sections, then services by name inside a section.
    public static IReadOnlyList<(ServiceGroupKind Kind, IReadOnlyList<T> Items)> Group<T>(
        IEnumerable<T> items,
        Func<T, string> definitionId,
        Func<T, string> name
    )
    {
        return items
            .GroupBy(item => GroupOf(definitionId(item)))
            .OrderBy(group => (int)group.Key)
            .Select(group => (group.Key, (IReadOnlyList<T>)group
                .OrderBy(item => name(item), StringComparer.CurrentCultureIgnoreCase)
                .ToList()))
            .ToList();
    }

    // The environment block shown in the details pane: secrets masked, one KEY=value per line.
    // Copying uses the real values.
    // The mask is shown bare (not quoted like a real value would be).
    public static string MaskedEnvironment(IReadOnlyList<ServiceEnvironmentVariable> variables) =>
        string.Concat(variables.Select(variable => EnvironmentEditorModel.IsSecret(variable.Key, variable.Value)
            ? variable.Key + "=" + EnvironmentEditorModel.MaskText + "\n"
            : ServiceEnvironmentFile.FormatLines([variable], "\n")));
}
