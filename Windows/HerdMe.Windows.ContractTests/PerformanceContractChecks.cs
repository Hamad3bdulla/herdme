using HerdMe.Windows.Services;

internal static partial class ContractChecks
{
    internal static async Task VerifyPerformanceContractsAsync(string supportRoot)
    {
        VerifyFastCgiWorkerCount();
        await VerifyPhpProbeCacheAsync(Path.Combine(supportRoot, "performance-probe"));
        VerifySettingsFileCache(Path.Combine(supportRoot, "performance-settings"));
        VerifyServiceText();
        VerifyCapturePruneSchedule();
        await VerifyQueuedLogAsync(Path.Combine(supportRoot, "performance-log"));
    }

    private static void VerifyFastCgiWorkerCount()
    {
        Check(
            PhpFastCgiProcess.FastCgiWorkerCount(1) == 4
                && PhpFastCgiProcess.FastCgiWorkerCount(8) == 8
                && PhpFastCgiProcess.FastCgiWorkerCount(64) == 16,
            "FastCGI worker count follows the processor count within 4 to 16 workers"
        );
    }

    private static async Task VerifyPhpProbeCacheAsync(string root)
    {
        var runtime = Path.Combine(root, "php", "8.4");
        Directory.CreateDirectory(runtime);
        var executable = Path.Combine(runtime, "php.exe");
        var ini = Path.Combine(runtime, "php.ini");
        File.WriteAllText(executable, "fixture");
        File.WriteAllText(ini, "memory_limit=128M\n");
        var probes = 0;
        Task<string> Probe(CancellationToken _)
        {
            probes++;
            return Task.FromResult("probe-" + probes);
        }

        var first = await PhpModuleProbeCache.GetOrProbeAsync(executable, "fixture", [], Probe);
        var second = await PhpModuleProbeCache.GetOrProbeAsync(executable, "fixture", [], Probe);
        Check(first == "probe-1" && second == "probe-1" && probes == 1,
            "PHP probe results are reused while php.exe and php.ini are unchanged");

        PhpModuleProbeCache.Invalidate(runtime);
        var afterInvalidate = await PhpModuleProbeCache.GetOrProbeAsync(executable, "fixture", [], Probe);
        Check(afterInvalidate == "probe-2" && probes == 2,
            "Invalidating a runtime directory forces the next PHP probe");

        File.WriteAllText(ini, "memory_limit=512M\nextension=fixture\n");
        var afterIniChange = await PhpModuleProbeCache.GetOrProbeAsync(executable, "fixture", [], Probe);
        Check(afterIniChange == "probe-3" && probes == 3,
            "Changing php.ini outside HerdMe invalidates cached PHP probes");

        var uncached = await PhpModuleProbeCache.GetOrProbeAsync(
            executable,
            "fixture-uncached",
            [],
            Probe,
            shouldCache: _ => false
        );
        var uncachedAgain = await PhpModuleProbeCache.GetOrProbeAsync(
            executable,
            "fixture-uncached",
            [],
            Probe,
            shouldCache: _ => false
        );
        Check(uncached != uncachedAgain && probes == 5,
            "Rejected PHP probe results are not cached");
        PhpModuleProbeCache.Invalidate(executable);
    }

    private static void VerifySettingsFileCache(string root)
    {
        var now = DateTime.UtcNow;
        Check(
            !SettingsFileCache.IsSettled(now, now)
                && SettingsFileCache.IsSettled(now - TimeSpan.FromSeconds(3), now),
            "Settings files modified within the last seconds are never served from memory"
        );

        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        File.WriteAllText(path, "{\"a\":1}");
        Check(SettingsFileCache.ReadAllText(path) == "{\"a\":1}" && !SettingsFileCache.IsCached(path),
            "A freshly written settings file is read from disk");

        var settled = DateTime.UtcNow - TimeSpan.FromMinutes(1);
        File.SetLastWriteTimeUtc(path, settled);
        Check(SettingsFileCache.ReadAllText(path) == "{\"a\":1}" && SettingsFileCache.IsCached(path),
            "Unchanged settings files are cached in memory");

        File.WriteAllText(path, "{\"a\":22}");
        File.SetLastWriteTimeUtc(path, settled);
        Check(SettingsFileCache.ReadAllText(path) == "{\"a\":22}",
            "A settings file with a different size is reread even with the same timestamp");

        SettingsFileCache.Invalidate(path);
        Check(!SettingsFileCache.IsCached(path), "Saving settings invalidates the cached text");
    }

    private static void VerifyServiceText()
    {
        var previous = ServiceText.Localize;
        try
        {
            ServiceText.Localize = null;
            Check(
                ServiceText.Get("Health_Ready", "Ready") == "Ready"
                    && ServiceText.Format("Health_PortAccepts", "Port {0} accepts connections", 5432)
                        == "Port 5432 accepts connections",
                "Service text falls back to English without a localizer"
            );

            ServiceText.Localize = key => key == "Health_Ready" ? "Localized ready" : null;
            Check(
                ServiceText.Get("Health_Ready", "Ready") == "Localized ready"
                    && ServiceText.Get("Health_Valid", "Valid") == "Valid",
                "Service text uses the localizer and falls back for unknown keys"
            );
        }
        finally
        {
            ServiceText.Localize = previous;
        }
    }

    private static void VerifyCapturePruneSchedule()
    {
        var now = DateTimeOffset.UtcNow;
        Check(
            CaptureDatabase.ShouldPrune(null, 0, now)
                && !CaptureDatabase.ShouldPrune(now, 1, now)
                && CaptureDatabase.ShouldPrune(now, CaptureDatabase.PruneEveryInserts, now)
                && CaptureDatabase.ShouldPrune(now - CaptureDatabase.PruneInterval, 0, now),
            "Capture pruning runs once, then every few inserts or minutes"
        );
    }

    private static async Task VerifyQueuedLogAsync(string root)
    {
        var path = Path.Combine(root, "php-errors.log");
        for (var index = 0; index < 100; index++)
        {
            QueuedLog.AppendLine(path, "line " + index);
        }
        await QueuedLog.FlushAsync(path);
        var lines = File.ReadAllLines(path);
        Check(
            lines.Length == 100 && lines[0] == "line 0" && lines[99] == "line 99",
            "Queued PHP log writes are complete and ordered after a flush"
        );

        QueuedLog.Append(path, "after flush" + Environment.NewLine);
        await QueuedLog.FlushAllAsync();
        Check(File.ReadAllLines(path)[^1] == "after flush",
            "PHP log writes continue after the queue was flushed");
    }
}
