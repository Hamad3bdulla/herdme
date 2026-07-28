import Combine
import Foundation

@MainActor
final class RuntimeCoordinator: ObservableObject {
    @Published var phpVersions: [RuntimeVersion] = []
    @Published var nodeVersions: [RuntimeVersion] = []
    @Published private(set) var operation: String?
    @Published var debuggerSettings = DebuggerSettings.load()
    @Published var phpRequestSettings = PHPRequestSettings.load()
    @Published var xdebugInstallation: XdebugInstallation?
    @Published private(set) var debuggerOperation: String?
    @Published var composerVersion: String?
    @Published var latestComposerVersion: String?
    @Published var laravelInstallerVersion: String?
    @Published var latestLaravelInstallerVersion: String?
    @Published var latestPHPVersions: [String: String] = [:]
    @Published var latestNodeVersions: [String: String] = [:]

    private let installer: any RuntimeInstalling
    private let xdebugManager: XdebugManager
    private let executableLocator: ExecutableLocator

    init(rootURL: URL, installer: (any RuntimeInstalling)? = nil) {
        self.installer = installer ?? RuntimeInstaller(rootURL: rootURL)
        xdebugManager = XdebugManager(rootURL: rootURL)
        executableLocator = ExecutableLocator(managedRoot: rootURL)
    }

    func beginOperation(_ identifier: String) -> Bool {
        guard operation == nil else { return false }
        operation = identifier
        return true
    }

    func endOperation(_ identifier: String) {
        if operation == identifier { operation = nil }
    }

    func beginDebuggerOperation(_ identifier: String) -> Bool {
        guard debuggerOperation == nil else { return false }
        debuggerOperation = identifier
        return true
    }

    func endDebuggerOperation(_ identifier: String) {
        if debuggerOperation == identifier { debuggerOperation = nil }
    }

    func executable(named name: String) -> URL? {
        executableLocator.find(name)
    }

    func activatePHP(cycle: String) async throws {
        try await installer.activatePHP(cycle: cycle)
    }

    func installPHP(cycle: String) async throws -> String {
        let version = try await installer.installPHP(cycle: cycle)
        try Task.checkCancellation()
        latestPHPVersions[cycle] = version
        return version
    }

    func installNode(cycle: String) async throws -> String {
        let version = try await installer.installNode(cycle: cycle)
        try Task.checkCancellation()
        latestNodeVersions[cycle] = version
        return version
    }

    func activateNode(cycle: String) async throws {
        try await installer.activateNode(cycle: cycle)
    }

    func removeNode(cycle: String) async throws {
        try await installer.removeNode(cycle: cycle)
    }

    func composerVersion(cycle: String) async -> String? {
        await installer.composerVersion(cycle: cycle)
    }

    func latestComposerVersion(cycle: String) async throws -> String {
        try await installer.latestComposerVersion(cycle: cycle)
    }

    func updateComposer(cycle: String) async throws -> String {
        let version = try await installer.updateComposer(cycle: cycle)
        try Task.checkCancellation()
        composerVersion = version
        latestComposerVersion = version
        return version
    }

    func laravelInstallerVersion(cycle: String) async -> String? {
        await installer.laravelInstallerVersion(cycle: cycle)
    }

    func latestLaravelInstallerVersion() async throws -> String {
        try await installer.latestLaravelInstallerVersion()
    }

    func updateLaravelInstaller(cycle: String) async throws -> String {
        let version = try await installer.updateLaravelInstaller(cycle: cycle)
        try Task.checkCancellation()
        laravelInstallerVersion = version
        latestLaravelInstallerVersion = version
        return version
    }

    func prepareLaravelInstallerForProjectCreation(cycle: String) async throws {
        try await installer.prepareLaravelInstallerForProjectCreation(cycle: cycle)
    }

    func latestPHPVersions(cycles: [String]) async throws -> [String: String] {
        try await installer.latestPHPVersions(cycles: cycles)
    }

    func latestNodeVersions(cycles: [String]) async throws -> [String: String] {
        try await installer.latestNodeVersions(cycles: cycles)
    }

    func installedXdebug(cycle: String, php: URL) async -> XdebugInstallation? {
        await xdebugManager.installed(cycle: cycle, php: php)
    }

    func installXdebug(cycle: String) async throws -> XdebugInstallation {
        let installation = try await xdebugManager.install(cycle: cycle)
        try Task.checkCancellation()
        xdebugInstallation = installation
        return installation
    }

    func refreshTooling(cycle: String, php: URL) async {
        async let xdebug = xdebugManager.installed(cycle: cycle, php: php)
        async let composer = installer.composerVersion(cycle: cycle)
        async let latestComposer: String? = try? await installer.latestComposerVersion(cycle: cycle)
        async let laravel = installer.laravelInstallerVersion(cycle: cycle)
        async let latestLaravel: String? = try? await installer.latestLaravelInstallerVersion()

        let values = await (xdebug, composer, latestComposer, laravel, latestLaravel)
        guard !Task.isCancelled else { return }
        xdebugInstallation = values.0
        composerVersion = values.1
        latestComposerVersion = values.2
        laravelInstallerVersion = values.3
        latestLaravelInstallerVersion = values.4
    }

    func refreshXdebug(cycle: String, php: URL) async {
        let installation = await xdebugManager.installed(cycle: cycle, php: php)
        guard !Task.isCancelled else { return }
        xdebugInstallation = installation
    }

    func refreshComposer(cycle: String) async {
        async let installed = installer.composerVersion(cycle: cycle)
        async let latest: String? = try? await installer.latestComposerVersion(cycle: cycle)
        let values = await (installed, latest)
        guard !Task.isCancelled else { return }
        composerVersion = values.0
        latestComposerVersion = values.1
    }

    func refreshLaravelInstaller(cycle: String) async {
        async let installed = installer.laravelInstallerVersion(cycle: cycle)
        async let latest: String? = try? await installer.latestLaravelInstallerVersion()
        let values = await (installed, latest)
        guard !Task.isCancelled else { return }
        laravelInstallerVersion = values.0
        latestLaravelInstallerVersion = values.1
    }

    func refreshAvailableUpdates(phpCycles: [String], nodeCycles: [String]) async {
        async let php: [String: String]? = try? await installer.latestPHPVersions(cycles: phpCycles)
        async let node: [String: String]? = try? await installer.latestNodeVersions(cycles: nodeCycles)
        let values = await (php, node)
        guard !Task.isCancelled else { return }
        latestPHPVersions = values.0 ?? [:]
        latestNodeVersions = values.1 ?? [:]
    }

    func refreshPHPUpdates(cycles: [String]) async {
        let versions = (try? await installer.latestPHPVersions(cycles: cycles)) ?? [:]
        guard !Task.isCancelled else { return }
        latestPHPVersions = versions
    }

    func refreshNodeUpdates(cycles: [String]) async {
        let versions = (try? await installer.latestNodeVersions(cycles: cycles)) ?? [:]
        guard !Task.isCancelled else { return }
        latestNodeVersions = versions
    }

    func persistDebuggerSettings() -> Bool {
        let normalized = debuggerSettings.normalized
        guard !normalized.enabled || xdebugInstallation != nil else {
            debuggerSettings.enabled = false
            debuggerSettings = debuggerSettings.normalized
            debuggerSettings.save()
            return false
        }
        debuggerSettings = normalized
        debuggerSettings.save()
        return true
    }

    func persistPHPRequestSettings(defaults: UserDefaults = .standard) {
        phpRequestSettings = phpRequestSettings.normalized
        defaults.set(
            phpRequestSettings.maxUploadMegabytes,
            forKey: PHPRequestSettings.uploadKey
        )
        defaults.set(
            phpRequestSettings.memoryLimitMegabytes,
            forKey: PHPRequestSettings.memoryKey
        )
    }

    var isLaravelInstallerUpdateAvailable: Bool {
        guard let installed = laravelInstallerVersion,
            let latest = latestLaravelInstallerVersion
        else { return false }
        return RuntimeInstaller.isNewerVersion(latest, than: installed)
    }

    var isComposerUpdateAvailable: Bool {
        guard let installed = composerVersion,
            let latest = latestComposerVersion
        else { return false }
        return RuntimeInstaller.isNewerVersion(latest, than: installed)
    }

    func isNodeUpdateAvailable(_ runtime: RuntimeVersion) -> Bool {
        guard let installed = runtime.installedVersion,
            let latest = latestNodeVersions[runtime.cycle]
        else { return false }
        return RuntimeInstaller.isNewerVersion(latest, than: installed)
    }

    func isPHPUpdateAvailable(_ runtime: RuntimeVersion) -> Bool {
        guard let installed = runtime.installedVersion,
            let latest = latestPHPVersions[runtime.cycle]
        else { return false }
        return RuntimeInstaller.isNewerVersion(latest, than: installed)
    }
}
