using System.Text;
using System.Text.Json;

namespace HerdMe.Windows.Services;

public sealed record TinkerSnippet
{
    public TinkerSnippet(string name, string code)
    {
        Name = name;
        Code = code;
    }

    public string Name { get; set; }

    public string Code { get; set; }
}

/// <summary>
/// Named Tinker snippets saved per site, in %LOCALAPPDATA%\HerdMe\Config\tinker-snippets.json
/// (site folder path -> snippets, newest first). Bounded and written atomically; nothing is
/// written into the project.
/// </summary>
public sealed class TinkerSnippetStore
{
    public const int MaximumSnippetsPerSite = 50;
    public const int MaximumSites = 200;
    public const int MaximumNameCharacters = 60;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object sync = new();

    public TinkerSnippetStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe",
            "Config",
            "tinker-snippets.json"
        ))
    {
    }

    public TinkerSnippetStore(string path)
    {
        FilePath = Path.GetFullPath(path);
    }

    public string FilePath { get; }

    public IReadOnlyList<TinkerSnippet> Load(string sitePath)
    {
        lock (sync)
        {
            return ReadAll().TryGetValue(SiteKey(sitePath), out var snippets) ? snippets : [];
        }
    }

    // Saving a name that already exists (ignoring case) replaces that snippet.
    public IReadOnlyList<TinkerSnippet> Save(string sitePath, string name, string code)
    {
        var cleanName = NormalizeName(name);
        var cleanCode = TinkerScript.Normalize(code).Trim();
        lock (sync)
        {
            var all = ReadAll();
            var key = SiteKey(sitePath);
            var snippets = all.TryGetValue(key, out var existing) ? existing : [];
            snippets = snippets
                .Where(snippet => !string.Equals(snippet.Name, cleanName, StringComparison.OrdinalIgnoreCase))
                .Prepend(new TinkerSnippet(cleanName, cleanCode))
                .Take(MaximumSnippetsPerSite)
                .ToList();
            all.Remove(key);
            if (all.Count >= MaximumSites) all.Remove(all.Keys.First());
            all[key] = snippets;
            WriteAll(all);
            return snippets;
        }
    }

    public IReadOnlyList<TinkerSnippet> Delete(string sitePath, string name)
    {
        lock (sync)
        {
            var all = ReadAll();
            var key = SiteKey(sitePath);
            if (!all.TryGetValue(key, out var snippets)) return [];
            var remaining = snippets
                .Where(snippet => !string.Equals(snippet.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (remaining.Count == snippets.Count) return snippets;
            if (remaining.Count == 0) all.Remove(key);
            else all[key] = remaining;
            WriteAll(all);
            return remaining;
        }
    }

    public static string NormalizeName(string name)
    {
        var clean = new string((name ?? string.Empty).Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        if (clean.Length == 0)
        {
            throw new ArgumentException(ServiceText.Get("TinkerSnippetNameEmpty", "Enter a name for the snippet."), nameof(name));
        }
        if (clean.Length > MaximumNameCharacters)
        {
            throw new ArgumentException(
                ServiceText.Get("TinkerSnippetNameTooLong", "Snippet names are limited to 60 characters."),
                nameof(name)
            );
        }
        return clean;
    }

    private static string SiteKey(string sitePath) =>
        Path.GetFullPath(sitePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // Insertion order is kept, so the oldest site is dropped first when MaximumSites is reached.
    private Dictionary<string, List<TinkerSnippet>> ReadAll()
    {
        var result = new Dictionary<string, List<TinkerSnippet>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(FilePath)) return result;
            var stored = JsonSerializer.Deserialize<Dictionary<string, List<TinkerSnippet>>>(File.ReadAllText(FilePath));
            foreach (var (site, snippets) in stored ?? [])
            {
                if (string.IsNullOrWhiteSpace(site) || snippets is null) continue;
                var valid = snippets
                    .Where(snippet => snippet is not null
                        && !string.IsNullOrWhiteSpace(snippet.Name)
                        && snippet.Name.Length <= MaximumNameCharacters
                        && !string.IsNullOrWhiteSpace(snippet.Code)
                        && snippet.Code.Length <= TinkerScript.MaximumCodeCharacters)
                    .DistinctBy(snippet => snippet.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(MaximumSnippetsPerSite)
                    .ToList();
                if (valid.Count > 0 && result.Count < MaximumSites) result[site] = valid;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or NotSupportedException or ArgumentException)
        {
            result.Clear();
        }
        return result;
    }

    private void WriteAll(Dictionary<string, List<TinkerSnippet>> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(all, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
