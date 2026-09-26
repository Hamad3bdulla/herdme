using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace HerdMe.Windows.Services;

/// <summary>
/// A local site that HerdMe serves at https://name.test/ by forwarding requests to a
/// development server that already listens on a loopback port (Nuxt, Next, Node, ...).
/// </summary>
public sealed record ProxySite(string Name, int Port)
{
    public string Domain(string tld) => $"{Name}.{tld}";
}

public sealed class ProxySiteStore
{
    public const int MaximumProxySites = 64;

    private sealed class ProxyDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public List<ProxySite> Sites { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> FileLocks = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly string path;

    public ProxySiteStore(string supportRoot)
    {
        path = Path.Combine(Path.GetFullPath(supportRoot), "Config", "proxy-sites.json");
    }

    public string SettingsPath => path;

    public IReadOnlyList<ProxySite> Load()
    {
        lock (FileLock()) return LoadDocument().Sites.ToArray();
    }

    /// <summary>Adds or replaces the proxy with the same name.</summary>
    public void Save(ProxySite site, IEnumerable<string> reservedNames)
    {
        ArgumentNullException.ThrowIfNull(site);
        var name = NormalizeName(site.Name)
            ?? throw new ArgumentException("Use lowercase letters, digits, and hyphens for the proxy name.");
        if (!IsValidPort(site.Port))
        {
            throw new ArgumentOutOfRangeException(nameof(site), "Use a port between 1024 and 65535.");
        }
        if (reservedNames.Any(reserved => reserved.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"A site named {name} already exists.");
        }
        lock (FileLock())
        {
            var document = LoadDocument();
            document.Sites.RemoveAll(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (document.Sites.Count >= MaximumProxySites)
            {
                throw new InvalidOperationException("HerdMe supports up to 64 proxy sites.");
            }
            document.Sites.Add(new ProxySite(name, site.Port));
            document.Sites.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
            SaveDocument(document);
        }
    }

    public bool Remove(string name)
    {
        lock (FileLock())
        {
            var document = LoadDocument();
            var removed = document.Sites.RemoveAll(item =>
                item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) SaveDocument(document);
            return removed;
        }
    }

    /// <summary>
    /// Accepts a DNS label (the part before .test). Returns null when the name is not usable.
    /// </summary>
    public static string? NormalizeName(string? value)
    {
        var name = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name) || name.Length > 63) return null;
        if (name[0] == '-' || name[^1] == '-') return null;
        return name.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
            ? name
            : null;
    }

    public static bool IsValidPort(int port) => port is >= 1024 and <= 65535;

    /// <summary>
    /// Parses a user-entered target such as "3000", ":3000", "localhost:3000",
    /// "127.0.0.1:3000" or "http://localhost:3000/". Only loopback targets are allowed so a
    /// proxy site can never forward local traffic to another machine.
    /// </summary>
    public static bool TryParseTarget(string? value, out int port)
    {
        port = 0;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) text = text[7..];
        else if (text.Contains("://", StringComparison.Ordinal)) return false;
        text = text.TrimEnd('/');
        if (text.Contains('/', StringComparison.Ordinal)) return false;
        string portText;
        if (text.StartsWith(':')) portText = text[1..];
        else if (!text.Contains(':', StringComparison.Ordinal)) portText = text;
        else
        {
            var separator = text.LastIndexOf(':');
            var host = text[..separator];
            portText = text[(separator + 1)..];
            if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                && host != "127.0.0.1"
                && host != "[::1]") return false;
        }
        return int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && IsValidPort(port);
    }

    private ProxyDocument LoadDocument()
    {
        if (!File.Exists(path)) return new ProxyDocument();
        try
        {
            var document = JsonSerializer.Deserialize<ProxyDocument>(SettingsFileCache.ReadAllText(path));
            if (document?.Sites is null) throw new JsonException("The proxy site document is empty.");
            if (document.SchemaVersion != 1) return new ProxyDocument();
            document.Sites = document.Sites
                .Where(item => item is not null && NormalizeName(item.Name) is not null && IsValidPort(item.Port))
                .Select(item => new ProxySite(NormalizeName(item.Name)!, item.Port))
                .DistinctBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumProxySites)
                .ToList();
            return document;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or JsonException or ArgumentException or NotSupportedException)
        {
            return new ProxyDocument();
        }
    }

    private void SaveDocument(ProxyDocument document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporary, path, true);
        SettingsFileCache.Invalidate(path);
    }

    private object FileLock() => FileLocks.GetOrAdd(path, _ => new object());
}
