using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using HerdMe.Windows.Services;

namespace HerdMe.Cli;

[SupportedOSPlatform("windows")]
public static class HerdMeCli
{
    private const int NotRunningExitCode = 3;
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan LaunchWait = TimeSpan.FromSeconds(30);

    public static async Task<int> Main(string[] args)
    {
        var version = Version();
        var invocation = AppCommandProtocol.ParseCliArguments(args, Environment.CurrentDirectory, version);
        if (invocation.Request is not { } request)
        {
            var writer = invocation.ExitCode == 0 ? Console.Out : Console.Error;
            writer.WriteLine(invocation.LocalOutput);
            return invocation.ExitCode;
        }

        var response = await AppCommandClient.TrySendAsync(request, TimeSpan.FromSeconds(1), ResponseTimeout);
        if (response is null)
        {
            if (request.Command is "stop" or "ping")
            {
                Console.WriteLine("HerdMe is not running.");
                return request.Command == "stop" ? 0 : NotRunningExitCode;
            }
            if (request.Command == "status")
            {
                Console.WriteLine("HerdMe is not running. Run 'herdme start' or open HerdMe.");
                return NotRunningExitCode;
            }
            if (!LaunchApplication()) return 1;
            response = await AppCommandClient.SendWithRetryAsync(request, LaunchWait, ResponseTimeout);
            if (response is null)
            {
                Console.Error.WriteLine("HerdMe did not start in time. Open HerdMe and try again.");
                return 1;
            }
        }

        var output = response.Ok ? Console.Out : Console.Error;
        if (!string.IsNullOrWhiteSpace(response.Output)) output.WriteLine(response.Output);
        return response.Ok ? 0 : response.ExitCode;
    }

    private static bool LaunchApplication()
    {
        var application = Path.Combine(AppContext.BaseDirectory, "HerdMe.Windows.exe");
        if (!File.Exists(application))
        {
            Console.Error.WriteLine("HerdMe is not running, and HerdMe.Windows.exe is not next to herdme.exe.");
            return false;
        }
        Console.Error.WriteLine("Starting HerdMe in the background...");
        var startInfo = new ProcessStartInfo(application)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("--background");
        try
        {
            Process.Start(startInfo)?.Dispose();
            return true;
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            Console.Error.WriteLine($"HerdMe could not be started: {error.Message}");
            return false;
        }
    }

    private static string Version()
    {
        var informational = typeof(HerdMeCli).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational)) return "0.0.0";
        var metadata = informational.IndexOf('+', StringComparison.Ordinal);
        return metadata < 0 ? informational : informational[..metadata];
    }
}
