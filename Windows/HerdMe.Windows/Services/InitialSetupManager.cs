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

    public InitialSetupManager(
        SiteConfigurationStore? settingsStore = null,
        WindowsHostsManager? hostsManager = null,
        WindowsCertificateManager? certificateManager = null,
        CoreClient? coreClient = null,
        PhpRuntimeInstaller? phpInstaller = null,
        PhpRuntimePolicy? phpPolicy = null,
        ComposerToolManager? composerTools = null,
        NodeRuntimeInstaller? nodeInstaller = null
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
    }

    public async Task RunAsync(
        IProgress<InitialSetupStage> progress,
        CancellationToken cancellationToken = default
    )
    {
        var settings = settingsStore.Load();
        settings.OnboardingCompleted = false;
        foreach (var root in settings.Roots) Directory.CreateDirectory(root);
        settingsStore.Save(settings);

        progress.Report(InitialSetupStage.LocalDomains);
        await hostsManager.EnsureMappingsAsync(
            [$"herdme.{settings.Tld}"],
            cancellationToken
        );

        progress.Report(InitialSetupStage.Certificate);
        if (!certificateManager.IsAuthorityTrusted()) certificateManager.TrustAuthority();

        progress.Report(InitialSetupStage.Php);
        if (!phpInstaller.IsInstalled(DefaultPhpCycle))
        {
            await phpInstaller.InstallAsync(DefaultPhpCycle, cancellationToken);
        }
        var phpExecutable = phpInstaller.PhpExecutable(DefaultPhpCycle);
        var extensionReport = await coreClient.ValidatePhpAsync(phpExecutable, cancellationToken);
        if (!extensionReport.Compatible)
        {
            throw new InvalidOperationException(
                "PHP cannot start Laravel sites. Missing extensions: "
                + string.Join(", ", extensionReport.Missing)
            );
        }
        var phpSettings = phpPolicy.Load();
        phpSettings.PhpCycle = DefaultPhpCycle;
        phpPolicy.Save(phpSettings);

        progress.Report(InitialSetupStage.Composer);
        await composerTools.EnsureLaravelInstallerAsync(DefaultPhpCycle, cancellationToken);

        progress.Report(InitialSetupStage.Node);
        var installedNode = nodeInstaller.InstalledVersion(DefaultNodeCycle);
        if (installedNode is null)
        {
            installedNode = (await nodeInstaller.InstallAsync(
                DefaultNodeCycle,
                cancellationToken
            )).Version;
        }
        else
        {
            nodeInstaller.SetActive(installedNode);
        }

        progress.Report(InitialSetupStage.Finishing);
        settings = settingsStore.Load();
        settings.OnboardingCompleted = true;
        settingsStore.Save(settings);
        progress.Report(InitialSetupStage.Completed);
    }
}
