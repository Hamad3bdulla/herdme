using System.Text.Json;

internal static partial class ContractChecks
{
    internal static void VerifyPlatformToolingContracts(string repositoryRoot)
    {
        var windows = Path.Combine(repositoryRoot, "Windows");
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "windows-x64.yml"));

        var accessibility = File.ReadAllText(Path.Combine(windows, "test-accessibility.ps1"));
        Check(workflow.Contains("./Windows/test-accessibility.ps1", StringComparison.Ordinal), "CI runs the accessibility check");
        Check(workflow.Contains("build/windows-accessibility", StringComparison.Ordinal), "CI keeps the accessibility report");
        Check(accessibility.Contains("Add-Type -AssemblyName UIAutomationClient", StringComparison.Ordinal), "the accessibility check uses the UI Automation client built into Windows");
        Check(!accessibility.Contains("Invoke-WebRequest", StringComparison.Ordinal) && !accessibility.Contains("HttpClient", StringComparison.Ordinal), "the accessibility check downloads nothing");
        foreach (var rule in new[] { "missing-name", "not-keyboard-focusable" })
        {
            Check(accessibility.Contains($"\"{rule}\"", StringComparison.Ordinal), $"the accessibility check reports {rule}");
        }
        Check(accessibility.Contains("throw \"The accessibility check found", StringComparison.Ordinal), "accessibility findings fail the check");
        Check(accessibility.Contains("Close HerdMe before the accessibility check", StringComparison.Ordinal), "the accessibility check never starts a second HerdMe");
        foreach (var page in new[] { "DashboardPageRoot", "SitesPageRoot", "ServicesPageRoot", "LogsPageRoot", "AboutPageRoot" })
        {
            Check(accessibility.Contains($"Page = \"{page}\"", StringComparison.Ordinal), $"the accessibility check scans {page}");
        }

        var build = File.ReadAllText(Path.Combine(windows, "build.ps1"));
        Check(build.Contains("[ValidateSet(\"x64\", \"ARM64\")]", StringComparison.Ordinal), "the build accepts x64 and ARM64");
        Check(build.Contains("\"win-arm64\"", StringComparison.Ordinal), "ARM64 builds use the win-arm64 runtime");
        Check(build.Contains("pass -SkipTests for a cross-compiled", StringComparison.Ordinal), "a cross-compiled ARM64 build never skips tests silently");
        var project = File.ReadAllText(Path.Combine(windows, "HerdMe.Windows", "HerdMe.Windows.csproj"));
        Check(project.Contains("<Platforms>x64;ARM64</Platforms>", StringComparison.Ordinal) && project.Contains("win-x64;win-arm64", StringComparison.Ordinal), "the app project builds for x64 and ARM64");
        var packaging = File.ReadAllText(Path.Combine(windows, "package-portable.ps1"));
        Check(packaging.Contains("HerdMe packages are x64 only.", StringComparison.Ordinal), "release packages stay x64 while ARM64 is a preview");
        Check(workflow.Contains("-Architecture ARM64 -Configuration Release -SkipTests", StringComparison.Ordinal) && workflow.Contains("0xAA64", StringComparison.Ordinal), "CI cross-compiles ARM64 and checks the machine type");
        Check(workflow.Contains("win-arm64-unsigned-preview", StringComparison.Ordinal), "the ARM64 artifact is labelled an unsigned preview");

        var sandbox = File.ReadAllText(Path.Combine(windows, "clean-acceptance.wsb"));
        Check(sandbox.Contains("<ReadOnly>true</ReadOnly>", StringComparison.Ordinal), "Windows Sandbox maps the repository read-only");
        Check(sandbox.Contains("<Networking>Enable</Networking>", StringComparison.Ordinal), "the clean profile can download official runtimes");

        // Valkey and Typesense have no official native Windows build, and HerdMe only installs
        // official downloads with a published SHA-256, so they stay unavailable on Windows.
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot, "HerdMe", "Resources", "runtime-catalog.json")));
        foreach (var service in catalog.RootElement.GetProperty("services").EnumerateArray())
        {
            var id = service.GetProperty("id").GetString();
            if (id is not ("valkey" or "typesense")) continue;
            var entry = service.GetProperty("windows");
            Check(!entry.GetProperty("installable").GetBoolean(), $"{id} is not offered on Windows without an official build");
            Check(entry.GetProperty("unavailableReason").GetString()!.Contains("official", StringComparison.Ordinal), $"{id} explains why it is unavailable");
        }
    }
}
