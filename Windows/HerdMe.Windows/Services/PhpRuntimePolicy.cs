using System.Text.Json;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed class PhpRuntimePolicy
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly CoreClient coreClient;

    public PhpRuntimePolicy(CoreClient? coreClient = null)
    {
        this.coreClient = coreClient ?? new CoreClient();
    }

    public string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HerdMe",
        "Config",
        "php.json"
    );

    public PhpRuntimeSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<PhpRuntimeSettings>(
                    File.ReadAllText(SettingsPath)
                );
                if (settings is not null)
                {
                    return Normalize(settings);
                }
            }
        }
        catch (JsonException)
        {
            // Invalid local settings fall back to the bounded defaults.
        }

        return new PhpRuntimeSettings();
    }

    public void Save(PhpRuntimeSettings settings)
    {
        var normalized = Normalize(settings);
        var directory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("The PHP settings path is invalid.");
        Directory.CreateDirectory(directory);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(normalized, JsonOptions));
        File.Move(temporaryPath, SettingsPath, true);
    }

    public async Task<PhpRuntimeLaunchContract> PrepareLaunchAsync(
        string phpExecutable,
        CancellationToken cancellationToken = default
    )
    {
        return await PrepareLaunchCoreAsync(
            phpExecutable,
            phpCycle: null,
            cancellationToken
        );
    }

    public async Task<PhpRuntimeLaunchContract> PrepareLaunchAsync(
        string phpExecutable,
        string phpCycle,
        CancellationToken cancellationToken = default
    )
    {
        return await PrepareLaunchCoreAsync(phpExecutable, phpCycle, cancellationToken);
    }

    private async Task<PhpRuntimeLaunchContract> PrepareLaunchCoreAsync(
        string phpExecutable,
        string? phpCycle,
        CancellationToken cancellationToken
    )
    {
        var extensions = await coreClient.ValidatePhpAsync(phpExecutable, cancellationToken);
        if (!extensions.Compatible)
        {
            throw new InvalidOperationException(
                "PHP cannot start Laravel sites. Missing extensions: "
                + string.Join(", ", extensions.Missing)
            );
        }

        var settings = Normalize(Load());
        if (!string.IsNullOrWhiteSpace(phpCycle)) settings.PhpCycle = phpCycle;
        return new PhpRuntimeLaunchContract(
            extensions,
            settings,
            BuildPhpOptions(settings)
        );
    }

    public static PhpRuntimeSettings Normalize(PhpRuntimeSettings settings)
    {
        settings.MemoryLimitMegabytes = Math.Clamp(settings.MemoryLimitMegabytes, 16, 100_000);
        settings.MaxUploadMegabytes = Math.Clamp(settings.MaxUploadMegabytes, 1, 100_000);
        settings.PhpCycle = string.IsNullOrWhiteSpace(settings.PhpCycle)
            ? RuntimeCatalog.DefaultPhpCycle
            : new string(settings.PhpCycle.Trim().Where(character =>
                char.IsAsciiDigit(character) || character == '.'
            ).ToArray());
        if (string.IsNullOrEmpty(settings.PhpCycle))
        {
            settings.PhpCycle = RuntimeCatalog.DefaultPhpCycle;
        }

        settings.Debugger ??= new DebuggerSettings();
        settings.Debugger.Port = Math.Clamp(settings.Debugger.Port, 1, 65_535);
        settings.Debugger.IdeKey = new string(
            (settings.Debugger.IdeKey ?? string.Empty).Trim().Where(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'
            ).ToArray()
        );
        if (string.IsNullOrEmpty(settings.Debugger.IdeKey)) settings.Debugger.IdeKey = "VSCODE";
        return settings;
    }

    public static IReadOnlyDictionary<string, string> BuildPhpOptions(PhpRuntimeSettings input)
    {
        var settings = Normalize(input);
        var options = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["memory_limit"] = $"{settings.MemoryLimitMegabytes}M",
            ["post_max_size"] = $"{settings.MaxUploadMegabytes}M",
            ["upload_max_filesize"] = $"{settings.MaxUploadMegabytes}M"
        };
        if (!settings.Debugger.Enabled) return options;

        var supportPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe"
        );
        var extensionPath = Path.Combine(
            supportPath,
            "Extensions",
            "php",
            settings.PhpCycle,
            "php_xdebug.dll"
        );
        if (!File.Exists(extensionPath))
        {
            throw new InvalidOperationException(
                $"Install HerdMe Xdebug for PHP {settings.PhpCycle} before enabling the debugger."
            );
        }

        options["zend_extension"] = extensionPath;
        options["xdebug.client_host"] = "127.0.0.1";
        options["xdebug.client_port"] = settings.Debugger.Port.ToString();
        options["xdebug.discover_client_host"] = "0";
        options["xdebug.idekey"] = settings.Debugger.IdeKey;
        options["xdebug.log"] = Path.Combine(supportPath, "Log", "xdebug", "xdebug.log");
        options["xdebug.log_level"] = "1";
        options["xdebug.mode"] = "debug,develop";
        options["xdebug.start_with_request"] = settings.Debugger.DetectBreakpoints ? "trigger" : "yes";
        options["xdebug.trigger_value"] = settings.Debugger.IdeKey;
        return options;
    }
}
