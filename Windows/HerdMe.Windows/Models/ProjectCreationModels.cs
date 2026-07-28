using System.Text.Json;
using System.Text.RegularExpressions;

namespace HerdMe.Windows.Models;

public sealed record ComposerRelease(
    string Version,
    string Sha256,
    Uri DownloadUri
);

public sealed record LaravelProjectRequest(
    string Name,
    string ParentDirectory,
    string StarterKit,
    string TestingFramework,
    bool InstallBoost,
    bool InitializeGit,
    string? CustomStarterKit = null
);

public sealed record PresentedCommandError(string Message, string? TechnicalDetails);

public static partial class CommandErrorPresenter
{
    public static PresentedCommandError Present(
        string rawValue,
        string fallback = "The operation could not be completed."
    )
    {
        var extracted = ExtractDetails(rawValue);
        var cleaned = Clean(extracted.Details);
        var normalized = cleaned.ToLowerInvariant();
        string message;
        if (normalized.Contains("no space left on device", StringComparison.Ordinal)
            || normalized.Contains("not enough space on the disk", StringComparison.Ordinal))
        {
            message = "There is not enough free disk space to finish the operation. Free some space and try again.";
        }
        else if (normalized.Contains("could not be opened in append mode", StringComparison.Ordinal)
            || normalized.Contains("unexpectedvalueexception", StringComparison.Ordinal)
                && normalized.Contains("streamhandler", StringComparison.Ordinal))
        {
            message = "Laravel could not write to the new project's log files. Check disk space and folder permissions, then try again.";
        }
        else if (normalized.Contains("permission denied", StringComparison.Ordinal)
            || normalized.Contains("operation not permitted", StringComparison.Ordinal)
            || normalized.Contains("access is denied", StringComparison.Ordinal))
        {
            message = "HerdMe does not have permission to write the required files. Choose a writable folder and try again.";
        }
        else if (normalized.Contains("could not resolve host", StringComparison.Ordinal)
            || normalized.Contains("network is unreachable", StringComparison.Ordinal)
            || normalized.Contains("connection timed out", StringComparison.Ordinal))
        {
            message = "The download server could not be reached. Check the network connection and try again.";
        }
        else if (IsPlainMessage(cleaned))
        {
            message = cleaned;
        }
        else
        {
            message = fallback;
        }

        var details = extracted.Context is null
            ? cleaned
            : extracted.Context + Environment.NewLine + Environment.NewLine + cleaned;
        if (string.IsNullOrWhiteSpace(details) || details.Equals(message, StringComparison.Ordinal))
        {
            return new PresentedCommandError(message, null);
        }
        const int limit = 6_000;
        if (details.Length > limit) details = "..." + Environment.NewLine + details[^limit..];
        return new PresentedCommandError(message, details);
    }

    private static (string? Context, string Details) ExtractDetails(string rawValue)
    {
        try
        {
            using var document = JsonDocument.Parse(rawValue);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return (null, rawValue);
            var context = new List<string>();
            if (document.RootElement.TryGetProperty("directory", out var directory)
                && directory.GetString() is { Length: > 0 } directoryValue)
            {
                context.Add("Project folder: " + directoryValue);
            }
            if (document.RootElement.TryGetProperty("log", out var log)
                && log.GetString() is { Length: > 0 } logValue)
            {
                context.Add("Installer log: " + logValue);
            }
            var details = document.RootElement.TryGetProperty("tail", out var tail)
                ? tail.GetString() ?? rawValue
                : rawValue;
            return (context.Count == 0 ? null : string.Join(Environment.NewLine, context), details);
        }
        catch (JsonException)
        {
            return (null, rawValue);
        }
    }

    private static string Clean(string value)
    {
        return AnsiSequence().Replace(value, string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();
    }

    private static bool IsPlainMessage(string value)
    {
        if (value.Length is 0 or > 500 || value.Contains('\n')) return false;
        return !value.StartsWith('{')
            && !value.Contains("stack trace", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("vendor/", StringComparison.OrdinalIgnoreCase)
            && !value.Contains(" at ", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiSequence();
}

public enum LaravelProjectCreationStage
{
    ValidatingRequest,
    PreparingLaravelInstaller,
    CreatingLaravelProject,
    InstallingLaravelBoost,
    PreparingNodeRuntime,
    InstallingFrontendDependencies,
    BuildingFrontendAssets,
    InitializingGitRepository,
    VerifyingProject,
    RegisteringSite,
    Completed
}

public static class LaravelProjectCreationStages
{
    public static string TitleKey(LaravelProjectCreationStage stage)
    {
        if (!Enum.IsDefined(stage)) throw new ArgumentOutOfRangeException(nameof(stage), stage, null);
        return $"SitesProjectStage{stage}Title";
    }

    public static string DetailKey(LaravelProjectCreationStage stage)
    {
        if (!Enum.IsDefined(stage)) throw new ArgumentOutOfRangeException(nameof(stage), stage, null);
        return $"SitesProjectStage{stage}Detail";
    }

    public static IReadOnlyList<LaravelProjectCreationStage> For(LaravelProjectRequest request)
    {
        var stages = new List<LaravelProjectCreationStage>
        {
            LaravelProjectCreationStage.ValidatingRequest,
            LaravelProjectCreationStage.PreparingLaravelInstaller,
            LaravelProjectCreationStage.CreatingLaravelProject
        };
        if (request.InstallBoost) stages.Add(LaravelProjectCreationStage.InstallingLaravelBoost);
        if (RequiresFrontendAssets(request))
        {
            stages.Add(LaravelProjectCreationStage.PreparingNodeRuntime);
            stages.Add(LaravelProjectCreationStage.InstallingFrontendDependencies);
            stages.Add(LaravelProjectCreationStage.BuildingFrontendAssets);
        }
        if (request.InitializeGit) stages.Add(LaravelProjectCreationStage.InitializingGitRepository);
        stages.Add(LaravelProjectCreationStage.VerifyingProject);
        stages.Add(LaravelProjectCreationStage.RegisteringSite);
        stages.Add(LaravelProjectCreationStage.Completed);
        return stages;
    }

    public static bool RequiresFrontendAssets(LaravelProjectRequest request)
    {
        return request.StarterKit.Trim().ToLowerInvariant()
            is "react" or "vue" or "svelte" or "livewire";
    }

    public static string Title(LaravelProjectCreationStage stage) => stage switch
    {
        LaravelProjectCreationStage.ValidatingRequest => "Checking project details",
        LaravelProjectCreationStage.PreparingLaravelInstaller => "Preparing Laravel Installer",
        LaravelProjectCreationStage.CreatingLaravelProject => "Creating Laravel project",
        LaravelProjectCreationStage.InstallingLaravelBoost => "Installing Laravel Boost",
        LaravelProjectCreationStage.PreparingNodeRuntime => "Preparing Node.js",
        LaravelProjectCreationStage.InstallingFrontendDependencies => "Installing frontend packages",
        LaravelProjectCreationStage.BuildingFrontendAssets => "Building frontend assets",
        LaravelProjectCreationStage.InitializingGitRepository => "Initializing Git repository",
        LaravelProjectCreationStage.VerifyingProject => "Verifying Laravel project",
        LaravelProjectCreationStage.RegisteringSite => "Registering local site",
        LaravelProjectCreationStage.Completed => "Site created",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
    };

    public static string Detail(LaravelProjectCreationStage stage) => stage switch
    {
        LaravelProjectCreationStage.ValidatingRequest => "Verifying the name, location, and project folder.",
        LaravelProjectCreationStage.PreparingLaravelInstaller =>
            "Using the managed Laravel Installer already on this PC, and installing it only if needed.",
        LaravelProjectCreationStage.CreatingLaravelProject =>
            "Running the installed Laravel Installer to create and configure the application.",
        LaravelProjectCreationStage.InstallingLaravelBoost => "Adding Laravel Boost as a development dependency.",
        LaravelProjectCreationStage.PreparingNodeRuntime => "Making sure a HerdMe-managed Node.js runtime is ready.",
        LaravelProjectCreationStage.InstallingFrontendDependencies => "Restoring the starter kit's npm packages.",
        LaravelProjectCreationStage.BuildingFrontendAssets => "Compiling the production Vite assets used by the site.",
        LaravelProjectCreationStage.InitializingGitRepository => "Creating the initial local Git repository.",
        LaravelProjectCreationStage.VerifyingProject => "Checking that Laravel finished with its required files.",
        LaravelProjectCreationStage.RegisteringSite => "Adding the project to HerdMe's local sites.",
        LaravelProjectCreationStage.Completed => "Your project is ready to open.",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
    };
}
