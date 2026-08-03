using System.Text.Json;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed class PhpRuntimePolicy
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly CoreClient coreClient;
    private readonly string supportRoot;

    public PhpRuntimePolicy(CoreClient? coreClient = null, string? supportRoot = null)
    {
        this.coreClient = coreClient ?? new CoreClient();
        this.supportRoot = supportRoot ?? SupportPath;
    }

    public string SettingsPath => Path.Combine(supportRoot, "Config", "php.json");

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

        var storedSettings = Normalize(Load());
        var requireDebuggerExtension = RequiresDebuggerExtension(
            phpCycle,
            storedSettings.PhpCycle
        );
        var selectedCycle = string.IsNullOrWhiteSpace(phpCycle)
            ? storedSettings.PhpCycle
            : phpCycle;
        var settings = ResolveVersion(storedSettings, selectedCycle);
        return new PhpRuntimeLaunchContract(
            extensions,
            settings,
            BuildPhpOptions(settings, requireDebuggerExtension, supportRoot)
        );
    }

    public static PhpRuntimeSettings Normalize(PhpRuntimeSettings settings)
    {
        settings.MemoryLimitMegabytes = Math.Clamp(settings.MemoryLimitMegabytes, 16, 100_000);
        settings.MaxUploadMegabytes = Math.Clamp(settings.MaxUploadMegabytes, 1, 100_000);
        NormalizeLimits(settings);
        settings.PhpCycle = string.IsNullOrWhiteSpace(settings.PhpCycle)
            ? RuntimeCatalog.DefaultPhpCycle
            : new string(settings.PhpCycle.Trim().Where(character =>
                char.IsAsciiDigit(character) || character == '.'
            ).ToArray());
        if (string.IsNullOrEmpty(settings.PhpCycle))
        {
            settings.PhpCycle = RuntimeCatalog.DefaultPhpCycle;
        }

        settings.Versions ??= [];
        settings.Versions = settings.Versions
            .Where(pair => ValidCycle(pair.Key) && pair.Value is not null)
            .ToDictionary(
                pair => pair.Key,
                pair => NormalizeVersion(pair.Value),
                StringComparer.Ordinal
            );

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

    public static PhpRuntimeSettings ResolveVersion(PhpRuntimeSettings source, string cycle)
    {
        var settings = Normalize(source);
        var version = settings.Versions.TryGetValue(cycle, out var configured)
            ? configured
            : VersionFrom(settings);
        return new PhpRuntimeSettings
        {
            PhpCycle = cycle,
            MemoryLimitMegabytes = version.MemoryLimitMegabytes,
            MaxUploadMegabytes = version.MaxUploadMegabytes,
            MaxExecutionTimeSeconds = version.MaxExecutionTimeSeconds,
            MaxInputTimeSeconds = version.MaxInputTimeSeconds,
            MaxInputVariables = version.MaxInputVariables,
            MaxFileUploads = version.MaxFileUploads,
            DisplayErrors = version.DisplayErrors,
            OpcacheEnabled = version.OpcacheEnabled,
            Timezone = version.Timezone,
            Debugger = settings.Debugger,
            Versions = settings.Versions
        };
    }

    public static void SetVersion(PhpRuntimeSettings settings, string cycle, PhpRuntimeSettings value)
    {
        Normalize(settings);
        if (!ValidCycle(cycle)) throw new ArgumentException("The PHP cycle is invalid.", nameof(cycle));
        settings.Versions[cycle] = NormalizeVersion(VersionFrom(value));
    }

    public static IReadOnlyDictionary<string, bool> ExtensionPreferences(
        string supportRoot,
        string cycle
    )
    {
        try
        {
            var path = Path.Combine(supportRoot, "Config", "php.json");
            if (!File.Exists(path)) return new Dictionary<string, bool>();
            var settings = JsonSerializer.Deserialize<PhpRuntimeSettings>(File.ReadAllText(path));
            return settings?.Versions?.TryGetValue(cycle, out var version) == true
                ? new Dictionary<string, bool>(
                    version.Extensions ?? [], StringComparer.OrdinalIgnoreCase
                )
                : new Dictionary<string, bool>();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or JsonException or ArgumentException)
        {
            return new Dictionary<string, bool>();
        }
    }

    public static IReadOnlyDictionary<string, string> BuildPhpOptions(PhpRuntimeSettings input)
    {
        return BuildPhpOptions(input, requireDebuggerExtension: true, SupportPath);
    }

    internal static IReadOnlyDictionary<string, string> BuildPhpOptions(
        PhpRuntimeSettings input,
        bool requireDebuggerExtension,
        string supportPath
    )
    {
        var settings = Normalize(input);
        var options = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["memory_limit"] = $"{settings.MemoryLimitMegabytes}M",
            ["post_max_size"] = $"{settings.MaxUploadMegabytes}M",
            ["upload_max_filesize"] = $"{settings.MaxUploadMegabytes}M",
            ["max_execution_time"] = settings.MaxExecutionTimeSeconds.ToString(),
            ["max_input_time"] = settings.MaxInputTimeSeconds.ToString(),
            ["max_input_vars"] = settings.MaxInputVariables.ToString(),
            ["max_file_uploads"] = settings.MaxFileUploads.ToString(),
            ["display_errors"] = settings.DisplayErrors ? "1" : "0",
            ["display_startup_errors"] = settings.DisplayErrors ? "1" : "0",
            ["opcache.enable"] = settings.OpcacheEnabled ? "1" : "0",
            ["date.timezone"] = settings.Timezone
        };
        if (!settings.Debugger.Enabled) return options;

        var extensionPath = Path.Combine(
            supportPath,
            "Extensions",
            "php",
            settings.PhpCycle,
            "php_xdebug.dll"
        );
        if (!File.Exists(extensionPath))
        {
            if (!requireDebuggerExtension) return options;
            throw new InvalidOperationException(
                $"Install HerdMe Xdebug for PHP {settings.PhpCycle} before enabling the debugger."
            );
        }

        var xdebugLogDirectory = Path.Combine(supportPath, "Log", "xdebug");
        Directory.CreateDirectory(xdebugLogDirectory);

        options["zend_extension"] = extensionPath;
        options["xdebug.client_host"] = "127.0.0.1";
        options["xdebug.client_port"] = settings.Debugger.Port.ToString();
        options["xdebug.discover_client_host"] = "0";
        options["xdebug.idekey"] = settings.Debugger.IdeKey;
        options["xdebug.log"] = Path.Combine(xdebugLogDirectory, "xdebug.log");
        options["xdebug.log_level"] = "1";
        options["xdebug.mode"] = "debug,develop";
        options["xdebug.start_with_request"] = settings.Debugger.DetectBreakpoints ? "trigger" : "yes";
        options["xdebug.trigger_value"] = settings.Debugger.IdeKey;
        return options;
    }

    internal static bool RequiresDebuggerExtension(
        string? requestedPhpCycle,
        string defaultPhpCycle
    )
    {
        return string.IsNullOrWhiteSpace(requestedPhpCycle)
            || string.Equals(requestedPhpCycle, defaultPhpCycle, StringComparison.Ordinal);
    }

    private static string SupportPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HerdMe"
    );

    private static void NormalizeLimits(PhpRuntimeSettings settings)
    {
        settings.MaxExecutionTimeSeconds = Math.Clamp(settings.MaxExecutionTimeSeconds, 0, 86_400);
        settings.MaxInputTimeSeconds = Math.Clamp(settings.MaxInputTimeSeconds, -1, 86_400);
        settings.MaxInputVariables = Math.Clamp(settings.MaxInputVariables, 100, 1_000_000);
        settings.MaxFileUploads = Math.Clamp(settings.MaxFileUploads, 1, 10_000);
        settings.Timezone = NormalizeTimezone(settings.Timezone);
    }

    private static PhpVersionConfiguration NormalizeVersion(PhpVersionConfiguration version)
    {
        var temporary = new PhpRuntimeSettings
        {
            MemoryLimitMegabytes = version.MemoryLimitMegabytes,
            MaxUploadMegabytes = version.MaxUploadMegabytes,
            MaxExecutionTimeSeconds = version.MaxExecutionTimeSeconds,
            MaxInputTimeSeconds = version.MaxInputTimeSeconds,
            MaxInputVariables = version.MaxInputVariables,
            MaxFileUploads = version.MaxFileUploads,
            DisplayErrors = version.DisplayErrors,
            OpcacheEnabled = version.OpcacheEnabled,
            Timezone = version.Timezone
        };
        temporary.MemoryLimitMegabytes = Math.Clamp(temporary.MemoryLimitMegabytes, 16, 100_000);
        temporary.MaxUploadMegabytes = Math.Clamp(temporary.MaxUploadMegabytes, 1, 100_000);
        NormalizeLimits(temporary);
        version.MemoryLimitMegabytes = temporary.MemoryLimitMegabytes;
        version.MaxUploadMegabytes = temporary.MaxUploadMegabytes;
        version.MaxExecutionTimeSeconds = temporary.MaxExecutionTimeSeconds;
        version.MaxInputTimeSeconds = temporary.MaxInputTimeSeconds;
        version.MaxInputVariables = temporary.MaxInputVariables;
        version.MaxFileUploads = temporary.MaxFileUploads;
        version.Timezone = temporary.Timezone;
        version.Extensions = (version.Extensions ?? [])
            .Where(pair => ValidExtensionName(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return version;
    }

    private static PhpVersionConfiguration VersionFrom(PhpRuntimeSettings settings) => new()
    {
        MemoryLimitMegabytes = settings.MemoryLimitMegabytes,
        MaxUploadMegabytes = settings.MaxUploadMegabytes,
        MaxExecutionTimeSeconds = settings.MaxExecutionTimeSeconds,
        MaxInputTimeSeconds = settings.MaxInputTimeSeconds,
        MaxInputVariables = settings.MaxInputVariables,
        MaxFileUploads = settings.MaxFileUploads,
        DisplayErrors = settings.DisplayErrors,
        OpcacheEnabled = settings.OpcacheEnabled,
        Timezone = settings.Timezone,
        Extensions = settings.Versions.TryGetValue(settings.PhpCycle, out var configured)
            ? new Dictionary<string, bool>(configured.Extensions, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
    };

    private static string NormalizeTimezone(string? value)
    {
        var timezone = new string((value ?? "UTC").Trim().Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '/' or '_' or '-' or '+'
        ).ToArray());
        return string.IsNullOrWhiteSpace(timezone) ? "UTC" : timezone[..Math.Min(64, timezone.Length)];
    }

    private static bool ValidCycle(string value)
    {
        var parts = value.Split('.');
        return parts.Length == 2 && parts.All(part => part.Length > 0 && part.All(char.IsAsciiDigit));
    }

    private static bool ValidExtensionName(string value) => value.Length is > 0 and <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}
