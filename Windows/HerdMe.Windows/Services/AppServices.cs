namespace HerdMe.Windows.Services;

public sealed class AppServices
{
    public AppServices()
    {
        Core = new CoreClient();
        SiteSettings = new SiteConfigurationStore();
        Hosts = new WindowsHostsManager();
        Certificates = new WindowsCertificateManager();
        RuntimePolicy = new PhpRuntimePolicy(Core);
        PhpInstaller = new PhpRuntimeInstaller(Core);
        NodeInstaller = new NodeRuntimeInstaller();
        GitInstaller = new GitRuntimeInstaller();
        UserPath = new WindowsUserPathManager();
        ComposerTools = new ComposerToolManager(
            coreClient: Core,
            phpInstaller: PhpInstaller,
            phpPolicy: RuntimePolicy,
            nodeInstaller: NodeInstaller
        );
        ProjectCreator = new LaravelProjectCreator(
            ComposerTools,
            PhpInstaller,
            RuntimePolicy,
            NodeInstaller,
            GitInstaller,
            UserPath
        );
        Environment = new WindowsLocalEnvironment(
            Core,
            PhpInstaller,
            RuntimePolicy,
            Certificates,
            Hosts
        );
        Mail = new MailCaptureService();
        Dumps = new DumpCaptureService();
        Services = new WindowsServiceManager();
        Startup = new WindowsStartupManager();
        Updates = AppUpdateManager.Configured();
        Xdebug = new XdebugManager();
        ComponentUpdates = new ManagedComponentUpdateManager(
            PhpInstaller,
            RuntimePolicy,
            NodeInstaller,
            ComposerTools,
            GitInstaller,
            Xdebug,
            Services
        );
        SiteRuntimes = new SiteRuntimeStore();
        CommandFavorites = new SiteCommandFavoritesStore(SiteSettings.SupportRoot);
        SiteProcesses = new SiteProcessManager();
        InitialSetup = new InitialSetupManager(
            SiteSettings,
            Hosts,
            Certificates,
            Core,
            PhpInstaller,
            RuntimePolicy,
            ComposerTools,
            NodeInstaller,
            GitInstaller,
            UserPath
        );
    }

    public CoreClient Core { get; }

    public SiteConfigurationStore SiteSettings { get; }

    public WindowsHostsManager Hosts { get; }

    public WindowsCertificateManager Certificates { get; }

    public PhpRuntimePolicy RuntimePolicy { get; }

    public PhpRuntimeInstaller PhpInstaller { get; }

    public NodeRuntimeInstaller NodeInstaller { get; }

    public GitRuntimeInstaller GitInstaller { get; }

    public WindowsUserPathManager UserPath { get; }

    public ComposerToolManager ComposerTools { get; }

    public LaravelProjectCreator ProjectCreator { get; }

    public WindowsLocalEnvironment Environment { get; }

    public MailCaptureService Mail { get; }

    public DumpCaptureService Dumps { get; }

    public WindowsServiceManager Services { get; }

    public WindowsStartupManager Startup { get; }

    public AppUpdateManager Updates { get; }

    public XdebugManager Xdebug { get; }

    public ManagedComponentUpdateManager ComponentUpdates { get; }

    public SiteRuntimeStore SiteRuntimes { get; }

    public SiteCommandFavoritesStore CommandFavorites { get; }

    public SiteProcessManager SiteProcesses { get; }

    public InitialSetupManager InitialSetup { get; }
}
