using System.Text.RegularExpressions;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed partial class LaravelProjectCreator
{
    private readonly ComposerToolManager tools;
    private readonly PhpRuntimeInstaller phpInstaller;
    private readonly PhpRuntimePolicy phpPolicy;
    private readonly NodeRuntimeInstaller nodeInstaller;
    private readonly GitRuntimeInstaller gitInstaller;
    private readonly WindowsUserPathManager userPathManager;

    public LaravelProjectCreator(
        ComposerToolManager? tools = null,
        PhpRuntimeInstaller? phpInstaller = null,
        PhpRuntimePolicy? phpPolicy = null,
        NodeRuntimeInstaller? nodeInstaller = null,
        GitRuntimeInstaller? gitInstaller = null,
        WindowsUserPathManager? userPathManager = null
    )
    {
        this.tools = tools ?? new ComposerToolManager();
        this.phpInstaller = phpInstaller ?? new PhpRuntimeInstaller();
        this.phpPolicy = phpPolicy ?? new PhpRuntimePolicy();
        this.nodeInstaller = nodeInstaller ?? new NodeRuntimeInstaller(this.tools.SupportRoot);
        this.gitInstaller = gitInstaller ?? new GitRuntimeInstaller(this.tools.SupportRoot);
        this.userPathManager = userPathManager ?? new WindowsUserPathManager(this.tools.SupportRoot);
    }

    public async Task<string> CreateAsync(
        LaravelProjectRequest request,
        IProgress<LaravelProjectCreationStage>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        progress?.Report(LaravelProjectCreationStage.ValidatingRequest);
        var name = request.Name.Trim();
        if (!ProjectNamePattern().IsMatch(name))
        {
            throw new ArgumentException("Use only letters, numbers, dots, underscores, and hyphens for the project name.");
        }
        if (request.StarterKit.Trim().Equals("Custom", StringComparison.OrdinalIgnoreCase)
            && !CustomStarterKitPattern().IsMatch(request.CustomStarterKit?.Trim() ?? string.Empty))
        {
            throw new ArgumentException(
                "Enter the custom starter kit as a Composer package such as vendor/package."
            );
        }
        var parent = Path.GetFullPath(request.ParentDirectory);
        if (SiteConfigurationStore.BelongsToOtherHerd(parent))
        {
            throw new ArgumentException(
                "HerdMe does not create projects inside another application's folders. Choose a HerdMe-owned folder instead."
            );
        }
        if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
        var destination = Path.Combine(parent, name);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException("A file or folder with this project name already exists.");
        }

        var settings = phpPolicy.Load();
        progress?.Report(LaravelProjectCreationStage.PreparingLaravelInstaller);
        // Repair runtimes installed by older HerdMe releases before any Composer command runs.
        phpInstaller.EnsureManagedConfiguration(settings.PhpCycle);
        await tools.EnsureLaravelInstallerAsync(settings.PhpCycle, cancellationToken);
        var php = phpInstaller.PhpExecutable(settings.PhpCycle);
        var arguments = BuildLaravelArguments(request with { Name = name });
        var stagingRoot = Path.Combine(parent, $".herdme-create-{Guid.NewGuid():N}");
        var stagedDestination = Path.Combine(stagingRoot, name);
        Directory.CreateDirectory(stagingRoot);
        File.SetAttributes(stagingRoot, File.GetAttributes(stagingRoot) | FileAttributes.Hidden);
        try
        {
            progress?.Report(LaravelProjectCreationStage.CreatingLaravelProject);
            await ComposerToolManager.RunAsync(
                php,
                [tools.LaravelExecutable, .. arguments],
                stagingRoot,
                tools.ManagedEnvironment(settings.PhpCycle),
                cancellationToken
            );

            if (request.InstallBoost)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(LaravelProjectCreationStage.InstallingLaravelBoost);
                await ComposerToolManager.RunAsync(
                    php,
                    [
                        tools.ComposerPath,
                        "require",
                        "laravel/boost",
                        "--dev",
                        "--no-interaction",
                        "--no-progress",
                        "--no-ansi"
                    ],
                    stagedDestination,
                    tools.ManagedEnvironment(settings.PhpCycle),
                    cancellationToken
                );
            }
            if (LaravelProjectCreationStages.RequiresFrontendAssets(request))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(LaravelProjectCreationStage.PreparingNodeRuntime);
                var nodeDirectory = await nodeInstaller.EnsureActiveRuntimeAsync(
                    RuntimeCatalog.DefaultNodeMajor,
                    cancellationToken
                );
                var npm = Path.Combine(nodeDirectory, "npm.cmd");
                ValidateFrontendBuild(stagedDestination);

                progress?.Report(LaravelProjectCreationStage.InstallingFrontendDependencies);
                await ComposerToolManager.RunAsync(
                    npm,
                    ["install", "--no-audit", "--no-fund", "--no-progress"],
                    stagedDestination,
                    tools.ManagedEnvironment(settings.PhpCycle),
                    cancellationToken
                );

                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(LaravelProjectCreationStage.BuildingFrontendAssets);
                await ComposerToolManager.RunAsync(
                    npm,
                    ["run", "build"],
                    stagedDestination,
                    tools.ManagedEnvironment(settings.PhpCycle),
                    cancellationToken
                );
            }
            if (request.InitializeGit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(LaravelProjectCreationStage.InitializingGitRepository);
                var git = await gitInstaller.EnsureInstalledAsync(cancellationToken);
                userPathManager.Synchronize(tools.CommandLineDirectories(settings.PhpCycle));
                await ComposerToolManager.RunAsync(
                    git,
                    ["init"],
                    stagedDestination,
                    tools.ManagedEnvironment(settings.PhpCycle),
                    cancellationToken
                );
            }
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(LaravelProjectCreationStage.VerifyingProject);
            if (!File.Exists(Path.Combine(stagedDestination, "artisan"))
                || !File.Exists(Path.Combine(stagedDestination, "vendor", "autoload.php"))
                || LaravelProjectCreationStages.RequiresFrontendAssets(request)
                    && !File.Exists(Path.Combine(stagedDestination, "public", "build", "manifest.json")))
            {
                throw new InvalidDataException("Laravel Installer finished without creating a complete Laravel project.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                throw new IOException("A file or folder with this project name already exists.");
            }
            Directory.Move(stagedDestination, destination);
            return destination;
        }
        finally
        {
            await DeleteStagingDirectoryAsync(stagingRoot).ConfigureAwait(false);
        }
    }

    internal static Task DeleteStagingDirectoryAsync(string stagingRoot)
    {
        return Task.Run(() => DeleteStagingDirectory(stagingRoot));
    }

    private static void DeleteStagingDirectory(string stagingRoot)
    {
        if (!Directory.Exists(stagingRoot)) return;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                ClearReadOnlyAttributes(stagingRoot);
                Directory.Delete(stagingRoot, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(100 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < 3)
            {
                Thread.Sleep(100 * attempt);
            }
        }
        Directory.Delete(stagingRoot, recursive: true);
    }

    private static void ClearReadOnlyAttributes(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                    attributes &= ~FileAttributes.ReadOnly;
                }
                if ((attributes & FileAttributes.Directory) != 0
                    && (attributes & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push(entry);
                }
            }
            var directoryAttributes = File.GetAttributes(directory);
            if ((directoryAttributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(directory, directoryAttributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    public static IReadOnlyList<string> BuildLaravelArguments(LaravelProjectRequest request)
    {
        var arguments = new List<string>
        {
            "new", request.Name.Trim(), "--no-interaction", "--no-ansi",
            request.TestingFramework.Equals("PHPUnit", StringComparison.OrdinalIgnoreCase)
                ? "--phpunit"
                : "--pest"
        };
        var starter = request.StarterKit.Trim().ToLowerInvariant();
        if (starter is "react" or "vue" or "svelte" or "livewire")
        {
            arguments.Add("--" + starter);
            arguments.Add("--no-node");
        }
        else if (starter == "custom")
        {
            arguments.Add("--using=" + request.CustomStarterKit!.Trim());
            arguments.Add("--npm");
        }
        return arguments;
    }

    private static void ValidateFrontendBuild(string destination)
    {
        var packagePath = Path.Combine(destination, "package.json");
        try
        {
            using var package = System.Text.Json.JsonDocument.Parse(File.ReadAllText(packagePath));
            var build = package.RootElement
                .GetProperty("scripts")
                .GetProperty("build")
                .GetString();
            if (!string.IsNullOrWhiteSpace(build)) return;
        }
        catch (Exception error) when (error is IOException
            or System.Text.Json.JsonException
            or KeyNotFoundException
            or InvalidOperationException)
        {
        }
        throw new InvalidDataException(
            "The selected starter kit did not provide a valid npm build script."
        );
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?::\S+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex CustomStarterKitPattern();
}
