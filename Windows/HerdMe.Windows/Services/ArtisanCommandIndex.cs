using System.Text.Json;

namespace HerdMe.Windows.Services;

public sealed record ArtisanCommandInfo(string Name, string Description, string Usage);

// One autocomplete row. ToString is what the AutoSuggestBox shows; Command is what gets typed.
public sealed class ArtisanSuggestion(string command, string description, bool isRecent, string display)
{
    public string Command { get; } = command;

    public string Description { get; } = description;

    public bool IsRecent { get; } = isRecent;

    public override string ToString() => display;
}

public static class ArtisanCommandIndex
{
    public const int MaximumSuggestions = 30;

    // "php artisan list --format=json": commands[] with name, description, usage[] and hidden.
    public static IReadOnlyList<ArtisanCommandInfo> ParseCommandInfoJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("commands", out var commands)
            || commands.ValueKind != JsonValueKind.Array)
        {
            return ArtisanCommandCatalog.ParseCommandListJson(json)
                .Select(name => new ArtisanCommandInfo(name, string.Empty, string.Empty))
                .ToArray();
        }

        var result = new Dictionary<string, ArtisanCommandInfo>(StringComparer.Ordinal);
        foreach (var command in commands.EnumerateArray())
        {
            if (command.ValueKind != JsonValueKind.Object) continue;
            if (command.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True) continue;
            var name = Text(command, "name")?.Trim();
            if (string.IsNullOrEmpty(name)
                || name.Length > 128
                || name.StartsWith('-')
                || name.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            {
                continue;
            }
            var usage = string.Empty;
            if (command.TryGetProperty("usage", out var usages) && usages.ValueKind == JsonValueKind.Array)
            {
                usage = usages.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty)
                    .FirstOrDefault() ?? string.Empty;
            }
            result.TryAdd(name, new ArtisanCommandInfo(name, Clean(Text(command, "description"), 240), Clean(usage, 400)));
        }
        return result.Values.OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // The project's commands plus HerdMe's built-in list for anything the project did not report.
    public static IReadOnlyList<ArtisanCommandInfo> WithFallback(IEnumerable<ArtisanCommandInfo> discovered)
    {
        var known = discovered.ToList();
        var names = known.Select(info => info.Name).ToHashSet(StringComparer.Ordinal);
        known.AddRange(ArtisanCommandCatalog.Suggestions
            .Where(name => !names.Contains(name.Split(' ')[0]))
            .Select(name => new ArtisanCommandInfo(name, string.Empty, string.Empty)));
        return known.OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // Recent commands that match come first, then names that start with the query, then names
    // or descriptions that contain it. Only the first word of the query is matched.
    public static IReadOnlyList<ArtisanSuggestion> Suggest(
        string? query,
        IReadOnlyList<ArtisanCommandInfo> commands,
        IReadOnlyList<string> recent,
        string recentLabel,
        int maximum = MaximumSuggestions
    )
    {
        var text = (query ?? string.Empty).Trim();
        var word = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var byName = commands
            .GroupBy(info => info.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var result = new List<ArtisanSuggestion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var command in recent)
        {
            if (text.Length > 0 && !command.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(command)) continue;
            var name = command.Split(' ', 2)[0];
            var description = byName.TryGetValue(name, out var info) ? info.Description : string.Empty;
            result.Add(new ArtisanSuggestion(command, description, true, $"{command}  \u00B7  {recentLabel}"));
        }

        // Once arguments follow the command name only recent entries can still match usefully.
        if (text.Contains(' ')) return result.Take(maximum).ToArray();

        IEnumerable<ArtisanCommandInfo> ranked = word.Length == 0
            ? commands
            : commands.Where(info => info.Name.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                .Concat(commands.Where(info => !info.Name.StartsWith(word, StringComparison.OrdinalIgnoreCase)
                    && info.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
                .Concat(commands.Where(info => !info.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
                    && info.Description.Contains(word, StringComparison.OrdinalIgnoreCase)));
        foreach (var info in ranked)
        {
            if (result.Count >= maximum) break;
            if (!seen.Add(info.Name)) continue;
            var display = info.Description.Length == 0 ? info.Name : $"{info.Name}  \u2014  {info.Description}";
            result.Add(new ArtisanSuggestion(info.Name, info.Description, false, display));
        }
        return result.Take(maximum).ToArray();
    }

    // The known command for the first word typed, for the hint under the box.
    public static ArtisanCommandInfo? Describe(string? text, IReadOnlyList<ArtisanCommandInfo> commands)
    {
        var word = (text ?? string.Empty).Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(word)) return null;
        return commands.FirstOrDefault(info => info.Name.Equals(word, StringComparison.Ordinal));
    }

    // Presets run with --no-interaction; history shows what the user would type.
    public static string HistoryText(IReadOnlyList<string> arguments) =>
        string.Join(' ', arguments.Where(argument => argument != "--no-interaction"));

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Clean(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var single = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        single = new string(single.Where(character => !char.IsControl(character)).ToArray());
        return single.Length <= maximum ? single : single[..maximum];
    }
}

/// <summary>
/// %LOCALAPPDATA%\HerdMe\Cache\artisan-commands.json: each site's discovered Artisan commands,
/// so autocomplete is ready the moment the dialog opens. An entry is stale once composer.lock
/// or the artisan file changes, or after a week; the dialog then refreshes it in the background.
/// </summary>
public sealed class ArtisanCommandCache
{
    public const int SiteLimit = 40;
    public static readonly TimeSpan MaximumAge = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly object sync = new();

    public ArtisanCommandCache(string supportRoot)
    {
        CachePath = Path.Combine(supportRoot, "Cache", "artisan-commands.json");
    }

    public string CachePath { get; }

    private sealed class CacheDocument
    {
        public int SchemaVersion { get; set; } = 1;

        public Dictionary<string, CacheEntry> Sites { get; set; } = [];
    }

    private sealed class CacheEntry
    {
        public string Stamp { get; set; } = string.Empty;

        public DateTimeOffset SavedAt { get; set; }

        public List<ArtisanCommandInfo> Commands { get; set; } = [];
    }

    public (IReadOnlyList<ArtisanCommandInfo> Commands, bool Fresh)? Load(string sitePath, DateTimeOffset? now = null)
    {
        lock (sync)
        {
            var document = Read();
            if (!document.Sites.TryGetValue(Key(sitePath), out var entry) || entry.Commands.Count == 0) return null;
            var fresh = entry.Stamp == Stamp(sitePath)
                && (now ?? DateTimeOffset.UtcNow) - entry.SavedAt <= MaximumAge;
            return (entry.Commands, fresh);
        }
    }

    public void Save(string sitePath, IReadOnlyList<ArtisanCommandInfo> commands, DateTimeOffset? now = null)
    {
        if (commands.Count == 0) return;
        lock (sync)
        {
            var document = Read();
            document.Sites[Key(sitePath)] = new CacheEntry
            {
                Stamp = Stamp(sitePath),
                SavedAt = now ?? DateTimeOffset.UtcNow,
                Commands = commands.Take(2_000).ToList()
            };
            if (document.Sites.Count > SiteLimit)
            {
                foreach (var old in document.Sites.OrderBy(pair => pair.Value.SavedAt).Take(document.Sites.Count - SiteLimit).ToArray())
                {
                    document.Sites.Remove(old.Key);
                }
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                var temporary = CachePath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
                File.Move(temporary, CachePath, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A cache that cannot be written only means discovery runs again next time.
            }
        }
    }

    // Changes whenever Composer installs or removes packages (which add or remove commands).
    public static string Stamp(string sitePath)
    {
        static string Part(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "-";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return "?";
            }
        }
        return Part(Path.Combine(sitePath, "composer.lock")) + "|" + Part(Path.Combine(sitePath, "artisan"));
    }

    private CacheDocument Read()
    {
        try
        {
            if (!File.Exists(CachePath)) return new CacheDocument();
            var document = JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(CachePath), JsonOptions);
            if (document?.Sites is null || document.SchemaVersion != 1) return new CacheDocument();
            document.Sites = document.Sites
                .Where(pair => pair.Value?.Commands is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            return document;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new CacheDocument();
        }
    }

    private static string Key(string sitePath) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(sitePath));
}
