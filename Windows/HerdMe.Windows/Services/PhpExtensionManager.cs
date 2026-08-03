using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed record PhpExtensionState(
    string Name,
    bool Enabled,
    bool Loaded,
    bool Available,
    bool CanToggle,
    bool Required
);

public sealed class PhpExtensionManager(
    PhpRuntimeInstaller installer,
    CoreClient coreClient
)
{
    public async Task<IReadOnlyList<PhpExtensionState>> InspectAsync(
        string cycle,
        CancellationToken cancellationToken = default
    )
    {
        if (!installer.IsInstalled(cycle)) return [];
        var runtimeDirectory = Path.Combine(installer.RuntimeRoot, cycle);
        var configurationPath = Path.Combine(runtimeDirectory, "php.ini");
        var dllExtensions = Directory.Exists(Path.Combine(runtimeDirectory, "ext"))
            ? Directory.GetFiles(Path.Combine(runtimeDirectory, "ext"), "php_*.dll")
                .Select(path => Path.GetFileNameWithoutExtension(path)[4..])
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configured = ReadConfiguration(configurationPath);
        var report = await coreClient.ValidatePhpAsync(
            installer.PhpExecutable(cycle), cancellationToken
        );
        var loaded = report.Loaded.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var required = report.Required.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return dllExtensions.Concat(configured.Keys).Concat(loaded).Concat(required)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(name => new PhpExtensionState(
                name,
                loaded.Contains(name) || configured.GetValueOrDefault(name),
                loaded.Contains(name),
                dllExtensions.Contains(name) || loaded.Contains(name),
                dllExtensions.Contains(name),
                required.Contains(name)
            ))
            .ToArray();
    }

    public void SetEnabled(string cycle, string extension, bool enabled)
    {
        if (!ValidName(extension))
        {
            throw new ArgumentException("The PHP extension name is invalid.", nameof(extension));
        }
        var runtimeDirectory = Path.Combine(installer.RuntimeRoot, cycle);
        var dll = Path.Combine(runtimeDirectory, "ext", $"php_{extension}.dll");
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException(
                $"PHP {cycle} does not contain a loadable {extension} extension.", dll
            );
        }
        SetConfigured(
            Path.Combine(runtimeDirectory, "php.ini"), extension, enabled
        );
    }

    internal static IReadOnlyDictionary<string, bool> ReadConfiguration(string path)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path))
        {
            if (TryExtensionLine(line, out var name, out var enabled)) result[name] = enabled;
        }
        return result;
    }

    internal static void ApplyPreferences(
        string configurationPath,
        IReadOnlyDictionary<string, bool> preferences
    )
    {
        foreach (var preference in preferences.Where(pair => ValidName(pair.Key)))
        {
            SetConfigured(configurationPath, preference.Key, preference.Value);
        }
    }

    private static void SetConfigured(string path, string extension, bool enabled)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The PHP configuration was not found.", path);
        var lines = File.ReadAllLines(path).ToList();
        var found = false;
        for (var index = 0; index < lines.Count; index++)
        {
            if (!TryExtensionLine(lines[index], out var name, out _)
                || !name.Equals(extension, StringComparison.OrdinalIgnoreCase)) continue;
            var declaration = lines[index].TrimStart().TrimStart(';').TrimStart();
            lines[index] = enabled ? declaration : "; " + declaration;
            found = true;
        }
        if (!found && enabled)
        {
            var directive = extension.Equals("opcache", StringComparison.OrdinalIgnoreCase)
                ? "zend_extension"
                : "extension";
            lines.Add($"{directive} = {extension}");
        }
        var temporary = path + ".tmp";
        File.WriteAllLines(temporary, lines);
        File.Move(temporary, path, true);
    }

    private static bool TryExtensionLine(
        string line,
        out string name,
        out bool enabled
    )
    {
        name = string.Empty;
        var trimmed = line.Trim();
        enabled = !trimmed.StartsWith(';');
        trimmed = trimmed.TrimStart(';').TrimStart();
        var separator = trimmed.IndexOf('=');
        if (separator < 0) return false;
        var directive = trimmed[..separator].Trim();
        if (!directive.Equals("extension", StringComparison.OrdinalIgnoreCase)
            && !directive.Equals("zend_extension", StringComparison.OrdinalIgnoreCase)) return false;
        var value = trimmed[(separator + 1)..].Split(';', 2)[0].Trim().Trim('"', '\'');
        name = Path.GetFileNameWithoutExtension(value);
        if (name.StartsWith("php_", StringComparison.OrdinalIgnoreCase)) name = name[4..];
        return ValidName(name);
    }

    private static bool ValidName(string value) => value.Length is > 0 and <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}
