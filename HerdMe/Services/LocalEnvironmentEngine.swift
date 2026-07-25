import Darwin
import Foundation

enum LocalEnvironmentError: LocalizedError {
    case phpMissing
    case noSites
    case noAvailablePort
    case processFailed(String)

    var errorDescription: String? {
        switch self {
        case .phpMissing:
            "Install and activate a HerdMe-managed PHP version before starting sites."
        case .noSites:
            "No local sites were found to start."
        case .noAvailablePort:
            "HerdMe could not find an available local development port."
        case let .processFailed(site):
            "The local server for " + site + " could not be started."
        }
    }
}

final class LocalEnvironmentEngine: @unchecked Sendable {
    let rootURL: URL
    private let fileManager = FileManager.default
    private let phpValidator = PHPRuntimeValidator()
    private let fpmManager: PHPFPMManager
    private var gateways: [String: LocalFastCGIGateway] = [:]
    private let httpProxy = LocalHTTPProxy()
    private let httpsProxy = LocalHTTPProxy()
    private let certificateManager: LocalCertificateManager
    private(set) var ports: [String: Int] = [:]
    private(set) var proxyPort: Int?
    private(set) var httpsProxyPort: Int?

    init(rootURL: URL, certificateManager: LocalCertificateManager? = nil) {
        self.rootURL = rootURL
        fpmManager = PHPFPMManager(rootURL: rootURL)
        self.certificateManager = certificateManager ?? LocalCertificateManager(rootURL: rootURL)
    }

    var isRunning: Bool {
        fpmManager.isRunning && gateways.values.contains(where: \.isRunning)
    }

    func start(
        sites: [SiteProject],
        defaultPHP: URL?,
        defaultPHPCycle: String? = nil,
        tld: String,
        debuggerSettings: DebuggerSettings = .disabled,
        phpRequestSettings: PHPRequestSettings = .default
    ) throws -> [String: Int] {
        guard !sites.isEmpty else { throw LocalEnvironmentError.noSites }
        guard let defaultPHP, fileManager.isExecutableFile(atPath: defaultPHP.path) else {
            throw LocalEnvironmentError.phpMissing
        }
        if isRunning { return ports }

        let logsDirectory = rootURL.appendingPathComponent("Log/sites", isDirectory: true)
        try fileManager.createDirectory(at: logsDirectory, withIntermediateDirectories: true)

        do {
            var fpmPorts: [String: Int] = [:]
            var validatedPHP = Set<String>()
            for (index, site) in sites.enumerated() {
                let php = phpExecutable(for: site) ?? defaultPHP
                if validatedPHP.insert(php.path).inserted {
                    try phpValidator.validate(executable: php)
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
                    fpmPort = try fpmManager.start(
                        executable: fpm,
                        identifier: "runtime-\(fpmPorts.count + 1)",
                        preferredPort: 9_070 + fpmPorts.count * 20,
                        phpOptions: phpOptions
                    )
                    fpmPorts[fpm.path] = fpmPort
                }
                let documentRoot = Self.documentRoot(for: site)
                let logURL = logsDirectory.appendingPathComponent(Self.safeName(site.name) + ".log")
                if !fileManager.fileExists(atPath: logURL.path) {
                    fileManager.createFile(atPath: logURL.path, contents: nil)
                }
                let outputHandle = try FileHandle(forWritingTo: logURL)
                defer { try? outputHandle.close() }
                try outputHandle.seekToEnd()
                let startMessage = "\n[HerdMe] Starting " + site.name + " with PHP-FPM on 127.0.0.1:\(fpmPort)\n"
                try outputHandle.write(contentsOf: Data(startMessage.utf8))

                let gateway = LocalFastCGIGateway(documentRoot: documentRoot, fpmPort: fpmPort)
                let gatewayPort = try gateway.start(preferredPort: 8_790 + index)
                gateways[site.id] = gateway
                ports[site.id] = gatewayPort
            }
            var routes: [String: Int] = [:]
            for site in sites {
                guard let port = ports[site.id] else { continue }
                routes[site.domain(tld: tld)] = port
            }
            proxyPort = try httpProxy.start(routes: routes)
            let identity = try certificateManager.prepareIdentity(tld: tld, domains: Array(routes.keys))
            httpsProxyPort = try httpsProxy.start(
                routes: routes,
                identity: identity,
                preferredPort: 443,
                fallbackPort: 8_443
            )
            return ports
        } catch {
            stopAll()
            throw error
        }
    }

    func stopAll() {
        httpProxy.stop()
        httpsProxy.stop()
        proxyPort = nil
        httpsProxyPort = nil
        gateways.values.forEach { $0.stop() }
        gateways.removeAll()
        fpmManager.stopAll()
        ports.removeAll()
    }

    static func documentRoot(for site: SiteProject) -> URL {
        let publicDirectory = site.path.appendingPathComponent("public", isDirectory: true)
        var isDirectory: ObjCBool = false
        if FileManager.default.fileExists(atPath: publicDirectory.path, isDirectory: &isDirectory), isDirectory.boolValue {
            return publicDirectory
        }
        return site.path
    }

    static func routerScript(documentRoot: URL) -> String {
        let escapedRoot = documentRoot.path
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "'", with: "\\'")
        return """
        <?php
        $public = '\(escapedRoot)';
        $path = urldecode(parse_url($_SERVER['REQUEST_URI'], PHP_URL_PATH));
        if ($path !== '/' && is_file($public . $path)) {
            return false;
        }
        $frontController = $public . '/index.php';
        if (is_file($frontController)) {
            require $frontController;
            return true;
        }
        return false;
        """
    }

    private func phpExecutable(for site: SiteProject) -> URL? {
        guard let cycle = site.phpVersion else { return nil }
        let executable = rootURL.appendingPathComponent("Runtimes/php/\(cycle)/bin/php")
        return fileManager.isExecutableFile(atPath: executable.path) ? executable : nil
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

    static func availablePort(startingAt start: Int) -> Int? {
        guard start > 0, start <= 65_535 else { return nil }
        let end = min(start + 199, 65_535)
        for port in start...end where canBind(port: port) {
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
}
