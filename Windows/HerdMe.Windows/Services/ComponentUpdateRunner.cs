using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed record ComponentUpdateResult(ManagedComponentUpdate Update, UpdateOutcome Outcome, string? Error = null);

// What "Update now" will interrupt, shown before anything stops.
public sealed record UpdateImpact(
    IReadOnlyList<string> StoppedServices,
    IReadOnlyList<string> StoppedPhpLines,
    int StoppedSites,
    TimeSpan Duration,
    TimeSpan SiteDowntime,
    long DownloadBytes,
    long RequiredBytes
)
{
    public bool StopsSomething => StoppedServices.Count > 0 || StoppedSites > 0;
}

// The new version was installed but did not start, so the previous one was put back.
public sealed class ComponentUpdateRestoredException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// Installs component updates for the Updates page and for automatic installs: one at a time,
/// continuing past failures, stopping only what uses the component (the sites of a PHP line
/// that changes, the running instances of a service) and only right before its files are
/// swapped, keeping the replaced version for rollback, putting it back when the new one does
/// not start, and writing every outcome to the update history.
/// </summary>
public sealed class ComponentUpdateRunner
{
    private readonly SiteConfigurationStore settingsStore;
    private readonly WindowsLocalEnvironment environment;
    private readonly PhpRuntimeInstaller phpInstaller;
    private readonly PhpRuntimePolicy runtimePolicy;
    private readonly NodeRuntimeInstaller nodeInstaller;
    private readonly ComposerToolManager composerTools;
    private readonly GitRuntimeInstaller gitInstaller;
    private readonly XdebugManager xdebugManager;
    private readonly WindowsServiceManager serviceManager;
    private readonly WindowsUserPathManager userPathManager;
    private readonly UpdatePreferencesStore preferences;
    private readonly UpdateHistoryStore history;
    private readonly UpdateRollbackStore rollbacks;
    private readonly SemaphoreSlim batchLock = new(1, 1);
    private readonly object sync = new();
    private readonly List<string> queued = [];
    private readonly Dictionary<string, ComponentUpdateResult> lastResults = new(StringComparer.OrdinalIgnoreCase);
    private string? active;

    public ComponentUpdateRunner(
        SiteConfigurationStore settingsStore,
        WindowsLocalEnvironment environment,
        PhpRuntimeInstaller phpInstaller,
        PhpRuntimePolicy runtimePolicy,
        NodeRuntimeInstaller nodeInstaller,
        ComposerToolManager composerTools,
        GitRuntimeInstaller gitInstaller,
        XdebugManager xdebugManager,
        WindowsServiceManager serviceManager,
        WindowsUserPathManager userPathManager,
        UpdatePreferencesStore preferences,
        UpdateHistoryStore history,
        UpdateRollbackStore rollbacks
    )
    {
        this.settingsStore = settingsStore;
        this.environment = environment;
        this.phpInstaller = phpInstaller;
        this.runtimePolicy = runtimePolicy;
        this.nodeInstaller = nodeInstaller;
        this.composerTools = composerTools;
        this.gitInstaller = gitInstaller;
        this.xdebugManager = xdebugManager;
        this.serviceManager = serviceManager;
        this.userPathManager = userPathManager;
        this.preferences = preferences;
        this.history = history;
        this.rollbacks = rollbacks;
    }

    public UpdatePreferencesStore Preferences => preferences;

    public UpdateHistoryStore History => history;

    public UpdateRollbackStore Rollbacks => rollbacks;

    // Raised (from any thread) when a row starts, finishes or leaves the queue.
    public event EventHandler? StateChanged;

    public bool IsBusy
    {
        get
        {
            lock (sync) return active is not null || queued.Count > 0;
        }
    }

    public string? Active
    {
        get
        {
            lock (sync) return active;
        }
    }

    public bool IsQueued(string operationKey)
    {
        lock (sync) return queued.Contains(operationKey, StringComparer.OrdinalIgnoreCase);
    }

    // The last outcome of a row, so a failure keeps its reason and Retry until the next try.
    public ComponentUpdateResult? LastResult(string operationKey)
    {
        lock (sync) return lastResults.GetValueOrDefault(operationKey);
    }

    public void ClearResults()
    {
        lock (sync) lastResults.Clear();
        RaiseStateChanged();
    }

    // Cancel on a row that has not started yet takes it out of the batch.
    public bool Dequeue(string operationKey)
    {
        bool removed;
        lock (sync) removed = queued.RemoveAll(key => key.Equals(operationKey, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed) RaiseStateChanged();
        return removed;
    }

    // Composer and the Laravel installer update together, so they are one operation.
    public static string OperationKey(ManagedComponentUpdate update) => OperationKey(update.Id);

    public static string OperationKey(string id) => id is "composer" or "laravel-installer" ? "php-tools" : id;

    public async Task<IReadOnlyList<ComponentUpdateResult>> RunAsync(
        IReadOnlyList<ManagedComponentUpdate> updates,
        CancellationToken cancellationToken = default
    )
    {
        var groups = updates
            .GroupBy(OperationKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.ToList())
            .OrderBy(group => BatchOrder(group[0]))
            .ToList();
        var results = new List<ComponentUpdateResult>();
        if (groups.Count == 0) return results;
        lock (sync)
        {
            foreach (var group in groups)
            {
                var key = OperationKey(group[0]);
                lastResults.Remove(key);
                if (!queued.Contains(key, StringComparer.OrdinalIgnoreCase) && key != active) queued.Add(key);
            }
        }
        RaiseStateChanged();
        try
        {
            await batchLock.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            lock (sync) queued.RemoveAll(key => groups.Any(group => OperationKey(group[0]) == key));
            RaiseStateChanged();
            throw;
        }
        try
        {
            if (DiskSpaceProblem(groups.Select(group => group[0]).ToList()) is { } space)
            {
                foreach (var group in groups) results.AddRange(Record(group, UpdateOutcome.Failed, space));
                return results;
            }
            await WithStoppedEnvironmentAsync(async hold =>
            {
                foreach (var group in groups)
                {
                    var key = OperationKey(group[0]);
                    lock (sync)
                    {
                        if (queued.RemoveAll(item => item.Equals(key, StringComparison.OrdinalIgnoreCase)) == 0)
                        {
                            // Cancelled before it started.
                            results.AddRange(group.Select(item => new ComponentUpdateResult(item, UpdateOutcome.Cancelled)));
                            continue;
                        }
                        active = key;
                    }
                    RaiseStateChanged();
                    var outcome = await InstallGroupAsync(group, hold, cancellationToken);
                    results.AddRange(outcome);
                    lock (sync)
                    {
                        active = null;
                        lastResults[key] = outcome[0];
                    }
                    RaiseStateChanged();
                }
            }, results);
        }
        finally
        {
            lock (sync)
            {
                queued.RemoveAll(key => groups.Any(group => OperationKey(group[0]).Equals(key, StringComparison.OrdinalIgnoreCase)));
                active = null;
            }
            batchLock.Release();
            RaiseStateChanged();
        }
        return results;
    }

    private async Task<List<ComponentUpdateResult>> InstallGroupAsync(
        List<ManagedComponentUpdate> group,
        EnvironmentHold hold,
        CancellationToken cancellationToken
    )
    {
        var update = group[0];
        UpdateOutcome outcome;
        string? reason = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await InstallAsync(update, group, hold, cancellationToken);
            outcome = UpdateOutcome.Updated;
        }
        catch (OperationCanceledException)
        {
            outcome = UpdateOutcome.Cancelled;
        }
        catch (ComponentUpdateRestoredException restored)
        {
            outcome = UpdateOutcome.Restored;
            reason = restored.Message;
        }
        catch (Exception error)
        {
            outcome = UpdateOutcome.Failed;
            reason = error.Message;
        }
        return Record(group, outcome, reason);
    }

    private List<ComponentUpdateResult> Record(List<ManagedComponentUpdate> group, UpdateOutcome outcome, string? reason)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in group)
        {
            history.Add(new UpdateHistoryEntry(item.Id, item.Name, item.InstalledVersion, item.LatestVersion, now, outcome, reason));
            if (outcome == UpdateOutcome.Updated) preferences.ForgetCachedUpdate(item.Id);
        }
        return group.Select(item => new ComponentUpdateResult(item, outcome, reason)).ToList();
    }

    private async Task InstallAsync(
        ManagedComponentUpdate update,
        IReadOnlyList<ManagedComponentUpdate> group,
        EnvironmentHold hold,
        CancellationToken cancellationToken
    )
    {
        if (update.Id.StartsWith("php:", StringComparison.OrdinalIgnoreCase))
        {
            var cycle = update.Id[4..];
            // The new build downloads while the sites keep running; they stop only for the swap,
            // and only when they use this PHP line.
            Func<Task>? beforePromote = null;
            if (hold.CyclesInUse.Contains(cycle, StringComparer.Ordinal))
            {
                beforePromote = () => StopEnvironmentOnceAsync(hold);
            }
            var previous = phpInstaller.InstalledVersion(cycle);
            var release = await phpInstaller.InstallAsync(cycle, cancellationToken, beforePromote);
            if (beforePromote is not null
                && previous is not null
                && !previous.Equals(release.Version, StringComparison.OrdinalIgnoreCase))
            {
                hold.Swapped.Add(update);
            }
            await phpInstaller.EnsureManagedConfigurationAsync(cycle, cancellationToken);
            SynchronizeUserPath();
            return;
        }
        if (update.Id.StartsWith("node:", StringComparison.OrdinalIgnoreCase))
        {
            var major = update.Id[5..];
            var previous = nodeInstaller.InstalledVersion(major);
            var release = await nodeInstaller.InstallAsync(major, cancellationToken);
            // Node.js versions sit side by side; the older one stays installed for a week.
            if (previous is not null && !previous.Equals(release.Version, StringComparison.OrdinalIgnoreCase))
            {
                rollbacks.KeepSideBySide(update.Id, update.Name, previous, release.Version, DateTimeOffset.UtcNow);
            }
            SynchronizeUserPath();
            return;
        }
        if (update.Id is "composer" or "laravel-installer")
        {
            var key = OperationKey(update);
            await RuntimeOperations.Shared.RunAsync(key, string.Join(", ", group.Select(item => item.Name)), async (token, progress) =>
            {
                progress.Report(new(key, ServiceInstallationStage.Installing));
                await composerTools.InstallOrUpdateAsync(runtimePolicy.Load().PhpCycle, token);
            }, cancellationToken);
            SynchronizeUserPath();
            return;
        }
        if (update.Id.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            await RuntimeOperations.Shared.RunAsync("git", update.Name, async (token, progress) =>
            {
                progress.Report(new("git", ServiceInstallationStage.Installing));
                await gitInstaller.InstallOrUpdateAsync(token);
            }, cancellationToken);
            SynchronizeUserPath();
            return;
        }
        if (update.Id.StartsWith("xdebug:", StringComparison.OrdinalIgnoreCase))
        {
            var cycle = update.Id[7..];
            await RuntimeOperations.Shared.RunAsync(update.Id, update.Name, async (token, progress) =>
            {
                progress.Report(new(update.Id, ServiceInstallationStage.Installing));
                // The extension is loaded by the sites of this PHP line only.
                if (hold.CyclesInUse.Contains(cycle, StringComparer.Ordinal)) await StopEnvironmentOnceAsync(hold);
                await xdebugManager.InstallAsync(phpInstaller.PhpExecutable(cycle), token);
            }, cancellationToken);
            return;
        }
        if (update.Id.StartsWith("service:", StringComparison.OrdinalIgnoreCase))
        {
            await UpdateServiceAsync(update, cancellationToken);
            return;
        }
        throw new InvalidOperationException(ServiceText.Format(
            "UpdatesUnsupportedComponent",
            "HerdMe cannot update {0} yet.",
            update.Name
        ));
    }

    // Kept for the whole batch: the sites stop at most once and start again at the end.
    private sealed class EnvironmentHold(IReadOnlyList<string> cyclesInUse)
    {
        public IReadOnlyList<string> CyclesInUse { get; } = cyclesInUse;

        public bool Stopped { get; set; }

        public bool Restart { get; set; }

        // PHP lines whose files changed while the sites were stopped (put back if they fail).
        public List<ManagedComponentUpdate> Swapped { get; } = [];
    }

    private async Task WithStoppedEnvironmentAsync(Func<EnvironmentHold, Task> update, List<ComponentUpdateResult>? results)
    {
        var hold = new EnvironmentHold(environment.PhpCyclesInUse());
        try
        {
            await update(hold);
        }
        finally
        {
            if (hold.Restart) await RestartEnvironmentAsync(hold, results);
        }
    }

    private async Task StopEnvironmentOnceAsync(EnvironmentHold hold)
    {
        if (hold.Stopped) return;
        hold.Stopped = true;
        hold.Restart = environment.IsRunning || environment.IsDegraded;
        if (hold.Restart) await environment.StopAsync();
    }

    // Starts the sites again; when a PHP line that just changed keeps them from starting, its
    // previous build goes back and they start with that.
    private async Task RestartEnvironmentAsync(EnvironmentHold hold, List<ComponentUpdateResult>? results)
    {
        Exception? failure = null;
        try
        {
            await environment.StartConfiguredAsync(settingsStore);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            failure = error;
        }
        if (failure is null && !environment.IsDegraded) return;
        if (hold.Swapped.Count == 0)
        {
            await WriteFailureAsync("environment-restart", "The sites did not start again after an update.", failure);
            return;
        }

        if (environment.IsRunning || environment.IsDegraded) await environment.StopAsync();
        var reason = failure?.Message ?? ServiceText.Get("UpdatesSitesDidNotStart", "The sites did not start with the new version.");
        foreach (var update in hold.Swapped)
        {
            var point = rollbacks.Find(update.Id, DateTimeOffset.UtcNow);
            if (point is not { Kind: UpdateRollbackKind.Directory }) continue;
            try
            {
                RestorePhpDirectory(update.Id[4..], point);
                await phpInstaller.EnsureManagedConfigurationAsync(update.Id[4..]);
                var message = ServiceText.Format(
                    "UpdatesRestoredMessage",
                    "{0} {1} did not start ({2}); {3} was put back.",
                    update.Name,
                    point.ReplacedBy,
                    reason,
                    point.Version
                );
                history.Add(new UpdateHistoryEntry(update.Id, update.Name, point.ReplacedBy, point.Version, DateTimeOffset.UtcNow, UpdateOutcome.Restored, message));
                if (results is not null)
                {
                    var index = results.FindIndex(item => item.Update.Id.Equals(update.Id, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0) results[index] = results[index] with { Outcome = UpdateOutcome.Restored, Error = message };
                }
                lock (sync)
                {
                    lastResults[OperationKey(update)] = new ComponentUpdateResult(update, UpdateOutcome.Restored, message);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                await WriteFailureAsync("php-restore", "The previous PHP build could not be put back.", error);
            }
        }
        try
        {
            await environment.StartConfiguredAsync(settingsStore);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await WriteFailureAsync("environment-restart", "The sites did not start after the previous PHP build was put back.", error);
        }
        RaiseStateChanged();
    }

    private async Task UpdateServiceAsync(ManagedComponentUpdate update, CancellationToken cancellationToken)
    {
        var definitionId = update.Id[8..];
        var instances = serviceManager.LoadInstances()
            .Where(instance => instance.DefinitionId.Equals(
                definitionId,
                StringComparison.OrdinalIgnoreCase
            ))
            .ToArray();
        var running = instances.Where(instance => serviceManager.State(
            instance.Id,
            instance.DefinitionId
        ) == ManagedServiceState.Running).ToArray();
        foreach (var instance in running) await serviceManager.StopAsync(instance.Id);
        var installed = false;
        Exception? startFailure = null;
        try
        {
            await serviceManager.InstallAsync(definitionId, cancellationToken);
            installed = true;
        }
        finally
        {
            try
            {
                foreach (var instance in running) await serviceManager.StartAsync(instance.Id);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                startFailure = error;
            }
        }
        if (!installed || startFailure is null) return;

        // The new version does not start: put the previous one and its data back.
        var point = rollbacks.Find(update.Id, DateTimeOffset.UtcNow);
        if (point is not { Kind: UpdateRollbackKind.Directory }) throw startFailure;
        await RestoreServiceAsync(definitionId, point, instances, running, restoreData: true, CancellationToken.None);
        throw new ComponentUpdateRestoredException(
            ServiceText.Format(
                "UpdatesRestoredMessage",
                "{0} {1} did not start ({2}); {3} was put back.",
                update.Name,
                point.ReplacedBy,
                startFailure.Message,
                point.Version
            ),
            startFailure
        );
    }

    private async Task RestoreServiceAsync(
        string definitionId,
        UpdateRollbackPoint point,
        IReadOnlyList<ManagedServiceInstance> instances,
        IReadOnlyList<ManagedServiceInstance> running,
        bool restoreData,
        CancellationToken cancellationToken
    )
    {
        foreach (var instance in instances)
        {
            if (serviceManager.State(instance.Id, instance.DefinitionId) == ManagedServiceState.Running)
            {
                await serviceManager.StopAsync(instance.Id);
            }
        }
        // The installer may still be finishing its bookkeeping for a moment.
        for (var attempt = 0; attempt < 50 && serviceManager.IsInstalling(definitionId); attempt++)
        {
            await Task.Delay(100, cancellationToken);
        }
        rollbacks.RestoreDirectory(point);
        if (restoreData)
        {
            // The newer version may already have converted the data; use the copy taken right
            // before the update (a copy of the current data is made first).
            var backup = serviceManager.Backups.List(definitionId)
                .Where(item => string.Equals(item.RuntimeVersion, point.Version, StringComparison.OrdinalIgnoreCase)
                    && item.CreatedAt <= point.KeptAt + TimeSpan.FromMinutes(1))
                .OrderByDescending(item => item.CreatedAt)
                .FirstOrDefault();
            if (backup is not null)
            {
                foreach (var instance in instances.Where(instance => backup.Instances.Contains(instance.Id)))
                {
                    await serviceManager.RestoreDataAsync(instance.Id, backup, cancellationToken);
                }
            }
        }
        foreach (var instance in running) await serviceManager.StartAsync(instance.Id, cancellationToken);
    }

    public bool CanRollBack(string id) => rollbacks.Find(id, DateTimeOffset.UtcNow) is not null;

    public bool HasDataBackup(string id)
    {
        if (!id.StartsWith("service:", StringComparison.OrdinalIgnoreCase)) return false;
        var point = rollbacks.Find(id, DateTimeOffset.UtcNow);
        return point is not null && serviceManager.Backups.List(id[8..])
            .Any(item => string.Equals(item.RuntimeVersion, point.Version, StringComparison.OrdinalIgnoreCase));
    }

    // "Roll back" on the Updates page. The newer version is skipped afterwards so it is not
    // offered again right away; a later release shows as usual.
    public async Task<UpdateRollbackPoint> RollBackAsync(string id, bool restoreData, CancellationToken cancellationToken = default)
    {
        await batchLock.WaitAsync(cancellationToken);
        var key = OperationKey(id);
        try
        {
            var point = rollbacks.Find(id, DateTimeOffset.UtcNow)
                ?? throw new InvalidOperationException(ServiceText.Get(
                    "UpdatesRollbackUnavailable",
                    "The previous version is no longer kept."
                ));
            lock (sync) active = key;
            RaiseStateChanged();
            if (id.StartsWith("php:", StringComparison.OrdinalIgnoreCase))
            {
                var cycle = id[4..];
                await WithStoppedEnvironmentAsync(async hold =>
                {
                    if (hold.CyclesInUse.Contains(cycle, StringComparer.Ordinal)) await StopEnvironmentOnceAsync(hold);
                    RestorePhpDirectory(cycle, point);
                    await phpInstaller.EnsureManagedConfigurationAsync(cycle, cancellationToken);
                }, null);
                SynchronizeUserPath();
            }
            else if (id.StartsWith("node:", StringComparison.OrdinalIgnoreCase))
            {
                if (!nodeInstaller.InstalledVersions().Contains(point.Version, StringComparer.OrdinalIgnoreCase))
                {
                    rollbacks.Forget(id);
                    throw new InvalidOperationException(ServiceText.Get(
                        "UpdatesRollbackUnavailable",
                        "The previous version is no longer kept."
                    ));
                }
                nodeInstaller.SetActive(point.Version);
                nodeInstaller.Remove(point.ReplacedBy);
                rollbacks.Forget(id);
                SynchronizeUserPath();
            }
            else if (id.StartsWith("service:", StringComparison.OrdinalIgnoreCase))
            {
                var definitionId = id[8..];
                var instances = serviceManager.LoadInstances()
                    .Where(instance => instance.DefinitionId.Equals(definitionId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var running = instances
                    .Where(instance => serviceManager.State(instance.Id, instance.DefinitionId) == ManagedServiceState.Running)
                    .ToArray();
                await RestoreServiceAsync(definitionId, point, instances, running, restoreData, cancellationToken);
            }
            else
            {
                throw new InvalidOperationException(ServiceText.Get(
                    "UpdatesRollbackUnavailable",
                    "The previous version is no longer kept."
                ));
            }
            history.Add(new UpdateHistoryEntry(id, point.Name, point.ReplacedBy, point.Version, DateTimeOffset.UtcNow, UpdateOutcome.RolledBack));
            preferences.Skip(id, point.ReplacedBy);
            return point;
        }
        finally
        {
            lock (sync) active = null;
            batchLock.Release();
            RaiseStateChanged();
        }
    }

    // Swaps the kept PHP build back but keeps the php.ini the user has now.
    private void RestorePhpDirectory(string cycle, UpdateRollbackPoint point)
    {
        var iniPath = Path.Combine(point.Target, "php.ini");
        var currentIni = File.Exists(iniPath) ? File.ReadAllBytes(iniPath) : null;
        rollbacks.RestoreDirectory(point);
        if (currentIni is not null) File.WriteAllBytes(iniPath, currentIni);
        PhpModuleProbeCache.Invalidate(Path.GetDirectoryName(phpInstaller.PhpExecutable(cycle))!);
    }

    // What an update stops and roughly for how long, before the user confirms it.
    public UpdateImpact EstimateImpact(IReadOnlyList<ManagedComponentUpdate> updates)
    {
        var groups = updates
            .GroupBy(OperationKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var cyclesInUse = environment.PhpCyclesInUse();
        var phpLines = groups
            .Where(update => update.Id.StartsWith("php:", StringComparison.OrdinalIgnoreCase)
                || update.Id.StartsWith("xdebug:", StringComparison.OrdinalIgnoreCase))
            .Select(update => update.Id[(update.Id.IndexOf(':') + 1)..])
            .Where(cycle => cyclesInUse.Contains(cycle, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(cycle => cycle, StringComparer.Ordinal)
            .ToList();
        var services = new List<string>();
        foreach (var update in groups.Where(update => update.Id.StartsWith("service:", StringComparison.OrdinalIgnoreCase)))
        {
            services.AddRange(serviceManager.LoadInstances()
                .Where(instance => instance.DefinitionId.Equals(update.Id[8..], StringComparison.OrdinalIgnoreCase)
                    && serviceManager.State(instance.Id, instance.DefinitionId) == ManagedServiceState.Running)
                .Select(instance => instance.Name));
        }
        var duration = TimeSpan.FromSeconds(groups.Sum(update => EstimatedSeconds(update.Id)));
        // Every changed PHP line after the first downloads while the sites are already stopped.
        var downtime = phpLines.Count == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(20 + 60 * (phpLines.Count - 1));
        return new UpdateImpact(
            services,
            phpLines,
            phpLines.Count > 0 ? environment.RunningSiteCount : 0,
            duration,
            downtime,
            groups.Sum(update => ApproximateDownloadBytes(update.Id)),
            groups.Sum(update => RequiredBytes(update.Id))
        );
    }

    private string? DiskSpaceProblem(IReadOnlyList<ManagedComponentUpdate> updates)
    {
        var required = updates.Sum(update => RequiredBytes(update.Id));
        try
        {
            InstallationPreflight.EnsureStorage(Path.Combine(serviceManager.SupportRoot, "Runtimes"), required);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return ServiceText.Format(
                "UpdatesNotEnoughSpace",
                "Not enough free disk space: these updates need about {0} MB.",
                required / (1024 * 1024)
            );
        }
    }

    // Sites-affecting updates run last, so the sites stop as late and as briefly as possible.
    private int BatchOrder(ManagedComponentUpdate update)
    {
        if (update.Id.StartsWith("php:", StringComparison.OrdinalIgnoreCase))
        {
            return environment.PhpCyclesInUse().Contains(update.Id[4..], StringComparer.Ordinal) ? 2 : 1;
        }
        return update.Id.StartsWith("xdebug:", StringComparison.OrdinalIgnoreCase) ? 3 : 0;
    }

    public static int EstimatedSeconds(string id)
    {
        if (id.StartsWith("php:", StringComparison.OrdinalIgnoreCase)) return 60;
        if (id.StartsWith("node:", StringComparison.OrdinalIgnoreCase)) return 60;
        if (id.StartsWith("xdebug:", StringComparison.OrdinalIgnoreCase)) return 20;
        if (id.StartsWith("service:", StringComparison.OrdinalIgnoreCase)) return 150;
        return id.Equals("git", StringComparison.OrdinalIgnoreCase) ? 90 : 30;
    }

    // Rough download sizes of the official Windows packages, shown as "about".
    public static long ApproximateDownloadBytes(string id)
    {
        const long MB = 1024 * 1024;
        if (id.StartsWith("php:", StringComparison.OrdinalIgnoreCase)) return 32 * MB;
        if (id.StartsWith("node:", StringComparison.OrdinalIgnoreCase)) return 34 * MB;
        if (id.StartsWith("xdebug:", StringComparison.OrdinalIgnoreCase)) return 1 * MB;
        if (id.StartsWith("service:", StringComparison.OrdinalIgnoreCase))
        {
            return id[8..].ToLowerInvariant() switch
            {
                "mysql" => 280 * MB,
                "mariadb" => 90 * MB,
                "postgresql" => 330 * MB,
                "mongodb" => 600 * MB,
                "redis" => 10 * MB,
                "meilisearch" => 130 * MB,
                "minio" => 110 * MB,
                _ => 100 * MB
            };
        }
        return id.ToLowerInvariant() switch
        {
            "git" => 65 * MB,
            "composer" => 3 * MB,
            _ => 1 * MB
        };
    }

    // Free space needed on the drive of %LOCALAPPDATA%\HerdMe: the download, the unpacked
    // files and room for the previous version kept for rollback.
    public static long RequiredBytes(string id)
    {
        const long MB = 1024 * 1024;
        var download = ApproximateDownloadBytes(id);
        if (id.StartsWith("service:", StringComparison.OrdinalIgnoreCase)) return Math.Max(512 * MB, download * 4);
        if (id.Equals("git", StringComparison.OrdinalIgnoreCase)) return 600 * MB;
        return Math.Max(64 * MB, download * 6);
    }

    // The official release notes of a version; opened in the browser on request.
    public static Uri? ReleaseNotesUri(string id, string version)
    {
        var escaped = Uri.EscapeDataString(RuntimeVersionComparison.Normalize(version));
        string? address = null;
        if (id.Equals(UpdatePreferencesStore.ApplicationId, StringComparison.OrdinalIgnoreCase))
        {
            address = $"https://github.com/Hamad3bdulla/herdme/releases/tag/windows-v{escaped}";
        }
        else if (id.StartsWith("php:", StringComparison.OrdinalIgnoreCase))
        {
            var major = UpdatePreferencesStore.MajorOf(version);
            address = $"https://www.php.net/ChangeLog-{Uri.EscapeDataString(major)}.php#{escaped}";
        }
        else if (id.StartsWith("node:", StringComparison.OrdinalIgnoreCase))
        {
            address = $"https://github.com/nodejs/node/releases/tag/v{escaped}";
        }
        else if (id.StartsWith("xdebug:", StringComparison.OrdinalIgnoreCase))
        {
            address = "https://xdebug.org/updates";
        }
        else if (id.StartsWith("service:", StringComparison.OrdinalIgnoreCase))
        {
            var definitionId = id[8..].ToLowerInvariant();
            address = definitionId switch
            {
                "postgresql" => $"https://www.postgresql.org/docs/release/{escaped}/",
                "meilisearch" => $"https://github.com/meilisearch/meilisearch/releases/tag/v{escaped}",
                "minio" => "https://github.com/minio/minio/releases",
                _ => ServiceDirectory.DocumentationUri(definitionId)?.AbsoluteUri
            };
        }
        else
        {
            address = id.ToLowerInvariant() switch
            {
                "composer" => $"https://github.com/composer/composer/releases/tag/{escaped}",
                "laravel-installer" => $"https://github.com/laravel/installer/releases/tag/v{escaped}",
                "git" => "https://github.com/git-for-windows/git/releases",
                _ => null
            };
        }
        return address is not null && Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : null;
    }

    private void SynchronizeUserPath()
    {
        userPathManager.Synchronize(
            composerTools.CommandLineDirectories(runtimePolicy.Load().PhpCycle)
        );
    }

    private static Task WriteFailureAsync(string eventName, string message, Exception? error) =>
        DiagnosticLog.WriteFailureAsync("updates", eventName, message, error?.ToString());

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
