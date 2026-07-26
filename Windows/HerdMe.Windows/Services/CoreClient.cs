using System.Diagnostics;
using System.Text.Json;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed class CoreClient
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CoreClient(string? executablePath = null)
    {
        ExecutablePath = string.IsNullOrWhiteSpace(executablePath)
            ? Path.Combine(AppContext.BaseDirectory, "Runtime", "herdme-core.exe")
            : Path.GetFullPath(executablePath);
    }

    public string ExecutablePath { get; }

    public async Task<DoctorResponse> DoctorAsync(CancellationToken cancellationToken = default)
    {
        return Deserialize<DoctorResponse>(await RunAsync(["doctor"], cancellationToken));
    }

    public async Task<PhpExtensionReport> ValidatePhpAsync(
        string phpExecutable,
        CancellationToken cancellationToken = default
    )
    {
        var modules = await RunExecutableAsync(
            phpExecutable,
            ["-m"],
            standardInput: null,
            cancellationToken: cancellationToken
        );
        var report = await RunExecutableAsync(
            ExecutablePath,
            ["php-extensions"],
            standardInput: modules,
            cancellationToken: cancellationToken
        );
        return Deserialize<PhpExtensionReport>(report);
    }

    public async Task<IReadOnlyList<SiteRecord>> ScanAsync(
        IEnumerable<string> roots,
        string tld,
        CancellationToken cancellationToken = default
    )
    {
        return await ScanAsync(roots, tld, [], cancellationToken);
    }

    public async Task<IReadOnlyList<SiteRecord>> ScanAsync(
        IEnumerable<string> roots,
        string tld,
        IEnumerable<string> linkedSites,
        CancellationToken cancellationToken = default
    )
    {
        var arguments = new List<string> { "scan", "--tld", tld };
        foreach (var root in roots)
        {
            arguments.Add("--path");
            arguments.Add(root);
        }
        foreach (var site in linkedSites)
        {
            arguments.Add("--site");
            arguments.Add(site);
        }
        return Deserialize<SitesResponse>(await RunAsync(arguments, cancellationToken)).Sites
            .Where(site => !SiteConfigurationStore.BelongsToOtherHerd(site.Path))
            .ToList();
    }

    private async Task<string> RunAsync(
        IEnumerable<string> arguments,
        CancellationToken cancellationToken
    )
    {
        if (!File.Exists(ExecutablePath))
        {
            throw new FileNotFoundException(
                "The portable HerdMe core was not found. Run Windows/build.ps1 first.",
                ExecutablePath
            );
        }

        return await RunExecutableAsync(
            ExecutablePath,
            arguments,
            standardInput: null,
            cancellationToken: cancellationToken
        );
    }

    private static async Task<string> RunExecutableAsync(
        string executable,
        IEnumerable<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"{Path.GetFileName(executable)} could not be started.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OperationTimeout);
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), timeout.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error) ? "HerdMe Core failed." : error.Trim()
                );
            }
            return output;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"{Path.GetFileName(executable)} did not finish within {OperationTimeout.TotalSeconds:0} seconds."
            );
        }
    }

    private static T Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidDataException("HerdMe Core returned an invalid response.");
    }
}
