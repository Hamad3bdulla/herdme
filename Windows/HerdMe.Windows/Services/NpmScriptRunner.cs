using System.Collections;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace HerdMe.Windows.Services;

public sealed record NpmScript(string Name);

public sealed record NpmScriptInvocation(
    string NodeExecutable,
    string NpmCli,
    string ProjectDirectory,
    string ScriptName,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout
);

public sealed record NpmToolInvocation(
    string NodeExecutable,
    string NpmCli,
    string ProjectDirectory,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout
);

public sealed record NpmScriptResult(int ExitCode, string Output);

public sealed class NpmScriptException : Exception
{
    public NpmScriptException(
        string resourceKey,
        string message,
        params object?[] resourceArguments
    ) : base(message)
    {
        ResourceKey = resourceKey;
        ResourceArguments = resourceArguments;
    }

    public NpmScriptException(
        string resourceKey,
        string message,
        Exception innerException,
        params object?[] resourceArguments
    ) : base(message, innerException)
    {
        ResourceKey = resourceKey;
        ResourceArguments = resourceArguments;
    }

    public string ResourceKey { get; }

    public IReadOnlyList<object?> ResourceArguments { get; }
}

public static class NpmScriptCatalog
{
    private const int MaximumPackageBytes = 1 * 1_024 * 1_024;
    private const int MaximumScriptNameBytes = 256;
    private const int MaximumScripts = 256;
    private static readonly string[] PreferredOrder = ["dev", "build", "test"];

    public static IReadOnlyList<NpmScript> Discover(string projectDirectory)
    {
        var projectPath = Path.GetFullPath(projectDirectory);
        var packagePath = Path.Combine(projectPath, "package.json");
        if (!File.Exists(packagePath))
        {
            throw new NpmScriptException(
                "SitesNpmErrorPackageMissing",
                "This project does not contain a package.json file.",
                packagePath
            );
        }
        var file = new FileInfo(packagePath);
        if (file.Length > MaximumPackageBytes)
        {
            throw new NpmScriptException(
                "SitesNpmErrorPackageTooLarge",
                "package.json must be no larger than 1 MB."
            );
        }

        JsonDocument document;
        try
        {
            using var stream = new FileStream(
                packagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1_024,
                FileOptions.SequentialScan
            );
            if (stream.Length > MaximumPackageBytes)
            {
                throw new NpmScriptException(
                    "SitesNpmErrorPackageTooLarge",
                    "package.json must be no larger than 1 MB."
                );
            }
            document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException error)
        {
            throw new NpmScriptException(
                "SitesNpmErrorPackageInvalid",
                "package.json does not contain valid JSON.",
                error
            );
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("scripts", out var scriptsElement)
                || scriptsElement.ValueKind != JsonValueKind.Object)
            {
                throw new NpmScriptException(
                    "SitesNpmErrorNoScripts",
                    "This project does not define any runnable npm scripts."
                );
            }

            var scripts = new Dictionary<string, NpmScript>(StringComparer.Ordinal);
            var count = 0;
            foreach (var property in scriptsElement.EnumerateObject())
            {
                count++;
                if (count > MaximumScripts)
                {
                    throw new NpmScriptException(
                        "SitesNpmErrorTooManyScripts",
                        "package.json contains more than 256 npm scripts."
                    );
                }
                if (!IsValidName(property.Name)) continue;
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    scripts[property.Name] = new NpmScript(property.Name);
                }
                else
                {
                    scripts.Remove(property.Name);
                }
            }

            if (scripts.Count == 0)
            {
                throw new NpmScriptException(
                    "SitesNpmErrorNoScripts",
                    "This project does not define any runnable npm scripts."
                );
            }
            return scripts.Values.OrderBy(script => script, NpmScriptComparer.Instance).ToArray();
        }
    }

    public static void ValidateName(string name)
    {
        if (!IsValidName(name))
        {
            throw new NpmScriptException(
                "SitesNpmErrorInvalidName",
                "The npm script name is invalid."
            );
        }
    }

    public static TimeSpan TimeoutFor(string name)
    {
        return name.Equals("dev", StringComparison.OrdinalIgnoreCase)
            || name.Equals("start", StringComparison.OrdinalIgnoreCase)
            || name.Equals("serve", StringComparison.OrdinalIgnoreCase)
            || name.Equals("watch", StringComparison.OrdinalIgnoreCase)
                ? TimeSpan.FromHours(24)
                : TimeSpan.FromMinutes(30);
    }

    private static bool IsValidName(string name)
    {
        return name.Length > 0
            && name == name.Trim()
            && !name.StartsWith("-", StringComparison.Ordinal)
            && Encoding.UTF8.GetByteCount(name) <= MaximumScriptNameBytes
            && !name.Any(char.IsControl);
    }

    private sealed class NpmScriptComparer : IComparer<NpmScript>
    {
        public static NpmScriptComparer Instance { get; } = new();

        public int Compare(NpmScript? left, NpmScript? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var leftPriority = Array.FindIndex(
                PreferredOrder,
                value => value.Equals(left.Name, StringComparison.OrdinalIgnoreCase)
            );
            var rightPriority = Array.FindIndex(
                PreferredOrder,
                value => value.Equals(right.Name, StringComparison.OrdinalIgnoreCase)
            );
            if (leftPriority >= 0 || rightPriority >= 0)
            {
                if (leftPriority < 0) return 1;
                if (rightPriority < 0) return -1;
                return leftPriority.CompareTo(rightPriority);
            }
            return StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        }
    }
}

public static class NpmScriptRunner
{
    private const int MaximumCapturedCharacters = 1 * 1_024 * 1_024;

    public static NpmScriptInvocation CreateInvocation(
        NodeRuntimeInstaller installer,
        string projectDirectory,
        string? requestedVersion,
        string scriptName
    )
    {
        NpmScriptCatalog.ValidateName(scriptName);
        var projectPath = Path.GetFullPath(projectDirectory);
        var scripts = NpmScriptCatalog.Discover(projectPath);
        if (!scripts.Any(script => script.Name.Equals(scriptName, StringComparison.Ordinal)))
        {
            throw new NpmScriptException(
                "SitesNpmErrorScriptUnavailable",
                "The selected npm script is no longer available. Reload the script list."
            );
        }

        var version = ResolveInstalledVersion(installer, requestedVersion);
        var runtimeDirectory = Path.Combine(installer.RuntimeRoot, version);
        var node = Path.Combine(runtimeDirectory, "node.exe");
        var npmCli = Path.Combine(runtimeDirectory, "node_modules", "npm", "bin", "npm-cli.js");
        if (!File.Exists(node))
        {
            throw new NpmScriptException(
                "SitesNpmErrorNodeNotInstalled",
                $"Install the selected HerdMe Node.js {version} runtime first.",
                version
            );
        }
        if (!File.Exists(npmCli))
        {
            throw new NpmScriptException(
                "SitesNpmErrorNpmUnavailable",
                "The selected managed Node.js runtime does not contain npm."
            );
        }

        return new NpmScriptInvocation(
            node,
            npmCli,
            projectPath,
            scriptName,
            ManagedEnvironment(installer.SupportRoot, runtimeDirectory),
            NpmScriptCatalog.TimeoutFor(scriptName)
        );
    }

    public static NpmToolInvocation CreateToolInvocation(
        NodeRuntimeInstaller installer,
        string projectDirectory,
        string? requestedVersion,
        IReadOnlyList<string> arguments,
        TimeSpan timeout
    )
    {
        var supported = new[] { "install", "update", "audit" };
        if (arguments.Count is 0 or > 16
            || !supported.Contains(arguments[0], StringComparer.Ordinal)
            || arguments.Any(argument => string.IsNullOrWhiteSpace(argument)
                || argument.Length > 256 || argument.Any(char.IsControl))
            || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(2))
        {
            throw new ArgumentException("The npm workflow command is invalid.", nameof(arguments));
        }
        var version = ResolveInstalledVersion(installer, requestedVersion);
        var runtimeDirectory = Path.Combine(installer.RuntimeRoot, version);
        var node = Path.Combine(runtimeDirectory, "node.exe");
        var npmCli = Path.Combine(runtimeDirectory, "node_modules", "npm", "bin", "npm-cli.js");
        if (!File.Exists(node) || !File.Exists(npmCli))
        {
            throw new NpmScriptException(
                "SitesNpmErrorNpmUnavailable",
                "The selected managed Node.js runtime does not contain npm."
            );
        }
        return new NpmToolInvocation(
            node,
            npmCli,
            Path.GetFullPath(projectDirectory),
            arguments.ToArray(),
            ManagedEnvironment(installer.SupportRoot, runtimeDirectory),
            timeout
        );
    }

    public static async Task<NpmScriptResult> RunToolAsync(
        NpmToolInvocation invocation,
        IProgress<string>? outputProgress = null,
        CancellationToken cancellationToken = default
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.NodeExecutable,
            WorkingDirectory = invocation.ProjectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add(invocation.NpmCli);
        startInfo.ArgumentList.Add("--no-update-notifier");
        foreach (var argument in invocation.Arguments) startInfo.ArgumentList.Add(argument);
        foreach (var variable in invocation.Environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }
        using var process = Process.Start(startInfo)
            ?? throw new NpmScriptException(
                "SitesNpmErrorStartFailed",
                "The selected Node.js runtime could not start npm."
            );
        var capture = new BoundedOutputCapture(MaximumCapturedCharacters);
        var standardOutput = PumpAsync(process.StandardOutput, capture, outputProgress);
        var standardError = PumpAsync(process.StandardError, capture, outputProgress);
        using var timeoutCancellation = new CancellationTokenSource(invocation.Timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token
        );
        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(standardOutput, standardError);
            cancellationToken.ThrowIfCancellationRequested();
            throw new NpmScriptException(
                "SitesNpmErrorTimedOut",
                $"The npm command did not finish within {invocation.Timeout.TotalMinutes:0} minutes."
            );
        }
        await Task.WhenAll(standardOutput, standardError);
        return new NpmScriptResult(process.ExitCode, capture.Value);
    }

    public static async Task<NpmScriptResult> RunAsync(
        NpmScriptInvocation invocation,
        IProgress<string>? outputProgress = null,
        CancellationToken cancellationToken = default
    )
    {
        NpmScriptCatalog.ValidateName(invocation.ScriptName);
        if (!File.Exists(invocation.NodeExecutable))
        {
            throw new NpmScriptException(
                "SitesNpmErrorNodeUnavailable",
                "The selected managed Node.js executable is unavailable."
            );
        }
        if (!File.Exists(invocation.NpmCli))
        {
            throw new NpmScriptException(
                "SitesNpmErrorNpmUnavailable",
                "The selected managed Node.js runtime does not contain npm."
            );
        }
        var projectPath = Path.GetFullPath(invocation.ProjectDirectory);
        if (!Directory.Exists(projectPath) || invocation.Timeout <= TimeSpan.Zero)
        {
            throw new NpmScriptException(
                "SitesNpmErrorInvocationIncomplete",
                "The npm script invocation is incomplete."
            );
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.NodeExecutable,
            WorkingDirectory = projectPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add(invocation.NpmCli);
        startInfo.ArgumentList.Add("--no-update-notifier");
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add(invocation.ScriptName);
        foreach (var variable in invocation.Environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new NpmScriptException(
                "SitesNpmErrorStartFailed",
                "The selected Node.js runtime could not start npm."
            );
        var capture = new BoundedOutputCapture(MaximumCapturedCharacters);
        var standardOutput = PumpAsync(process.StandardOutput, capture, outputProgress);
        var standardError = PumpAsync(process.StandardError, capture, outputProgress);
        using var timeoutCancellation = new CancellationTokenSource(invocation.Timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token
        );
        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited while cancellation was being delivered.
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(standardOutput, standardError);
            cancellationToken.ThrowIfCancellationRequested();
            throw new NpmScriptException(
                "SitesNpmErrorTimedOut",
                $"The npm script did not finish within {invocation.Timeout.TotalMinutes:0} minutes.",
                invocation.Timeout.TotalMinutes
            );
        }
        await Task.WhenAll(standardOutput, standardError);
        return new NpmScriptResult(process.ExitCode, capture.Value);
    }

    private static string ResolveInstalledVersion(
        NodeRuntimeInstaller installer,
        string? requestedVersion
    )
    {
        if (!string.IsNullOrWhiteSpace(requestedVersion))
        {
            var normalized = requestedVersion.Trim().TrimStart('v');
            if (installer.InstalledVersions().Contains(normalized, StringComparer.Ordinal))
            {
                return normalized;
            }
            if (!normalized.Contains('.')
                && installer.InstalledVersion(normalized) is { } installedForMajor)
            {
                return installedForMajor;
            }
            throw new NpmScriptException(
                "SitesNpmErrorNodeNotInstalled",
                $"HerdMe Node.js {requestedVersion} is not installed.",
                requestedVersion
            );
        }

        var active = installer.LoadSettings().ActiveVersion;
        if (!string.IsNullOrWhiteSpace(active)
            && installer.InstalledVersions().Contains(active, StringComparer.Ordinal))
        {
            return active;
        }
        return installer.InstalledVersions().FirstOrDefault()
            ?? throw new NpmScriptException(
                "SitesNpmErrorInstallNode",
                "Install a HerdMe Node.js runtime before running npm scripts."
            );
    }

    private static IReadOnlyDictionary<string, string> ManagedEnvironment(
        string supportRoot,
        string runtimeDirectory
    )
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is not string key || variable.Value is not string value) continue;
            if (key.StartsWith("HERD_", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("NODE_OPTIONS", StringComparison.OrdinalIgnoreCase)
                || key.Equals("NODE_PATH", StringComparison.OrdinalIgnoreCase)
                || key.Equals("NPM_CONFIG_PREFIX", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            environment[key] = value;
        }
        var cache = Path.Combine(supportRoot, "Cache", "npm");
        Directory.CreateDirectory(cache);
        environment["PATH"] = string.Join(
            Path.PathSeparator,
            new[]
            {
                runtimeDirectory,
                Path.Combine(supportRoot, "bin"),
                environment.GetValueOrDefault("PATH", string.Empty)
            }.Where(value => !string.IsNullOrWhiteSpace(value))
        );
        environment["npm_config_cache"] = cache;
        environment["npm_config_update_notifier"] = "false";
        environment["NO_COLOR"] = "1";
        return environment;
    }

    private static async Task PumpAsync(
        StreamReader reader,
        BoundedOutputCapture capture,
        IProgress<string>? outputProgress
    )
    {
        var buffer = new char[4_096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory());
            if (count == 0) return;
            var chunk = new string(buffer, 0, count);
            capture.Append(chunk);
            outputProgress?.Report(chunk);
        }
    }

    private sealed class BoundedOutputCapture(int maximumCharacters)
    {
        private readonly object sync = new();
        private readonly StringBuilder output = new();

        public string Value
        {
            get
            {
                lock (sync) return output.ToString();
            }
        }

        public void Append(string value)
        {
            lock (sync)
            {
                output.Append(value);
                if (output.Length > maximumCharacters)
                {
                    output.Remove(0, output.Length - maximumCharacters);
                }
            }
        }
    }
}
