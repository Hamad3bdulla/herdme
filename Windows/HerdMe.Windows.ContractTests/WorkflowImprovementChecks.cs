using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;

internal static partial class ContractChecks
{
    internal static async Task VerifyWorkflowImprovementsAsync(string root)
    {
        var preflight = Path.Combine(root, "preflight");
        InstallationPreflight.EnsureStorage(preflight, 1);
        Check(Directory.Exists(preflight), "installation preflight creates a writable destination");
        var bytes = Encoding.UTF8.GetBytes("a complete verified package");
        var checksum = Convert.ToHexString(SHA256.HashData(bytes));
        foreach (var supportsResume in new[] { true, false })
        {
            var resumed = false;
            var handler = new SequenceHttpMessageHandler(
                _ =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes[..8]) };
                    response.Content.Headers.ContentLength = bytes.Length;
                    response.Headers.ETag = new EntityTagHeaderValue("\"original\"");
                    return response;
                },
                request =>
                {
                    resumed = request.Headers.Range?.Ranges.Single().From == 8
                        && request.Headers.IfRange?.EntityTag?.Tag == "\"original\"";
                    var response = new HttpResponseMessage(supportsResume ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                    { Content = new ByteArrayContent(supportsResume ? bytes[8..] : bytes) };
                    response.Headers.ETag = new EntityTagHeaderValue("\"original\"");
                    if (supportsResume) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(8, bytes.Length - 1, bytes.Length);
                    return response;
                });
            using var client = new HttpClient(handler);
            var destination = Path.Combine(root, "resume-" + supportsResume);
            await ServicePackageInstaller.DownloadAndVerifyAsync(new ServicePackageRelease("mysql", "1", "package.zip",
                ServicePackageChecksumAlgorithm.Sha256, checksum, new Uri("https://example.test/package"), true),
                destination, CancellationToken.None, client, maximumAttempts: 2, delayFactory: _ => TimeSpan.Zero);
            Check(resumed && File.ReadAllBytes(destination).SequenceEqual(bytes),
                "partial downloads validate Range and If-Range and safely handle servers ignoring resume");
        }

        var operationCenter = new RuntimeOperations();
        var attempts = 0;
        try
        {
            await operationCenter.RunAsync("php:8.4", "PHP", async (token, _) =>
            {
                await Task.Yield();
                if (++attempts == 1) throw new IOException("fixture");
                return 42;
            }, CancellationToken.None);
        }
        catch (IOException) { }
        Check(operationCenter.Snapshot().Single().Progress.Stage == ServiceInstallationStage.Failed,
            "shared operation history retains failed runtime installations");
        await operationCenter.RetryAsync("php:8.4");
        Check(attempts == 2 && operationCenter.Snapshot().Single().Progress.Stage == ServiceInstallationStage.Completed,
            "shared download center retries the complete runtime installation");

        var store = new SiteConfigurationStore(Path.Combine(root, "upgrade-settings"));
        var project = Path.Combine(root, "existing-project");
        store.UpdateRoots([project]);
        store.ToggleFavorite(project);
        store.UpdateTld("local");
        var reloaded = new SiteConfigurationStore(store.SupportRoot).Load();
        Check(reloaded.FavoriteSites.Single() == project && reloaded.Roots.Single() == project && reloaded.Tld == "local",
            "settings changes and reload preserve favorites and existing project configuration");
        store.ToggleFavorite(project.ToUpperInvariant());
        Check(store.Load().FavoriteSites.Count == 0, "favorite toggles compare Windows paths case-insensitively");

        var instance = new ManagedServiceInstance { DefinitionId = "mysql" };
        var backupRoot = Path.Combine(root, "backup-fixture");
        var data = Path.Combine(backupRoot, "Services", instance.Id.ToString("D"), "data");
        Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(Path.Combine(data, "database.bin"), "original database");
        var backupStore = new ServiceBackupStore(backupRoot);
        var backup = await backupStore.CreateAsync("mysql", [instance], runtimeVersion: "8.4");
        await File.WriteAllTextAsync(Path.Combine(data, "database.bin"), "changed database");
        Check(await File.ReadAllTextAsync(Path.Combine(backup.Directory, instance.Id.ToString("D"), "database.bin")) == "original database",
            "service backup is independent of later data changes");
        await backupStore.RestoreInstanceAsync(backup, instance.Id, CancellationToken.None);
        Check(await File.ReadAllTextAsync(Path.Combine(data, "database.bin")) == "original database",
            "data recovery restores the selected instance without replacing runtime files");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await backupStore.CreateAsync("mysql", [instance], cancelled.Token); Check(false, "cancelled backup fails"); }
        catch (OperationCanceledException) { }
        Check(backupStore.List("mysql").Count == 1 && !Directory.EnumerateDirectories(backupStore.Root, ".pending-*").Any(),
            "cancelled backups never publish incomplete data and remove their staging directory");
    }
}
