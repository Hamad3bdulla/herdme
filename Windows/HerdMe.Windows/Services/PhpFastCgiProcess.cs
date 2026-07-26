using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed class PhpFastCgiProcess : IAsyncDisposable
{
    private Process? process;
    private WindowsJobObject? job;
    private string? logPath;

    public int? Port { get; private set; }

    public bool IsRunning => process is { HasExited: false };

    public async Task<int> StartAsync(
        string phpCgiExecutable,
        PhpRuntimeLaunchContract contract,
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

        var candidate = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        candidate.OutputDataReceived += LogLine;
        candidate.ErrorDataReceived += LogLine;
        if (!candidate.Start()) throw new InvalidOperationException("php-cgi.exe could not be started.");
        process = candidate;
        try
        {
            job = new WindowsJobObject();
            job.Add(candidate);
            candidate.BeginOutputReadLine();
            candidate.BeginErrorReadLine();
            await WaitUntilReadyAsync(candidate, port, cancellationToken);
            Port = port;
            return port;
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        var active = Interlocked.Exchange(ref process, null);
        Port = null;
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
