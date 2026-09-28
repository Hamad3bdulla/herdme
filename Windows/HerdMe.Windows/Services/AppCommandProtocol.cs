using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HerdMe.Windows.Services;

// One request per pipe connection: a single UTF-8 JSON line from the client, then a single
// JSON line back. Interactive requests come from Explorer, the taskbar Jump List, or a
// herdme:// link and may bring the window forward; the command-line tool is non-interactive.
public sealed record AppCommandRequest(
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("arguments")] IReadOnlyList<string> Arguments,
    [property: JsonPropertyName("interactive")] bool Interactive = false,
    [property: JsonPropertyName("version")] int Version = AppCommandProtocol.ProtocolVersion
);

public sealed record AppCommandResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("output")] string Output
)
{
    public static AppCommandResponse Success(string output) => new(true, 0, output);

    public static AppCommandResponse Failure(string output, int exitCode = 1) =>
        new(false, exitCode <= 0 ? 1 : exitCode, output);
}

// Result of parsing herdme.exe arguments: a request to send, or local text and exit code.
public sealed record CliInvocation(AppCommandRequest? Request, string? LocalOutput, int ExitCode);

public static partial class AppCommandProtocol
{
    public const int ProtocolVersion = 1;
    public const int MaximumMessageBytes = 256 * 1024;
    public const int MaximumArguments = 8;
    public const int MaximumArgumentLength = 1_024;
    public const string CommandSwitch = "--command";
    public const string LinkSwitch = "--link";
    public const string UriScheme = "herdme";
    public const int UsageExitCode = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false
    };

    // Command name -> (minimum, maximum) positional arguments.
    private static readonly IReadOnlyDictionary<string, (int Minimum, int Maximum)> Arity =
        new Dictionary<string, (int, int)>(StringComparer.Ordinal)
        {
            ["ping"] = (0, 0),
            ["status"] = (0, 0),
            ["sites"] = (0, 0),
            ["open"] = (1, 1),
            ["show"] = (0, 1),
            ["site"] = (1, 1),
            ["share"] = (1, 1),
            ["start"] = (0, 0),
            ["stop"] = (0, 0),
            ["link"] = (1, 1),
            ["unlink"] = (1, 1),
            ["logs"] = (0, 1),
            ["tinker"] = (0, 1)
        };

    // Commands a herdme:// link may run. Links only navigate; they never change state,
    // start processes, or expose anything.
    private static readonly IReadOnlySet<string> UriCommands =
        new HashSet<string>(["show", "site", "logs", "tinker"], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> SiteCommands =
        new HashSet<string>(["open", "site", "share", "logs", "tinker"], StringComparer.Ordinal);

    public static IReadOnlyList<string> Commands { get; } = [.. Arity.Keys];

    public static IReadOnlyList<string> Pages { get; } =
    [
        "dashboard", "general", "sites", "php", "node", "services", "updates", "mail",
        "dumps", "logs", "debugger", "tinker", "about"
    ];

    public static string PipeName(int sessionId) => $"HerdMe.Command.{sessionId}";

    public static bool IsSiteName(string value) => SiteNamePattern().IsMatch(value);

    public static bool IsFullyQualifiedWindowsPath(string value)
    {
        if (value.Split('\\', '/').Any(segment => segment is "." or "..")) return false;
        if (WindowsPathPattern().IsMatch(value)) return true;
        return value.StartsWith(@"\\", StringComparison.Ordinal)
            && value.Length > 4
            && !value.StartsWith(@"\\?\", StringComparison.Ordinal)
            && !value.StartsWith(@"\\.\", StringComparison.Ordinal);
    }

    // Returns null when the request is acceptable, otherwise an English usage message.
    public static string? Validate(AppCommandRequest? request)
    {
        if (request is null) return "The request was empty.";
        if (request.Version != ProtocolVersion) return "HerdMe and the herdme command are different versions.";
        if (!Arity.TryGetValue(request.Command ?? string.Empty, out var arity))
        {
            return $"Unknown command '{request.Command}'. Run 'herdme help'.";
        }
        var arguments = request.Arguments ?? [];
        if (arguments.Count > MaximumArguments
            || arguments.Count < arity.Minimum
            || arguments.Count > arity.Maximum)
        {
            return arity.Minimum == arity.Maximum
                ? $"'{request.Command}' takes {arity.Minimum} argument(s)."
                : $"'{request.Command}' takes at most {arity.Maximum} argument(s).";
        }
        foreach (var argument in arguments)
        {
            if (argument is null || argument.Length == 0 || argument.Length > MaximumArgumentLength)
            {
                return "An argument is empty or too long.";
            }
            if (argument.Any(char.IsControl)) return "Arguments cannot contain control characters.";
        }
        if (SiteCommands.Contains(request.Command!) && arguments.Count == 1 && !IsSiteName(arguments[0]))
        {
            return $"'{arguments[0]}' is not a valid site name.";
        }
        if (request.Command == "show" && arguments.Count == 1 && !Pages.Contains(arguments[0]))
        {
            return $"Unknown page '{arguments[0]}'. Pages: {string.Join(", ", Pages)}.";
        }
        if (request.Command == "link" && !IsFullyQualifiedWindowsPath(arguments[0]))
        {
            return "link needs a full folder path.";
        }
        if (request.Command == "unlink"
            && !IsSiteName(arguments[0])
            && !IsFullyQualifiedWindowsPath(arguments[0]))
        {
            return "unlink needs a site name or a full folder path.";
        }
        return null;
    }

    // Arguments are the process arguments without the executable path. Returns null for a
    // normal launch (no command, link, or herdme:// link).
    public static AppCommandRequest? ParseLaunchArguments(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (string.Equals(argument, CommandSwitch, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= arguments.Count) return null;
                var command = arguments[index + 1].Trim().ToLowerInvariant();
                var rest = arguments.Skip(index + 2).Take(MaximumArguments + 1).ToList();
                var request = new AppCommandRequest(command, rest, Interactive: true);
                return Validate(request) is null ? request : null;
            }
            if (string.Equals(argument, LinkSwitch, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= arguments.Count) return null;
                var path = NormalizeFolderArgument(arguments[index + 1]);
                var request = new AppCommandRequest("link", [path], Interactive: true);
                return Validate(request) is null ? request : null;
            }
            if (argument.StartsWith(UriScheme + ":", StringComparison.OrdinalIgnoreCase))
            {
                return ParseUri(argument);
            }
        }
        return null;
    }

    // Accepted forms: herdme://show[/page], herdme://site/<name>, herdme://logs[/<name>],
    // herdme://tinker[/<name>]. Anything else (queries, extra segments, other commands) is
    // rejected so a web page cannot use a link to change HerdMe state.
    public static AppCommandRequest? ParseUri(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Scheme, UriScheme, StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo) || (!uri.IsDefaultPort && uri.Port != -1)) return null;
        var command = uri.Host.ToLowerInvariant();
        if (!UriCommands.Contains(command)) return null;
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToList();
        if (segments.Count > 1) return null;
        if (command == "show" && segments.Count == 1) segments[0] = segments[0].ToLowerInvariant();
        var request = new AppCommandRequest(command, segments, Interactive: true);
        return Validate(request) is null ? request : null;
    }

    // Turns herdme.exe arguments into a request, or into text to print locally (help,
    // version, usage errors). Folder arguments are resolved against the current directory.
    public static CliInvocation ParseCliArguments(
        IReadOnlyList<string> arguments,
        string currentDirectory,
        string version
    )
    {
        if (arguments.Count == 0) return new CliInvocation(null, HelpText(version), 0);
        var command = arguments[0].Trim().ToLowerInvariant();
        if (command is "help" or "--help" or "-h" or "/?" or "-?")
        {
            return new CliInvocation(null, HelpText(version), 0);
        }
        if (command is "--version" or "-v" or "version")
        {
            return new CliInvocation(null, $"herdme {version}", 0);
        }
        var rest = arguments.Skip(1).ToList();
        if (command is "link" or "unlink")
        {
            if (rest.Count == 0) rest.Add(currentDirectory);
            else if (command == "link" || LooksLikePath(rest[0]))
            {
                rest[0] = ResolveFolder(rest[0], currentDirectory);
            }
        }
        var request = new AppCommandRequest(command, rest);
        var problem = Validate(request);
        return problem is null
            ? new CliInvocation(request, null, 0)
            : new CliInvocation(null, problem, UsageExitCode);
    }

    private static bool LooksLikePath(string value)
    {
        return value is "." or ".."
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Contains('/', StringComparison.Ordinal)
            || value.Contains(':', StringComparison.Ordinal);
    }

    private static string ResolveFolder(string value, string currentDirectory)
    {
        var path = NormalizeFolderArgument(value);
        try
        {
            return NormalizeFolderArgument(Path.GetFullPath(path, currentDirectory));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    public static string NormalizeFolderArgument(string value)
    {
        var path = value.Trim().Trim('"');
        // Explorer passes a drive root with its trailing separator; keep that, trim the rest.
        if (path.Length > 3) path = path.TrimEnd('\\', '/');
        return path;
    }

    public static byte[] Serialize(AppCommandRequest request) => SerializeLine(request);

    public static byte[] Serialize(AppCommandResponse response) => SerializeLine(response);

    public static AppCommandRequest? DeserializeRequest(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<AppCommandRequest>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static AppCommandResponse? DeserializeResponse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<AppCommandResponse>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Reads one '\n'-terminated UTF-8 line. Returns null at end of stream before a newline;
    // throws InvalidDataException when the line exceeds the size limit.
    public static async Task<string?> ReadLineAsync(
        Stream stream,
        CancellationToken cancellationToken,
        int maximumBytes = MaximumMessageBytes
    )
    {
        using var buffer = new MemoryStream();
        var single = new byte[1];
        while (true)
        {
            // Byte-at-a-time keeps the reader from consuming past the newline; messages are small.
            var read = await stream.ReadAsync(single.AsMemory(0, 1), cancellationToken);
            if (read == 0) return null;
            if (single[0] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimEnd('\r');
            }
            buffer.WriteByte(single[0]);
            if (buffer.Length > maximumBytes)
            {
                throw new InvalidDataException("The HerdMe command message is too large.");
            }
        }
    }

    public static string HelpText(string version)
    {
        return $$"""
            HerdMe {{version}} command line

            Usage: herdme <command> [argument]

              status            Show the environment, sites and services
              sites             List sites and their addresses
              open <site>       Open https://<site>.test/ in the browser
              link [folder]     Link a folder as a site (default: current folder)
              unlink [site]     Unlink a site by name or folder (default: current folder)
              start             Start the environment and enabled services
              stop              Stop the environment and all services
              show [page]       Show the HerdMe window, optionally on a page
              site <site>       Show a site in the HerdMe window
              share <site>      Open the share dialog for a site (you confirm in HerdMe)
              logs [site]       Show the Logs page
              tinker [site]     Open Tinker in the Sites page
              ping              Exit 0 when HerdMe is running (does not start it)
              --version         Print the version
              help              Show this help

            Pages: {{string.Join(", ", Pages)}}
            """;
    }

    private static byte[] SerializeLine<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        if (bytes.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException("The HerdMe command message is too large.");
        }
        return bytes;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex SiteNamePattern();

    [GeneratedRegex(@"^[A-Za-z]:\\", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathPattern();
}
