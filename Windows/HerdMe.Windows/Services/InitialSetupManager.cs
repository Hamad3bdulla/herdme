using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed class InitialSetupManager
{
    private static string DefaultPhpCycle => RuntimeCatalog.DefaultPhpCycle;
    private static string DefaultNodeCycle => RuntimeCatalog.DefaultNodeMajor;
    private readonly SiteConfigurationStore settingsStore;
    private readonly WindowsHostsManager hostsManager;
    private readonly WindowsCertificateManager certificateManager;
    private readonly CoreClient coreClient;
    private readonly PhpRuntimeInstaller phpInstaller;
    private readonly PhpRuntimePolicy phpPolicy;
    private readonly ComposerToolManager composerTools;
    private readonly NodeRuntimeInstaller nodeInstaller;
    private readonly GitRuntimeInstaller gitInstaller;
    private readonly WindowsUserPathManager userPathManager;

    public InitialSetupManager(
        SiteConfigurationStore? settingsStore = null,
        WindowsHostsManager? hostsManager = null,
        WindowsCertificateManager? certificateManager = null,
        CoreClient? coreClient = null,
        PhpRuntimeInstaller? phpInstaller = null,
        PhpRuntimePolicy? phpPolicy = null,
        ComposerToolManager? composerTools = null,
        NodeRuntimeInstaller? nodeInstaller = null,
        GitRuntimeInstaller? gitInstaller = null,
        WindowsUserPathManager? userPathManager = null
    )
    {
        this.settingsStore = settingsStore ?? new SiteConfigurationStore();
        this.hostsManager = hostsManager ?? new WindowsHostsManager();
        this.certificateManager = certificateManager ?? new WindowsCertificateManager();
        this.coreClient = coreClient ?? new CoreClient();
        this.phpInstaller = phpInstaller ?? new PhpRuntimeInstaller(this.coreClient);
        this.phpPolicy = phpPolicy ?? new PhpRuntimePolicy(this.coreClient);
        this.composerTools = composerTools ?? new ComposerToolManager(
            coreClient: this.coreClient,
            phpInstaller: this.phpInstaller,
            phpPolicy: this.phpPolicy
        );
        this.nodeInstaller = nodeInstaller ?? new NodeRuntimeInstaller(this.composerTools.SupportRoot);
        this.gitInstaller = gitInstaller ?? new GitRuntimeInstaller(this.composerTools.SupportRoot);
        this.userPathManager = userPathManager
            ?? new WindowsUserPathManager(this.composerTools.SupportRoot);
    }

    public async Task RunAsync(
        IProgress<InitialSetupStage> progress,
        CancellationToken cancellationToken = default
    )
    {
        var settings = settingsStore.Load();
        settingsStore.UpdateOnboardingCompleted(false);
        foreach (var root in settings.Roots) Directory.CreateDirectory(root);

        progress.Report(InitialSetupStage.LocalDomains);
        await hostsManager.EnsureMappingsAsync(
            [$"herdme.{settings.Tld}"],
            cancellationToken
        );

        progress.Report(InitialSetupStage.Certificate);
        if (!certificateManager.IsAuthorityTrusted()) certificateManager.TrustAuthority();

        await EnsureCommandLineToolsAsync(progress, cancellationToken);

        progress.Report(InitialSetupStage.Finishing);
        settingsStore.UpdateOnboardingCompleted(true);
        progress.Report(InitialSetupStage.Completed);
    }

    public async Task EnsureCommandLineToolsAsync(
        IProgress<InitialSetupStage>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        var phpSettings = phpPolicy.Load();
        var phpCycle = PhpRuntimeInstaller.IsSupportedCycle(phpSettings.PhpCycle)
            ? phpSettings.PhpCycle
            : DefaultPhpCycle;

        progress?.Report(InitialSetupStage.Php);
        if (!phpInstaller.IsInstalled(phpCycle))
        {
            await phpInstaller.InstallAsync(phpCycle, cancellationToken);
        }
        phpInstaller.EnsureManagedConfiguration(phpCycle);
        var phpExecutable = phpInstaller.PhpExecutable(phpCycle);
        var extensionReport = await coreClient.ValidatePhpAsync(phpExecutable, cancellationToken);
        if (!extensionReport.Compatible)
        {
            throw new InvalidOperationException(
                "PHP cannot start Laravel sites. Missing extensions: "
                + string.Join(", ", extensionReport.Missing)
            );
        }
        phpSettings.PhpCycle = phpCycle;
        phpPolicy.Save(phpSettings);

        progress?.Report(InitialSetupStage.Git);
        await gitInstaller.EnsureInstalledAsync(cancellationToken);

        progress?.Report(InitialSetupStage.Composer);
        await composerTools.EnsureLaravelInstallerAsync(phpCycle, cancellationToken);

        progress?.Report(InitialSetupStage.Node);
        await nodeInstaller.EnsureActiveRuntimeAsync(DefaultNodeCycle, cancellationToken);
        userPathManager.Synchronize(composerTools.CommandLineDirectories(phpCycle));
    }
}
