import Darwin
import Foundation

enum LocalEnvironmentError: LocalizedError {
    case phpMissing
    case noSites
    case noAvailablePort
    case unhealthy
    case processFailed(String)

    var errorDescription: String? {
        switch self {
        case .phpMissing:
            String(localized: "Install and activate a HerdMe-managed PHP version before starting sites.")
        case .noSites:
            String(localized: "No local sites were found to start.")
        case .noAvailablePort:
            String(localized: "HerdMe could not find an available local development port.")
        case .unhealthy:
            String(localized: "The local site environment did not become healthy in time.")
        case .processFailed(let site):
            String.localizedStringWithFormat(
                String(localized: "The local server for %@ could not be started."),
                site
            )
        }
    }
}

protocol LocalEnvironmentRunning: Sendable {
    var isRunning: Bool { get }
    var developmentSitesHealthy: Bool { get }
    var hasManagedState: Bool { get }
    var ports: [String: Int] { get }
    var proxyPort: Int? { get }
    var httpsProxyPort: Int? { get }
    var httpsStartupError: String? { get }
    var httpsStartupNeedsApproval: Bool { get }

    func start(
        sites: [SiteProject],
        defaultPHP: URL?,
        defaultPHPCycle: String?,
        tld: String,
        debuggerSettings: DebuggerSettings,
        phpRequestSettings: PHPRequestSettings,
        enableHTTPS: Bool
    ) async throws -> [String: Int]
    func stopAll() async
    func stopAllImmediately()
}

final class LocalEnvironmentEngine: LocalEnvironmentRunning, @unchecked Sendable {
    let rootURL: URL
    private let fileManager = FileManager.default
    private let phpValidator = PHPRuntimeValidator()
    private let xdebugManager: any XdebugManaging
    private let fpmManager: any ProcessRunning
    private let gatewayFactory: FastCGIListenerFactory
    private var gateways: [String: any FastCGIListening] = [:]
    private var developmentServers: [String: SiteDevelopmentServer] = [:]
    private var configuredSites: [SiteProject] = []
    private let httpProxy: any HTTPListening
    private let httpsProxy: any HTTPListening
    private let certificateManager: LocalCertificateManager
    private(set) var ports: [String: Int] = [:]
    private(set) var proxyPort: Int?
    private(set) var httpsProxyPort: Int?
    private(set) var httpsStartupError: String?
    private(set) var httpsStartupNeedsApproval = false

    init(
        rootURL: URL,
        certificateManager: LocalCertificateManager? = nil,
        xdebugManager: (any XdebugManaging)? = nil,
        fpmManager: (any ProcessRunning)? = nil,
        gatewayFactory: @escaping FastCGIListenerFactory = {
            LocalFastCGIGateway(documentRoot: $0, fpmPort: $1)
        },
        httpProxy: (any HTTPListening)? = nil,
        httpsProxy: (any HTTPListening)? = nil
    ) {
        self.rootURL = rootURL
        self.xdebugManager = xdebugManager ?? XdebugManager(rootURL: rootURL)
        self.fpmManager = fpmManager ?? PHPFPMManager(rootURL: rootURL)
        self.gatewayFactory = gatewayFactory
        self.httpProxy = httpProxy ?? LocalHTTPProxy()
        self.httpsProxy = httpsProxy ?? LocalHTTPProxy()
        self.certificateManager = certificateManager ?? LocalCertificateManager(rootURL: rootURL)
    }

    var isRunning: Bool {
        fpmManager.isHealthy
            && !gateways.isEmpty
            && gateways.values.allSatisfy(\.isHealthy)
            && httpProxy.isHealthy
            && (httpsProxyPort == nil || httpsProxy.isHealthy)
    }

    var developmentSitesHealthy: Bool {
        developmentServersAreHealthy(for: configuredSites)
    }

    var hasManagedState: Bool {
        fpmManager.isRunning
            || !gateways.isEmpty
            || !developmentServers.isEmpty
            || proxyPort != nil
            || httpsProxyPort != nil
    }

    func start(
        sites: [SiteProject],
        defaultPHP: URL?,
        defaultPHPCycle: String? = nil,
        tld: String,
        debuggerSettings: DebuggerSettings = .disabled,
        phpRequestSettings: PHPRequestSettings = .default,
        enableHTTPS: Bool = true
    ) async throws -> [String: Int] {
        guard !sites.isEmpty else { throw LocalEnvironmentError.noSites }
        guard let defaultPHP, fileManager.isExecutableFile(atPath: defaultPHP.path) else {
            throw LocalEnvironmentError.phpMissing
        }
        _ = Self.removeLegacyRouterArtifacts(rootURL: rootURL, fileManager: fileManager)
        if isRunning {
            configuredSites = sites
            if !developmentServersAreHealthy(for: sites) {
                try await startDevelopmentServers(for: sites)
                await updateProxyRoutes(for: sites, tld: tld)
            }
            return ports
        }
        if hasManagedState { await stopAll() }
        httpsStartupError = nil
        httpsStartupNeedsApproval = false
        configuredSites = sites

        let logsDirectory = rootURL.appendingPathComponent("Log/sites", isDirectory: true)
        try fileManager.createDirectory(at: logsDirectory, withIntermediateDirectories: true)

        do {
            var fpmPorts: [String: Int] = [:]
            var validatedPHP = Set<String>()
            for (index, site) in sites.enumerated() {
                let php = phpExecutable(for: site) ?? defaultPHP
                if validatedPHP.insert(php.path).inserted {
                    try phpValidator.validate(executable: php)
                    if debuggerSettings.enabled {
                        let cycle = site.phpVersion ?? defaultPHPCycle ?? "default"
                        try await prepareXdebugIfNeeded(cycle: cycle, php: php)
                    }
                }
                let fpm = Self.phpFPMExecutable(for: php)
                guard fileManager.isExecutableFile(atPath: fpm.path) else {
                    throw PHPFPMError.executableMissing
                }
                let fpmPort: Int
                if let existing = fpmPorts[fpm.path] {
                    fpmPort = existing
                } else {
                    let cycle = site.phpVersion ?? defaultPHPCycle ?? "default"
                    let phpOptions = XdebugManager.phpOptions(
                        rootURL: rootURL,
                        cycle: cycle,
                        debugger: debuggerSettings,
                        request: phpRequestSettings
                    )
                    fpmPort = try await fpmManager.start(
                        executable: fpm,
                        identifier: "runtime-\(fpmPorts.count + 1)",
                        preferredPort: 9_070 + fpmPorts.count * 20,
                        phpOptions: phpOptions
                    )
                    fpmPorts[fpm.path] = fpmPort
                }
                let documentRoot = Self.documentRoot(for: site)
                let logURL = logsDirectory.appendingPathComponent(Self.safeName(site.name) + ".log")
                try LogRotation.rotateIfNeeded(logURL)
                if !fileManager.fileExists(atPath: logURL.path) {
                    fileManager.createFile(atPath: logURL.path, contents: nil)
                }
                let outputHandle = try FileHandle(forWritingTo: logURL)
                defer { try? outputHandle.close() }
                try outputHandle.seekToEnd()
                let startMessage = "\n[HerdMe] Starting " + site.name + " with PHP-FPM on 127.0.0.1:\(fpmPort)\n"
                try outputHandle.write(contentsOf: Data(startMessage.utf8))

                let gateway = gatewayFactory(documentRoot, fpmPort)
                let gatewayPort = try gateway.start(preferredPort: 8_790 + index)
                gateways[site.id] = gateway
                ports[site.id] = gatewayPort
            }
            try await startDevelopmentServers(for: sites)
            let routes = routeMap(for: sites, tld: tld)
            proxyPort = try httpProxy.start(
                routes: routes,
                identity: nil,
                preferredPort: 80,
                fallbackPort: 8_080
            )
            if enableHTTPS {
                do {
                    let identity = try certificateManager.prepareIdentity(
                        tld: tld,
                        domains: Array(routes.keys),
                        allowKeychainInteraction: false
                    )
                    httpsProxyPort = try httpsProxy.start(
                        routes: routes,
                        identity: identity,
                        preferredPort: 443,
                        fallbackPort: 8_443
                    )
                } catch {
                    httpsProxyPort = nil
                    httpsStartupError = error.localizedDescription
                    if case CertificateSecretError.interactionRequired = error {
                        httpsStartupNeedsApproval = true
                    }
                }
            } else {
                httpsProxyPort = nil
                httpsStartupError = "HTTPS is waiting for explicit Keychain approval."
                httpsStartupNeedsApproval = true
            }
            guard try await waitUntilHealthy() else { throw LocalEnvironmentError.unhealthy }
            return ports
        } catch {
            await stopAll()
            throw error
        }
    }

    func stopAll() async {
        httpProxy.stop()
        httpsProxy.stop()
        proxyPort = nil
        httpsProxyPort = nil
        for gateway in gateways.values { gateway.stop() }
        gateways.removeAll()
        for server in developmentServers.values { await server.stop() }
        developmentServers.removeAll()
        configuredSites.removeAll()
        await fpmManager.stopAll()
        ports.removeAll()
        httpsStartupError = nil
        httpsStartupNeedsApproval = false
    }

    func stopAllImmediately() {
        httpProxy.stop()
        httpsProxy.stop()
        proxyPort = nil
        httpsProxyPort = nil
        for gateway in gateways.values { gateway.stop() }
        gateways.removeAll()
        for server in developmentServers.values {
            Task { await server.stop() }
        }
        developmentServers.removeAll()
        configuredSites.removeAll()
        fpmManager.stopAllImmediately()
        ports.removeAll()
        httpsStartupError = nil
        httpsStartupNeedsApproval = false
    }

    static func documentRoot(for site: SiteProject) -> URL {
        let publicDirectory = site.path.appendingPathComponent("public", isDirectory: true)
        var isDirectory: ObjCBool = false
        if FileManager.default.fileExists(atPath: publicDirectory.path, isDirectory: &isDirectory), isDirectory.boolValue {
            return publicDirectory
        }
        return site.path
    }

    @discardableResult
    static func removeLegacyRouterArtifacts(
        rootURL: URL,
        fileManager: FileManager = .default
    ) -> Bool {
        let legacyRouters = rootURL.appendingPathComponent("Runtime/routers", isDirectory: true)
        guard fileManager.fileExists(atPath: legacyRouters.path) else { return true }
        do {
            try fileManager.removeItem(at: legacyRouters)
            return true
        } catch {
            return false
        }
    }

    private func phpExecutable(for site: SiteProject) -> URL? {
        guard let cycle = site.phpVersion else { return nil }
        let executable = rootURL.appendingPathComponent("Runtimes/php/\(cycle)/bin/php")
        return fileManager.isExecutableFile(atPath: executable.path) ? executable : nil
    }

    private func prepareXdebugIfNeeded(cycle: String, php: URL) async throws {
        guard await xdebugManager.installed(cycle: cycle, php: php) == nil else { return }
        do {
            _ = try await xdebugManager.install(cycle: cycle)
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            try? LogStore(rootURL: rootURL.appendingPathComponent("Log", isDirectory: true)).append(
                "Xdebug for PHP \(cycle) could not be installed automatically. Sites will start without the debugger: \(error.localizedDescription)"
            )
        }
    }

    private func routeMap(for sites: [SiteProject], tld: String) -> [String: Int] {
        sites.reduce(into: [:]) { routes, site in
            guard let phpPort = ports[site.id] else { return }
            if let development = developmentServers[site.id], development.proxiesSiteTraffic,
                let developmentPort = development.port
            {
                routes[site.domain(tld: tld)] = developmentPort
            } else {
                routes[site.domain(tld: tld)] = phpPort
            }
        }
    }

    private func updateProxyRoutes(for sites: [SiteProject], tld: String) async {
        let routes = routeMap(for: sites, tld: tld)
        httpProxy.update(routes: routes)
        if httpsProxyPort != nil { httpsProxy.update(routes: routes) }
    }

    private func startDevelopmentServers(for sites: [SiteProject]) async throws {
        let expectedSites = sites.filter {
            SiteDevelopmentServer.isDevelopmentSite($0, fileManager: fileManager)
        }
        let expectedIDs = Set(expectedSites.map(\.id))
        let obsoleteIDs = developmentServers.keys.filter { !expectedIDs.contains($0) }
        for id in obsoleteIDs {
            guard let server = developmentServers.removeValue(forKey: id) else { continue }
            await server.stop()
        }

        for site in expectedSites {
            if let existing = developmentServers[site.id], existing.isRunning { continue }
            let server = developmentServers[site.id] ?? SiteDevelopmentServer(rootURL: rootURL, fileManager: fileManager)
            do {
                _ = try await server.start(site: site)
                developmentServers[site.id] = server
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                developmentServers[site.id] = server
                try? LogStore(rootURL: rootURL.appendingPathComponent("Log", isDirectory: true)).append(
                    "The development server for \(site.name) could not start: \(error.localizedDescription)"
                )
            }
        }
    }

    private func developmentServersAreHealthy(for sites: [SiteProject]) -> Bool {
        let expected = sites.filter { SiteDevelopmentServer.isDevelopmentSite($0, fileManager: fileManager) }
        guard Set(expected.map(\.id)) == Set(developmentServers.keys) else { return false }
        return expected.allSatisfy { site in
            guard let server = developmentServers[site.id], server.isRunning else { return false }
            return server.port.map(Self.canConnect(port:)) == true
        }
    }

    static func phpFPMExecutable(for php: URL) -> URL {
        php.deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("sbin/php-fpm")
    }

    private static func safeName(_ value: String) -> String {
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-_."))
        let mapped = value.unicodeScalars.map { allowed.contains($0) ? Character(String($0)) : "-" }
        return String(mapped)
    }

    static func availablePort(startingAt start: Int, excluding reserved: Set<Int> = []) -> Int? {
        guard start > 0, start <= 65_535 else { return nil }
        let end = min(start + 199, 65_535)
        for port in start...end where !reserved.contains(port) && canBind(port: port) {
            return port
        }
        return nil
    }

    static func canBind(port: Int) -> Bool {
        guard port > 0, port <= 65_535 else { return false }
        let descriptor = socket(AF_INET, SOCK_STREAM, 0)
        guard descriptor >= 0 else { return false }
        defer { Darwin.close(descriptor) }
        var reuseAddress: Int32 = 1
        setsockopt(
            descriptor,
            SOL_SOCKET,
            SO_REUSEADDR,
            &reuseAddress,
            socklen_t(MemoryLayout<Int32>.size)
        )

        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = in_port_t(UInt16(port).bigEndian)
        address.sin_addr = in_addr(s_addr: inet_addr("127.0.0.1"))
        return withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.bind(descriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) == 0
            }
        }
    }

    static func canConnect(port: Int) -> Bool {
        guard port > 0, port <= 65_535 else { return false }
        let descriptor = socket(AF_INET, SOCK_STREAM, 0)
        guard descriptor >= 0 else { return false }
        defer { Darwin.close(descriptor) }
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = in_port_t(UInt16(port).bigEndian)
        address.sin_addr = in_addr(s_addr: inet_addr("127.0.0.1"))
        return withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.connect(descriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) == 0
            }
        }
    }

    private func waitUntilHealthy() async throws -> Bool {
        for _ in 0..<200 {
            try Task.checkCancellation()
            if isRunning { return true }
            try await Task.sleep(for: .milliseconds(10))
        }
        return false
    }
}
