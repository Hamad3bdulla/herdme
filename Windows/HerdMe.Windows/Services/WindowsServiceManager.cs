using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed class WindowsServiceManager : IAsyncDisposable
{
    private sealed record ActiveService(Process Process, WindowsJobObject Job, int? ConsolePort);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object sync = new();
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly Dictionary<Guid, ActiveService> active = [];
    private readonly ServicePackageInstaller installer;
    private readonly WindowsServiceCredentialStore credentialStore;

    public WindowsServiceManager(
        string? supportRoot = null,
        WindowsServiceCredentialStore? credentialStore = null
    )
    {
        SupportRoot = supportRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe"
        );
        installer = new ServicePackageInstaller(SupportRoot);
        this.credentialStore = credentialStore ?? new WindowsServiceCredentialStore();
    }

    public string SupportRoot { get; }

    public string ConfigurationPath => Path.Combine(SupportRoot, "Config", "services.json");

    public string? LastLoadWarning { get; private set; }

    public string? LastBackupPath { get; private set; }

    public IReadOnlyList<ManagedServiceInstance> LoadInstances()
    {
        if (!File.Exists(ConfigurationPath)) return [];
        LastLoadWarning = null;
        LastBackupPath = null;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<ManagedServiceInstance>>(
                File.ReadAllText(ConfigurationPath)
            ) ?? throw new JsonException("The service settings document is empty.");
            return Normalize(loaded);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            PreserveUnreadableConfiguration();
            return [];
        }
    }

    private void PreserveUnreadableConfiguration()
    {
        var backupPath = Path.Combine(
            Path.GetDirectoryName(ConfigurationPath)!,
            $"services.corrupt-{Guid.NewGuid():N}.json"
        );
        try
        {
            File.Move(ConfigurationPath, backupPath);
            LastBackupPath = backupPath;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            backupPath = ConfigurationPath;
        }
        LastLoadWarning =
            $"HerdMe could not read its service settings. The original file was preserved at {backupPath}. No replacement settings were saved.";
    }

    public void SaveInstances(IEnumerable<ManagedServiceInstance> instances)
    {
        var normalized = Normalize(instances).ToList();
        if (OperatingSystem.IsWindows())
        {
            foreach (var instance in normalized) _ = credentialStore.GetOrCreate(instance.Id);
        }
        var directory = Path.GetDirectoryName(ConfigurationPath)!;
        Directory.CreateDirectory(directory);
        var temporary = ConfigurationPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, JsonOptions));
        File.Move(temporary, ConfigurationPath, true);
    }

    public ManagedServiceState State(Guid id, string definitionId)
    {
        lock (sync)
        {
            if (active.TryGetValue(id, out var service) && !service.Process.HasExited)
            {
                return ManagedServiceState.Running;
            }
        }
        return installer.IsInstalled(definitionId)
            ? ManagedServiceState.Stopped
            : ManagedServiceState.NotInstalled;
    }

    public bool IsInstalled(string definitionId) => installer.IsInstalled(definitionId);

    public string InstalledVersion(string definitionId) => installer.InstalledVersion(definitionId) ?? "-";

    public Task<ServicePackageRelease> InstallAsync(
        string definitionId,
        CancellationToken cancellationToken = default
    ) => installer.InstallAsync(definitionId, cancellationToken);

    public Task<ServicePackageRelease> ResolveReleaseAsync(
        string definitionId,
        CancellationToken cancellationToken = default
    ) => installer.ResolveReleaseAsync(definitionId, cancellationToken);

    public string DataDirectory(Guid id) => Path.Combine(SupportRoot, "Services", id.ToString("D"), "data");

    public int? ConsolePort(Guid id)
    {
        lock (sync)
        {
            return active.TryGetValue(id, out var service) && !service.Process.HasExited
                ? service.ConsolePort
                : null;
        }
    }

    public Uri? ConsoleUri(Guid id)
    {
        return ConsolePort(id) is { } port
            ? new Uri($"http://127.0.0.1:{port}")
            : null;
    }

    public async Task StartAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            await StartCoreAsync(id, cancellationToken);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private async Task StartCoreAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Managed service processes require Windows.");
        }
        var instance = LoadInstances().FirstOrDefault(candidate => candidate.Id == id)
            ?? throw new InvalidOperationException("The service instance no longer exists.");
        if (State(id, instance.DefinitionId) == ManagedServiceState.Running) return;
        if (!installer.IsInstalled(instance.DefinitionId))
        {
            throw new InvalidOperationException($"Install {instance.Name} before starting it.");
        }
        EnsurePortAvailable(instance.Port);

        var dataDirectory = DataDirectory(instance.Id);
        var logDirectory = Path.Combine(SupportRoot, "Log", "services");
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(logDirectory);
        var credentials = RequiresCredentials(instance.DefinitionId)
            ? credentialStore.GetOrCreate(instance.Id)
            : null;
        if (instance.DefinitionId == "mariadb")
        {
            await InitializeMariaDbAsync(dataDirectory, cancellationToken);
        }
        else if (instance.DefinitionId == "mysql")
        {
            await InitializeMySqlAsync(dataDirectory, cancellationToken);
        }
        else if (instance.DefinitionId == "postgresql")
        {
            await InitializePostgreSqlAsync(dataDirectory, credentials!, cancellationToken);
        }

        var consolePort = instance.DefinitionId is "minio" or "rustfs" ? FindAvailablePort() : 0;
        var spec = BuildLaunchSpec(
            instance,
            installer.ExecutablePath(instance.DefinitionId),
            dataDirectory,
            consolePort,
            credentials
        );
        var logPath = Path.Combine(logDirectory, instance.Id.ToString("D") + ".log");
        BoundedLog.RotateIfNeeded(logPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.Executable,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in spec.Arguments) startInfo.ArgumentList.Add(argument);
        foreach (var variable in spec.Environment) startInfo.Environment[variable.Key] = variable.Value;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) => AppendLog(logPath, args.Data);
        process.ErrorDataReceived += (_, args) => AppendLog(logPath, args.Data);
        AppendLog(logPath, $"[HerdMe] Starting {instance.Name} on port {instance.Port}");
        if (!process.Start()) throw new InvalidOperationException($"{instance.Name} could not be started.");

        WindowsJobObject? job = null;
        try
        {
            job = new WindowsJobObject();
            job.Add(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await WaitUntilReadyAsync(process, instance.Port, cancellationToken);
            if (credentials is not null
                && DatabaseServiceAuthenticator.ProtectedDefinitions.Contains(instance.DefinitionId))
            {
                await DatabaseServiceAuthenticator.SecureAsync(
                    instance,
                    spec.Executable,
                    dataDirectory,
                    credentials,
                    cancellationToken
                );
            }
            lock (sync)
            {
                active[instance.Id] = new ActiveService(
                    process,
                    job,
                    consolePort > 0 ? consolePort : null
                );
            }
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.Dispose();
            job?.Dispose();
            throw;
        }
    }

    public async Task StopAsync(Guid id)
    {
        await lifecycle.WaitAsync();
        try
        {
            await StopCoreAsync(id);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private async Task StopCoreAsync(Guid id)
    {
        ActiveService? service;
        lock (sync)
        {
            active.Remove(id, out service);
        }
        if (service is null) return;
        try
        {
            if (!service.Process.HasExited) service.Process.Kill(entireProcessTree: true);
            await service.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception) when (service.Process.HasExited)
        {
        }
        finally
        {
            service.Process.Dispose();
            service.Job.Dispose();
        }
    }

    public async Task StartEnabledAsync(CancellationToken cancellationToken = default)
    {
        foreach (var instance in LoadInstances().Where(instance => instance.StartAutomatically))
        {
            if (!installer.IsInstalled(instance.DefinitionId)) continue;
            try
            {
                await StartAsync(instance.Id, cancellationToken);
            }
            catch (Exception error)
            {
                AppendLog(
                    Path.Combine(SupportRoot, "Log", "services", instance.Id.ToString("D") + ".log"),
                    "[HerdMe] Automatic start failed: " + error.Message
                );
            }
        }
    }

    public async Task StopAllAsync()
    {
        Guid[] identifiers;
        lock (sync) identifiers = active.Keys.ToArray();
        foreach (var identifier in identifiers) await StopAsync(identifier);
    }

    public async Task RemoveAsync(Guid id, bool deleteData)
    {
        await lifecycle.WaitAsync();
        try
        {
            await StopCoreAsync(id);
            SaveInstances(LoadInstances().Where(instance => instance.Id != id));
            credentialStore.Delete(id);
            if (deleteData)
            {
                var instanceRoot = Path.Combine(SupportRoot, "Services", id.ToString("D"));
                if (Directory.Exists(instanceRoot)) Directory.Delete(instanceRoot, true);
            }
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public static ServiceLaunchSpec BuildLaunchSpec(
        ManagedServiceInstance instance,
        string executable,
        string dataDirectory,
        int minioConsolePort = 0,
        ServiceCredentials? credentials = null
    )
    {
        if (RequiresLaunchCredentials(instance.DefinitionId) && credentials is null)
        {
            throw new ArgumentNullException(
                nameof(credentials),
                $"{instance.Name} requires managed service credentials."
            );
        }
        var runtimeDirectory = instance.DefinitionId is "mariadb" or "mysql" or "postgresql" or "mongodb"
            ? Directory.GetParent(Path.GetDirectoryName(executable)!)!.FullName
            : Path.GetDirectoryName(executable)!;
        return instance.DefinitionId switch
        {
            "mariadb" => new ServiceLaunchSpec(
                executable,
                runtimeDirectory,
                [
                    "--no-defaults",
                    "--console",
                    $"--basedir={runtimeDirectory}",
                    $"--datadir={dataDirectory}",
                    $"--port={instance.Port}",
                    "--bind-address=127.0.0.1",
                    "--skip-name-resolve",
                    $"--pid-file={Path.Combine(dataDirectory, "mariadb.pid")}"
                ],
                new Dictionary<string, string>()
            ),
            "mysql" => new ServiceLaunchSpec(
                executable,
                runtimeDirectory,
                [
                    "--no-defaults",
                    "--console",
                    $"--basedir={runtimeDirectory}",
                    $"--datadir={dataDirectory}",
                    $"--port={instance.Port}",
                    "--bind-address=127.0.0.1",
                    "--mysqlx=0",
                    "--skip-name-resolve",
                    $"--pid-file={Path.Combine(dataDirectory, "mysql.pid")}"
                ],
                new Dictionary<string, string>()
            ),
            "postgresql" => new ServiceLaunchSpec(
                executable,
                runtimeDirectory,
                [
                    "-D", dataDirectory,
                    "-h", "127.0.0.1",
                    "-p", instance.Port.ToString()
                ],
                new Dictionary<string, string>()
            ),
            "mongodb" => new ServiceLaunchSpec(
                executable,
                runtimeDirectory,
                [
                    "--dbpath", dataDirectory,
                    "--bind_ip", "127.0.0.1",
                    "--port", instance.Port.ToString()
                ],
                new Dictionary<string, string>()
            ),
            "redis" => new ServiceLaunchSpec(
                executable,
                dataDirectory,
                [
                    "--bind", "127.0.0.1",
                    "--port", instance.Port.ToString(),
                    "--dir", dataDirectory.Replace('\\', '/'),
                    "--protected-mode", "yes",
                    "--appendonly", "yes",
                    "--daemonize", "no"
                ],
                new Dictionary<string, string>()
            ),
            "meilisearch" => new ServiceLaunchSpec(
                executable,
                dataDirectory,
                [],
                new Dictionary<string, string>
                {
                    ["MEILI_HTTP_ADDR"] = $"127.0.0.1:{instance.Port}",
                    ["MEILI_DB_PATH"] = dataDirectory,
                    ["MEILI_NO_ANALYTICS"] = "true"
                }
            ),
            "minio" when minioConsolePort is > 0 and <= 65_535 => new ServiceLaunchSpec(
                executable,
                dataDirectory,
                [
                    "server", dataDirectory,
                    "--address", $"127.0.0.1:{instance.Port}",
                    "--console-address", $"127.0.0.1:{minioConsolePort}"
                ],
                new Dictionary<string, string>
                {
                    ["MINIO_ROOT_USER"] = credentials!.Username,
                    ["MINIO_ROOT_PASSWORD"] = credentials.Secret
                }
            ),
            "minio" => throw new ArgumentOutOfRangeException(
                nameof(minioConsolePort), "MinIO requires an available console port."
            ),
            "rustfs" when minioConsolePort is > 0 and <= 65_535 => new ServiceLaunchSpec(
                executable,
                dataDirectory,
                [
                    "server", dataDirectory,
                    "--address", $"127.0.0.1:{instance.Port}",
                    "--console-address", $"127.0.0.1:{minioConsolePort}"
                ],
                new Dictionary<string, string>
                {
                    ["RUSTFS_ACCESS_KEY"] = credentials!.Username,
                    ["RUSTFS_SECRET_KEY"] = credentials.Secret
                }
            ),
            "rustfs" => throw new ArgumentOutOfRangeException(
                nameof(minioConsolePort), "RustFS requires an available console port."
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(instance.DefinitionId), instance.DefinitionId, "Unsupported managed service."
            )
        };
    }

    public ServiceEnvironmentUpdate AddToEnvironment(
        string projectPath,
        ManagedServiceInstance instance
    )
    {
        var credentials = credentialStore.GetOrCreate(instance.Id);
        return ServiceEnvironmentFile.Update(projectPath, instance, credentials);
    }

    public void OpenInTablePlus(ManagedServiceInstance instance)
    {
        TablePlusConnection.Open(ConnectionUri(instance));
    }

    public Uri ConnectionUri(ManagedServiceInstance instance)
    {
        var credentials = DatabaseServiceAuthenticator.ProtectedDefinitions.Contains(instance.DefinitionId)
            ? credentialStore.GetOrCreate(instance.Id)
            : null;
        return TablePlusConnection.UriFor(instance, credentials)
            ?? throw new NotSupportedException(
                $"Connection URLs are not available for {instance.Name}."
            );
    }

    private static bool RequiresCredentials(string definitionId) => definitionId is
        "mysql" or "mariadb" or "postgresql" or "typesense" or "minio" or "rustfs";

    private static bool RequiresLaunchCredentials(string definitionId) => definitionId is
        "typesense" or "minio" or "rustfs";

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync();
        lifecycle.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task InitializeMariaDbAsync(
        string dataDirectory,
        CancellationToken cancellationToken
    )
    {
        if (Directory.Exists(Path.Combine(dataDirectory, "mysql"))) return;
        var executable = installer.ExecutablePath("mariadb");
        var runtimeDirectory = Directory.GetParent(Path.GetDirectoryName(executable)!)!.FullName;
        var initializer = Path.Combine(runtimeDirectory, "bin", "mariadb-install-db.exe");
        if (!File.Exists(initializer))
        {
            throw new FileNotFoundException("The MariaDB package has no database initializer.", initializer);
        }
        var startInfo = new ProcessStartInfo
        {
            FileName = initializer,
            WorkingDirectory = runtimeDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--no-defaults");
        startInfo.ArgumentList.Add("--auth-root-authentication-method=normal");
        startInfo.ArgumentList.Add($"--basedir={runtimeDirectory}");
        startInfo.ArgumentList.Add($"--datadir={dataDirectory}");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("MariaDB's data directory could not be initialized.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = (await standardOutput) + Environment.NewLine + (await standardError);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("MariaDB initialization failed: " + output.Trim());
        }
    }

    private async Task InitializeMySqlAsync(
        string dataDirectory,
        CancellationToken cancellationToken
    )
    {
        if (Directory.Exists(Path.Combine(dataDirectory, "mysql"))) return;
        var executable = installer.ExecutablePath("mysql");
        var runtimeDirectory = Directory.GetParent(Path.GetDirectoryName(executable)!)!.FullName;
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = runtimeDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--no-defaults");
        startInfo.ArgumentList.Add("--initialize-insecure");
        startInfo.ArgumentList.Add($"--basedir={runtimeDirectory}");
        startInfo.ArgumentList.Add($"--datadir={dataDirectory}");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("MySQL's data directory could not be initialized.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = (await standardOutput) + Environment.NewLine + (await standardError);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("MySQL initialization failed: " + output.Trim());
        }
    }

    private async Task InitializePostgreSqlAsync(
        string dataDirectory,
        ServiceCredentials credentials,
        CancellationToken cancellationToken
    )
    {
        if (File.Exists(Path.Combine(dataDirectory, "PG_VERSION"))) return;
        var executable = installer.ExecutablePath("postgresql");
        var runtimeDirectory = Directory.GetParent(Path.GetDirectoryName(executable)!)!.FullName;
        var initializer = Path.Combine(runtimeDirectory, "bin", "initdb.exe");
        if (!File.Exists(initializer))
        {
            throw new FileNotFoundException("The PostgreSQL package has no database initializer.", initializer);
        }
        var startInfo = new ProcessStartInfo
        {
            FileName = initializer,
            WorkingDirectory = runtimeDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-D");
        startInfo.ArgumentList.Add(dataDirectory);
        var passwordPath = Path.Combine(
            Directory.GetParent(dataDirectory)!.FullName,
            $".herdme-initdb-{Guid.NewGuid():N}.password"
        );
        try
        {
            await File.WriteAllTextAsync(
                passwordPath,
                credentials.Secret + Environment.NewLine,
                new System.Text.UTF8Encoding(false),
                cancellationToken
            );
            startInfo.ArgumentList.Add($"--username={credentials.Username}");
            startInfo.ArgumentList.Add($"--pwfile={passwordPath}");
            startInfo.ArgumentList.Add("--encoding=UTF8");
            startInfo.ArgumentList.Add("--auth-local=scram-sha-256");
            startInfo.ArgumentList.Add("--auth-host=scram-sha-256");
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("PostgreSQL's data directory could not be initialized.");
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = (await standardOutput) + Environment.NewLine + (await standardError);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("PostgreSQL initialization failed: " + output.Trim());
            }
        }
        finally
        {
            if (File.Exists(passwordPath)) File.Delete(passwordPath);
        }
    }

    private static IReadOnlyList<ManagedServiceInstance> Normalize(
        IEnumerable<ManagedServiceInstance> instances
    )
    {
        var result = new List<ManagedServiceInstance>();
        var identifiers = new HashSet<Guid>();
        foreach (var source in instances)
        {
            var definition = ManagedServiceCatalog.All.FirstOrDefault(candidate =>
                candidate.Id.Equals(source.DefinitionId, StringComparison.OrdinalIgnoreCase)
            );
            if (definition is null) continue;
            var identifier = source.Id == Guid.Empty || !identifiers.Add(source.Id)
                ? Guid.NewGuid()
                : source.Id;
            identifiers.Add(identifier);
            result.Add(new ManagedServiceInstance
            {
                Id = identifier,
                DefinitionId = definition.Id,
                Name = string.IsNullOrWhiteSpace(source.Name) ? definition.Name : source.Name.Trim(),
                Port = source.Port is > 0 and <= 65_535 ? source.Port : definition.DefaultPort,
                StartAutomatically = source.StartAutomatically
            });
        }
        return result;
    }

    public static bool IsPortAvailable(int port)
    {
        if (port is <= 0 or > 65_535) return false;
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    public static int? AvailablePort(
        int startingAt,
        IEnumerable<int>? reservedPorts = null
    )
    {
        return AvailablePort(
            startingAt,
            reservedPorts is null ? [] : new HashSet<int>(reservedPorts),
            IsPortAvailable
        );
    }

    internal static int? AvailablePort(
        int startingAt,
        IReadOnlySet<int> reservedPorts,
        Func<int, bool> canBind
    )
    {
        if (startingAt is <= 0 or > 65_535) return null;
        for (var port = startingAt; port <= 65_535; port++)
        {
            if (!reservedPorts.Contains(port) && canBind(port)) return port;
        }
        for (var port = 1_024; port < startingAt; port++)
        {
            if (!reservedPorts.Contains(port) && canBind(port)) return port;
        }
        return null;
    }

    private static void EnsurePortAvailable(int port)
    {
        if (port is <= 0 or > 65_535) throw new ArgumentOutOfRangeException(nameof(port));
        if (!IsPortAvailable(port))
        {
            throw new InvalidOperationException($"Port {port} is already in use.");
        }
    }

    private static int FindAvailablePort()
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

    private static async Task WaitUntilReadyAsync(
        Process process,
        int port,
        CancellationToken cancellationToken
    )
    {
        for (var attempt = 0; attempt < 160; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException($"The service exited with code {process.ExitCode}.");
            }
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken)
                    .AsTask().WaitAsync(TimeSpan.FromMilliseconds(150), cancellationToken);
                return;
            }
            catch (Exception error) when (error is SocketException or TimeoutException)
            {
                await Task.Delay(50, cancellationToken);
            }
        }
        throw new TimeoutException($"The service did not open port {port}.");
    }

    private static void AppendLog(string path, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            BoundedLog.AppendLine(path, $"[{DateTimeOffset.Now:O}] {line}");
        }
        catch (IOException)
        {
        }
    }
}
