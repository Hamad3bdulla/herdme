using System.Collections.Concurrent;
using System.Text.Json;

namespace HerdMe.Windows.Services;

public sealed record SiteCommandFavorite(string Tool, string Command);

public sealed class SiteCommandFavoritesStore
{
    private sealed class FavoritesDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, List<SiteCommandFavorite>> Sites { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> FileLocks = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly string path;

    public SiteCommandFavoritesStore(string supportRoot)
    {
        path = Path.Combine(Path.GetFullPath(supportRoot), "Config", "command-favorites.json");
    }

    public IReadOnlyList<SiteCommandFavorite> Load(string sitePath, string tool)
    {
        var normalizedTool = NormalizeTool(tool);
        lock (FileLock())
        {
            var document = LoadDocument();
            return document.Sites.TryGetValue(NormalizeSite(sitePath), out var favorites)
                ? favorites.Where(item => item.Tool == normalizedTool).ToArray()
                : [];
        }
    }

    public void Add(string sitePath, string tool, string command)
    {
        var normalizedTool = NormalizeTool(tool);
        var normalizedCommand = NormalizeCommand(command);
        lock (FileLock())
        {
            var document = LoadDocument();
            var key = NormalizeSite(sitePath);
            if (!document.Sites.TryGetValue(key, out var favorites))
            {
                favorites = [];
                document.Sites[key] = favorites;
            }
            favorites.RemoveAll(item => item.Tool == normalizedTool
                && item.Command.Equals(normalizedCommand, StringComparison.OrdinalIgnoreCase));
            favorites.Insert(0, new SiteCommandFavorite(normalizedTool, normalizedCommand));
            if (favorites.Count > 60) favorites.RemoveRange(60, favorites.Count - 60);
            SaveDocument(document);
        }
    }

    public void Remove(string sitePath, string tool, string command)
    {
        var normalizedTool = NormalizeTool(tool);
        var normalizedCommand = NormalizeCommand(command);
        lock (FileLock())
        {
            var document = LoadDocument();
            var key = NormalizeSite(sitePath);
            if (!document.Sites.TryGetValue(key, out var favorites)) return;
            favorites.RemoveAll(item => item.Tool == normalizedTool
                && item.Command.Equals(normalizedCommand, StringComparison.OrdinalIgnoreCase));
            if (favorites.Count == 0) document.Sites.Remove(key);
            SaveDocument(document);
        }
    }

    private FavoritesDocument LoadDocument()
    {
        if (!File.Exists(path)) return new FavoritesDocument();
        try
        {
            var document = JsonSerializer.Deserialize<FavoritesDocument>(File.ReadAllText(path));
            if (document is null || document.Sites is null)
            {
                throw new JsonException("The command favorites document is empty.");
            }
            if (document.SchemaVersion != 1)
            {
                Preserve("unsupported");
                return new FavoritesDocument();
            }
            document.Sites = document.Sites
                .Where(pair => Path.IsPathFullyQualified(pair.Key))
                .ToDictionary(
                    pair => NormalizeSite(pair.Key),
                    pair => (pair.Value ?? [])
                        .Where(item => item is not null
                            && ValidTool(item.Tool) && ValidCommand(item.Command))
                        .DistinctBy(item => $"{item.Tool}\0{item.Command}", StringComparer.OrdinalIgnoreCase)
                        .Take(60)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase
                );
            return document;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or JsonException or ArgumentException)
        {
            Preserve("corrupt");
            return new FavoritesDocument();
        }
    }

    private void Preserve(string reason)
    {
        if (!File.Exists(path)) return;
        try
        {
            File.Move(
                path,
                Path.Combine(
                    Path.GetDirectoryName(path)!,
                    $"command-favorites.{reason}-{Guid.NewGuid():N}.json"
                )
            );
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void SaveDocument(FavoritesDocument document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporary, path, true);
    }

    private object FileLock() => FileLocks.GetOrAdd(path, _ => new object());

    private static string NormalizeSite(string value) => Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(value)
    );

    private static string NormalizeTool(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return ValidTool(normalized)
            ? normalized
            : throw new ArgumentException("Choose a supported command tool.", nameof(value));
    }

    private static string NormalizeCommand(string value)
    {
        var normalized = value.Trim();
        return ValidCommand(normalized)
            ? normalized
            : throw new ArgumentException("The favorite command is invalid.", nameof(value));
    }

    private static bool ValidTool(string value) => value is "artisan" or "composer" or "npm";

    private static bool ValidCommand(string value) => value.Length is > 0 and <= 512
        && !value.Any(char.IsControl);
}
