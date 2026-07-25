using System.Text.Json.Serialization;

namespace HerdMe.Windows.Models;

public sealed class PhpRuntimeSettings
{
    public int MemoryLimitMegabytes { get; set; } = 512;

    public int MaxUploadMegabytes { get; set; } = 100;

    public string PhpCycle { get; set; } = "8.4";

    public DebuggerSettings Debugger { get; set; } = new();
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
