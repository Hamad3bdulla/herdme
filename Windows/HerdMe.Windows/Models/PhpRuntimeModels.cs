using System.Text.Json.Serialization;

namespace HerdMe.Windows.Models;

public sealed class PhpRuntimeSettings
{
    public int MemoryLimitMegabytes { get; set; } = 512;

    public int MaxUploadMegabytes { get; set; } = 100;

    public int MaxExecutionTimeSeconds { get; set; } = 120;

    public int MaxInputTimeSeconds { get; set; } = 60;

    public int MaxInputVariables { get; set; } = 1_000;

    public int MaxFileUploads { get; set; } = 20;

    public bool DisplayErrors { get; set; } = true;

    public bool OpcacheEnabled { get; set; } = true;

    public string Timezone { get; set; } = "UTC";

    public string PhpCycle { get; set; } = RuntimeCatalog.DefaultPhpCycle;

    public DebuggerSettings Debugger { get; set; } = new();

    public Dictionary<string, PhpVersionConfiguration> Versions { get; set; } = [];
}

public sealed class PhpVersionConfiguration
{
    public int MemoryLimitMegabytes { get; set; } = 512;

    public int MaxUploadMegabytes { get; set; } = 100;

    public int MaxExecutionTimeSeconds { get; set; } = 120;

    public int MaxInputTimeSeconds { get; set; } = 60;

    public int MaxInputVariables { get; set; } = 1_000;

    public int MaxFileUploads { get; set; } = 20;

    public bool DisplayErrors { get; set; } = true;

    public bool OpcacheEnabled { get; set; } = true;

    public string Timezone { get; set; } = "UTC";

    public Dictionary<string, bool> Extensions { get; set; } = new(
        StringComparer.OrdinalIgnoreCase
    );
}

public sealed class DebuggerSettings
{
    public bool Enabled { get; set; }

    public bool DetectBreakpoints { get; set; } = true;

    public int Port { get; set; } = 9_003;

    public string IdeKey { get; set; } = "VSCODE";
}

public sealed record PhpRuntimeLaunchContract(
    PhpExtensionReport Extensions,
    PhpRuntimeSettings Settings,
    IReadOnlyDictionary<string, string> PhpOptions
);
