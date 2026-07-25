import AppKit
import Combine
import Foundation

@MainActor
final class AppModel: ObservableObject {
    @Published var selectedPage: SidebarPage = .general
    @Published var configuration: AppConfiguration
    @Published var sites: [SiteProject] = []
    @Published var selectedSiteID: SiteProject.ID?
    @Published var phpVersions: [RuntimeVersion] = []
    @Published var nodeVersions: [RuntimeVersion] = []
    @Published var environmentStatus: EnvironmentStatus = .stopped
    @Published var lastError: String?
    @Published var isRefreshing = false
    @Published var runtimeOperation: String?
    @Published var siteRuntimePorts: [String: Int] = [:]
    @Published var environmentProxyPort: Int?
    @Published var environmentHTTPSPort: Int?
    @Published var mailMessages: [CapturedMail] = []
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
    private let siteScanner = SiteScanner()
    private let siteRuntimeStore = SiteRuntimeStore()
    private let runtimeInspector: RuntimeInspector
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
    private let logStore: LogStore
    private let appUpdateManager: AppUpdateManager?
    private var didStartApplicationServices = false

    init(configurationStore: ConfigurationStore = ConfigurationStore()) {
        self.configurationStore = configurationStore
        appUpdateManager = AppUpdateManager.configured()
        runtimeInspector = RuntimeInspector(managedRoot: configurationStore.rootURL)
        runtimeInstaller = RuntimeInstaller(rootURL: configurationStore.rootURL)
        xdebugManager = XdebugManager(rootURL: configurationStore.rootURL)
        serviceProcessManager = ServiceProcessManager(rootURL: configurationStore.rootURL)
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
        for index in loadedConfiguration.serviceInstances.indices {
            loadedConfiguration.serviceInstances[index].isRunning = false
        }
        let resolverManager = DomainResolverManager(rootURL: configurationStore.rootURL)
        configuration = loadedConfiguration
        isPresentingOnboarding = !loadedConfiguration.onboardingCompleted
        self.resolverManager = resolverManager
        domainResolverState = resolverManager.state(tld: loadedConfiguration.tld)
        certificateTrustState = certificateManager.trustState()
        networkHelperNeedsUpdate = domainResolverState == .managed
            && !resolverManager.isNetworkHelperCurrent()
        let launchStatus = launchAtLoginManager.status()
        configuration.launchAtLogin = launchStatus.isEnabled
        launchAtLoginRequiresApproval = launchStatus.requiresApproval
        let linkedSitesPath = configurationStore.rootURL.appendingPathComponent("Sites").path
        if !configuration.parkPaths.contains(linkedSitesPath) {
            configuration.parkPaths.insert(linkedSitesPath, at: 0)
            try? configurationStore.save(configuration)
        }
        refresh()
        refreshServiceStates()
        if Self.isRunningUnitTests { return }
        if configuration.onboardingCompleted {
            startApplicationServices()
        }
    }

    private func startApplicationServices() {
        guard !didStartApplicationServices else { return }
        didStartApplicationServices = true
        Task {
            try? await runtimeInstaller.activatePHP(cycle: configuration.selectedPHP)
            phpVersions = runtimeInspector.phpVersions(activeCycle: configuration.selectedPHP)
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
            mailMessages = await mailStore.load()
            dumps = await dumpStore.load()
            startMailServer(reportErrors: false)
            startDumpServer(reportErrors: false)
            startManagedDNSServer(reportErrors: false)
            if configuration.startAutomatically, !sites.isEmpty {
                environmentStatus = .starting
                do {
                    try startLocalEnvironment()
                    startConfiguredServices(reportErrors: false)
                } catch {
                    reportFailure(
                        "Automatic site startup failed: " + error.localizedDescription,
                        reportErrors: false
                    )
                    detectEnvironmentStatus()
                }
            }
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

    deinit {
        environmentEngine.stopAll()
        smtpServer.stop()
        dumpServer.stop()
        dnsServer.stop()
        serviceProcessManager.stopAll()
    }

    func refresh() {
        isRefreshing = true
        sites = siteScanner.scan(paths: configuration.parkPaths)
        if selectedSiteID == nil || !sites.contains(where: { $0.id == selectedSiteID }) {
            selectedSiteID = sites.first?.id
        }
        phpVersions = runtimeInspector.phpVersions(activeCycle: configuration.selectedPHP)
        nodeVersions = runtimeInspector.nodeVersions()
        detectEnvironmentStatus()
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
                        downloadURL: release.downloadURL
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
        refresh()
        refreshDomainResolver()
        restartEnvironmentForRuntimeSettingsIfNeeded()
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
        domainResolverState = resolverManager.state(tld: configuration.tld)
        networkHelperNeedsUpdate = domainResolverState == .managed
            && !resolverManager.isNetworkHelperCurrent()
        dnsServer.stop()
        isDNSServerRunning = domainResolverState == .managed
            && resolverManager.isNetworkHelperRunning()
    }

    func installCertificateAuthority() {
        guard privilegedOperation == nil else { return }
        Task {
            do {
                try await prepareCertificateAuthority()
            } catch {
                lastError = error.localizedDescription
            }
        }
    }

    func refreshCertificateTrust() {
        certificateTrustState = certificateManager.trustState()
    }

    func addParkPath() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        panel.prompt = "Add"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        guard !IndependentPathPolicy.belongsToOtherHerd(url) else {
            lastError = IndependentPathError.otherHerdPath.localizedDescription
            return
        }
        guard !configuration.parkPaths.contains(url.path) else { return }
        configuration.parkPaths.append(url.path)
        persist()
        refresh()
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
                phpVersions = runtimeInspector.phpVersions(activeCycle: cycle)
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
                refresh()
                restartEnvironmentForRuntimeSettingsIfNeeded()
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
        let cycles = phpVersions.filter(\.isInstalled).map(\.cycle)
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
        NSWorkspace.shared.open(siteURL(for: site))
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
        refresh()
        selectedSiteID = sourceURL.resolvingSymlinksInPath().path
        selectedPage = .sites
        restartEnvironmentForRuntimeSettingsIfNeeded()
    }

    func unlinkSite(_ site: SiteProject) {
        do {
            try SiteLinkManager.unlink(site)
            if selectedSiteID == site.id { selectedSiteID = nil }
            refresh()
            restartEnvironmentForRuntimeSettingsIfNeeded()
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
        refresh()
        selectedSiteID = url.path
        selectedPage = .sites
        restartEnvironmentForRuntimeSettingsIfNeeded()
    }

    func addService(definition: ServiceDefinition, name: String, port: Int) {
        let instance = ServiceInstance(
            id: UUID(),
            definitionID: definition.id,
            name: name,
            version: definition.latestVersion,
            port: port,
            isRunning: false,
            startAutomatically: false
        )
        configuration.serviceInstances.append(instance)
        persist()
        refreshServiceStates()
        if serviceState(for: instance) == .notInstalled {
            installService(instance)
        }
    }

    func removeService(_ instance: ServiceInstance) {
        serviceProcessManager.stop(instance)
        configuration.serviceInstances.removeAll(where: { $0.id == instance.id })
        serviceStates[instance.id] = nil
        persist()
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
        serviceProcessManager.stop(instance)
        setServiceRunning(instance.id, running: false)
        refreshServiceStates()
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
            && TablePlusConnection.url(for: instance) != nil
    }

    func openServiceInTablePlus(_ instance: ServiceInstance) {
        guard serviceState(for: instance) == .running else {
            lastError = "Start this database service before opening it in TablePlus."
            return
        }
        guard let connectionURL = TablePlusConnection.url(for: instance) else {
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
    }

    func toggleEnvironment() {
        if environmentStatus == .running {
            configuration.startAutomatically = false
            persist()
            stopLocalEnvironment()
            return
        }

        environmentStatus = .starting
        do {
            try startLocalEnvironment()
            configuration.startAutomatically = true
            persist()
            startConfiguredServices(reportErrors: true)
        } catch {
            lastError = error.localizedDescription
            detectEnvironmentStatus()
        }
    }

    func shutdown() {
        environmentEngine.stopAll()
        siteRuntimePorts.removeAll()
        environmentProxyPort = nil
        environmentHTTPSPort = nil
        stopMailServer()
        stopDumpServer()
        dnsServer.stop()
        isDNSServerRunning = false
        serviceProcessManager.stopAll()
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

    func deleteMail(_ message: CapturedMail) {
        Task {
            do {
                try await mailStore.delete(message)
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
            mailMessages.insert(message, at: 0)
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
        dnsServer.stop()
        guard domainResolverState == .managed else {
            isDNSServerRunning = false
            return
        }
        isDNSServerRunning = resolverManager.isNetworkHelperRunning()
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
        if environmentEngine.isRunning {
            environmentStatus = .running
            siteRuntimePorts = environmentEngine.ports
            environmentProxyPort = environmentEngine.proxyPort
            environmentHTTPSPort = environmentEngine.httpsProxyPort
            refreshCertificateTrust()
            return
        }
        do {
            let result = try ProcessRunner.run(
                URL(fileURLWithPath: "/usr/sbin/lsof"),
                arguments: ["-nP", "-iTCP:80", "-sTCP:LISTEN"],
                timeout: 10
            )
            if result.output.contains("Herd") || result.output.contains("nginx") {
                environmentStatus = .conflict
            } else {
                environmentStatus = .stopped
            }
        } catch {
            environmentStatus = .stopped
        }
    }

    private func updateSiteRuntime(_ cycle: String?, kind: SiteRuntimeKind, for site: SiteProject) {
        let shouldRestart = environmentStatus == .running
        do {
            try siteRuntimeStore.set(cycle, kind: kind, for: site)
            let selectedID = site.id
            refresh()
            selectedSiteID = selectedID
            if shouldRestart {
                environmentStatus = .starting
                environmentEngine.stopAll()
                clearEnvironmentEndpoints()
                try startLocalEnvironment()
            }
        } catch {
            lastError = error.localizedDescription
            detectEnvironmentStatus()
        }
    }

    private func startLocalEnvironment() throws {
        siteRuntimePorts = try environmentEngine.start(
            sites: sites,
            defaultPHP: executableLocator.find("php"),
            defaultPHPCycle: configuration.selectedPHP,
            tld: configuration.tld,
            debuggerSettings: debuggerSettings,
            phpRequestSettings: phpRequestSettings
        )
        environmentProxyPort = environmentEngine.proxyPort
        environmentHTTPSPort = environmentEngine.httpsProxyPort
        if let environmentProxyPort, let environmentHTTPSPort {
            do {
                try resolverManager.updateNetworkRouting(
                    httpPort: environmentProxyPort,
                    httpsPort: environmentHTTPSPort,
                    tld: configuration.tld
                )
            } catch {
                environmentEngine.stopAll()
                clearEnvironmentEndpoints()
                throw error
            }
        }
        refreshCertificateTrust()
        environmentStatus = .running
    }

    private func restartEnvironmentForRuntimeSettingsIfNeeded() {
        guard environmentStatus == .running else { return }
        environmentStatus = .starting
        environmentEngine.stopAll()
        clearEnvironmentEndpoints()
        do {
            try startLocalEnvironment()
        } catch {
            lastError = error.localizedDescription
            detectEnvironmentStatus()
        }
    }

    private func managedPHPExecutable(cycle: String) -> URL {
        configurationStore.rootURL.appendingPathComponent("Runtimes/php/\(cycle)/bin/php")
    }

    private func stopLocalEnvironment() {
        environmentStatus = .stopping
        environmentEngine.stopAll()
        clearEnvironmentEndpoints()
        serviceProcessManager.stopAll()
        for index in configuration.serviceInstances.indices {
            configuration.serviceInstances[index].isRunning = false
        }
        refreshServiceStates()
        environmentStatus = .stopped
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
        guard serviceOperation == nil else { return }
        let instances = configuration.serviceInstances.filter {
            $0.startAutomatically && serviceProcessManager.state(for: $0) == .stopped
        }
        guard !instances.isEmpty else { return }

        Task {
            for instance in instances {
                serviceOperation = instance.id
                do {
                    try await serviceProcessManager.start(instance)
                    setServiceRunning(instance.id, running: true)
                } catch {
                    if reportErrors { lastError = error.localizedDescription }
                    setServiceRunning(instance.id, running: false)
                }
                refreshServiceStates()
            }
            serviceOperation = nil
        }
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
            let phpRuntime = runtimeInspector.phpVersions(activeCycle: phpCycle)
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
            let nodeRuntime = runtimeInspector.nodeVersions().first { $0.cycle == nodeCycle }
            if nodeRuntime?.isInstalled == true {
                try await runtimeInstaller.activateNode(cycle: nodeCycle)
            } else {
                latestNodeVersions[nodeCycle] = try await runtimeInstaller.installNode(cycle: nodeCycle)
            }

            onboardingStage = .finishing
            refresh()
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
        _ = try certificateManager.installAuthority(tld: configuration.tld)
        for _ in 0..<120 {
            try await Task.sleep(for: .milliseconds(500))
            refreshCertificateTrust()
            if certificateTrustState == .trusted {
                return
            }
        }
        throw LocalCertificateError.authorizationFailed("Certificate trust was not completed.")
    }

    private func prepareDomainResolver() async throws {
        privilegedOperation = "domains"
        defer { privilegedOperation = nil }
        _ = try resolverManager.install(
            tld: configuration.tld,
            replacingExternal: domainResolverState == .external
        )
        for _ in 0..<120 {
            try await Task.sleep(for: .milliseconds(500))
            refreshDomainResolver()
            if Self.domainResolverIsReady(
                state: domainResolverState,
                helperRunning: isDNSServerRunning,
                helperNeedsUpdate: networkHelperNeedsUpdate
            ) {
                startManagedDNSServer()
                return
            }
        }
        throw DomainResolverError.authorizationFailed("Local domain setup was not completed.")
    }
}
