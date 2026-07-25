using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed class InitialSetupManager
{
    private const string DefaultPhpCycle = "8.4";
    private const string DefaultNodeCycle = "22";
    private readonly SiteConfigurationStore settingsStore = AppServices.SiteSettings;
    private readonly WindowsHostsManager hostsManager = new();
    private readonly WindowsCertificateManager certificateManager = new();
    private readonly CoreClient coreClient = new();
    private readonly PhpRuntimeInstaller phpInstaller;
    private readonly PhpRuntimePolicy phpPolicy;
    private readonly ComposerToolManager composerTools;
    private readonly NodeRuntimeInstaller nodeInstaller;

    public InitialSetupManager()
    {
        phpInstaller = new PhpRuntimeInstaller(coreClient);
        phpPolicy = new PhpRuntimePolicy(coreClient);
        composerTools = new ComposerToolManager(coreClient: coreClient);
        nodeInstaller = new NodeRuntimeInstaller();
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
