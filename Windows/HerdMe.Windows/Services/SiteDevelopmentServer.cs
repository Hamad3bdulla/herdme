using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed class SiteDevelopmentServer : IAsyncDisposable
{
    public enum DevelopmentMode
    {
        PrimaryProxy,
        LaravelAssets
    }

    private readonly List<Process> processes = [];
    private readonly List<WindowsJobObject> jobs = [];
    private string? logPath;
    private string? managedHotPath;

    public int? Port { get; private set; }

    public bool IsRunning => Port is int port && processes.Count > 0
        && IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Any(endpoint => endpoint.Port == port);

    public DevelopmentMode? Mode { get; private set; }

    public bool ProxiesSiteTraffic => Mode == DevelopmentMode.PrimaryProxy;

    public static DevelopmentMode? ModeFor(SiteRecord site)
    {
        var root = Path.GetFullPath(site.Path);
        if (IsLaravelProject(site, root) && HasDevScript(root))
            return DevelopmentMode.LaravelAssets;
        if (site.Framework.Equals("Node.js", StringComparison.OrdinalIgnoreCase)
            && HasDevScript(root)) return DevelopmentMode.PrimaryProxy;
        var frontend = Path.Combine(root, "frontend");
        return HasDevScript(frontend) ? DevelopmentMode.PrimaryProxy : null;
    }

    public static string? ProjectDirectory(SiteRecord site)
    {
        var root = Path.GetFullPath(site.Path);
        var mode = ModeFor(site);
        if (mode == DevelopmentMode.LaravelAssets) return root;
        if (mode == DevelopmentMode.PrimaryProxy && HasDevScript(root)) return root;
        var frontend = Path.Combine(root, "frontend");
        return HasDevScript(frontend) ? frontend : null;
    }

    public static bool IsDevelopmentSite(SiteRecord site) => ProjectDirectory(site) is not null;

    public async Task<int> StartAsync(
        SiteRecord site,
        NodeRuntimeInstaller nodeInstaller,
        CancellationToken cancellationToken = default
    )
    {
        if (IsRunning) return Port!.Value;
        await StopAsync();
        var project = ProjectDirectory(site)
            ?? throw new InvalidOperationException("This site has no npm dev script.");
        Mode = ModeFor(site)
            ?? throw new InvalidOperationException("This site has no supported development mode.");
        if (Mode == DevelopmentMode.LaravelAssets)
        {
            managedHotPath = Path.Combine(site.Path, "public", "hot");
            DeleteManagedHotFile();
        }
        if (!Directory.Exists(Path.Combine(project, "node_modules")))
        {
            throw new InvalidOperationException("Install the frontend dependencies before starting dev mode.");
        }
        await EnsureRequestedNodeAsync(nodeInstaller, site.NodeVersion, cancellationToken);
        var invocation = NpmScriptRunner.CreateInvocation(
            nodeInstaller,
            project,
            site.NodeVersion,
            "dev"
        );
        var port = AvailablePort();
        var supportRoot = nodeInstaller.SupportRoot;
        var logDirectory = Path.Combine(supportRoot, "Log", "development");
        Directory.CreateDirectory(logDirectory);
        logPath = Path.Combine(logDirectory, $"{SafeName(site.Name)}.log");
        BoundedLog.RotateIfNeeded(logPath);

        try
        {
            await StartCameraBackendIfNeededAsync(site.Path, cancellationToken);
            var startInfo = new ProcessStartInfo
            {
                FileName = invocation.NodeExecutable,
                WorkingDirectory = project,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(invocation.NpmCli);
            startInfo.ArgumentList.Add("--no-update-notifier");
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("dev");
            startInfo.ArgumentList.Add("--");
            var nextProject = IsNextProject(project);
            startInfo.ArgumentList.Add(nextProject ? "--hostname" : "--host");
            startInfo.ArgumentList.Add("127.0.0.1");
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(port.ToString());
            foreach (var variable in invocation.Environment)
                startInfo.Environment[variable.Key] = variable.Value;
            StartManaged(startInfo);
            await WaitUntilReadyAsync(port, cancellationToken);
            if (Mode == DevelopmentMode.LaravelAssets)
                await WaitForLaravelHotFileAsync(port, cancellationToken);
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
        Port = null;
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception error) when (error is InvalidOperationException or TimeoutException) { }
            process.Dispose();
        }
        processes.Clear();
        foreach (var job in jobs) job.Dispose();
        jobs.Clear();
        DeleteManagedHotFile();
        managedHotPath = null;
        Mode = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        GC.SuppressFinalize(this);
    }

    private async Task StartCameraBackendIfNeededAsync(
        string sitePath,
        CancellationToken cancellationToken
    )
    {
        var backend = Path.Combine(sitePath, "backend");
        var python = Path.Combine(backend, ".venv", "Scripts", "python.exe");
        if (!File.Exists(python) || !File.Exists(Path.Combine(backend, "app", "main.py"))) return;
        if (await AcceptsConnectionsAsync(8_000, cancellationToken)) return;
        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = backend,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-m", "uvicorn", "app.main:app", "--host", "127.0.0.1", "--port", "8000" })
            startInfo.ArgumentList.Add(argument);
        StartManaged(startInfo);
        await WaitUntilReadyAsync(8_000, cancellationToken);
    }

    private void StartManaged(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += LogLine;
        process.ErrorDataReceived += LogLine;
        if (!process.Start()) throw new InvalidOperationException("The development process could not start.");
        var job = new WindowsJobObject();
        job.Add(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        processes.Add(process);
        jobs.Add(job);
    }

    private void LogLine(object sender, DataReceivedEventArgs eventArgs)
    {
        if (eventArgs.Data is null || logPath is null) return;
        try { BoundedLog.AppendLine(logPath, $"[{DateTimeOffset.Now:O}] {eventArgs.Data}"); }
        catch (IOException) { }
    }

    private static bool HasDevScript(string path)
    {
        if (!File.Exists(Path.Combine(path, "package.json"))) return false;
        try { return NpmScriptCatalog.Discover(path).Any(script => script.Name == "dev"); }
        catch (Exception error) when (error is IOException or InvalidDataException or NpmScriptException) { return false; }
    }

    private static bool IsNextProject(string path)
    {
        try
        {
            var package = File.ReadAllText(Path.Combine(path, "package.json"));
            return package.Contains("\"next\"", StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
    }

    private static bool IsLaravelProject(SiteRecord site, string root)
    {
        return site.Framework.Equals("Laravel", StringComparison.OrdinalIgnoreCase)
            || File.Exists(Path.Combine(root, "artisan"));
    }

    private async Task WaitForLaravelHotFileAsync(
        int port,
        CancellationToken cancellationToken
    )
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (managedHotPath is not null && File.Exists(managedHotPath))
                {
                    var value = (await File.ReadAllTextAsync(managedHotPath, cancellationToken)).Trim();
                    if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                        && uri.Port == port) return;
                }
            }
            catch (IOException) { }
            await Task.Delay(100, cancellationToken);
        }
        throw new TimeoutException("Laravel Vite did not publish its hot-file endpoint.");
    }

    private void DeleteManagedHotFile()
    {
        if (managedHotPath is null) return;
        try
        {
            if (File.Exists(managedHotPath)) File.Delete(managedHotPath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task EnsureRequestedNodeAsync(
        NodeRuntimeInstaller installer,
        string? requestedVersion,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(requestedVersion))
        {
            await installer.EnsureActiveRuntimeAsync(cancellationToken: cancellationToken);
            return;
        }
        var normalized = requestedVersion.Trim().TrimStart('v');
        if (installer.InstalledVersions().Contains(normalized, StringComparer.Ordinal)
            || !normalized.Contains('.') && installer.InstalledVersion(normalized) is not null)
        {
            return;
        }
        var major = normalized.Split('.', 2)[0];
        if (!int.TryParse(major, out var parsedMajor) || parsedMajor is < 18 or > 99)
            throw new InvalidOperationException($"The requested Node.js version '{requestedVersion}' is invalid.");
        await installer.InstallAsync(major, cancellationToken);
    }

    private static async Task WaitUntilReadyAsync(int port, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await AcceptsConnectionsAsync(port, cancellationToken)) return;
            await Task.Delay(100, cancellationToken);
        }
        throw new TimeoutException($"The development server did not open port {port}.");
    }

    private static async Task<bool> AcceptsConnectionsAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
            return true;
        }
        catch (Exception error) when (error is SocketException or TimeoutException) { return false; }
    }

    private static int AvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static string SafeName(string value) => string.Concat(value.Select(character =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
}
