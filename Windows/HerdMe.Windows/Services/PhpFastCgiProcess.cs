using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed class PhpFastCgiProcess : IAsyncDisposable
{
    private Process? process;
    private WindowsJobObject? job;
    private string? logPath;

    public int? Port { get; private set; }

    public bool UsesHttpFallback { get; private set; }

    public bool IsRunning => process is { HasExited: false };

    public async Task<int> StartAsync(
        string phpCgiExecutable,
        PhpRuntimeLaunchContract contract,
        IReadOnlyDictionary<string, string>? sites = null,
        CancellationToken cancellationToken = default
    )
    {
        if (IsRunning && Port is not null) return Port.Value;
        if (!File.Exists(phpCgiExecutable))
        {
            throw new FileNotFoundException("The managed PHP runtime has no php-cgi.exe.", phpCgiExecutable);
        }

        await StopAsync();
        var port = AvailablePort();
        var supportPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe"
        );
        var logDirectory = Path.Combine(supportPath, "Log", "fastcgi");
        Directory.CreateDirectory(logDirectory);
        logPath = Path.Combine(logDirectory, $"php-{contract.Settings.PhpCycle}.log");
        BoundedLog.RotateIfNeeded(logPath);

        var startInfo = CreateStartInfo(phpCgiExecutable, contract, port);
        try
        {
            await StartProcessAsync(startInfo, port, cancellationToken);
            UsesHttpFallback = false;
        }
        catch (Win32Exception error) when (IsApplicationControlBlock(error))
        {
            await StopAsync();
            var phpExecutable = Path.Combine(
                Path.GetDirectoryName(phpCgiExecutable)!,
                "php.exe"
            );
            if (!File.Exists(phpExecutable)) throw;
            var router = WriteFallbackRouter(supportPath);
            var fallback = CreateFallbackStartInfo(
                phpExecutable,
                router,
                sites ?? new Dictionary<string, string>(),
                contract,
                port
            );
            await StartProcessAsync(fallback, port, cancellationToken);
            UsesHttpFallback = true;
            await DiagnosticLog.WriteFailureAsync(
                "php",
                "application-control-fallback",
                $"Windows Application Control blocked php-cgi.exe for PHP {contract.Settings.PhpCycle}. HerdMe started the compatible PHP HTTP fallback.",
                error.ToString(),
                context: new Dictionary<string, string?>
                {
                    ["phpCycle"] = contract.Settings.PhpCycle,
                    ["blockedExecutable"] = phpCgiExecutable
                }
            );
        }
        Port = port;
        return port;
    }

    private static ProcessStartInfo CreateStartInfo(
        string phpCgiExecutable,
        PhpRuntimeLaunchContract contract,
        int port
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = phpCgiExecutable,
            WorkingDirectory = Path.GetDirectoryName(phpCgiExecutable)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var option in contract.PhpOptions.OrderBy(option => option.Key, StringComparer.Ordinal))
        {
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add($"{option.Key}={option.Value}");
        }
        startInfo.ArgumentList.Add("-b");
        startInfo.ArgumentList.Add($"127.0.0.1:{port}");
        startInfo.Environment["PHPRC"] = Path.GetDirectoryName(phpCgiExecutable)!;
        startInfo.Environment["PHP_FCGI_CHILDREN"] = "4";
        startInfo.Environment["PHP_FCGI_MAX_REQUESTS"] = "500";
        return startInfo;
    }

    private async Task StartProcessAsync(
        ProcessStartInfo startInfo,
        int port,
        CancellationToken cancellationToken
    )
    {
        var candidate = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        candidate.OutputDataReceived += LogLine;
        candidate.ErrorDataReceived += LogLine;
        if (!candidate.Start()) throw new InvalidOperationException("The PHP server could not be started.");
        process = candidate;
        try
        {
            job = new WindowsJobObject();
            job.Add(candidate);
            candidate.BeginOutputReadLine();
            candidate.BeginErrorReadLine();
            await WaitUntilReadyAsync(candidate, port, cancellationToken);
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    internal static bool IsApplicationControlBlock(Win32Exception error) =>
        error.NativeErrorCode == 4_551
        || error.Message.Contains("Application Control policy", StringComparison.OrdinalIgnoreCase);

    private static ProcessStartInfo CreateFallbackStartInfo(
        string phpExecutable,
        string router,
        IReadOnlyDictionary<string, string> sites,
        PhpRuntimeLaunchContract contract,
        int port
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = phpExecutable,
            WorkingDirectory = Path.GetDirectoryName(router)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var option in contract.PhpOptions.OrderBy(option => option.Key, StringComparer.Ordinal))
        {
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add($"{option.Key}={option.Value}");
        }
        startInfo.ArgumentList.Add("-S");
        startInfo.ArgumentList.Add($"127.0.0.1:{port}");
        startInfo.ArgumentList.Add(router);
        startInfo.Environment["PHPRC"] = Path.GetDirectoryName(phpExecutable)!;
        startInfo.Environment["HERDME_SITE_MAP"] = JsonSerializer.Serialize(sites);
        return startInfo;
    }

    private static string WriteFallbackRouter(string supportPath)
    {
        var directory = Path.Combine(supportPath, "Runtime");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "php-http-router.php");
        const string contents = """
<?php
$sites = json_decode(getenv('HERDME_SITE_MAP') ?: '{}', true) ?: [];
$host = strtolower(preg_replace('/:\\d+$/', '', $_SERVER['HTTP_HOST'] ?? ''));
$root = $sites[$host] ?? null;
if (!is_string($root) || $root === '') { http_response_code(404); exit; }
$originalScheme = strtolower($_SERVER['HTTP_X_HERDME_ORIGINAL_SCHEME'] ?? 'http');
unset($_SERVER['HTTP_X_HERDME_ORIGINAL_SCHEME']);
if ($originalScheme === 'https') {
    $_SERVER['HTTPS'] = 'on';
    $_SERVER['REQUEST_SCHEME'] = 'https';
    $_SERVER['SERVER_PORT'] = '443';
}
$root = rtrim($root, '/\\\\');
$public = is_dir($root . DIRECTORY_SEPARATOR . 'public')
    ? $root . DIRECTORY_SEPARATOR . 'public' : $root;
$uriPath = rawurldecode(parse_url($_SERVER['REQUEST_URI'] ?? '/', PHP_URL_PATH) ?: '/');
$candidate = realpath($public . str_replace('/', DIRECTORY_SEPARATOR, $uriPath));
$publicReal = realpath($public);
if ($candidate && $publicReal && str_starts_with(strtolower($candidate), strtolower($publicReal . DIRECTORY_SEPARATOR)) && is_file($candidate)) {
    $script = $candidate;
} else {
    $script = $public . DIRECTORY_SEPARATOR . 'index.php';
}
if (!is_file($script)) { http_response_code(404); exit; }
$_SERVER['DOCUMENT_ROOT'] = $public;
$_SERVER['SCRIPT_FILENAME'] = $script;
$_SERVER['SCRIPT_NAME'] = $script === $public . DIRECTORY_SEPARATOR . 'index.php' ? '/index.php' : $uriPath;
chdir(dirname($script));
require $script;
""";
        if (!File.Exists(path) || File.ReadAllText(path) != contents)
        {
            File.WriteAllText(path, contents);
        }
        return path;
    }

    public async Task StopAsync()
    {
        var active = Interlocked.Exchange(ref process, null);
        Port = null;
        UsesHttpFallback = false;
        if (active is not null)
        {
            try
            {
                if (!active.HasExited) active.Kill(entireProcessTree: true);
                await active.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception) when (active.HasExited)
            {
            }
            finally
            {
                active.Dispose();
            }
        }
        job?.Dispose();
        job = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        GC.SuppressFinalize(this);
    }

    private void LogLine(object sender, DataReceivedEventArgs eventArgs)
    {
        if (eventArgs.Data is null || logPath is null) return;
        try
        {
            BoundedLog.AppendLine(logPath, $"[{DateTimeOffset.Now:O}] {eventArgs.Data}");
        }
        catch (IOException)
        {
        }
    }

    private static async Task WaitUntilReadyAsync(
        Process process,
        int port,
        CancellationToken cancellationToken
    )
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException($"php-cgi.exe exited with code {process.ExitCode}.");
            }
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken)
                    .AsTask().WaitAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
                return;
            }
            catch (Exception error) when (error is SocketException or TimeoutException)
            {
                await Task.Delay(40, cancellationToken);
            }
        }
        throw new TimeoutException("php-cgi.exe did not open its FastCGI port.");
    }

    private static int AvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
