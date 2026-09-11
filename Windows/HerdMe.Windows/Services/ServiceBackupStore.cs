using System.Text.Json;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed record ServiceBackup(string Directory, string DefinitionId, DateTimeOffset CreatedAt, IReadOnlyList<Guid> Instances, string? RuntimeVersion = null);

public sealed class ServiceBackupStore(string supportRoot)
{
    public string Root => Path.Combine(supportRoot, "Backups", "services");

    public async Task<ServiceBackup> CreateAsync(string definitionId, IReadOnlyList<ManagedServiceInstance> instances,
        CancellationToken cancellationToken = default, string? runtimeVersion = null)
    {
        Directory.CreateDirectory(Root);
        var name = $"{definitionId}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var staging = Path.Combine(Root, ".pending-" + name);
        var destination = Path.Combine(Root, name);
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var instance in instances)
            {
                var source = Path.Combine(supportRoot, "Services", instance.Id.ToString("D"), "data");
                if (Directory.Exists(source)) await CopyDirectoryAsync(source, Path.Combine(staging, instance.Id.ToString("D")), cancellationToken);
            }
            await File.WriteAllTextAsync(Path.Combine(staging, "instances.json"), JsonSerializer.Serialize(instances), cancellationToken);
            var backup = new ServiceBackup(destination, definitionId, DateTimeOffset.UtcNow, instances.Select(item => item.Id).ToArray(), runtimeVersion);
            await File.WriteAllTextAsync(Path.Combine(staging, "backup.json"), JsonSerializer.Serialize(backup), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return backup;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    public async Task RestoreInstanceAsync(ServiceBackup backup, Guid instanceId, CancellationToken cancellationToken)
    {
        var known = List(backup.DefinitionId).FirstOrDefault(item => item.Directory == backup.Directory)
            ?? throw new InvalidOperationException("The backup is no longer available.");
        if (!known.Instances.Contains(instanceId)) throw new InvalidOperationException("This backup does not include the selected service.");
        var source = Path.Combine(known.Directory, instanceId.ToString("D"));
        if (!Directory.Exists(source)) throw new InvalidOperationException("This backup has no data directory for the selected service.");
        var parent = Path.Combine(supportRoot, "Services", instanceId.ToString("D"));
        var staging = Path.Combine(parent, ".restore-" + Guid.NewGuid().ToString("N"));
        var previous = Path.Combine(parent, ".before-restore-" + Guid.NewGuid().ToString("N"));
        try
        {
            await CopyDirectoryAsync(source, staging, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ServicePackageInstaller.PromoteRuntime(staging, Path.Combine(parent, "data"), previous);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    public IReadOnlyList<ServiceBackup> List(string definitionId)
    {
        if (!Directory.Exists(Root)) return [];
        var backups = new List<ServiceBackup>();
        foreach (var directory in Directory.EnumerateDirectories(Root, definitionId + "-*"))
        {
            try
            {
                var backup = JsonSerializer.Deserialize<ServiceBackup>(File.ReadAllText(Path.Combine(directory, "backup.json")));
                if (backup?.DefinitionId == definitionId) backups.Add(backup with { Directory = directory });
            }
            catch (Exception error) when (error is IOException or JsonException) { }
        }
        return backups.OrderByDescending(item => item.CreatedAt).ToArray();
    }

    public bool Contains(string definitionId, Guid instanceId)
        => List(definitionId).Any(backup => backup.Instances.Contains(instanceId));

    internal static async Task CopyDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Service backups cannot follow directory links.");
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Service backups cannot follow file or directory links.");
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0) await CopyDirectoryAsync(entry, target, cancellationToken);
            else
            {
                await using var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
        }
    }
}
