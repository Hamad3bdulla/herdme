namespace HerdMe.Windows.Services;

public sealed class AppServices : IAsyncDisposable
{
    private readonly object disposalSync = new();
    private readonly Lazy<MailCaptureService> mail = new(
        () => new MailCaptureService(),
        LazyThreadSafetyMode.ExecutionAndPublication
    );
    private readonly Lazy<DumpCaptureService> dumps = new(
        () => new DumpCaptureService(),
        LazyThreadSafetyMode.ExecutionAndPublication
    );
    private readonly object warmUpSync = new();
    private Task? disposalTask;
    private Task? warmUpTask;

    public AppServices()
    {
        Core = new CoreClient();
        SiteSettings = new SiteConfigurationStore();
        Hosts = new WindowsHostsManager();
        Certificates = new WindowsCertificateManager();
        RuntimePolicy = new PhpRuntimePolicy(Core);
        PhpInstaller = new PhpRuntimeInstaller(Core);
        PhpExtensions = new PhpExtensionManager(PhpInstaller, Core);
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
        Xdebug = new XdebugManager();
        ProxySites = new ProxySiteStore(SiteSettings.SupportRoot);
        Environment = new WindowsLocalEnvironment(
            Core,
            PhpInstaller,
            RuntimePolicy,
            Certificates,
            Hosts,
            Xdebug,
            NodeInstaller,
            ProxySites
        );
        Tunnels = new CloudflaredInstaller();
        Shares = new SiteShareManager(Tunnels, Environment);
        Services = new WindowsServiceManager();
        Startup = new WindowsStartupManager();
        ShellIntegration = new WindowsShellIntegration(SiteSettings.SupportRoot);
        Updates = AppUpdateManager.Configured();
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
        NotificationHistory = new NotificationHistory(SiteSettings.SupportRoot);
        RecentSites = new RecentSitesStore(SiteSettings.SupportRoot);
        StartupSnapshot = new StartupSnapshotStore(SiteSettings.SupportRoot);
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

    public PhpExtensionManager PhpExtensions { get; }

    public NodeRuntimeInstaller NodeInstaller { get; }

    public GitRuntimeInstaller GitInstaller { get; }

    public WindowsUserPathManager UserPath { get; }

    public ComposerToolManager ComposerTools { get; }

    public LaravelProjectCreator ProjectCreator { get; }

    public WindowsLocalEnvironment Environment { get; }

    // The capture services open and migrate captures.sqlite3 in their constructors, so they are
    // created on first use (or by WarmUpAsync on a background thread) instead of at startup.
    public MailCaptureService Mail => mail.Value;

    public DumpCaptureService Dumps => dumps.Value;

    public WindowsServiceManager Services { get; }

    public WindowsStartupManager Startup { get; }
    public WindowsShellIntegration ShellIntegration { get; }

    public AppUpdateManager Updates { get; }

    public XdebugManager Xdebug { get; }

    public ManagedComponentUpdateManager ComponentUpdates { get; }

    public SiteRuntimeStore SiteRuntimes { get; }

    public SiteCommandFavoritesStore CommandFavorites { get; }

    public ProxySiteStore ProxySites { get; }

    // The title bar bell (App.Notifications.cs, MainWindow.Bell.cs).
    public NotificationHistory NotificationHistory { get; }

    // Sites opened last, shown first in the tray and the Jump List.
    public RecentSitesStore RecentSites { get; }

    // Last session's sites and counts, painted before the first scan finishes.
    public StartupSnapshotStore StartupSnapshot { get; }

    public CloudflaredInstaller Tunnels { get; }

    public SiteShareManager Shares { get; }

    public SiteProcessManager SiteProcesses { get; }

    public InitialSetupManager InitialSetup { get; }

    /// <summary>
    /// Creates the lazily constructed services off the calling thread. Safe to call repeatedly;
    /// every call returns the same task.
    /// </summary>
    public Task WarmUpAsync()
    {
        lock (warmUpSync)
        {
            return warmUpTask ??= Task.WhenAll(
                Task.Run(() => _ = mail.Value),
                Task.Run(() => _ = dumps.Value)
            );
        }
    }

    public ValueTask DisposeAsync()
    {
        Task task;
        lock (disposalSync)
        {
            disposalTask ??= DisposeServicesAsync();
            task = disposalTask;
        }
        return new ValueTask(task);
    }

    private async Task DisposeServicesAsync()
    {
        var failures = new List<Exception>();
        Task? pendingWarmUp;
        lock (warmUpSync)
        {
            pendingWarmUp = warmUpTask;
        }
        if (pendingWarmUp is not null)
        {
            // Let an in-flight warm-up finish so a service created during shutdown is disposed.
            try
            {
                await pendingWarmUp;
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }
        await DisposeOneAsync(Shares, failures);
        await DisposeOneAsync(SiteProcesses, failures);
        await DisposeOneAsync(Environment, failures);
        await DisposeOneAsync(Services, failures);
        if (mail.IsValueCreated) await DisposeOneAsync(mail.Value, failures);
        if (dumps.IsValueCreated) await DisposeOneAsync(dumps.Value, failures);
        if (failures.Count > 0)
        {
            throw new AggregateException("One or more Windows services failed to shut down.", failures);
        }
    }

    private static async Task DisposeOneAsync(
        IAsyncDisposable service,
        ICollection<Exception> failures
    )
    {
        try
        {
            await service.DisposeAsync();
        }
        catch (Exception error)
        {
            failures.Add(error);
        }
    }
}
