import AppKit
import Combine
import Foundation

@MainActor
final class AppModel: ObservableObject {
    private static let automaticHTTPSKeychainAccessKey =
        "HerdMeAutomaticHTTPSKeychainAccessApproved"
    private static let automaticServiceKeychainAccessPrefix =
        "HerdMeAutomaticServiceKeychainAccessApproved."

    private struct RefreshSnapshot: Sendable {
        let sites: [SiteProject]
        let phpVersions: [RuntimeVersion]
        let nodeVersions: [RuntimeVersion]
        let domainResolverState: DomainResolverState
        let networkHelperNeedsUpdate: Bool
        let isNetworkHelperRunning: Bool
        let certificateTrustState: CertificateTrustState
    }

    private struct EnvironmentEndpoints: Sendable {
        let sitePorts: [String: Int]
        let proxyPort: Int?
        let httpsPort: Int?
    }

    private enum EnvironmentInspection: Sendable {
        case running(EnvironmentEndpoints)
        case inactive(hadManagedState: Bool, hasPortConflict: Bool)
    }

    enum SiteOpenPreparation: Equatable, Sendable {
        case open
        case start
        case restart
        case wait
    }

    @Published var selectedPage: SidebarPage = .general
    @Published var configuration: AppConfiguration
    @Published var sites: [SiteProject] = []
    @Published var selectedSiteID: SiteProject.ID?
    @Published var selectedLogSiteID: SiteProject.ID?
    @Published var phpVersions: [RuntimeVersion] = []
    @Published var nodeVersions: [RuntimeVersion] = []
    @Published var environmentStatus: EnvironmentStatus = .stopped
    @Published var lastError: String?
    @Published var isRefreshing = false
    @Published var runtimeOperation: String?
    @Published var siteRuntimePorts: [String: Int] = [:]
    @Published var environmentProxyPort: Int?
    @Published var environmentHTTPSPort: Int?
    @Published var mailMessages: [CapturedMailSummary] = []
    @Published var isMailServerRunning = false
    @Published var dumps: [CapturedDump] = []
    @Published var isDumpServerRunning = false
    @Published var domainResolverState: DomainResolverState
    @Published var isDNSServerRunning = false
    @Published var networkHelperNeedsUpdate = false
    @Published var certificateTrustState: CertificateTrustState
    @Published var privilegedOperation: String?
    @Published var launchAtLoginRequiresApproval = false
    @Published var serviceStates: [UUID: ServiceRuntimeState] = [:]
    @Published var serviceOperation: UUID?
    @Published var outdatedServiceDefinitionIDs: Set<String> = []
    @Published var debuggerSettings = DebuggerSettings.load()
    @Published var phpRequestSettings = PHPRequestSettings.load()
    @Published var xdebugInstallation: XdebugInstallation?
    @Published var debuggerOperation: String?
    @Published var composerVersion: String?
    @Published var latestComposerVersion: String?
    @Published var laravelInstallerVersion: String?
    @Published var latestLaravelInstallerVersion: String?
    @Published var latestPHPVersions: [String: String] = [:]
    @Published var latestNodeVersions: [String: String] = [:]
    @Published var isCheckingForUpdates = false
    @Published var updateNotice: AppUpdateNotice?
    @Published private(set) var isPresentingOnboarding = false
    @Published private(set) var onboardingStage: OnboardingStage = .welcome
    @Published private(set) var isRunningInitialSetup = false
    @Published private(set) var onboardingError: String?

    let configurationStore: ConfigurationStore
    private let siteRuntimeStore = SiteRuntimeStore()
    private let runtimeInstaller: RuntimeInstaller
    private let xdebugManager: XdebugManager
    private let executableLocator: ExecutableLocator
    private let environmentEngine: LocalEnvironmentEngine
    private let mailStore: MailStore
    private let smtpServer = SMTPServer()
    private let dumpStore: DumpStore
    private let dumpServer = DumpCaptureServer()
    private let resolverManager: DomainResolverManager
    private let dnsServer = LocalDNSServer()
    private let certificateManager: LocalCertificateManager
    private let terminalCommandLauncher: TerminalCommandLauncher
    private let launchAtLoginManager = LaunchAtLoginManager()
    private let serviceProcessManager: ServiceProcessManager
    private let serviceCredentialStore: ServiceCredentialStore
    let logStore: LogStore
    private let appUpdateManager: AppUpdateManager?
    private var didStartApplicationServices = false
    private var didShutdown = false

    init(
        configurationStore: ConfigurationStore = ConfigurationStore(),
        serviceCredentialStore: ServiceCredentialStore = ServiceCredentialStore()
    ) {
        self.configurationStore = configurationStore
        self.serviceCredentialStore = serviceCredentialStore
        appUpdateManager = AppUpdateManager.configured()
        runtimeInstaller = RuntimeInstaller(rootURL: configurationStore.rootURL)
        xdebugManager = XdebugManager(rootURL: configurationStore.rootURL)
        serviceProcessManager = ServiceProcessManager(
            rootURL: configurationStore.rootURL,
            credentialStore: serviceCredentialStore
        )
        logStore = LogStore(rootURL: configurationStore.rootURL.appendingPathComponent("Log", isDirectory: true))
        terminalCommandLauncher = TerminalCommandLauncher(rootURL: configurationStore.rootURL)
        executableLocator = ExecutableLocator(managedRoot: configurationStore.rootURL)
        let certificateManager = LocalCertificateManager(rootURL: configurationStore.rootURL)
        self.certificateManager = certificateManager
        environmentEngine = LocalEnvironmentEngine(
            rootURL: configurationStore.rootURL,
            certificateManager: certificateManager
        )
        mailStore = MailStore(rootURL: configurationStore.rootURL)
        dumpStore = DumpStore(rootURL: configurationStore.rootURL)
        var loadedConfiguration = configurationStore.load()
        let configurationLoadIssue = configurationStore.loadIssue
        for index in loadedConfiguration.serviceInstances.indices {
            loadedConfiguration.serviceInstances[index].isRunning = false
        }
        let resolverManager = DomainResolverManager(rootURL: configurationStore.rootURL)
        configuration = loadedConfiguration
        lastError = configurationLoadIssue?.message
        isPresentingOnboarding = !loadedConfiguration.onboardingCompleted
        self.resolverManager = resolverManager
        domainResolverState = .missing
        certificateTrustState = .missing
        networkHelperNeedsUpdate = false
        let launchStatus = launchAtLoginManager.status()
        configuration.launchAtLogin = launchStatus.isEnabled
        launchAtLoginRequiresApproval = launchStatus.requiresApproval
        let linkedSitesPath = configurationStore.rootURL.appendingPathComponent("Sites").path
        if !configuration.parkPaths.contains(linkedSitesPath) {
            configuration.parkPaths.insert(linkedSitesPath, at: 0)
            if configurationLoadIssue == nil {
                try? configurationStore.save(configuration)
            }
        }
        refreshServiceStates()
        if Self.isRunningUnitTests { return }
        Task { [weak self] in
            guard let self else { return }
            await refreshState()
            if configuration.onboardingCompleted {
                startApplicationServices()
            }
        }
    }

    private func startApplicationServices() {
        guard !didStartApplicationServices else { return }
        didStartApplicationServices = true
        Task {
            startMailServer(reportErrors: false)
            startDumpServer(reportErrors: false)
            startManagedDNSServer(reportErrors: false)

            await startConfiguredServicesNow(reportErrors: false)
            try? await runtimeInstaller.activatePHP(cycle: configuration.selectedPHP)
            if configuration.startAutomatically, !sites.isEmpty {
                environmentStatus = .starting
                do {
                    try await startLocalEnvironment()
                } catch {
                    reportFailure(
                        "Automatic site startup failed: " + error.localizedDescription,
                        reportErrors: false
                    )
                    await detectEnvironmentStatusNow(afterFailedTransition: true)
                }
            }
            let rootURL = configurationStore.rootURL
            let selectedPHP = configuration.selectedPHP
            phpVersions = await Task.detached(priority: .userInitiated) {
                RuntimeInspector(managedRoot: rootURL).phpVersions(activeCycle: selectedPHP)
            }.value
            xdebugInstallation = await xdebugManager.installed(
                cycle: configuration.selectedPHP,
                php: managedPHPExecutable(cycle: configuration.selectedPHP)
            )
            composerVersion = await runtimeInstaller.composerVersion(
                cycle: configuration.selectedPHP
            )
            latestComposerVersion = try? await runtimeInstaller.latestComposerVersion(
                cycle: configuration.selectedPHP
            )
            laravelInstallerVersion = await runtimeInstaller.laravelInstallerVersion(
                cycle: configuration.selectedPHP
            )
            latestLaravelInstallerVersion = try? await runtimeInstaller.latestLaravelInstallerVersion()
            latestPHPVersions = (try? await runtimeInstaller.latestPHPVersions(
                cycles: phpVersions.filter(\.isInstalled).map(\.cycle)
            )) ?? [:]
            latestNodeVersions = (try? await runtimeInstaller.latestNodeVersions(
                cycles: nodeVersions.map(\.cycle)
            )) ?? [:]
            outdatedServiceDefinitionIDs = (try? await serviceProcessManager.outdatedDefinitionIDs()) ?? []
            mailMessages = await mailStore.loadSummaries()
            dumps = await dumpStore.load()
        }
        if configuration.automaticUpdates {
            checkForUpdates(userInitiated: false)
        }
    }

    private static var isRunningUnitTests: Bool {
        let environment = ProcessInfo.processInfo.environment
        return environment["XCTestConfigurationFilePath"] != nil
            || environment["XCTestBundlePath"] != nil
    }

    var selectedSite: SiteProject? {
        sites.first(where: { $0.id == selectedSiteID }) ?? sites.first
    }

    func showApplicationLogs() {
        selectedLogSiteID = nil
        selectedPage = .logs
    }

    func showLogs(for site: SiteProject) {
        selectedSiteID = site.id
        selectedLogSiteID = site.id
        selectedPage = .logs
    }

    deinit {
        environmentEngine.stopAllImmediately()
        smtpServer.stop()
        dumpServer.stop()
        dnsServer.stop()
        serviceProcessManager.stopAllImmediately()
    }

    func refresh() {
        Task { await refreshState() }
    }

    private func refreshState() async {
        isRefreshing = true
        let rootURL = configurationStore.rootURL
        let parkPaths = configuration.parkPaths
        let selectedPHP = configuration.selectedPHP
        let tld = configuration.tld
        let snapshot = await Task.detached(priority: .userInitiated) {
            let resolver = DomainResolverManager(rootURL: rootURL)
            let resolverState = resolver.state(tld: tld)
            return RefreshSnapshot(
                sites: SiteScanner().scan(paths: parkPaths),
                phpVersions: RuntimeInspector(managedRoot: rootURL).phpVersions(activeCycle: selectedPHP),
                nodeVersions: RuntimeInspector(managedRoot: rootURL).nodeVersions(),
                domainResolverState: resolverState,
                networkHelperNeedsUpdate: resolverState == .managed
                    && !resolver.isNetworkHelperCurrent(),
                isNetworkHelperRunning: resolverState == .managed
                    && resolver.isNetworkHelperRunning(),
                certificateTrustState: LocalCertificateManager(rootURL: rootURL).trustState()
            )
        }.value
        guard !Task.isCancelled else {
            isRefreshing = false
            return
        }
        sites = snapshot.sites
        if selectedSiteID == nil || !sites.contains(where: { $0.id == selectedSiteID }) {
            selectedSiteID = sites.first?.id
        }
        phpVersions = snapshot.phpVersions
        nodeVersions = snapshot.nodeVersions
        domainResolverState = snapshot.domainResolverState
        networkHelperNeedsUpdate = snapshot.networkHelperNeedsUpdate
        isDNSServerRunning = snapshot.isNetworkHelperRunning
        certificateTrustState = snapshot.certificateTrustState
        await detectEnvironmentStatusNow()
        isRefreshing = false
    }

    func persist() {
        do {
            try configurationStore.save(configuration)
        } catch {
            lastError = error.localizedDescription
        }
    }

    func beginInitialSetup() {
        guard !isRunningInitialSetup else { return }
        isRunningInitialSetup = true
        onboardingError = nil
        onboardingStage = .localDomains
        Task { await runInitialSetup() }
    }

    func finishOnboarding() {
        guard onboardingStage == .completed else { return }
        isPresentingOnboarding = false
    }

    func setAutomaticUpdates(_ enabled: Bool) {
        configuration.automaticUpdates = enabled
        persist()
        if enabled { checkForUpdates(userInitiated: false) }
    }

    func setUpdateChannel(_ channel: String) {
        configuration.updateChannel = channel
        persist()
        if configuration.automaticUpdates { checkForUpdates(userInitiated: false) }
    }

    func checkForUpdates(userInitiated: Bool = true) {
        guard !isCheckingForUpdates else { return }
        guard let appUpdateManager else {
            if userInitiated {
                updateNotice = AppUpdateNotice(
                    title: "Updates unavailable",
                    message: "This build does not contain an update feed.",
                    downloadURL: nil
                )
            }
            return
        }
        isCheckingForUpdates = true
        let channel = configuration.updateChannel
        Task {
            defer { isCheckingForUpdates = false }
            do {
                switch try await appUpdateManager.check(channel: channel) {
                case let .upToDate(version):
                    if userInitiated {
                        updateNotice = AppUpdateNotice(
                            title: "HerdMe is up to date",
                            message: "Version \(version) is the newest \(channel.lowercased()) release.",
                            downloadURL: nil
                        )
                    }
                case let .available(release):
                    updateNotice = AppUpdateNotice(
                        title: "HerdMe \(release.version) is available",
                        message: release.notes,
                        downloadURL: release.platformDownloadURL
                    )
                }
            } catch {
                if userInitiated {
                    updateNotice = AppUpdateNotice(
                        title: "Update check failed",
                        message: error.localizedDescription,
                        downloadURL: nil
                    )
                }
            }
        }
    }

    func setLaunchAtLogin(_ enabled: Bool) {
        do {
            try launchAtLoginManager.setEnabled(enabled)
            refreshLaunchAtLogin()
            persist()
        } catch {
            refreshLaunchAtLogin()
            lastError = error.localizedDescription
        }
    }

    func refreshLaunchAtLogin() {
        let status = launchAtLoginManager.status()
        configuration.launchAtLogin = status.isEnabled
        launchAtLoginRequiresApproval = status.requiresApproval
    }

    func openLoginItemsSettings() {
        launchAtLoginManager.openSystemSettings()
    }

    func updateTLD(_ tld: String) {
        let normalized = tld.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard DomainResolverManager.isValid(tld: normalized) else {
            lastError = DomainResolverError.invalidTLD.localizedDescription
            return
        }
        configuration.tld = normalized
        persist()
        let shouldRestart = environmentStatus == .running
        Task {
            await refreshState()
            if shouldRestart, environmentStatus == .running { await restartEnvironmentNow() }
        }
    }

    func installDomainResolver() {
        guard privilegedOperation == nil else { return }
        Task {
            do {
                try await prepareDomainResolver()
            } catch {
                lastError = error.localizedDescription
            }
        }
    }

    func refreshDomainResolver() {
        Task { await refreshDomainResolverNow() }
    }

    private func refreshDomainResolverNow() async {
        let resolver = resolverManager
        let tld = configuration.tld
        let snapshot = await Task.detached(priority: .utility) {
            let state = resolver.state(tld: tld)
            return (
                state,
                state == .managed && !resolver.isNetworkHelperCurrent(),
                state == .managed && resolver.isNetworkHelperRunning()
            )
        }.value
        domainResolverState = snapshot.0
        networkHelperNeedsUpdate = snapshot.1
        dnsServer.stop()
        isDNSServerRunning = snapshot.2
    }

    func installCertificateAuthority() {
        guard privilegedOperation == nil else { return }
        Task {
            do {
                try await prepareCertificateAuthority()
                if environmentStatus == .running, environmentHTTPSPort == nil {
                    await restartEnvironmentNow()
                }
            } catch {
                lastError = error.localizedDescription
            }
        }
    }

    func refreshCertificateTrust() {
        Task { await refreshCertificateTrustNow() }
    }

    private func refreshCertificateTrustNow() async {
        let rootURL = configurationStore.rootURL
        certificateTrustState = await Task.detached(priority: .utility) {
            LocalCertificateManager(rootURL: rootURL).trustState()
        }.value
    }

    @discardableResult
    func addParkPath(_ url: URL) -> Bool {
        guard !IndependentPathPolicy.belongsToOtherHerd(url) else {
            lastError = IndependentPathError.otherHerdPath.localizedDescription
            return false
        }
        guard !configuration.parkPaths.contains(url.path) else { return false }
        configuration.parkPaths.append(url.path)
        persist()
        refresh()
        return true
    }

    func removeParkPath(at offsets: IndexSet) {
        configuration.parkPaths.remove(atOffsets: offsets)
        persist()
        refresh()
    }

    func setActivePHP(_ cycle: String) {
        guard runtimeOperation == nil else { return }
        runtimeOperation = "php-\(cycle)"
        Task {
            do {
                try await runtimeInstaller.activatePHP(cycle: cycle)
                configuration.selectedPHP = cycle
                persist()
                let rootURL = configurationStore.rootURL
                phpVersions = await Task.detached(priority: .userInitiated) {
                    RuntimeInspector(managedRoot: rootURL).phpVersions(activeCycle: cycle)
                }.value
                xdebugInstallation = await xdebugManager.installed(
                    cycle: cycle,
                    php: managedPHPExecutable(cycle: cycle)
                )
                composerVersion = await runtimeInstaller.composerVersion(cycle: cycle)
                latestComposerVersion = try? await runtimeInstaller.latestComposerVersion(cycle: cycle)
                laravelInstallerVersion = await runtimeInstaller.laravelInstallerVersion(cycle: cycle)
                latestLaravelInstallerVersion = try? await runtimeInstaller.latestLaravelInstallerVersion()
                restartEnvironmentForRuntimeSettingsIfNeeded()
            } catch {
                lastError = error.localizedDescription
            }
            runtimeOperation = nil
        }
    }

    func installPHP(_ cycle: String) {
        guard runtimeOperation == nil else { return }
        runtimeOperation = "php-\(cycle)"
        Task {
            do {
                let installedVersion = try await runtimeInstaller.installPHP(cycle: cycle)
                latestPHPVersions[cycle] = installedVersion
                configuration.selectedPHP = cycle
                persist()
                xdebugInstallation = await xdebugManager.installed(
                    cycle: cycle,
                    php: managedPHPExecutable(cycle: cycle)
                )
                composerVersion = await runtimeInstaller.composerVersion(cycle: cycle)
                latestComposerVersion = try? await runtimeInstaller.latestComposerVersion(cycle: cycle)
                laravelInstallerVersion = await runtimeInstaller.laravelInstallerVersion(cycle: cycle)
                latestLaravelInstallerVersion = try? await runtimeInstaller.latestLaravelInstallerVersion()
                await refreshState()
                await restartEnvironmentIfNeededNow()
            } catch {
                lastError = error.localizedDescription
            }
            runtimeOperation = nil
        }
    }

    func installNode(_ cycle: String) {
        guard runtimeOperation == nil else { return }
        runtimeOperation = "node-\(cycle)"
        Task {
            do {
                let installedVersion = try await runtimeInstaller.installNode(cycle: cycle)
                latestNodeVersions[cycle] = installedVersion
                refresh()
            } catch {
                lastError = error.localizedDescription
            }
            runtimeOperation = nil
        }
    }

    func refreshLaravelInstaller() {
        let cycle = configuration.selectedPHP
        Task {
            laravelInstallerVersion = await runtimeInstaller.laravelInstallerVersion(cycle: cycle)
            latestLaravelInstallerVersion = try? await runtimeInstaller.latestLaravelInstallerVersion()
        }
    }

    func refreshComposer() {
        let cycle = configuration.selectedPHP
        Task {
            composerVersion = await runtimeInstaller.composerVersion(cycle: cycle)
            latestComposerVersion = try? await runtimeInstaller.latestComposerVersion(cycle: cycle)
        }
    }

    func refreshNodeUpdates() {
        let cycles = nodeVersions.map(\.cycle)
        Task {
            latestNodeVersions = (try? await runtimeInstaller.latestNodeVersions(cycles: cycles)) ?? [:]
        }
    }

    func refreshPHPUpdates() {
        let cycles = phpVersions
            .filter { $0.isInstalled && PHPRuntimeSupport.isInstallable($0.cycle) }
            .map(\.cycle)
        Task {
            latestPHPVersions = (try? await runtimeInstaller.latestPHPVersions(cycles: cycles)) ?? [:]
        }
    }

    var isLaravelInstallerUpdateAvailable: Bool {
        guard let installed = laravelInstallerVersion,
              let latest = latestLaravelInstallerVersion else { return false }
        return RuntimeInstaller.isNewerVersion(latest, than: installed)
    }

    var isComposerUpdateAvailable: Bool {
        guard let installed = composerVersion,
              let latest = latestComposerVersion else { return false }
        return RuntimeInstaller.isNewerVersion(latest, than: installed)
    }

    func isNodeUpdateAvailable(_ runtime: RuntimeVersion) -> Bool {
        guard let installed = runtime.installedVersion,
              let latest = latestNodeVersions[runtime.cycle] else { return false }
        return RuntimeInstaller.isNewerVersion(latest, than: installed)
    }

    func isPHPUpdateAvailable(_ runtime: RuntimeVersion) -> Bool {
        guard let installed = runtime.installedVersion,
              let latest = latestPHPVersions[runtime.cycle] else { return false }
        return RuntimeInstaller.isNewerVersion(latest, than: installed)
    }

    func updateLaravelInstaller() {
        guard runtimeOperation == nil else { return }
        let cycle = configuration.selectedPHP
        runtimeOperation = "laravel-installer"
        Task {
            do {
                laravelInstallerVersion = try await runtimeInstaller.updateLaravelInstaller(cycle: cycle)
                latestLaravelInstallerVersion = laravelInstallerVersion
            } catch {
                lastError = error.localizedDescription
            }
            runtimeOperation = nil
        }
    }

    func updateComposer() {
        guard runtimeOperation == nil else { return }
        let cycle = configuration.selectedPHP
        runtimeOperation = "composer"
        Task {
            do {
                composerVersion = try await runtimeInstaller.updateComposer(cycle: cycle)
                latestComposerVersion = composerVersion
            } catch {
                lastError = error.localizedDescription
            }
            runtimeOperation = nil
        }
    }

    func createProject(
        _ request: NewProjectRequest,
        progress: @escaping @MainActor @Sendable (ProjectCreationStage) -> Void = { _ in }
    ) async throws -> URL {
        progress(.validatingRequest)
        try ProjectCreator.validate(request)
        progress(.preparingLaravelInstaller)
        try await runtimeInstaller.prepareLaravelInstallerForProjectCreation(
            cycle: configuration.selectedPHP
        )
        try Task.checkCancellation()
        return try await ProjectCreator(rootURL: configurationStore.rootURL).create(
            request,
            progress: progress
        )
    }

    func activateNode(_ cycle: String) {
        guard runtimeOperation == nil else { return }
        runtimeOperation = "node-\(cycle)"
        Task {
            do {
                try await runtimeInstaller.activateNode(cycle: cycle)
                refresh()
            } catch {
                lastError = error.localizedDescription
            }
            runtimeOperation = nil
        }
    }

    func removeNode(_ cycle: String) {
        guard runtimeOperation == nil else { return }
        runtimeOperation = "node-\(cycle)"
        Task {
            do {
                try await runtimeInstaller.removeNode(cycle: cycle)
                refresh()
            } catch {
                lastError = error.localizedDescription
            }
            runtimeOperation = nil
        }
    }

    func refreshXdebugInstallation() {
        let cycle = configuration.selectedPHP
        Task {
            xdebugInstallation = await xdebugManager.installed(
                cycle: cycle,
                php: managedPHPExecutable(cycle: cycle)
            )
        }
    }

    func installXdebug() {
        guard debuggerOperation == nil else { return }
        let cycle = configuration.selectedPHP
        debuggerOperation = "Installing Xdebug"
        Task {
            do {
                xdebugInstallation = try await xdebugManager.install(cycle: cycle)
                restartEnvironmentForRuntimeSettingsIfNeeded()
            } catch {
                lastError = error.localizedDescription
            }
            debuggerOperation = nil
        }
    }

    func persistDebuggerSettings() {
        let normalized = debuggerSettings.normalized
        if normalized.enabled, xdebugInstallation == nil {
            debuggerSettings.enabled = false
            debuggerSettings = debuggerSettings.normalized
            debuggerSettings.save()
            lastError = "Install Xdebug for PHP \(configuration.selectedPHP) before enabling the debugger."
            return
        }
        debuggerSettings = normalized
        debuggerSettings.save()
        restartEnvironmentForRuntimeSettingsIfNeeded()
    }

    func persistPHPRequestSettings() {
        phpRequestSettings = phpRequestSettings.normalized
        let defaults = UserDefaults.standard
        defaults.set(phpRequestSettings.maxUploadMegabytes, forKey: PHPRequestSettings.uploadKey)
        defaults.set(phpRequestSettings.memoryLimitMegabytes, forKey: PHPRequestSettings.memoryKey)
        restartEnvironmentForRuntimeSettingsIfNeeded()
    }

    func openDebugSession(for site: SiteProject) {
        guard xdebugInstallation != nil else {
            lastError = "Install Xdebug for PHP \(configuration.selectedPHP) before starting a debug session."
            return
        }
        guard debuggerSettings.enabled else {
            lastError = "Enable Xdebug before starting a debug session."
            return
        }
        guard var components = URLComponents(url: siteURL(for: site), resolvingAgainstBaseURL: false) else {
            lastError = "HerdMe could not create the debug URL for \(site.name)."
            return
        }
        var queryItems = components.queryItems ?? []
        queryItems.removeAll { $0.name == "XDEBUG_TRIGGER" }
        queryItems.append(URLQueryItem(name: "XDEBUG_TRIGGER", value: debuggerSettings.normalized.ideKey))
        components.queryItems = queryItems
        guard let url = components.url else {
            lastError = "HerdMe could not create the debug URL for \(site.name)."
            return
        }
        NSWorkspace.shared.open(url)
    }

    func setSitePHPVersion(_ cycle: String?, for site: SiteProject) {
        if let cycle, !phpVersions.contains(where: { $0.cycle == cycle && $0.isInstalled }) {
            lastError = RuntimeInstallationError.runtimeNotInstalled(name: "PHP", cycle: cycle).localizedDescription
            return
        }
        updateSiteRuntime(cycle, kind: .php, for: site)
    }

    func setSiteNodeVersion(_ cycle: String?, for site: SiteProject) {
        if let cycle, !nodeVersions.contains(where: { $0.cycle == cycle && $0.isInstalled }) {
            lastError = RuntimeInstallationError.runtimeNotInstalled(name: "Node.js", cycle: cycle).localizedDescription
            return
        }
        updateSiteRuntime(cycle, kind: .node, for: site)
    }

    func openSite(_ site: SiteProject) {
        let preparation = Self.siteOpenPreparation(
            environmentStatus: environmentStatus,
            hasRuntimePort: siteRuntimePorts[site.id] != nil
        )
        switch preparation {
        case .open:
            openSiteURL(for: site)
        case .start, .restart:
            environmentStatus = .starting
            Task {
                await prepareEnvironmentAndOpenSite(
                    site,
                    restart: preparation == .restart
                )
            }
        case .wait:
            lastError = "HerdMe is still updating the local site environment. Try again in a moment."
        }
    }

    nonisolated static func siteOpenPreparation(
        environmentStatus: EnvironmentStatus,
        hasRuntimePort: Bool
    ) -> SiteOpenPreparation {
        switch environmentStatus {
        case .running:
            hasRuntimePort ? .open : .restart
        case .stopped, .conflict:
            .start
        case .starting, .stopping:
            .wait
        }
    }

    nonisolated static func performBlockingOperation<T: Sendable>(
        priority: TaskPriority = .userInitiated,
        _ operation: @escaping @Sendable () throws -> T
    ) async throws -> T {
        try await Task.detached(priority: priority, operation: operation).value
    }

    func sitePreviewURL(for site: SiteProject) -> URL {
        if let port = siteRuntimePorts[site.id] {
            return URL(string: "http://127.0.0.1:\(port)")!
        }
        return siteURL(for: site)
    }

    func siteURL(for site: SiteProject) -> URL {
        if certificateTrustState == .trusted, let httpsPort = environmentHTTPSPort {
            let usesStandardPort = httpsPort == 443
                || domainResolverState == .managed && isDNSServerRunning
            let port = usesStandardPort ? "" : ":\(httpsPort)"
            return URL(string: "https://\(site.domain(tld: configuration.tld))\(port)")!
        }
        if let proxyPort = environmentProxyPort {
            let usesStandardPort = proxyPort == 80
                || domainResolverState == .managed && isDNSServerRunning
            let port = usesStandardPort ? "" : ":\(proxyPort)"
            return URL(string: "http://\(site.domain(tld: configuration.tld))\(port)")!
        }
        if let port = siteRuntimePorts[site.id] {
            return URL(string: "http://127.0.0.1:\(port)")!
        }
        return URL(string: "http://\(site.domain(tld: configuration.tld))")!
    }

    func siteDisplayAddress(for site: SiteProject) -> String {
        Self.siteDisplayAddress(
            domain: site.domain(tld: configuration.tld),
            navigationURL: siteURL(for: site)
        )
    }

    nonisolated static func siteDisplayAddress(domain: String, navigationURL: URL) -> String {
        let scheme = navigationURL.scheme == "https" ? "https" : "http"
        return "\(scheme)://\(domain)"
    }

    var isHTTPSActive: Bool {
        environmentStatus == .running && environmentHTTPSPort != nil
    }

    var automaticHTTPSEnabled: Bool {
        UserDefaults.standard.bool(forKey: Self.automaticHTTPSKeychainAccessKey)
    }

    var httpsStatusTitle: String {
        Self.httpsStatusTitle(
            certificateTrustState: certificateTrustState,
            environmentStatus: environmentStatus,
            hasHTTPSPort: environmentHTTPSPort != nil,
            automaticHTTPSEnabled: automaticHTTPSEnabled
        )
    }

    var shouldOfferHTTPSAction: Bool {
        certificateTrustState != .trusted
            || !automaticHTTPSEnabled
            || environmentStatus == .running && !isHTTPSActive
    }

    var httpsActionTitle: String {
        if certificateTrustState != .trusted { return "Trust" }
        if automaticHTTPSEnabled, environmentStatus == .running, !isHTTPSActive {
            return "Retry"
        }
        return "Enable"
    }

    nonisolated static func httpsStatusTitle(
        certificateTrustState: CertificateTrustState,
        environmentStatus: EnvironmentStatus,
        hasHTTPSPort: Bool,
        automaticHTTPSEnabled: Bool
    ) -> String {
        guard certificateTrustState == .trusted else {
            return certificateTrustState.title
        }
        if environmentStatus == .running {
            if hasHTTPSPort { return "Active" }
            return automaticHTTPSEnabled ? "Unavailable" : "HTTP only"
        }
        return automaticHTTPSEnabled ? "Enabled" : "Disabled"
    }

    nonisolated static func shouldAttemptAutomaticHTTPS(
        certificateTrustState: CertificateTrustState,
        automaticHTTPSEnabled _: Bool
    ) -> Bool {
        certificateTrustState == .trusted
    }

    nonisolated static func domainResolverIsReady(
        state: DomainResolverState,
        helperRunning: Bool,
        helperNeedsUpdate: Bool
    ) -> Bool {
        state == .managed && helperRunning && !helperNeedsUpdate
    }

    func openTerminal(for site: SiteProject) {
        openTerminal(
            command: "cd \(TerminalCommandLauncher.shellQuote(site.path.path))\nexec \"${SHELL:-/bin/zsh}\" -l",
            title: site.name
        )
    }

    func openTinker(for site: SiteProject) {
        let artisan = site.path.appendingPathComponent("artisan")
        guard FileManager.default.isReadableFile(atPath: artisan.path) else {
            lastError = "Tinker is available only for Laravel projects with an artisan executable."
            return
        }
        let cycle = site.phpVersion ?? configuration.selectedPHP
        let php = managedPHPExecutable(cycle: cycle)
        guard FileManager.default.isExecutableFile(atPath: php.path) else {
            lastError = RuntimeInstallationError.runtimeNotInstalled(name: "PHP", cycle: cycle).localizedDescription
            return
        }
        openTerminal(
            command: "cd \(TerminalCommandLauncher.shellQuote(site.path.path))\n"
                + "\(TerminalCommandLauncher.shellQuote(php.path)) artisan tinker\n"
                + "exec \"${SHELL:-/bin/zsh}\" -l",
            title: site.name + "-Tinker"
        )
    }

    func openIDE(for site: SiteProject) {
        let bundleIdentifiers: [String: String] = [
            "VSCode": "com.microsoft.VSCode",
            "PhpStorm": "com.jetbrains.PhpStorm",
            "Sublime Text": "com.sublimetext.4"
        ]
        if let identifier = bundleIdentifiers[configuration.ide],
           let appURL = NSWorkspace.shared.urlForApplication(withBundleIdentifier: identifier) {
            let configuration = NSWorkspace.OpenConfiguration()
            NSWorkspace.shared.open([site.path], withApplicationAt: appURL, configuration: configuration)
        } else {
            NSWorkspace.shared.open(site.path)
        }
    }

    func openConfigurationDirectory() {
        NSWorkspace.shared.open(configurationStore.rootURL)
    }

    func linkExistingSite(at sourceURL: URL) throws {
        guard !IndependentPathPolicy.belongsToOtherHerd(sourceURL) else {
            throw IndependentPathError.otherHerdPath
        }
        let linksDirectory = configurationStore.rootURL.appendingPathComponent("Sites", isDirectory: true)
        let destination = linksDirectory.appendingPathComponent(sourceURL.lastPathComponent)
        if FileManager.default.fileExists(atPath: destination.path) {
            throw CocoaError(.fileWriteFileExists)
        }
        try FileManager.default.createSymbolicLink(at: destination, withDestinationURL: sourceURL)
        selectedSiteID = sourceURL.resolvingSymlinksInPath().path
        selectedPage = .sites
        let shouldRestart = environmentStatus == .running
        Task {
            await refreshState()
            selectedSiteID = sourceURL.resolvingSymlinksInPath().path
            if shouldRestart, environmentStatus == .running { await restartEnvironmentNow() }
        }
    }

    func unlinkSite(_ site: SiteProject) {
        do {
            try SiteLinkManager.unlink(site)
            if selectedSiteID == site.id { selectedSiteID = nil }
            let shouldRestart = environmentStatus == .running
            Task {
                await refreshState()
                if shouldRestart, environmentStatus == .running { await restartEnvironmentNow() }
            }
        } catch {
            lastError = error.localizedDescription
        }
    }

    func registerCreatedSite(at url: URL) {
        let parent = url.deletingLastPathComponent().path
        if !configuration.parkPaths.contains(parent) {
            configuration.parkPaths.append(parent)
            persist()
        }
        selectedSiteID = url.path
        selectedPage = .sites
        let shouldRestart = environmentStatus == .running
        Task {
            await refreshState()
            selectedSiteID = url.path
            if shouldRestart, environmentStatus == .running { await restartEnvironmentNow() }
        }
    }

    func suggestedServicePort(startingAt port: Int) -> Int? {
        LocalEnvironmentEngine.availablePort(
            startingAt: port,
            excluding: Set(configuration.serviceInstances.map(\.port))
        )
    }

    @discardableResult
    func addService(definition: ServiceDefinition, name: String, port: Int) -> Bool {
        let normalizedName = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !normalizedName.isEmpty, port > 0, port <= 65_535 else {
            lastError = "Enter a service name and a port between 1 and 65535."
            return false
        }
        if configuration.serviceInstances.contains(where: { $0.port == port }) {
            let suggestion = suggestedServicePort(startingAt: min(port + 1, 65_535))
            lastError = "Port \(port) is already assigned to another HerdMe service."
                + (suggestion.map { " Use port \($0) instead." } ?? "")
            return false
        }
        guard LocalEnvironmentEngine.canBind(port: port) else {
            let suggestion = suggestedServicePort(startingAt: min(port + 1, 65_535))
            lastError = "Port \(port) is already used by another application."
                + (suggestion.map { " Use port \($0) instead." } ?? "")
            return false
        }
        let instance = ServiceInstance(
            id: UUID(),
            definitionID: definition.id,
            name: normalizedName,
            version: definition.latestVersion,
            port: port,
            isRunning: false,
            startAutomatically: false
        )
        do {
            _ = try serviceCredentialStore.credentials(for: instance.id)
            approveAutomaticServiceKeychainAccess(for: instance)
        } catch {
            lastError = error.localizedDescription
            return false
        }
        configuration.serviceInstances.append(instance)
        persist()
        refreshServiceStates()
        if serviceState(for: instance) == .notInstalled {
            installService(instance)
        }
        return true
    }

    func removeService(_ instance: ServiceInstance) {
        guard serviceOperation == nil else { return }
        serviceOperation = instance.id
        Task {
            await serviceProcessManager.stop(instance)
            configuration.serviceInstances.removeAll(where: { $0.id == instance.id })
            serviceStates[instance.id] = nil
            UserDefaults.standard.removeObject(
                forKey: Self.automaticServiceKeychainAccessKey(for: instance.id)
            )
            persist()
            do {
                try serviceCredentialStore.delete(for: instance.id)
            } catch {
                lastError = error.localizedDescription
            }
            serviceOperation = nil
        }
    }

    func serviceState(for instance: ServiceInstance) -> ServiceRuntimeState {
        serviceStates[instance.id] ?? serviceProcessManager.state(for: instance)
    }

    func refreshServiceStates() {
        serviceStates = Dictionary(uniqueKeysWithValues: configuration.serviceInstances.map { instance in
            (instance.id, serviceProcessManager.state(for: instance))
        })
    }

    func refreshServiceUpdates() {
        Task {
            outdatedServiceDefinitionIDs = (try? await serviceProcessManager.outdatedDefinitionIDs()) ?? []
        }
    }

    func isServiceUpdateAvailable(_ instance: ServiceInstance) -> Bool {
        serviceState(for: instance) != .notInstalled
            && outdatedServiceDefinitionIDs.contains(instance.definitionID)
    }

    func installService(_ instance: ServiceInstance) {
        guard serviceOperation == nil else { return }
        serviceOperation = instance.id
        Task {
            do {
                let version = try await serviceProcessManager.install(definitionID: instance.definitionID)
                if let index = configuration.serviceInstances.firstIndex(where: { $0.id == instance.id }) {
                    configuration.serviceInstances[index].version = version
                    persist()
                }
                outdatedServiceDefinitionIDs.remove(instance.definitionID)
                refreshServiceStates()
            } catch {
                lastError = error.localizedDescription
            }
            serviceOperation = nil
        }
    }

    func startService(_ instance: ServiceInstance) {
        guard serviceOperation == nil else { return }
        serviceOperation = instance.id
        Task {
            do {
                try await serviceProcessManager.start(instance)
                approveAutomaticServiceKeychainAccess(for: instance)
                setServiceRunning(instance.id, running: true)
                refreshServiceStates()
            } catch {
                lastError = error.localizedDescription
                setServiceRunning(instance.id, running: false)
                refreshServiceStates()
            }
            serviceOperation = nil
        }
    }

    func stopService(_ instance: ServiceInstance) {
        guard serviceOperation == nil else { return }
        serviceOperation = instance.id
        Task {
            await serviceProcessManager.stop(instance)
            setServiceRunning(instance.id, running: false)
            refreshServiceStates()
            serviceOperation = nil
        }
    }

    func setServiceAutomaticStart(_ instance: ServiceInstance, enabled: Bool) {
        guard let index = configuration.serviceInstances.firstIndex(where: { $0.id == instance.id }) else { return }
        configuration.serviceInstances[index].startAutomatically = enabled
        persist()
    }

    func openServiceDataDirectory(_ instance: ServiceInstance) {
        let url = serviceProcessManager.dataDirectory(for: instance)
        do {
            try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
            NSWorkspace.shared.open(url)
        } catch {
            lastError = error.localizedDescription
        }
    }

    func addServiceEnvironment(_ instance: ServiceInstance, to site: SiteProject) throws
        -> ServiceEnvironmentUpdate {
        let credentials = try serviceCredentialStore.credentials(for: instance.id)
        let update = try ServiceEnvironmentFile.update(
            projectURL: site.path,
            instance: instance,
            credentials: credentials
        )
        try? logStore.append("Updated \(update.environmentURL.path) for \(instance.name).")
        return update
    }

    func openServiceConsole(_ instance: ServiceInstance) {
        guard let url = serviceProcessManager.consoleURL(for: instance) else {
            lastError = "Start this storage service before opening its console."
            return
        }
        NSWorkspace.shared.open(url)
    }

    func canOpenServiceConsole(_ instance: ServiceInstance) -> Bool {
        serviceState(for: instance) == .running
            && serviceProcessManager.consoleURL(for: instance) != nil
    }

    func canOpenServiceInTablePlus(_ instance: ServiceInstance) -> Bool {
        serviceState(for: instance) == .running
            && TablePlusConnection.supports(instance.definitionID)
    }

    func openServiceInTablePlus(_ instance: ServiceInstance) {
        guard serviceState(for: instance) == .running else {
            lastError = "Start this database service before opening it in TablePlus."
            return
        }
        do {
            guard let connectionURL = try serviceConnectionURL(for: instance) else {
                lastError = "TablePlus connections are not available for \(instance.name)."
                return
            }
            guard let applicationURL = NSWorkspace.shared.urlForApplication(
                withBundleIdentifier: TablePlusConnection.bundleIdentifier
            ) else {
                lastError = "Install TablePlus before opening this database service."
                return
            }

            let openConfiguration = NSWorkspace.OpenConfiguration()
            openConfiguration.activates = true
            NSWorkspace.shared.open(
                [connectionURL],
                withApplicationAt: applicationURL,
                configuration: openConfiguration
            )
        } catch {
            lastError = error.localizedDescription
        }
    }

    func copyServiceConnectionURL(_ instance: ServiceInstance) {
        guard serviceState(for: instance) == .running else {
            lastError = "Start this database service before copying its connection URL."
            return
        }
        do {
            guard let connectionURL = try serviceConnectionURL(for: instance) else {
                lastError = "Connection URLs are not available for \(instance.name)."
                return
            }
            let pasteboard = NSPasteboard.general
            pasteboard.clearContents()
            guard pasteboard.setString(connectionURL.absoluteString, forType: .string) else {
                lastError = "HerdMe could not copy the connection URL."
                return
            }
        } catch {
            lastError = error.localizedDescription
        }
    }

    private func serviceConnectionURL(for instance: ServiceInstance) throws -> URL? {
        let credentials = DatabaseServiceAuthenticator.protectedDefinitions.contains(instance.definitionID)
            ? try serviceCredentialStore.credentials(for: instance.id)
            : nil
        return TablePlusConnection.url(for: instance, credentials: credentials)
    }

    func toggleEnvironment() {
        guard environmentStatus != .starting, environmentStatus != .stopping else { return }
        if environmentStatus == .running {
            configuration.startAutomatically = false
            persist()
            environmentStatus = .stopping
            Task { await stopLocalEnvironment() }
            return
        }

        environmentStatus = .starting
        Task {
            do {
                await startConfiguredServicesNow(reportErrors: true)
                try await startLocalEnvironment()
                configuration.startAutomatically = true
                persist()
            } catch {
                lastError = error.localizedDescription
                await detectEnvironmentStatusNow(afterFailedTransition: true)
            }
        }
    }

    func shutdown() async {
        guard !didShutdown else { return }
        didShutdown = true
        await environmentEngine.stopAll()
        siteRuntimePorts.removeAll()
        environmentProxyPort = nil
        environmentHTTPSPort = nil
        stopMailServer()
        stopDumpServer()
        dnsServer.stop()
        isDNSServerRunning = false
        await serviceProcessManager.stopAll()
        for index in configuration.serviceInstances.indices {
            configuration.serviceInstances[index].isRunning = false
        }
        refreshServiceStates()
        environmentStatus = .stopped
    }

    func startMailServer(reportErrors: Bool = true) {
        guard !smtpServer.isRunning else {
            isMailServerRunning = true
            return
        }
        do {
            try smtpServer.start(
                port: configuration.smtpPort,
                onStateChange: { [weak self] running, errorMessage in
                    Task { @MainActor in
                        self?.isMailServerRunning = running
                        if let errorMessage {
                            self?.reportFailure(
                                "SMTP server failed: " + errorMessage,
                                reportErrors: reportErrors
                            )
                        }
                    }
                },
                onMessage: { [weak self] message in
                    Task { @MainActor in
                        await self?.captureMail(message)
                    }
                }
            )
        } catch {
            isMailServerRunning = false
            reportFailure("SMTP server failed: " + error.localizedDescription, reportErrors: reportErrors)
        }
    }

    func stopMailServer() {
        smtpServer.stop()
        isMailServerRunning = false
    }

    func restartMailServer() {
        stopMailServer()
        persist()
        startMailServer()
    }

    func mailMessage(id: CapturedMail.ID) async throws -> CapturedMail {
        try await mailStore.message(id: id)
    }

    func deleteMail(_ message: CapturedMail) {
        Task {
            do {
                try await mailStore.delete(id: message.id)
                mailMessages.removeAll { $0.id == message.id }
            } catch {
                lastError = error.localizedDescription
            }
        }
    }

    func clearMail() {
        Task {
            do {
                try await mailStore.clear()
                mailMessages.removeAll()
            } catch {
                lastError = error.localizedDescription
            }
        }
    }

    private func captureMail(_ message: CapturedMail) async {
        do {
            try await mailStore.save(message)
            mailMessages.insert(message.summary, at: 0)
        } catch {
            lastError = error.localizedDescription
        }
    }

    func startDumpServer(reportErrors: Bool = true) {
        guard !dumpServer.isRunning else {
            isDumpServerRunning = true
            return
        }
        do {
            try dumpServer.start(
                port: configuration.dumpPort,
                onStateChange: { [weak self] running, errorMessage in
                    Task { @MainActor in
                        self?.isDumpServerRunning = running
                        if let errorMessage {
                            self?.reportFailure(
                                "Dump server failed: " + errorMessage,
                                reportErrors: reportErrors
                            )
                        }
                    }
                },
                onDump: { [weak self] dump in
                    Task { @MainActor in
                        await self?.captureDump(dump)
                    }
                }
            )
        } catch {
            isDumpServerRunning = false
            reportFailure("Dump server failed: " + error.localizedDescription, reportErrors: reportErrors)
        }
    }

    func stopDumpServer() {
        dumpServer.stop()
        isDumpServerRunning = false
    }

    func clearDumps() {
        Task {
            do {
                try await dumpStore.clear()
                dumps.removeAll()
            } catch {
                lastError = error.localizedDescription
            }
        }
    }

    private func captureDump(_ dump: CapturedDump) async {
        do {
            try await dumpStore.save(dump)
            dumps.insert(dump, at: 0)
        } catch {
            lastError = error.localizedDescription
        }
    }

    private func startManagedDNSServer(reportErrors: Bool = true) {
        Task { await startManagedDNSServerNow(reportErrors: reportErrors) }
    }

    private func startManagedDNSServerNow(reportErrors: Bool = true) async {
        dnsServer.stop()
        guard domainResolverState == .managed else {
            isDNSServerRunning = false
            return
        }
        let resolver = resolverManager
        isDNSServerRunning = await Task.detached(priority: .utility) {
            resolver.isNetworkHelperRunning()
        }.value
        if !isDNSServerRunning {
            reportFailure(
                "The HerdMe local network helper is configured but is not running.",
                reportErrors: reportErrors
            )
        }
    }

    private func reportFailure(_ message: String, reportErrors: Bool) {
        if reportErrors {
            lastError = message
        } else {
            try? logStore.append(message)
        }
    }

    private func detectEnvironmentStatus() {
        Task { await detectEnvironmentStatusNow() }
    }

    private func detectEnvironmentStatusNow(afterFailedTransition: Bool = false) async {
        guard afterFailedTransition
                || environmentStatus != .starting && environmentStatus != .stopping else {
            return
        }
        let engine = environmentEngine
        let inspection = await Task.detached(priority: .utility) {
            if engine.isRunning {
                return EnvironmentInspection.running(EnvironmentEndpoints(
                    sitePorts: engine.ports,
                    proxyPort: engine.proxyPort,
                    httpsPort: engine.httpsProxyPort
                ))
            }
            let hadManagedState = engine.hasManagedState
            if hadManagedState { await engine.stopAll() }
            let hasPortConflict: Bool
            do {
                let result = try ProcessRunner.run(
                    URL(fileURLWithPath: "/usr/sbin/lsof"),
                    arguments: ["-nP", "-iTCP:80", "-sTCP:LISTEN"],
                    timeout: 10
                )
                hasPortConflict = result.output.contains("Herd") || result.output.contains("nginx")
            } catch {
                hasPortConflict = false
            }
            return EnvironmentInspection.inactive(
                hadManagedState: hadManagedState,
                hasPortConflict: hasPortConflict
            )
        }.value

        switch inspection {
        case let .running(endpoints):
            apply(endpoints)
            environmentStatus = .running
            await refreshCertificateTrustNow()
        case let .inactive(hadManagedState, hasPortConflict):
            clearEnvironmentEndpoints()
            if hadManagedState, configuration.startAutomatically, !sites.isEmpty {
                environmentStatus = .starting
                do {
                    try await startLocalEnvironment()
                    try? logStore.append("Recovered an unhealthy local site environment.")
                    return
                } catch {
                    lastError = "HerdMe could not recover the local site environment: "
                        + error.localizedDescription
                }
            }
            environmentStatus = hasPortConflict ? .conflict : .stopped
        }
    }

    private func updateSiteRuntime(_ cycle: String?, kind: SiteRuntimeKind, for site: SiteProject) {
        let shouldRestart = environmentStatus == .running
        do {
            try siteRuntimeStore.set(cycle, kind: kind, for: site)
            let selectedID = site.id
            selectedSiteID = selectedID
            Task {
                await refreshState()
                selectedSiteID = selectedID
                if shouldRestart, environmentStatus == .running {
                    await restartEnvironmentNow()
                }
            }
        } catch {
            lastError = error.localizedDescription
            detectEnvironmentStatus()
        }
    }

    private func startLocalEnvironment() async throws {
        let engine = environmentEngine
        let startSites = sites
        let defaultPHP = executableLocator.find("php")
        let defaultPHPCycle = configuration.selectedPHP
        let tld = configuration.tld
        let debugger = debuggerSettings
        let requestSettings = phpRequestSettings
        let automaticHTTPSEnabled = UserDefaults.standard.bool(
            forKey: Self.automaticHTTPSKeychainAccessKey
        )
        let enableHTTPS = Self.shouldAttemptAutomaticHTTPS(
            certificateTrustState: certificateTrustState,
            automaticHTTPSEnabled: automaticHTTPSEnabled
        )
        let endpoints = try await Task.detached(priority: .userInitiated) {
            let ports = try await engine.start(
                sites: startSites,
                defaultPHP: defaultPHP,
                defaultPHPCycle: defaultPHPCycle,
                tld: tld,
                debuggerSettings: debugger,
                phpRequestSettings: requestSettings,
                enableHTTPS: enableHTTPS
            )
            return EnvironmentEndpoints(
                sitePorts: ports,
                proxyPort: engine.proxyPort,
                httpsPort: engine.httpsProxyPort
            )
        }.value
        apply(endpoints)
        if enableHTTPS, environmentHTTPSPort != nil, !automaticHTTPSEnabled {
            UserDefaults.standard.set(
                true,
                forKey: Self.automaticHTTPSKeychainAccessKey
            )
        }
        if let httpsStartupError = engine.httpsStartupError {
            let message: String
            if automaticHTTPSEnabled {
                message = "Sites started over HTTP because HTTPS could not start. Open General settings and enable the HTTPS certificate again."
                lastError = message
            } else if certificateTrustState == .trusted {
                message = "Sites started over HTTP because saved HTTPS credentials need approval. Open General settings and choose Enable."
            } else {
                message = "Sites started over HTTP. HTTPS is waiting for explicit approval in General settings."
            }
            try? logStore.append(message + " " + httpsStartupError)
        }
        if let environmentProxyPort {
            let resolver = resolverManager
            let httpsPort = environmentHTTPSPort
            do {
                try await Task.detached(priority: .utility) {
                    try resolver.updateNetworkRouting(
                        httpPort: environmentProxyPort,
                        httpsPort: httpsPort,
                        tld: tld
                    )
                }.value
            } catch {
                await engine.stopAll()
                clearEnvironmentEndpoints()
                throw error
            }
        }
        await refreshCertificateTrustNow()
        environmentStatus = .running
    }

    private func restartEnvironmentForRuntimeSettingsIfNeeded() {
        Task { await restartEnvironmentIfNeededNow() }
    }

    private func restartEnvironmentIfNeededNow() async {
        guard environmentStatus == .running else { return }
        environmentStatus = .starting
        await restartEnvironmentNow()
    }

    private func restartEnvironmentNow() async {
        environmentStatus = .starting
        let engine = environmentEngine
        await engine.stopAll()
        clearEnvironmentEndpoints()
        do {
            try await startLocalEnvironment()
        } catch {
            lastError = error.localizedDescription
            await detectEnvironmentStatusNow(afterFailedTransition: true)
        }
    }

    private func prepareEnvironmentAndOpenSite(_ site: SiteProject, restart: Bool) async {
        if restart {
            await environmentEngine.stopAll()
            clearEnvironmentEndpoints()
        }
        do {
            await startConfiguredServicesNow(reportErrors: true)
            try await startLocalEnvironment()
            configuration.startAutomatically = true
            persist()
            guard siteRuntimePorts[site.id] != nil else {
                lastError = "HerdMe started the local environment but could not route \(site.domain(tld: configuration.tld))."
                return
            }
            openSiteURL(for: site)
        } catch {
            lastError = "HerdMe could not open \(site.domain(tld: configuration.tld)): "
                + error.localizedDescription
            await detectEnvironmentStatusNow(afterFailedTransition: true)
        }
    }

    private func openSiteURL(for site: SiteProject) {
        guard NSWorkspace.shared.open(siteURL(for: site)) else {
            lastError = "HerdMe could not open the default browser."
            return
        }
    }

    private func managedPHPExecutable(cycle: String) -> URL {
        configurationStore.rootURL.appendingPathComponent("Runtimes/php/\(cycle)/bin/php")
    }

    private func stopLocalEnvironment() async {
        let engine = environmentEngine
        let services = serviceProcessManager
        await engine.stopAll()
        await services.stopAll()
        clearEnvironmentEndpoints()
        for index in configuration.serviceInstances.indices {
            configuration.serviceInstances[index].isRunning = false
        }
        refreshServiceStates()
        environmentStatus = .stopped
    }

    private func apply(_ endpoints: EnvironmentEndpoints) {
        siteRuntimePorts = endpoints.sitePorts
        environmentProxyPort = endpoints.proxyPort
        environmentHTTPSPort = endpoints.httpsPort
    }

    private func clearEnvironmentEndpoints() {
        siteRuntimePorts.removeAll()
        environmentProxyPort = nil
        environmentHTTPSPort = nil
    }

    private func setServiceRunning(_ id: UUID, running: Bool) {
        guard let index = configuration.serviceInstances.firstIndex(where: { $0.id == id }) else { return }
        configuration.serviceInstances[index].isRunning = running
        persist()
    }

    private func startConfiguredServices(reportErrors: Bool) {
        Task { await startConfiguredServicesNow(reportErrors: reportErrors) }
    }

    private func startConfiguredServicesNow(reportErrors: Bool) async {
        guard serviceOperation == nil else { return }
        let instances = configuration.serviceInstances.filter {
            $0.startAutomatically && serviceProcessManager.state(for: $0) == .stopped
                && automaticServiceStartupIsAuthorized(for: $0)
        }
        guard !instances.isEmpty else { return }

        for instance in instances {
            serviceOperation = instance.id
            do {
                try await serviceProcessManager.start(
                    instance,
                    allowCredentialInteraction: false
                )
                setServiceRunning(instance.id, running: true)
            } catch {
                if reportErrors { lastError = error.localizedDescription }
                setServiceRunning(instance.id, running: false)
            }
            refreshServiceStates()
        }
        serviceOperation = nil
    }

    private static func automaticServiceKeychainAccessKey(for identifier: UUID) -> String {
        automaticServiceKeychainAccessPrefix + identifier.uuidString.lowercased()
    }

    private func automaticServiceStartupIsAuthorized(for instance: ServiceInstance) -> Bool {
        !ServiceProcessManager.requiresCredentials(definitionID: instance.definitionID)
            || UserDefaults.standard.bool(
                forKey: Self.automaticServiceKeychainAccessKey(for: instance.id)
            )
    }

    private func approveAutomaticServiceKeychainAccess(for instance: ServiceInstance) {
        guard ServiceProcessManager.requiresCredentials(definitionID: instance.definitionID) else { return }
        UserDefaults.standard.set(
            true,
            forKey: Self.automaticServiceKeychainAccessKey(for: instance.id)
        )
    }

    private func openTerminal(command: String, title: String) {
        do {
            try terminalCommandLauncher.open(command: command, title: title)
        } catch {
            lastError = error.localizedDescription
        }
    }

    private func runInitialSetup() async {
        do {
            try await prepareDomainResolver()

            onboardingStage = .certificate
            try await prepareCertificateAuthority()

            let phpCycle = AppConfiguration.default.selectedPHP
            onboardingStage = .php
            let rootURL = configurationStore.rootURL
            let phpRuntime = await Task.detached(priority: .userInitiated) {
                RuntimeInspector(managedRoot: rootURL).phpVersions(activeCycle: phpCycle)
            }.value
                .first { $0.cycle == phpCycle }
            if phpRuntime?.isInstalled == true {
                try await runtimeInstaller.activatePHP(cycle: phpCycle)
            } else {
                latestPHPVersions[phpCycle] = try await runtimeInstaller.installPHP(cycle: phpCycle)
            }
            try PHPRuntimeValidator().validate(executable: managedPHPExecutable(cycle: phpCycle))
            configuration.selectedPHP = phpCycle
            try configurationStore.save(configuration)

            onboardingStage = .composer
            try await runtimeInstaller.prepareLaravelInstallerForProjectCreation(cycle: phpCycle)
            composerVersion = await runtimeInstaller.composerVersion(cycle: phpCycle)
            laravelInstallerVersion = await runtimeInstaller.laravelInstallerVersion(cycle: phpCycle)

            let nodeCycle = "22"
            onboardingStage = .node
            let nodeRuntime = await Task.detached(priority: .userInitiated) {
                RuntimeInspector(managedRoot: rootURL).nodeVersions()
            }.value.first { $0.cycle == nodeCycle }
            if nodeRuntime?.isInstalled == true {
                try await runtimeInstaller.activateNode(cycle: nodeCycle)
            } else {
                latestNodeVersions[nodeCycle] = try await runtimeInstaller.installNode(cycle: nodeCycle)
            }

            onboardingStage = .finishing
            await refreshState()
            var completedConfiguration = configuration
            completedConfiguration.onboardingCompleted = true
            try configurationStore.save(completedConfiguration)
            configuration = completedConfiguration
            onboardingStage = .completed
            startApplicationServices()
        } catch {
            onboardingError = ErrorPresentation(error.localizedDescription).message
        }
        isRunningInitialSetup = false
    }

    private func prepareCertificateAuthority() async throws {
        privilegedOperation = "certificate"
        defer { privilegedOperation = nil }
        let manager = certificateManager
        let tld = configuration.tld
        _ = try await Self.performBlockingOperation {
            try manager.installAuthority(tld: tld)
        }
        for _ in 0..<120 {
            try await Task.sleep(for: .milliseconds(500))
            await refreshCertificateTrustNow()
            if certificateTrustState == .trusted {
                UserDefaults.standard.set(
                    true,
                    forKey: Self.automaticHTTPSKeychainAccessKey
                )
                return
            }
        }
        throw LocalCertificateError.authorizationFailed("Certificate trust was not completed.")
    }

    private func prepareDomainResolver() async throws {
        privilegedOperation = "domains"
        defer { privilegedOperation = nil }
        let resolver = resolverManager
        let tld = configuration.tld
        let replacingExternal = domainResolverState == .external
        _ = try await Self.performBlockingOperation {
            try resolver.install(tld: tld, replacingExternal: replacingExternal)
        }
        for _ in 0..<120 {
            try await Task.sleep(for: .milliseconds(500))
            await refreshDomainResolverNow()
            if Self.domainResolverIsReady(
                state: domainResolverState,
                helperRunning: isDNSServerRunning,
                helperNeedsUpdate: networkHelperNeedsUpdate
            ) {
                await startManagedDNSServerNow()
                return
            }
        }
        throw DomainResolverError.authorizationFailed("Local domain setup was not completed.")
    }
}
