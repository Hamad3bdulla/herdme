using System.Text;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace HerdMe.Windows.Pages;

public sealed partial class SitesPage
{
    private sealed record WorkflowTools(
        string Php,
        string Cycle,
        IReadOnlyDictionary<string, string> Environment
    );

    private sealed record WorkflowBackup(
        string ArchivePath,
        string? DatabasePath,
        ManagedServiceInstance? DatabaseInstance,
        SiteDatabaseProvisioning? DatabaseProvisioning
    );

    private async void UpdateLaravelWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site || !await ConfirmWorkflowAsync(
            "SitesWorkflowUpdateTitle",
            "SitesWorkflowUpdateDescription"
        )) return;

        await RunSiteOperationAsync(
            AppLocalization.Get("SitesWorkflowUpdateRunning"),
            async (progress, cancellationToken) =>
            {
                var rollback = CaptureWorkflowFiles(site.Path);
                var backup = await CreateWorkflowBackupAsync(site, progress, cancellationToken);
                var report = new StringBuilder();
                report.AppendLine(AppLocalization.Format(
                    "SitesWorkflowBackupCreated",
                    backup.ArchivePath
                ));
                try
                {
                    var tools = await PrepareWorkflowToolsAsync(site, progress, cancellationToken);
                    if (File.Exists(Path.Combine(site.Path, "composer.json")))
                    {
                        await RunComposerWorkflowAsync(
                            site,
                            tools,
                            ["update", "--no-interaction", "--with-all-dependencies"],
                            progress,
                            cancellationToken
                        );
                        report.AppendLine("[OK] composer update");
                    }
                    if (File.Exists(Path.Combine(site.Path, "package.json")))
                    {
                        await RunNpmToolWorkflowAsync(
                            site,
                            ["update", "--no-audit", "--no-fund"],
                            TimeSpan.FromMinutes(45),
                            progress,
                            cancellationToken
                        );
                        report.AppendLine("[OK] npm update");
                        if (HasNpmScript(site.Path, "build"))
                        {
                            await RunNpmScriptWorkflowAsync(
                                site,
                                "build",
                                progress,
                                cancellationToken
                            );
                            report.AppendLine("[OK] npm run build");
                        }
                    }
                    if (File.Exists(Path.Combine(site.Path, "artisan")))
                    {
                        await RunArtisanWorkflowAsync(
                            site,
                            tools,
                            ["migrate", "--force", "--no-interaction"],
                            TimeSpan.FromMinutes(30),
                            progress,
                            cancellationToken
                        );
                        await RunArtisanWorkflowAsync(
                            site,
                            tools,
                            ["optimize:clear", "--no-interaction"],
                            TimeSpan.FromMinutes(10),
                            progress,
                            cancellationToken
                        );
                        report.AppendLine("[OK] migrations and cache cleanup");
                    }
                }
                catch (Exception error)
                {
                    progress.Report(AppLocalization.Get("SitesWorkflowRollingBack") + "\n");
                    RestoreWorkflowFiles(site.Path, rollback);
                    await TryRestoreDependenciesAsync(site, progress);
                    await TryRestoreWorkflowDatabaseAsync(backup, progress);
                    throw new InvalidOperationException(
                        AppLocalization.Format(
                            "SitesWorkflowFailedWithBackup",
                            error.Message,
                            backup.ArchivePath
                        ),
                        error
                    );
                }
                await RefreshSiteDetailsAsync(site);
                await ShowCommandResultAsync(
                    AppLocalization.Get("SitesWorkflowUpdateResult"),
                    report.ToString().Trim(),
                    true
                );
            }
        );
    }

    private async void CleanProjectWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site || !await ConfirmWorkflowAsync(
            "SitesWorkflowCleanTitle",
            "SitesWorkflowCleanDescription"
        )) return;

        await RunSiteOperationAsync(
            AppLocalization.Get("SitesWorkflowCleanRunning"),
            async (progress, cancellationToken) =>
            {
                var backup = await CreateWorkflowBackupAsync(site, progress, cancellationToken);
                var staging = Path.Combine(site.Path, $".herdme-clean-{Guid.NewGuid():N}");
                Directory.CreateDirectory(staging);
                var moved = new List<(string Original, string Backup)>();
                try
                {
                    foreach (var directoryName in new[] { "vendor", "node_modules" })
                    {
                        var original = Path.Combine(site.Path, directoryName);
                        if (!Directory.Exists(original)) continue;
                        var destination = Path.Combine(staging, directoryName);
                        Directory.Move(original, destination);
                        moved.Add((original, destination));
                    }
                    ClearLaravelGeneratedFiles(site.Path);
                    var tools = await PrepareWorkflowToolsAsync(site, progress, cancellationToken);
                    if (File.Exists(Path.Combine(site.Path, "composer.json")))
                    {
                        await RunComposerWorkflowAsync(
                            site,
                            tools,
                            ["install", "--no-interaction", "--prefer-dist"],
                            progress,
                            cancellationToken
                        );
                    }
                    if (File.Exists(Path.Combine(site.Path, "package.json")))
                    {
                        await RunNpmToolWorkflowAsync(
                            site,
                            ["install", "--no-audit", "--no-fund"],
                            TimeSpan.FromMinutes(45),
                            progress,
                            cancellationToken
                        );
                        if (HasNpmScript(site.Path, "build"))
                        {
                            await RunNpmScriptWorkflowAsync(site, "build", progress, cancellationToken);
                        }
                    }
                    if (File.Exists(Path.Combine(site.Path, "artisan")))
                    {
                        await RunArtisanWorkflowAsync(
                            site,
                            tools,
                            ["optimize:clear", "--no-interaction"],
                            TimeSpan.FromMinutes(10),
                            progress,
                            cancellationToken
                        );
                    }
                    DeleteDirectorySafely(staging);
                }
                catch
                {
                    foreach (var item in moved.AsEnumerable().Reverse())
                    {
                        if (Directory.Exists(item.Original)) DeleteDirectorySafely(item.Original);
                        if (Directory.Exists(item.Backup)) Directory.Move(item.Backup, item.Original);
                    }
                    if (Directory.Exists(staging)) DeleteDirectorySafely(staging);
                    throw;
                }
                await RefreshSiteDetailsAsync(site);
                await ShowCommandResultAsync(
                    AppLocalization.Get("SitesWorkflowCleanResult"),
                    AppLocalization.Format("SitesWorkflowBackupCreated", backup.ArchivePath),
                    true
                );
            }
        );
    }

    private async void ExportProjectWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"{SiteDatabaseProvisioner.SuggestedDatabaseName(site.Name)}-herdme"
        };
        picker.FileTypeChoices.Add("ZIP", [".zip"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSaveFileAsync();
        if (file is null || !await ConfirmWorkflowAsync(
            "SitesWorkflowExportTitle",
            "SitesWorkflowExportDescription"
        )) return;

        await RunSiteOperationAsync(
            AppLocalization.Get("SitesWorkflowExportRunning"),
            async (progress, cancellationToken) =>
            {
                var databaseDump = await CreateTemporaryDatabaseDumpAsync(
                    site,
                    progress,
                    cancellationToken
                );
                try
                {
                    await SiteWorkflowArchive.CreateAsync(
                        site.Path,
                        file.Path,
                        site.Name,
                        databaseDump,
                        progress,
                        cancellationToken
                    );
                }
                finally
                {
                    if (databaseDump is not null && File.Exists(databaseDump)) File.Delete(databaseDump);
                }
                await ShowCommandResultAsync(
                    AppLocalization.Get("SitesWorkflowExportResult"),
                    file.Path,
                    true
                );
            }
        );
    }

    private async void SetupMailWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site || !await ConfirmWorkflowAsync(
            "SitesWorkflowMailTitle",
            "SitesWorkflowMailDescription"
        )) return;

        await RunSiteOperationAsync(
            AppLocalization.Get("SitesWorkflowMailRunning"),
            async (progress, cancellationToken) =>
            {
                await mail.StartAsync(cancellationToken: cancellationToken);
                var port = mail.Port ?? MailCaptureService.DefaultPort;
                var update = ServiceEnvironmentFile.Update(
                    site.Path,
                    MailEnvironmentConfiguration.Variables(port),
                    "HerdMe local mail"
                );
                if (File.Exists(Path.Combine(site.Path, "artisan")))
                {
                    var tools = await PrepareWorkflowToolsAsync(site, progress, cancellationToken);
                    await RunArtisanWorkflowAsync(
                        site,
                        tools,
                        ["config:clear", "--no-interaction"],
                        TimeSpan.FromMinutes(5),
                        progress,
                        cancellationToken
                    );
                }
                await ShowCommandResultAsync(
                    AppLocalization.Get("SitesWorkflowMailResult"),
                    AppLocalization.Format(
                        "SitesWorkflowMailConfigured",
                        port,
                        update.UpdatedKeys + update.AddedKeys
                    ),
                    true
                );
            }
        );
    }

    private async void HandoffAuditWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"{SiteDatabaseProvisioner.SuggestedDatabaseName(site.Name)}-handoff-report"
        };
        picker.FileTypeChoices.Add("Markdown", [".md"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSaveFileAsync();
        if (file is null || !await ConfirmWorkflowAsync(
            "SitesWorkflowAuditTitle",
            "SitesWorkflowAuditDescription"
        )) return;

        await RunSiteOperationAsync(
            AppLocalization.Get("SitesWorkflowAuditRunning"),
            async (progress, cancellationToken) =>
            {
                var report = new StringBuilder()
                    .AppendLine($"# HerdMe handoff report: {site.Name}")
                    .AppendLine()
                    .AppendLine($"Generated: {DateTimeOffset.Now:g}")
                    .AppendLine($"Path: `{site.Path}`")
                    .AppendLine();
                WorkflowTools? tools = null;
                if (File.Exists(Path.Combine(site.Path, "composer.json"))
                    || File.Exists(Path.Combine(site.Path, "artisan")))
                {
                    tools = await PrepareWorkflowToolsAsync(site, progress, cancellationToken);
                }
                var git = await SitePresentation.InspectGitAsync(site.Path, cancellationToken);
                AddAuditResult(
                    report,
                    "Git working tree",
                    git.IsRepository && git.ChangeCount == 0,
                    git.IsRepository ? $"{git.ChangeCount} uncommitted changes" : "Not a Git repository"
                );
                var environmentPath = Path.Combine(site.Path, ".env");
                var ignorePath = Path.Combine(site.Path, ".gitignore");
                var ignoresEnvironment = File.Exists(ignorePath)
                    && File.ReadLines(ignorePath).Any(line => line.Trim() is ".env" or "/.env");
                AddAuditResult(report, ".env protection", File.Exists(environmentPath)
                    && ignoresEnvironment, ignoresEnvironment ? "Ignored by Git" : "Add .env to .gitignore");

                if (File.Exists(Path.Combine(site.Path, "composer.json")))
                {
                    await AddComposerAuditAsync(site, tools!, report, progress, cancellationToken);
                }
                if (File.Exists(Path.Combine(site.Path, "package.json")))
                {
                    await AddNpmAuditAsync(site, report, progress, cancellationToken);
                }
                if (File.Exists(Path.Combine(site.Path, "artisan")))
                {
                    await AddArtisanTestAuditAsync(site, tools!, report, progress, cancellationToken);
                }
                await File.WriteAllTextAsync(
                    file.Path,
                    report.ToString(),
                    new UTF8Encoding(false),
                    cancellationToken
                );
                await ShowCommandResultAsync(
                    AppLocalization.Get("SitesWorkflowAuditResult"),
                    file.Path + Environment.NewLine + Environment.NewLine + report,
                    true
                );
            }
        );
    }

    private async Task<bool> ConfirmWorkflowAsync(string titleKey, string descriptionKey)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = AppLocalization.Get(titleKey),
            Content = new TextBlock
            {
                Text = AppLocalization.Get(descriptionKey),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520
            },
            PrimaryButtonText = AppLocalization.Get("SitesWorkflowRun"),
            CloseButtonText = AppLocalization.Get("SitesCancel"),
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<WorkflowTools> PrepareWorkflowToolsAsync(
        SiteRecord site,
        IProgress<string> progress,
        CancellationToken cancellationToken
    )
    {
        var cycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
        progress.Report(AppLocalization.Get("SitesWorkflowPreparingTools") + "\n");
        await phpInstaller.EnsureManagedConfigurationAsync(cycle, cancellationToken);
        if (!File.Exists(composerTools.ComposerPath)
            && File.Exists(Path.Combine(site.Path, "composer.json")))
        {
            await composerTools.InstallOrUpdateAsync(cycle, cancellationToken);
        }
        return new WorkflowTools(
            phpInstaller.PhpExecutable(cycle),
            cycle,
            composerTools.ManagedEnvironment(cycle)
        );
    }

    private async Task RunComposerWorkflowAsync(
        SiteRecord site,
        WorkflowTools tools,
        IReadOnlyList<string> arguments,
        IProgress<string> progress,
        CancellationToken cancellationToken
    )
    {
        progress.Report($"composer {string.Join(' ', arguments)}\n");
        var result = await ComposerCommandRunner.RunAsync(
            tools.Php,
            composerTools.ComposerPath,
            site.Path,
            arguments,
            tools.Environment,
            progress,
            cancellationToken
        );
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Output);
    }

    private static async Task RunArtisanWorkflowAsync(
        SiteRecord site,
        WorkflowTools tools,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        IProgress<string> progress,
        CancellationToken cancellationToken
    )
    {
        progress.Report($"artisan {string.Join(' ', arguments)}\n");
        var result = await ArtisanCommandRunner.RunAsync(
            tools.Php,
            site.Path,
            arguments,
            tools.Environment,
            timeout,
            progress,
            cancellationToken
        );
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Output);
    }

    private async Task RunNpmToolWorkflowAsync(
        SiteRecord site,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        IProgress<string> progress,
        CancellationToken cancellationToken
    )
    {
        progress.Report($"npm {string.Join(' ', arguments)}\n");
        var invocation = NpmScriptRunner.CreateToolInvocation(
            nodeInstaller,
            site.Path,
            site.NodeVersion,
            arguments,
            timeout
        );
        var result = await NpmScriptRunner.RunToolAsync(invocation, progress, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Output);
    }

    private async Task RunNpmScriptWorkflowAsync(
        SiteRecord site,
        string script,
        IProgress<string> progress,
        CancellationToken cancellationToken
    )
    {
        progress.Report($"npm run {script}\n");
        var invocation = NpmScriptRunner.CreateInvocation(
            nodeInstaller,
            site.Path,
            site.NodeVersion,
            script
        );
        var result = await NpmScriptRunner.RunAsync(invocation, progress, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Output);
    }

    private static bool HasNpmScript(string sitePath, string name)
    {
        try
        {
            return NpmScriptCatalog.Discover(sitePath).Any(script => script.Name == name);
        }
        catch (NpmScriptException)
        {
            return false;
        }
    }

    private async Task<WorkflowBackup> CreateWorkflowBackupAsync(
        SiteRecord site,
        IProgress<string> progress,
        CancellationToken cancellationToken
    )
    {
        progress.Report(AppLocalization.Get("SitesWorkflowCreatingBackup") + "\n");
        var directory = Path.Combine(
            settingsStore.SupportRoot,
            "Backups",
            "Sites",
            SiteDatabaseProvisioner.SuggestedDatabaseName(site.Name),
            DateTime.Now.ToString("yyyyMMdd-HHmmss")
        );
        Directory.CreateDirectory(directory);
        string? databasePath = null;
        ManagedServiceInstance? instance = null;
        SiteDatabaseProvisioning? provisioning = null;
        if (TryCurrentSiteDatabase(site, out var configured, out var database, out _))
        {
            instance = configured;
            provisioning = database;
            databasePath = Path.Combine(directory, "database.sql");
            await serviceManager.BackupSiteDatabaseAsync(
                configured,
                database,
                databasePath,
                cancellationToken
            );
        }
        var archivePath = Path.Combine(directory, "project.zip");
        await SiteWorkflowArchive.CreateAsync(
            site.Path,
            archivePath,
            site.Name,
            databasePath,
            progress,
            cancellationToken
        );
        return new WorkflowBackup(archivePath, databasePath, instance, provisioning);
    }

    private async Task<string?> CreateTemporaryDatabaseDumpAsync(
        SiteRecord site,
        IProgress<string> progress,
        CancellationToken cancellationToken
    )
    {
        if (!TryCurrentSiteDatabase(site, out var instance, out var provisioning, out _)) return null;
        var directory = Path.Combine(settingsStore.SupportRoot, "Temp");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"export-{Guid.NewGuid():N}.sql");
        progress.Report(AppLocalization.Get("SitesWorkflowBackingUpDatabase") + "\n");
        await serviceManager.BackupSiteDatabaseAsync(
            instance,
            provisioning,
            path,
            cancellationToken
        );
        return path;
    }

    private async Task TryRestoreWorkflowDatabaseAsync(
        WorkflowBackup backup,
        IProgress<string> progress
    )
    {
        if (backup.DatabasePath is null || backup.DatabaseInstance is null
            || backup.DatabaseProvisioning is null
            || backup.DatabaseInstance.DefinitionId is not ("mysql" or "mariadb")) return;
        try
        {
            progress.Report(AppLocalization.Get("SitesWorkflowRestoringDatabase") + "\n");
            await serviceManager.RestoreSiteDatabaseAsync(
                backup.DatabaseInstance,
                backup.DatabaseProvisioning,
                backup.DatabasePath,
                CancellationToken.None,
                mergeExisting: false
            );
        }
        catch (Exception error)
        {
            progress.Report(error.Message + "\n");
        }
    }

    private async Task TryRestoreDependenciesAsync(SiteRecord site, IProgress<string> progress)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var tools = await PrepareWorkflowToolsAsync(site, progress, timeout.Token);
            if (File.Exists(Path.Combine(site.Path, "composer.json")))
            {
                await RunComposerWorkflowAsync(
                    site,
                    tools,
                    ["install", "--no-interaction"],
                    progress,
                    timeout.Token
                );
            }
            if (File.Exists(Path.Combine(site.Path, "package.json")))
            {
                await RunNpmToolWorkflowAsync(
                    site,
                    ["install", "--no-audit", "--no-fund"],
                    TimeSpan.FromMinutes(25),
                    progress,
                    timeout.Token
                );
            }
        }
        catch (Exception error)
        {
            progress.Report(error.Message + "\n");
        }
    }

    private static IReadOnlyDictionary<string, byte[]?> CaptureWorkflowFiles(string sitePath)
    {
        var result = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[]
        {
            "composer.lock", "package-lock.json", "npm-shrinkwrap.json", "pnpm-lock.yaml", "yarn.lock"
        })
        {
            var path = Path.Combine(sitePath, name);
            result[name] = File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        return result;
    }

    private static void RestoreWorkflowFiles(
        string sitePath,
        IReadOnlyDictionary<string, byte[]?> snapshot
    )
    {
        foreach (var item in snapshot)
        {
            var path = Path.Combine(sitePath, item.Key);
            if (item.Value is null)
            {
                if (File.Exists(path)) File.Delete(path);
            }
            else
            {
                File.WriteAllBytes(path, item.Value);
            }
        }
    }

    private static void ClearLaravelGeneratedFiles(string sitePath)
    {
        foreach (var relative in new[]
        {
            Path.Combine("storage", "framework", "cache", "data"),
            Path.Combine("storage", "framework", "sessions"),
            Path.Combine("storage", "framework", "views")
        })
        {
            var path = Path.Combine(sitePath, relative);
            if (Directory.Exists(path)) DeleteDirectorySafely(path);
            Directory.CreateDirectory(path);
        }
        var bootstrap = Path.Combine(sitePath, "bootstrap", "cache");
        if (!Directory.Exists(bootstrap)) return;
        foreach (var file in Directory.EnumerateFiles(bootstrap, "*.php")) File.Delete(file);
    }

    private static void DeleteDirectorySafely(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists) return;
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            directory.Delete();
            return;
        }
        foreach (var file in directory.EnumerateFiles()) file.Delete();
        foreach (var child in directory.EnumerateDirectories())
        {
            DeleteDirectorySafely(child.FullName);
        }
        directory.Delete();
    }

    private async Task AddComposerAuditAsync(
        SiteRecord site,
        WorkflowTools tools,
        StringBuilder report,
        IProgress<string> progress,
        CancellationToken cancellationToken
    )
    {
        foreach (var check in new[]
        {
            ("Composer validation", (IReadOnlyList<string>)["validate", "--no-interaction"]),
            ("Composer security audit", (IReadOnlyList<string>)["audit", "--no-interaction"])
        })
        {
            try
            {
                await RunComposerWorkflowAsync(
                    site,
                    tools,
                    check.Item2,
                    progress,
                    cancellationToken
                );
                AddAuditResult(report, check.Item1, true, "Passed");
            }
            catch (Exception error)
            {
                AddAuditResult(report, check.Item1, false, TrimReport(error.Message));
            }
        }
    }

    private async Task AddNpmAuditAsync(
        SiteRecord site,
        StringBuilder report,
        IProgress<string> progress,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await RunNpmToolWorkflowAsync(
                site,
                ["audit", "--audit-level=high"],
                TimeSpan.FromMinutes(10),
                progress,
                cancellationToken
            );
            AddAuditResult(report, "npm security audit", true, "Passed");
        }
        catch (Exception error)
        {
            AddAuditResult(report, "npm security audit", false, TrimReport(error.Message));
        }
    }

    private static async Task AddArtisanTestAuditAsync(
        SiteRecord site,
        WorkflowTools tools,
        StringBuilder report,
        IProgress<string> progress,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await RunArtisanWorkflowAsync(
                site,
                tools,
                ["test", "--no-interaction"],
                TimeSpan.FromMinutes(30),
                progress,
                cancellationToken
            );
            AddAuditResult(report, "Laravel tests", true, "Passed");
        }
        catch (Exception error)
        {
            AddAuditResult(report, "Laravel tests", false, TrimReport(error.Message));
        }
    }

    private static void AddAuditResult(StringBuilder report, string name, bool success, string detail)
    {
        report.AppendLine($"- {(success ? "[PASS]" : "[FAIL]")} **{name}**: {detail.ReplaceLineEndings(" ")}");
    }

    private static string TrimReport(string value) => value.Length <= 2_000
        ? value
        : value[^2_000..];
}
