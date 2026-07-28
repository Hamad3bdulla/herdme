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
        let httpsStartupNeedsApproval: Bool
    }

    enum SiteOpenPreparation: Equatable, Sendable {
        case open
        case start
        case restart
        case wait
    }

    @Published var configuration: AppConfiguration
    @Published var lastError: String?
    @Published var isRefreshing = false

    let applicationSettings: ApplicationSettingsCoordinator
    let navigation: AppNavigation
    let mail: MailCoordinator
    let dumpsCoordinator: DumpsCoordinator
    let services: ServicesCoordinator
    let runtime: RuntimeCoordinator
    let sitesCoordinator: SitesCoordinator
    let siteTools: SiteToolsCoordinator
    let environment: EnvironmentCoordinator
    let security: SecuritySetupCoordinator
    let configurationStore: ConfigurationStore
    private let applicationTasks = ApplicationTaskRegistry()
    let logStore: LogStore
    private var isEnvironmentHealthMonitorRunning = false
    private var lastAutomaticDomainRepairFailure: String?
    private var isRepairingDomainResolverAutomatically = false
    private var domainResolverRefreshRevision: UInt64 = 0
    private var certificateTrustRefreshRevision: UInt64 = 0
    private var didStartApplicationServices = false
    private var didShutdown = false

    init(
        configurationStore: ConfigurationStore = ConfigurationStore(),
        serviceCredentialStore: ServiceCredentialStore = ServiceCredentialStore(),
        navigation: AppNavigation = AppNavigation(),
        siteTools: SiteToolsCoordinator? = nil,
        applicationSettings: ApplicationSettingsCoordinator? = nil
    ) {
        self.navigation = navigation
        self.configurationStore = configurationStore
        self.applicationSettings = applicationSettings ?? ApplicationSettingsCoordinator()
        runtime = RuntimeCoordinator(rootURL: configurationStore.rootURL)
        sitesCoordinator = SitesCoordinator(rootURL: configurationStore.rootURL)
        self.siteTools = siteTools ?? SiteToolsCoordinator(rootURL: configurationStore.rootURL)
        services = ServicesCoordinator(
            rootURL: configurationStore.rootURL,
            credentialStore: serviceCredentialStore
        )
        logStore = LogStore(rootURL: configurationStore.rootURL.appendingPathComponent("Log", isDirectory: true))
        let security = SecuritySetupCoordinator(rootURL: configurationStore.rootURL)
        self.security = security
        environment = EnvironmentCoordinator(
            rootURL: configurationStore.rootURL,
            certificateManager: security.certificateManager
        )
        mail = MailCoordinator(rootURL: configurationStore.rootURL)
        dumpsCoordinator = DumpsCoordinator(rootURL: configurationStore.rootURL)
        var loadedConfiguration = configurationStore.load()
        let configurationLoadIssue = configurationStore.loadIssue
        for index in loadedConfiguration.serviceInstances.indices {
            loadedConfiguration.serviceInstances[index].isRunning = false
        }
        configuration = loadedConfiguration
        lastError = configurationLoadIssue?.message ?? RuntimeCatalog.loadIssue
        security.isPresentingOnboarding = !loadedConfiguration.onboardingCompleted
        let launchStatus = self.applicationSettings.refreshLaunchAtLogin()
        configuration.launchAtLogin = launchStatus.isEnabled
        let linkedSitesPath = configurationStore.rootURL.appendingPathComponent("Sites").path
        if !configuration.parkPaths.contains(linkedSitesPath) {
            configuration.parkPaths.insert(linkedSitesPath, at: 0)
            if configurationLoadIssue == nil {
                try? configurationStore.save(configuration)
            }
        }
        refreshServiceStates()
        if AppExecutionContext.isTesting() { return }
        startTrackedTask { model in
            await model.refreshState()
            if model.configuration.onboardingCompleted {
                model.startApplicationServices()
            }
        }
    }

    private func startApplicationServices() {
        guard !didStartApplicationServices else { return }
        didStartApplicationServices = true
        startEnvironmentHealthMonitor()
        startTrackedTask { model in
            model.startMailServer(reportErrors: false)
            model.startDumpServer(reportErrors: false)
            await model.repairDomainResolverIfNeeded()
            await model.startManagedDNSServerNow(reportErrors: false)

            model.startConfiguredServicesInBackground(reportErrors: false)
            try? await model.runtime.activatePHP(cycle: model.configuration.selectedPHP)
            guard !Task.isCancelled, !model.didShutdown else { return }
            if model.configuration.startAutomatically, !model.sites.isEmpty {
                model.environmentStatus = .starting
                do {
                    await model.prepareAutomaticHTTPSIdentityIfNeeded()
                    try await model.startLocalEnvironment()
                } catch {
                    model.reportFailure(
                        "Automatic site startup failed: " + error.localizedDescription,
                        reportErrors: false
                    )
                    await model.detectEnvironmentStatusNow(afterFailedTransition: true)
                }
            }
            guard !Task.isCancelled, !model.didShutdown else { return }
            let rootURL = model.configurationStore.rootURL
            let selectedPHP = model.configuration.selectedPHP
            guard
                let phpVersions = try? await Self.performBlockingOperation({
                    let versions = RuntimeInspector(managedRoot: rootURL)
                        .phpVersions(activeCycle: selectedPHP)
                    try Task.checkCancellation()
                    return versions
                })
            else { return }
            model.phpVersions = phpVersions
            guard !Task.isCancelled, !model.didShutdown else { return }
            await model.runtime.refreshTooling(
                cycle: model.configuration.selectedPHP,
                php: model.managedPHPExecutable(cycle: model.configuration.selectedPHP)
            )
            await model.runtime.refreshAvailableUpdates(
                phpCycles: model.phpVersions.filter(\.isInstalled).map(\.cycle),
                nodeCycles: model.nodeVersions.map(\.cycle)
            )
            await model.services.refreshUpdates()
            await model.mail.load()
            await model.dumpsCoordinator.load()
        }
        if configuration.automaticUpdates {
            checkForUpdates(userInitiated: false)
        }
    }

    var selectedSite: SiteProject? {
        sitesCoordinator.selectedSite(identifier: selectedSiteID)
    }

    var launchAtLoginRequiresApproval: Bool {
        applicationSettings.launchAtLoginRequiresApproval
    }

    var isCheckingForUpdates: Bool {
        applicationSettings.isCheckingForUpdates
    }

    var updateNotice: AppUpdateNotice? {
        get { applicationSettings.updateNotice }
        set { applicationSettings.updateNotice = newValue }
    }

    var sites: [SiteProject] {
        get { sitesCoordinator.sites }
        set { sitesCoordinator.replaceSites(newValue) }
    }

    var siteRuntimePorts: [String: Int] {
        get { sitesCoordinator.runtimePorts }
        set { sitesCoordinator.replaceRuntimePorts(newValue) }
    }

    var environmentStatus: EnvironmentStatus {
        get { environment.status }
        set { environment.status = newValue }
    }

    var environmentProxyPort: Int? {
        get { environment.proxyPort }
        set { environment.proxyPort = newValue }
    }

    var environmentHTTPSPort: Int? {
        get { environment.httpsPort }
        set { environment.httpsPort = newValue }
    }

    var domainResolverState: DomainResolverState {
        get { security.domainResolverState }
        set { security.domainResolverState = newValue }
    }

    var isDNSServerRunning: Bool {
        get { security.isDNSServerRunning }
        set { security.isDNSServerRunning = newValue }
    }

    var networkHelperNeedsUpdate: Bool {
        get { security.networkHelperNeedsUpdate }
        set { security.networkHelperNeedsUpdate = newValue }
    }

    var certificateTrustState: CertificateTrustState {
        get { security.certificateTrustState }
        set { security.certificateTrustState = newValue }
    }

    var privilegedOperation: String? {
        get { security.privilegedOperation }
        set { security.privilegedOperation = newValue }
    }

    var isPresentingOnboarding: Bool {
        get { security.isPresentingOnboarding }
        set { security.isPresentingOnboarding = newValue }
    }

    var onboardingStage: OnboardingStage {
        get { security.onboardingStage }
        set { security.onboardingStage = newValue }
    }

    var isRunningInitialSetup: Bool {
        get { security.isRunningInitialSetup }
        set { security.isRunningInitialSetup = newValue }
    }

    var onboardingError: ErrorPresentation? {
        get { security.onboardingError }
        set { security.onboardingError = newValue }
    }

    var mailMessages: [CapturedMailSummary] { mail.messages }

    var isMailServerRunning: Bool { mail.isServerRunning }

    var dumps: [CapturedDump] { dumpsCoordinator.dumps }

    var isDumpServerRunning: Bool { dumpsCoordinator.isServerRunning }

    var phpVersions: [RuntimeVersion] {
        get { runtime.phpVersions }
        set { runtime.phpVersions = newValue }
    }

    var nodeVersions: [RuntimeVersion] {
        get { runtime.nodeVersions }
        set { runtime.nodeVersions = newValue }
    }

    var runtimeOperation: String? { runtime.operation }

    var debuggerSettings: DebuggerSettings {
        get { runtime.debuggerSettings }
        set { runtime.debuggerSettings = newValue }
    }

    var phpRequestSettings: PHPRequestSettings {
        get { runtime.phpRequestSettings }
        set { runtime.phpRequestSettings = newValue }
    }

    var xdebugInstallation: XdebugInstallation? {
        get { runtime.xdebugInstallation }
        set { runtime.xdebugInstallation = newValue }
    }

    var debuggerOperation: String? { runtime.debuggerOperation }

    var composerVersion: String? {
        get { runtime.composerVersion }
        set { runtime.composerVersion = newValue }
    }

    var latestComposerVersion: String? {
        get { runtime.latestComposerVersion }
        set { runtime.latestComposerVersion = newValue }
    }

    var laravelInstallerVersion: String? {
        get { runtime.laravelInstallerVersion }
        set { runtime.laravelInstallerVersion = newValue }
    }

    var latestLaravelInstallerVersion: String? {
        get { runtime.latestLaravelInstallerVersion }
        set { runtime.latestLaravelInstallerVersion = newValue }
    }

    var latestPHPVersions: [String: String] {
        get { runtime.latestPHPVersions }
        set { runtime.latestPHPVersions = newValue }
    }

    var latestNodeVersions: [String: String] {
        get { runtime.latestNodeVersions }
        set { runtime.latestNodeVersions = newValue }
    }

    var serviceStates: [UUID: ServiceRuntimeState] { services.states }

    var serviceOperation: UUID? { services.operation }

    var outdatedServiceDefinitionIDs: Set<String> { services.outdatedDefinitionIDs }

    var selectedPage: SidebarPage {
        get { navigation.selectedPage }
        set { navigation.selectedPage = newValue }
    }

    var selectedSiteID: SiteProject.ID? {
        get { navigation.selectedSiteID }
        set { navigation.selectedSiteID = newValue }
    }

    var selectedLogSiteID: SiteProject.ID? {
        get { navigation.selectedLogSiteID }
        set { navigation.selectedLogSiteID = newValue }
    }

    func showApplicationLogs() {
        navigation.showApplicationLogs()
    }

    func showLogs(for site: SiteProject) {
        navigation.showLogs(for: site)
    }

    deinit {
        applicationTasks.cancelAllImmediately()
        environment.stopImmediately()
        mail.stopImmediately()
        dumpsCoordinator.stopImmediately()
        security.stopDNSServer()
        services.stopAllImmediately()
    }

    @discardableResult
    private func startTrackedTask(
        name: String = #function,
        priority: TaskPriority? = nil,
        operation: @escaping @MainActor @Sendable (AppModel) async -> Void
    ) -> Bool {
        guard !didShutdown else { return false }
        return applicationTasks.start(name: name, priority: priority) { [weak self] in
            guard let self, !Task.isCancelled, !self.didShutdown else { return }
            await operation(self)
        }
    }

    private func reportOperationFailure(_ error: Error) {
        guard !didShutdown, !Task<Never, Never>.isCancelled else { return }
        lastError = error.localizedDescription
    }

    func refresh() {
        startTrackedTask { model in await model.refreshState() }
    }

    private func refreshState() async {
        isRefreshing = true
        domainResolverRefreshRevision &+= 1
        certificateTrustRefreshRevision &+= 1
        let domainRevision = domainResolverRefreshRevision
        let certificateRevision = certificateTrustRefreshRevision
        let rootURL = configurationStore.rootURL
        let parkPaths = configuration.parkPaths
        let selectedPHP = configuration.selectedPHP
        let tld = configuration.tld
        guard
            let snapshot = try? await Self.performBlockingOperation({
                let resolver = DomainResolverManager(rootURL: rootURL)
                let resolverState = resolver.state(tld: tld)
                let snapshot = RefreshSnapshot(
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
                try Task.checkCancellation()
                return snapshot
            })
        else {
            isRefreshing = false
            return
        }
        guard !Task.isCancelled else {
            isRefreshing = false
            return
        }
        sitesCoordinator.replaceSites(snapshot.sites)
        selectedSiteID = sitesCoordinator.validSelection(selectedSiteID)
        phpVersions = snapshot.phpVersions
        nodeVersions = snapshot.nodeVersions
        if domainRevision == domainResolverRefreshRevision {
            domainResolverState = snapshot.domainResolverState
            networkHelperNeedsUpdate = snapshot.networkHelperNeedsUpdate
            isDNSServerRunning = snapshot.isNetworkHelperRunning
        }
        if certificateRevision == certificateTrustRefreshRevision {
            certificateTrustState = snapshot.certificateTrustState
        }
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
        startTrackedTask { model in await model.runInitialSetup() }
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
        applicationSettings.checkForUpdates(
            channel: configuration.updateChannel,
            userInitiated: userInitiated
        )
    }

    func setLaunchAtLogin(_ enabled: Bool) {
        do {
            let status = try applicationSettings.setLaunchAtLogin(enabled)
            configuration.launchAtLogin = status.isEnabled
            persist()
        } catch {
            refreshLaunchAtLogin()
            lastError = error.localizedDescription
        }
    }

    func refreshLaunchAtLogin() {
        let status = applicationSettings.refreshLaunchAtLogin()
        configuration.launchAtLogin = status.isEnabled
    }

    func openLoginItemsSettings() {
        applicationSettings.openLoginItemsSettings()
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
        startTrackedTask { model in
            await model.refreshState()
            if shouldRestart, model.environmentStatus == .running {
                await model.restartEnvironmentNow()
            }
        }
    }

    func installDomainResolver() {
        guard privilegedOperation == nil, !isRepairingDomainResolverAutomatically else { return }
        startTrackedTask { model in
            do {
                try await model.prepareDomainResolver()
            } catch {
                model.reportOperationFailure(error)
            }
        }
    }

    func refreshDomainResolver() {
        startTrackedTask { model in await model.refreshDomainResolverNow() }
    }

    private func refreshDomainResolverNow() async {
        domainResolverRefreshRevision &+= 1
        let revision = domainResolverRefreshRevision
        let resolver = security.resolverManager
        let tld = configuration.tld
        guard
            let snapshot = try? await Self.performBlockingOperation(
                priority: .utility,
                {
                    let state = resolver.state(tld: tld)
                    let snapshot = (
                        state,
                        state == .managed && !resolver.isNetworkHelperCurrent(),
                        state == .managed && resolver.isNetworkHelperRunning()
                    )
                    try Task.checkCancellation()
                    return snapshot
                })
        else { return }
        guard !Task.isCancelled, !didShutdown,
            revision == domainResolverRefreshRevision
        else { return }
        domainResolverState = snapshot.0
        networkHelperNeedsUpdate = snapshot.1
        security.stopDNSServer()
        isDNSServerRunning = snapshot.2
    }

    func installCertificateAuthority() {
        guard privilegedOperation == nil else { return }
        lastError = nil
        startTrackedTask { model in
            do {
                try await model.prepareCertificateAuthority()
                if model.environmentStatus == .running, model.environmentHTTPSPort == nil {
                    await model.restartEnvironmentNow()
                }
            } catch {
                model.reportOperationFailure(error)
            }
        }
    }

    func refreshCertificateTrust() {
        startTrackedTask { model in await model.refreshCertificateTrustNow() }
    }

    private func refreshCertificateTrustNow() async {
        certificateTrustRefreshRevision &+= 1
        let revision = certificateTrustRefreshRevision
        let rootURL = configurationStore.rootURL
        guard
            let state = try? await Self.performBlockingOperation(
                priority: .utility,
                {
                    let state = LocalCertificateManager(rootURL: rootURL).trustState()
                    try Task.checkCancellation()
                    return state
                })
        else { return }
        guard !Task.isCancelled, !didShutdown,
            revision == certificateTrustRefreshRevision
        else { return }
        certificateTrustState = state
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
        let operation = "php-\(cycle)"
        guard runtime.beginOperation(operation) else { return }
        let started = startTrackedTask { model in
            defer { model.runtime.endOperation(operation) }
            do {
                try await model.runtime.activatePHP(cycle: cycle)
                guard !Task.isCancelled, !model.didShutdown else { return }
                model.configuration.selectedPHP = cycle
                model.persist()
                let rootURL = model.configurationStore.rootURL
                model.phpVersions = try await Self.performBlockingOperation {
                    let versions = RuntimeInspector(managedRoot: rootURL)
                        .phpVersions(activeCycle: cycle)
                    try Task.checkCancellation()
                    return versions
                }
                await model.runtime.refreshTooling(
                    cycle: cycle,
                    php: model.managedPHPExecutable(cycle: cycle)
                )
                model.restartEnvironmentForRuntimeSettingsIfNeeded()
            } catch {
                model.reportOperationFailure(error)
            }
        }
        if !started { runtime.endOperation(operation) }
    }

    func installPHP(_ cycle: String) {
        let operation = "php-\(cycle)"
        guard runtime.beginOperation(operation) else { return }
        let started = startTrackedTask { model in
            defer { model.runtime.endOperation(operation) }
            do {
                _ = try await model.runtime.installPHP(cycle: cycle)
                guard !Task.isCancelled, !model.didShutdown else { return }
                model.configuration.selectedPHP = cycle
                model.persist()
                await model.runtime.refreshTooling(
                    cycle: cycle,
                    php: model.managedPHPExecutable(cycle: cycle)
                )
                await model.refreshState()
                await model.restartEnvironmentIfNeededNow()
            } catch {
                model.reportOperationFailure(error)
            }
        }
        if !started { runtime.endOperation(operation) }
    }

    func installNode(_ cycle: String) {
        let operation = "node-\(cycle)"
        guard runtime.beginOperation(operation) else { return }
        let started = startTrackedTask { model in
            defer { model.runtime.endOperation(operation) }
            do {
                _ = try await model.runtime.installNode(cycle: cycle)
                guard !Task.isCancelled, !model.didShutdown else { return }
                model.refresh()
            } catch {
                model.reportOperationFailure(error)
            }
        }
        if !started { runtime.endOperation(operation) }
    }

    func refreshLaravelInstaller() {
        let cycle = configuration.selectedPHP
        startTrackedTask { model in
            await model.runtime.refreshLaravelInstaller(cycle: cycle)
        }
    }

    func refreshComposer() {
        let cycle = configuration.selectedPHP
        startTrackedTask { model in
            await model.runtime.refreshComposer(cycle: cycle)
        }
    }

    func refreshNodeUpdates() {
        let cycles = nodeVersions.map(\.cycle)
        startTrackedTask { model in
            await model.runtime.refreshNodeUpdates(cycles: cycles)
        }
    }

    func refreshPHPUpdates() {
        let cycles =
            phpVersions
            .filter { $0.isInstalled && PHPRuntimeSupport.isInstallable($0.cycle) }
            .map(\.cycle)
        startTrackedTask { model in
            await model.runtime.refreshPHPUpdates(cycles: cycles)
        }
    }

    var isLaravelInstallerUpdateAvailable: Bool {
        runtime.isLaravelInstallerUpdateAvailable
    }

    var isComposerUpdateAvailable: Bool {
        runtime.isComposerUpdateAvailable
    }

    func isNodeUpdateAvailable(_ runtime: RuntimeVersion) -> Bool {
        self.runtime.isNodeUpdateAvailable(runtime)
    }

    func isPHPUpdateAvailable(_ runtime: RuntimeVersion) -> Bool {
        self.runtime.isPHPUpdateAvailable(runtime)
    }

    func updateLaravelInstaller() {
        let cycle = configuration.selectedPHP
        let operation = "laravel-installer"
        guard runtime.beginOperation(operation) else { return }
        let started = startTrackedTask { model in
            defer { model.runtime.endOperation(operation) }
            do {
                _ = try await model.runtime.updateLaravelInstaller(cycle: cycle)
            } catch {
                model.reportOperationFailure(error)
            }
        }
        if !started { runtime.endOperation(operation) }
    }

    func updateComposer() {
        let cycle = configuration.selectedPHP
        let operation = "composer"
        guard runtime.beginOperation(operation) else { return }
        let started = startTrackedTask { model in
            defer { model.runtime.endOperation(operation) }
            do {
                _ = try await model.runtime.updateComposer(cycle: cycle)
            } catch {
                model.reportOperationFailure(error)
            }
        }
        if !started { runtime.endOperation(operation) }
    }

    func createProject(
        _ request: NewProjectRequest,
        progress: @escaping @MainActor @Sendable (ProjectCreationStage) -> Void = { _ in }
    ) async throws -> URL {
        progress(.validatingRequest)
        try ProjectCreator.validate(request)
        progress(.preparingLaravelInstaller)
        try await runtime.prepareLaravelInstallerForProjectCreation(
            cycle: configuration.selectedPHP
        )
        try Task.checkCancellation()
        return try await ProjectCreator(rootURL: configurationStore.rootURL).create(
            request,
            progress: progress
        )
    }

    func activateNode(_ cycle: String) {
        let operation = "node-\(cycle)"
        guard runtime.beginOperation(operation) else { return }
        let started = startTrackedTask { model in
            defer { model.runtime.endOperation(operation) }
            do {
                try await model.runtime.activateNode(cycle: cycle)
                model.refresh()
            } catch {
                model.reportOperationFailure(error)
            }
        }
        if !started { runtime.endOperation(operation) }
    }

    func removeNode(_ cycle: String) {
        let operation = "node-\(cycle)"
        guard runtime.beginOperation(operation) else { return }
        let started = startTrackedTask { model in
            defer { model.runtime.endOperation(operation) }
            do {
                try await model.runtime.removeNode(cycle: cycle)
                model.refresh()
            } catch {
                model.reportOperationFailure(error)
            }
        }
        if !started { runtime.endOperation(operation) }
    }

    func refreshXdebugInstallation() {
        let cycle = configuration.selectedPHP
        startTrackedTask { model in
            await model.runtime.refreshXdebug(
                cycle: cycle,
                php: model.managedPHPExecutable(cycle: cycle)
            )
        }
    }

    func installXdebug() {
        let cycle = configuration.selectedPHP
        let operation = "Installing Xdebug"
        guard runtime.beginDebuggerOperation(operation) else { return }
        let started = startTrackedTask { model in
            defer { model.runtime.endDebuggerOperation(operation) }
            do {
                _ = try await model.runtime.installXdebug(cycle: cycle)
                model.restartEnvironmentForRuntimeSettingsIfNeeded()
            } catch {
                model.reportOperationFailure(error)
            }
        }
        if !started { runtime.endDebuggerOperation(operation) }
    }

    func persistDebuggerSettings() {
        if !runtime.persistDebuggerSettings() {
            lastError = "Install Xdebug for PHP \(configuration.selectedPHP) before enabling the debugger."
            return
        }
        restartEnvironmentForRuntimeSettingsIfNeeded()
    }

    func persistPHPRequestSettings() {
        runtime.persistPHPRequestSettings()
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
        guard let siteURL = siteURL(for: site),
            var components = URLComponents(url: siteURL, resolvingAgainstBaseURL: false)
        else {
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
            startTrackedTask { model in
                await model.prepareEnvironmentAndOpenSite(
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
        try await AsyncProcessLifecycle.runDetached(
            priority: priority,
            operation: operation
        )
    }

    func sitePreviewURL(for site: SiteProject) -> URL? {
        if let port = siteRuntimePorts[site.id] {
            return Self.localSiteURL(scheme: "http", host: "127.0.0.1", port: port)
        }
        return siteURL(for: site)
    }

    func siteURL(for site: SiteProject) -> URL? {
        let host = site.domain(tld: configuration.tld)
        if certificateTrustState == .trusted, let httpsPort = environmentHTTPSPort {
            let usesStandardPort =
                httpsPort == 443
                || domainResolverState == .managed && isDNSServerRunning
            return Self.localSiteURL(
                scheme: "https",
                host: host,
                port: usesStandardPort ? nil : httpsPort
            )
        }
        if let proxyPort = environmentProxyPort {
            let usesStandardPort =
                proxyPort == 80
                || domainResolverState == .managed && isDNSServerRunning
            return Self.localSiteURL(
                scheme: "http",
                host: host,
                port: usesStandardPort ? nil : proxyPort
            )
        }
        if let port = siteRuntimePorts[site.id] {
            return Self.localSiteURL(scheme: "http", host: "127.0.0.1", port: port)
        }
        return Self.localSiteURL(scheme: "http", host: host)
    }

    func siteDisplayAddress(for site: SiteProject) -> String {
        Self.siteDisplayAddress(
            domain: site.domain(tld: configuration.tld),
            navigationURL: siteURL(for: site)
        )
    }

    nonisolated static func siteDisplayAddress(domain: String, navigationURL: URL?) -> String {
        let scheme = navigationURL?.scheme == "https" ? "https" : "http"
        return "\(scheme)://\(domain)"
    }

    nonisolated static func localSiteURL(scheme: String, host: String, port: Int? = nil) -> URL? {
        guard scheme == "http" || scheme == "https",
            port.map({ (1...65_535).contains($0) }) ?? true
        else {
            return nil
        }
        var components = URLComponents()
        components.scheme = scheme
        components.host = host
        components.port = port
        return components.url
    }

    var isHTTPSActive: Bool {
        environment.isHTTPSActive
    }

    var automaticHTTPSEnabled: Bool {
        UserDefaults.standard.bool(forKey: Self.automaticHTTPSKeychainAccessKey)
    }

    var httpsStatusTitle: String {
        Self.httpsStatusTitle(
            certificateTrustState: certificateTrustState,
            environmentStatus: environmentStatus,
            hasHTTPSPort: environmentHTTPSPort != nil,
            automaticHTTPSEnabled: automaticHTTPSEnabled,
            needsUserApproval: environment.httpsStartupNeedsApproval
        )
    }

    var shouldOfferHTTPSAction: Bool {
        certificateTrustState != .trusted
            || !automaticHTTPSEnabled
            || environmentStatus == .running && !isHTTPSActive
    }

    var httpsActionTitle: String {
        if certificateTrustState != .trusted { return String(localized: "Trust") }
        if automaticHTTPSEnabled,
            !environment.httpsStartupNeedsApproval,
            environmentStatus == .running,
            !isHTTPSActive
        {
            return String(localized: "Retry")
        }
        return String(localized: "Enable")
    }

    nonisolated static func httpsStatusTitle(
        certificateTrustState: CertificateTrustState,
        environmentStatus: EnvironmentStatus,
        hasHTTPSPort: Bool,
        automaticHTTPSEnabled: Bool,
        needsUserApproval: Bool = false
    ) -> String {
        guard certificateTrustState == .trusted else {
            return certificateTrustState.title
        }
        if environmentStatus == .running {
            if hasHTTPSPort { return String(localized: "Active") }
            if needsUserApproval { return String(localized: "Needs approval") }
            return automaticHTTPSEnabled
                ? String(localized: "Unavailable")
                : String(localized: "HTTP only")
        }
        return automaticHTTPSEnabled
            ? String(localized: "Enabled")
            : String(localized: "Disabled")
    }

    nonisolated static func shouldAttemptAutomaticHTTPS(
        certificateTrustState: CertificateTrustState,
        automaticHTTPSEnabled: Bool
    ) -> Bool {
        certificateTrustState == .trusted && automaticHTTPSEnabled
    }

    nonisolated static func shouldRecoverEnvironment(
        hadManagedState: Bool,
        previousStatus: EnvironmentStatus,
        startAutomatically: Bool,
        hasSites: Bool
    ) -> Bool {
        startAutomatically
            && hasSites
            && (hadManagedState || previousStatus == .running)
    }

    nonisolated static func shouldCommitEnvironmentInspection(
        expectedStatus: EnvironmentStatus,
        currentStatus: EnvironmentStatus,
        didShutdown: Bool,
        isCancelled: Bool
    ) -> Bool {
        !didShutdown && !isCancelled && currentStatus == expectedStatus
    }

    nonisolated static func domainResolverIsReady(
        state: DomainResolverState,
        helperRunning: Bool,
        helperNeedsUpdate: Bool
    ) -> Bool {
        state == .managed && helperRunning && !helperNeedsUpdate
    }

    nonisolated static func shouldRepairDomainResolverAutomatically(
        state: DomainResolverState,
        helperRunning: Bool,
        helperNeedsUpdate: Bool
    ) -> Bool {
        state == .managed && (!helperRunning || helperNeedsUpdate)
    }

    func openTerminal(for site: SiteProject) {
        do {
            try siteTools.openTerminal(for: site)
        } catch {
            lastError = error.localizedDescription
        }
    }

    func openTinker(for site: SiteProject) {
        do {
            try siteTools.openTinker(for: site, defaultPHP: configuration.selectedPHP)
        } catch {
            lastError = error.localizedDescription
        }
    }

    func artisanInvocation(
        for site: SiteProject,
        presetID: String,
        customCommand: String
    ) throws -> ArtisanInvocation {
        try siteTools.artisanInvocation(
            for: site,
            defaultPHP: configuration.selectedPHP,
            presetID: presetID,
            customCommand: customCommand
        )
    }

    func npmInvocation(for site: SiteProject, scriptName: String) throws -> NPMScriptInvocation {
        try siteTools.npmInvocation(for: site, scriptName: scriptName)
    }

    func openIDE(for site: SiteProject) {
        let bundleIdentifiers: [String: String] = [
            "VSCode": "com.microsoft.VSCode",
            "PhpStorm": "com.jetbrains.PhpStorm",
            "Sublime Text": "com.sublimetext.4"
        ]
        if let identifier = bundleIdentifiers[configuration.ide],
            let appURL = NSWorkspace.shared.urlForApplication(withBundleIdentifier: identifier)
        {
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
        let linkedSiteID = try sitesCoordinator.linkExistingSite(at: sourceURL)
        selectedSiteID = linkedSiteID
        selectedPage = .sites
        let shouldRestart = environmentStatus == .running
        startTrackedTask { model in
            await model.refreshState()
            model.selectedSiteID = linkedSiteID
            if shouldRestart, model.environmentStatus == .running {
                await model.restartEnvironmentNow()
            }
        }
    }

    func unlinkSite(_ site: SiteProject) {
        do {
            try sitesCoordinator.unlink(site)
            if selectedSiteID == site.id { selectedSiteID = nil }
            let shouldRestart = environmentStatus == .running
            startTrackedTask { model in
                await model.refreshState()
                if shouldRestart, model.environmentStatus == .running {
                    await model.restartEnvironmentNow()
                }
            }
        } catch {
            lastError = error.localizedDescription
        }
    }

    func moveSiteToTrash(_ site: SiteProject) {
        let parkPaths = configuration.parkPaths
        let shouldRestart = environmentStatus == .running
        startTrackedTask { model in
            do {
                try await Self.performBlockingOperation {
                    try SiteRemovalManager.moveToTrash(site, parkPaths: parkPaths)
                }
                guard !Task.isCancelled, !model.didShutdown else { return }
                if model.selectedSiteID == site.id { model.selectedSiteID = nil }
                await model.refreshState()
                guard !Task.isCancelled, !model.didShutdown, shouldRestart else { return }
                if model.sites.isEmpty {
                    await model.environment.stopEngine()
                    model.clearEnvironmentEndpoints()
                    model.environmentStatus = .stopped
                } else if model.environmentStatus == .running {
                    await model.restartEnvironmentNow()
                }
            } catch {
                model.reportOperationFailure(error)
            }
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
        startTrackedTask { model in
            await model.refreshState()
            model.selectedSiteID = url.path
            if shouldRestart, model.environmentStatus == .running {
                await model.restartEnvironmentNow()
            }
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
            lastError =
                "Port \(port) is already assigned to another HerdMe service."
                + (suggestion.map { " Use port \($0) instead." } ?? "")
            return false
        }
        guard LocalEnvironmentEngine.canBind(port: port) else {
            let suggestion = suggestedServicePort(startingAt: min(port + 1, 65_535))
            lastError =
                "Port \(port) is already used by another application."
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
            _ = try services.credentials(for: instance.id)
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
        guard services.beginOperation(for: instance.id) else { return }
        let started = startTrackedTask { model in
            defer { model.services.endOperation(for: instance.id) }
            await model.services.stop(instance)
            guard !Task.isCancelled, !model.didShutdown else { return }
            model.configuration.serviceInstances.removeAll(where: { $0.id == instance.id })
            model.services.removeState(for: instance.id)
            UserDefaults.standard.removeObject(
                forKey: Self.automaticServiceKeychainAccessKey(for: instance.id)
            )
            model.persist()
            do {
                try model.services.deleteCredentials(for: instance.id)
            } catch {
                model.reportOperationFailure(error)
            }
        }
        if !started { services.endOperation(for: instance.id) }
    }

    func serviceState(for instance: ServiceInstance) -> ServiceRuntimeState {
        services.state(for: instance)
    }

    func refreshServiceStates() {
        services.refreshStates(for: configuration.serviceInstances)
    }

    func refreshServiceUpdates() {
        startTrackedTask { model in
            await model.services.refreshUpdates()
        }
    }

    func isServiceUpdateAvailable(_ instance: ServiceInstance) -> Bool {
        services.isUpdateAvailable(for: instance)
    }

    func installService(_ instance: ServiceInstance) {
        guard services.beginOperation(for: instance.id) else { return }
        let started = startTrackedTask { model in
            defer { model.services.endOperation(for: instance.id) }
            do {
                let version = try await model.services.install(
                    definitionID: instance.definitionID
                )
                guard !Task.isCancelled, !model.didShutdown else { return }
                if let index = model.configuration.serviceInstances.firstIndex(where: {
                    $0.id == instance.id
                }) {
                    model.configuration.serviceInstances[index].version = version
                    model.persist()
                }
                model.refreshServiceStates()
            } catch {
                model.reportOperationFailure(error)
            }
        }
        if !started { services.endOperation(for: instance.id) }
    }

    func startService(_ instance: ServiceInstance) {
        guard services.beginOperation(for: instance.id) else { return }
        let started = startTrackedTask { model in
            defer { model.services.endOperation(for: instance.id) }
            do {
                try await model.services.start(instance)
                guard !Task.isCancelled, !model.didShutdown else { return }
                model.approveAutomaticServiceKeychainAccess(for: instance)
                model.setServiceRunning(instance.id, running: true)
                model.refreshServiceStates()
            } catch {
                model.reportOperationFailure(error)
                if !model.didShutdown {
                    model.setServiceRunning(instance.id, running: false)
                    model.refreshServiceStates()
                }
            }
        }
        if !started { services.endOperation(for: instance.id) }
    }

    func stopService(_ instance: ServiceInstance) {
        guard services.beginOperation(for: instance.id) else { return }
        let started = startTrackedTask { model in
            defer { model.services.endOperation(for: instance.id) }
            await model.services.stop(instance)
            guard !Task.isCancelled, !model.didShutdown else { return }
            model.setServiceRunning(instance.id, running: false)
            model.refreshServiceStates()
        }
        if !started { services.endOperation(for: instance.id) }
    }

    func setServiceAutomaticStart(_ instance: ServiceInstance, enabled: Bool) {
        guard let index = configuration.serviceInstances.firstIndex(where: { $0.id == instance.id }) else { return }
        configuration.serviceInstances[index].startAutomatically = enabled
        persist()
    }

    func openServiceDataDirectory(_ instance: ServiceInstance) {
        let url = services.dataDirectory(for: instance)
        do {
            try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
            NSWorkspace.shared.open(url)
        } catch {
            lastError = error.localizedDescription
        }
    }

    func addServiceEnvironment(_ instance: ServiceInstance, to site: SiteProject) throws
        -> ServiceEnvironmentUpdate
    {
        let credentials = try services.credentials(for: instance.id)
        let update = try ServiceEnvironmentFile.update(
            projectURL: site.path,
            instance: instance,
            credentials: credentials
        )
        try? logStore.append("Updated \(update.environmentURL.path) for \(instance.name).")
        return update
    }

    func openServiceConsole(_ instance: ServiceInstance) {
        guard let url = services.consoleURL(for: instance) else {
            lastError = "Start this storage service before opening its console."
            return
        }
        NSWorkspace.shared.open(url)
    }

    func canOpenServiceConsole(_ instance: ServiceInstance) -> Bool {
        serviceState(for: instance) == .running
            && services.consoleURL(for: instance) != nil
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
            guard
                let applicationURL = NSWorkspace.shared.urlForApplication(
                    withBundleIdentifier: TablePlusConnection.bundleIdentifier
                )
            else {
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
        let credentials =
            DatabaseServiceAuthenticator.protectedDefinitions.contains(instance.definitionID)
            ? try services.credentials(for: instance.id)
            : nil
        return TablePlusConnection.url(for: instance, credentials: credentials)
    }

    func toggleEnvironment() {
        guard environmentStatus != .starting, environmentStatus != .stopping else { return }
        if environmentStatus == .running {
            configuration.startAutomatically = false
            persist()
            environmentStatus = .stopping
            startTrackedTask { model in await model.stopLocalEnvironment() }
            return
        }

        environmentStatus = .starting
        startTrackedTask { model in
            do {
                model.startConfiguredServicesInBackground(reportErrors: true)
                try await model.startLocalEnvironment()
                guard !Task.isCancelled, !model.didShutdown else { return }
                model.configuration.startAutomatically = true
                model.persist()
            } catch {
                model.reportOperationFailure(error)
                if !model.didShutdown {
                    await model.detectEnvironmentStatusNow(afterFailedTransition: true)
                }
            }
        }
    }

    func shutdown() async {
        guard !didShutdown else { return }
        didShutdown = true

        applicationSettings.shutdown()
        applicationTasks.cancelAllImmediately()
        environment.stopImmediately()
        mail.shutdown()
        dumpsCoordinator.shutdown()
        security.stopDNSServer()
        services.beginShutdown()

        let pendingTaskNames = await applicationTasks.cancelAllAndWaitReporting()
        if !pendingTaskNames.isEmpty {
            try? logStore.append(
                "Background operations still active after five seconds during shutdown: "
                    + pendingTaskNames.joined(separator: ", ")
            )
        }
        await environment.stopEngine()
        sitesCoordinator.clearRuntimePorts()
        environmentProxyPort = nil
        environmentHTTPSPort = nil
        isDNSServerRunning = false
        await services.shutdown()
        for index in configuration.serviceInstances.indices {
            configuration.serviceInstances[index].isRunning = false
        }
        refreshServiceStates()
        environmentStatus = .stopped
    }

    func startMailServer(reportErrors: Bool = true) {
        mail.start(port: configuration.smtpPort, reportErrors: reportErrors) { [weak self] message, report in
            self?.reportFailure(message, reportErrors: report)
        }
    }

    func stopMailServer() {
        mail.stop()
    }

    func restartMailServer() {
        stopMailServer()
        persist()
        startMailServer()
    }

    func mailMessage(id: CapturedMail.ID) async throws -> CapturedMail {
        try await mail.message(id: id)
    }

    func deleteMail(_ message: CapturedMail) {
        startTrackedTask { model in
            do {
                try await model.mail.delete(id: message.id)
            } catch {
                model.reportOperationFailure(error)
            }
        }
    }

    func clearMail() {
        startTrackedTask { model in
            do {
                try await model.mail.clear()
            } catch {
                model.reportOperationFailure(error)
            }
        }
    }

    func startDumpServer(reportErrors: Bool = true) {
        dumpsCoordinator.start(
            port: configuration.dumpPort,
            reportErrors: reportErrors
        ) { [weak self] message, report in
            self?.reportFailure(message, reportErrors: report)
        }
    }

    func stopDumpServer() {
        dumpsCoordinator.stop()
    }

    func clearDumps() {
        startTrackedTask { model in
            do {
                try await model.dumpsCoordinator.clear()
            } catch {
                model.reportOperationFailure(error)
            }
        }
    }

    private func startManagedDNSServer(reportErrors: Bool = true) {
        startTrackedTask { model in
            await model.startManagedDNSServerNow(reportErrors: reportErrors)
        }
    }

    private func startManagedDNSServerNow(reportErrors: Bool = true) async {
        security.stopDNSServer()
        guard domainResolverState == .managed else {
            isDNSServerRunning = false
            return
        }
        let resolver = security.resolverManager
        guard
            let helperIsRunning = try? await Self.performBlockingOperation(
                priority: .utility,
                {
                    let isRunning = resolver.isNetworkHelperRunning()
                    try Task.checkCancellation()
                    return isRunning
                })
        else { return }
        guard !Task.isCancelled, !didShutdown else { return }
        isDNSServerRunning = helperIsRunning
        if !isDNSServerRunning {
            reportFailure(
                "The HerdMe local network helper is configured but is not running.",
                reportErrors: reportErrors
            )
        }
    }

    private func repairDomainResolverIfNeeded() async {
        guard privilegedOperation == nil, !isRepairingDomainResolverAutomatically else { return }
        isRepairingDomainResolverAutomatically = true
        defer { isRepairingDomainResolverAutomatically = false }
        if domainResolverState == .managed {
            await refreshDomainResolverNow()
        }
        guard
            Self.shouldRepairDomainResolverAutomatically(
                state: domainResolverState,
                helperRunning: isDNSServerRunning,
                helperNeedsUpdate: networkHelperNeedsUpdate
            )
        else {
            lastAutomaticDomainRepairFailure = nil
            return
        }

        privilegedOperation = "domains"
        defer { privilegedOperation = nil }
        let resolver = security.resolverManager
        let tld = configuration.tld
        do {
            _ = try await Self.performBlockingOperation(priority: .utility) {
                try resolver.install(
                    tld: tld,
                    openApprovalSettingsOnFailure: false
                )
            }
            await refreshDomainResolverNow()
            guard
                Self.domainResolverIsReady(
                    state: domainResolverState,
                    helperRunning: isDNSServerRunning,
                    helperNeedsUpdate: networkHelperNeedsUpdate
                )
            else {
                throw DomainResolverError.installationVerificationFailed
            }
            lastAutomaticDomainRepairFailure = nil
            try? logStore.append("Recovered the HerdMe local network helper.")
        } catch {
            let message = "Automatic local domain repair failed: " + error.localizedDescription
            if lastAutomaticDomainRepairFailure != message {
                lastAutomaticDomainRepairFailure = message
                try? logStore.append(message)
            }
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
        startTrackedTask { model in await model.detectEnvironmentStatusNow() }
    }

    private func detectEnvironmentStatusNow(
        afterFailedTransition: Bool = false,
        refreshCertificateTrust: Bool = true
    ) async {
        guard
            afterFailedTransition
                || environmentStatus != .starting && environmentStatus != .stopping
        else {
            return
        }
        await environment.acquireInspection()
        let shouldInspect =
            !didShutdown
            && !Task.isCancelled
            && (afterFailedTransition
                || environmentStatus != .starting && environmentStatus != .stopping)
        if shouldInspect {
            await inspectEnvironmentStatusNow(
                refreshCertificateTrust: refreshCertificateTrust
            )
        }
        await environment.releaseInspection()
    }

    private func inspectEnvironmentStatusNow(refreshCertificateTrust: Bool) async {
        let previousStatus = environmentStatus
        let engineSnapshot = environment.engineSnapshot()
        if engineSnapshot.isRunning {
            apply(
                EnvironmentEndpoints(
                    sitePorts: engineSnapshot.sitePorts,
                    proxyPort: engineSnapshot.proxyPort,
                    httpsPort: engineSnapshot.httpsPort,
                    httpsStartupNeedsApproval: engineSnapshot.httpsStartupNeedsApproval
                ))
            environmentStatus = .running
            if refreshCertificateTrust {
                await refreshCertificateTrustNow()
            }
            return
        }

        let hadManagedState = engineSnapshot.hasManagedState
        let shouldRecover = Self.shouldRecoverEnvironment(
            hadManagedState: hadManagedState,
            previousStatus: previousStatus,
            startAutomatically: configuration.startAutomatically,
            hasSites: !sites.isEmpty
        )
        let inspectionStatus: EnvironmentStatus
        if shouldRecover {
            inspectionStatus = .starting
        } else if hadManagedState || previousStatus == .running {
            inspectionStatus = .stopping
        } else {
            inspectionStatus = previousStatus
        }
        environmentStatus = inspectionStatus
        clearEnvironmentEndpoints()
        if hadManagedState {
            await environment.stopEngine()
        }

        let hasPortConflict =
            (try? await Self.performBlockingOperation(priority: .utility) {
                do {
                    let result = try ProcessRunner.run(
                        URL(fileURLWithPath: "/usr/sbin/lsof"),
                        arguments: ["-nP", "-iTCP:80", "-sTCP:LISTEN"],
                        timeout: 10,
                        cancellationRequested: { Task.isCancelled }
                    )
                    return result.output.contains("Herd") || result.output.contains("nginx")
                } catch {
                    return false
                }
            }) ?? false
        guard
            Self.shouldCommitEnvironmentInspection(
                expectedStatus: inspectionStatus,
                currentStatus: environmentStatus,
                didShutdown: didShutdown,
                isCancelled: Task.isCancelled
            )
        else {
            return
        }

        if shouldRecover {
            do {
                try await startLocalEnvironment()
                try? logStore.append("Recovered an unhealthy local site environment.")
                return
            } catch {
                lastError =
                    "HerdMe could not recover the local site environment: "
                    + error.localizedDescription
            }
        }
        environmentStatus = hasPortConflict ? .conflict : .stopped
    }

    private func startEnvironmentHealthMonitor() {
        guard !isEnvironmentHealthMonitorRunning, !didShutdown else { return }
        isEnvironmentHealthMonitorRunning = true
        let started = applicationTasks.start(name: "environment-health-monitor") { [weak self] in
            defer { self?.isEnvironmentHealthMonitorRunning = false }
            while !Task.isCancelled {
                do {
                    try await Task.sleep(for: .seconds(5))
                } catch {
                    return
                }
                guard let model = self else { return }
                guard !model.didShutdown else { return }
                await model.repairDomainResolverIfNeeded()
                guard model.environmentStatus == .running else { continue }
                await model.detectEnvironmentStatusNow(refreshCertificateTrust: false)
            }
        }
        if !started { isEnvironmentHealthMonitorRunning = false }
    }

    private func updateSiteRuntime(_ cycle: String?, kind: SiteRuntimeKind, for site: SiteProject) {
        let shouldRestart = environmentStatus == .running
        do {
            try sitesCoordinator.setRuntime(cycle, kind: kind, for: site)
            let selectedID = site.id
            selectedSiteID = selectedID
            startTrackedTask { model in
                await model.refreshState()
                model.selectedSiteID = selectedID
                if shouldRestart, model.environmentStatus == .running {
                    await model.restartEnvironmentNow()
                }
            }
        } catch {
            lastError = error.localizedDescription
            detectEnvironmentStatus()
        }
    }

    private func startLocalEnvironment() async throws {
        let startSites = sites
        let defaultPHP = runtime.executable(named: "php")
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
        let engineSnapshot = try await environment.startEngine(
            sites: startSites,
            defaultPHP: defaultPHP,
            defaultPHPCycle: defaultPHPCycle,
            tld: tld,
            debuggerSettings: debugger,
            phpRequestSettings: requestSettings,
            enableHTTPS: enableHTTPS
        )
        try Task.checkCancellation()
        guard !didShutdown else { throw CancellationError() }
        apply(
            EnvironmentEndpoints(
                sitePorts: engineSnapshot.sitePorts,
                proxyPort: engineSnapshot.proxyPort,
                httpsPort: engineSnapshot.httpsPort,
                httpsStartupNeedsApproval: engineSnapshot.httpsStartupNeedsApproval
            ))
        if enableHTTPS, environmentHTTPSPort != nil, !automaticHTTPSEnabled {
            UserDefaults.standard.set(
                true,
                forKey: Self.automaticHTTPSKeychainAccessKey
            )
        }
        if let httpsStartupError = engineSnapshot.httpsStartupError {
            let message: String
            if engineSnapshot.httpsStartupNeedsApproval {
                UserDefaults.standard.set(
                    false,
                    forKey: Self.automaticHTTPSKeychainAccessKey
                )
                message = "Sites started over HTTP because HTTPS credentials need approval. Choose Enable next to the HTTPS certificate."
            } else if automaticHTTPSEnabled {
                message =
                    "Sites started over HTTP because HTTPS could not start. Open General settings and enable the HTTPS certificate again."
                lastError = message
            } else if certificateTrustState == .trusted {
                message = "Sites started over HTTP because saved HTTPS credentials need approval. Open General settings and choose Enable."
            } else {
                message = "Sites started over HTTP. HTTPS is waiting for explicit approval in General settings."
            }
            try? logStore.append(message + " " + httpsStartupError)
        }
        if let environmentProxyPort {
            let resolver = security.resolverManager
            let httpsPort = environmentHTTPSPort
            do {
                try await Self.performBlockingOperation(priority: .utility) {
                    try Task.checkCancellation()
                    try resolver.updateNetworkRouting(
                        httpPort: environmentProxyPort,
                        httpsPort: httpsPort,
                        tld: tld
                    )
                    try Task.checkCancellation()
                }
            } catch {
                await environment.stopEngine()
                clearEnvironmentEndpoints()
                throw error
            }
        }
        await refreshCertificateTrustNow()
        environmentStatus = .running
    }

    private func prepareAutomaticHTTPSIdentityIfNeeded() async {
        guard
            Self.shouldAttemptAutomaticHTTPS(
                certificateTrustState: certificateTrustState,
                automaticHTTPSEnabled: automaticHTTPSEnabled
            )
        else { return }

        let manager = security.certificateManager
        let tld = configuration.tld
        let domains = sites.map { $0.domain(tld: tld) }
        do {
            try await Self.performBlockingOperation {
                _ = try manager.prepareIdentity(
                    tld: tld,
                    domains: domains,
                    allowKeychainInteraction: false
                )
                return ()
            }
        } catch {
            // App updates can invalidate a legacy Keychain ACL. Stop retrying
            // silently until the user explicitly enables HTTPS for this build.
            UserDefaults.standard.set(
                false,
                forKey: Self.automaticHTTPSKeychainAccessKey
            )
            try? logStore.append(
                "Automatic HTTPS credential approval failed: " + error.localizedDescription
            )
        }
    }

    private func restartEnvironmentForRuntimeSettingsIfNeeded() {
        startTrackedTask { model in await model.restartEnvironmentIfNeededNow() }
    }

    private func restartEnvironmentIfNeededNow() async {
        guard environmentStatus == .running else { return }
        environmentStatus = .starting
        await restartEnvironmentNow()
    }

    private func restartEnvironmentNow() async {
        environmentStatus = .starting
        await environment.stopEngine()
        clearEnvironmentEndpoints()
        do {
            try await startLocalEnvironment()
        } catch {
            reportOperationFailure(error)
            if !didShutdown {
                await detectEnvironmentStatusNow(afterFailedTransition: true)
            }
        }
    }

    private func prepareEnvironmentAndOpenSite(_ site: SiteProject, restart: Bool) async {
        if restart {
            await environment.stopEngine()
            clearEnvironmentEndpoints()
        }
        do {
            startConfiguredServicesInBackground(reportErrors: true)
            try await startLocalEnvironment()
            guard !Task.isCancelled, !didShutdown else { return }
            configuration.startAutomatically = true
            persist()
            guard siteRuntimePorts[site.id] != nil else {
                lastError = "HerdMe started the local environment but could not route \(site.domain(tld: configuration.tld))."
                return
            }
            openSiteURL(for: site)
        } catch {
            if !Task.isCancelled, !didShutdown {
                lastError =
                    "HerdMe could not open \(site.domain(tld: configuration.tld)): "
                    + error.localizedDescription
                await detectEnvironmentStatusNow(afterFailedTransition: true)
            }
        }
    }

    private func openSiteURL(for site: SiteProject) {
        guard let url = siteURL(for: site) else {
            lastError = "HerdMe could not create a valid local URL for \(site.name)."
            return
        }
        guard NSWorkspace.shared.open(url) else {
            lastError = "HerdMe could not open the default browser."
            return
        }
    }

    private func managedPHPExecutable(cycle: String) -> URL {
        configurationStore.rootURL.appendingPathComponent("Runtimes/php/\(cycle)/bin/php")
    }

    private func stopLocalEnvironment() async {
        let serviceCoordinator = services
        await environment.stopEngine()
        await serviceCoordinator.stopAll()
        clearEnvironmentEndpoints()
        for index in configuration.serviceInstances.indices {
            configuration.serviceInstances[index].isRunning = false
        }
        refreshServiceStates()
        environmentStatus = .stopped
    }

    private func apply(_ endpoints: EnvironmentEndpoints) {
        sitesCoordinator.replaceRuntimePorts(endpoints.sitePorts)
        environment.apply(
            proxyPort: endpoints.proxyPort,
            httpsPort: endpoints.httpsPort,
            httpsStartupNeedsApproval: endpoints.httpsStartupNeedsApproval
        )
    }

    private func clearEnvironmentEndpoints() {
        sitesCoordinator.clearRuntimePorts()
        environmentProxyPort = nil
        environmentHTTPSPort = nil
    }

    private func setServiceRunning(_ id: UUID, running: Bool) {
        guard let index = configuration.serviceInstances.firstIndex(where: { $0.id == id }) else { return }
        configuration.serviceInstances[index].isRunning = running
        persist()
    }

    private func startConfiguredServicesInBackground(reportErrors: Bool) {
        // A protected service may need Keychain approval; it must never gate sites.
        startTrackedTask { model in
            await model.startConfiguredServicesNow(reportErrors: reportErrors)
        }
    }

    private func startConfiguredServicesNow(reportErrors: Bool) async {
        guard services.operation == nil else { return }
        let instances = configuration.serviceInstances.filter {
            $0.startAutomatically && services.state(for: $0) == .stopped
                && automaticServiceStartupIsAuthorized(for: $0)
        }
        guard !instances.isEmpty else { return }

        for instance in instances {
            guard services.beginOperation(for: instance.id) else { break }
            do {
                try await services.start(
                    instance,
                    allowCredentialInteraction: false
                )
                setServiceRunning(instance.id, running: true)
            } catch {
                if reportErrors { lastError = error.localizedDescription }
                setServiceRunning(instance.id, running: false)
            }
            refreshServiceStates()
            services.endOperation(for: instance.id)
            if Task.isCancelled { break }
        }
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

    private func runInitialSetup() async {
        do {
            try await prepareDomainResolver()

            onboardingStage = .certificate
            try await prepareCertificateAuthority()

            let phpCycle = AppConfiguration.default.selectedPHP
            onboardingStage = .php
            let rootURL = configurationStore.rootURL
            let phpRuntime = try await Self.performBlockingOperation {
                let versions = RuntimeInspector(managedRoot: rootURL)
                    .phpVersions(activeCycle: phpCycle)
                try Task.checkCancellation()
                return versions
            }.first { $0.cycle == phpCycle }
            if phpRuntime?.isInstalled == true {
                try await runtime.activatePHP(cycle: phpCycle)
            } else {
                _ = try await runtime.installPHP(cycle: phpCycle)
            }
            try PHPRuntimeValidator().validate(executable: managedPHPExecutable(cycle: phpCycle))
            configuration.selectedPHP = phpCycle
            try configurationStore.save(configuration)

            onboardingStage = .composer
            try await runtime.prepareLaravelInstallerForProjectCreation(cycle: phpCycle)
            await runtime.refreshTooling(
                cycle: phpCycle,
                php: managedPHPExecutable(cycle: phpCycle)
            )

            let nodeCycle = RuntimeCatalog.defaultNodeMajor
            onboardingStage = .node
            let nodeRuntime = try await Self.performBlockingOperation {
                let versions = RuntimeInspector(managedRoot: rootURL).nodeVersions()
                try Task.checkCancellation()
                return versions
            }.first { $0.cycle == nodeCycle }
            if nodeRuntime?.isInstalled == true {
                try await runtime.activateNode(cycle: nodeCycle)
            } else {
                _ = try await runtime.installNode(cycle: nodeCycle)
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
            onboardingError = ErrorPresentation(error.localizedDescription)
        }
        isRunningInitialSetup = false
    }

    private func prepareCertificateAuthority() async throws {
        privilegedOperation = "certificate"
        defer { privilegedOperation = nil }
        let manager = security.certificateManager
        let tld = configuration.tld
        let domains = sites.map { $0.domain(tld: tld) }
        _ = try await Self.performBlockingOperation {
            try manager.installAuthority(tld: tld, domains: domains)
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
        let resolver = security.resolverManager
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
