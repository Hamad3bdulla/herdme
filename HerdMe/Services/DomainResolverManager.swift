import Darwin
import Foundation
import ServiceManagement

enum NetworkServiceRegistrationStatus: Equatable, Sendable {
    case notRegistered
    case enabled
    case requiresApproval
    case notFound
}

protocol NetworkServiceControlling: Sendable {
    func status() -> NetworkServiceRegistrationStatus
    func register() throws
    func unregister() throws
    func openApprovalSettings()
}

struct SMNetworkServiceController: NetworkServiceControlling {
    let plistName: String

    func status() -> NetworkServiceRegistrationStatus {
        switch service.status {
        case .notRegistered: .notRegistered
        case .enabled: .enabled
        case .requiresApproval: .requiresApproval
        case .notFound: .notFound
        @unknown default: .notFound
        }
    }

    func register() throws {
        try service.register()
    }

    func unregister() throws {
        try service.unregister()
    }

    func openApprovalSettings() {
        SMAppService.openSystemSettingsLoginItems()
    }

    private var service: SMAppService {
        SMAppService.daemon(plistName: plistName)
    }
}

enum DomainResolverState: Equatable, Sendable {
    case missing
    case managed
    case external

    var title: String {
        switch self {
        case .missing: "Not configured"
        case .managed: "HerdMe"
        case .external: "External"
        }
    }
}

enum DomainResolverError: LocalizedError {
    case invalidTLD
    case ownedByAnotherApplication
    case helperMissing
    case serviceManifestMissing
    case serviceRequiresApproval
    case invalidRoutingPorts
    case installationVerificationFailed
    case authorizationFailed(String)

    var errorDescription: String? {
        switch self {
        case .invalidTLD:
            "The top-level domain may only contain letters, numbers, and hyphens."
        case .ownedByAnotherApplication:
            "The resolver for this top-level domain is managed outside HerdMe and was not changed."
        case .helperMissing:
            "This HerdMe build does not contain its local network helper."
        case .serviceManifestMissing:
            "This HerdMe build does not contain its signed network service manifest."
        case .serviceRequiresApproval:
            "Approve HerdMe in System Settings > General > Login Items, then try local domains again."
        case .invalidRoutingPorts:
            "HerdMe could not prepare valid internal ports for local domains."
        case .installationVerificationFailed:
            "The local domain command completed, but its resolver or helper files could not be verified."
        case let .authorizationFailed(message):
            message.isEmpty ? "The local domain resolver could not be installed." : message
        }
    }
}

struct DomainResolverManager: Sendable {
    static let dnsPort = 53
    static let helperLabel = "app.herdme.network-helper"
    static let modernHelperLabel = "app.herdme.network-service"
    static let modernPlistName = "app.herdme.network-service.plist"
    static let helperDestination = "/Library/PrivilegedHelperTools/app.herdme.network-helper"
    static let launchDaemonDestination = "/Library/LaunchDaemons/app.herdme.network-helper.plist"
    let rootURL: URL
    private let networkService: any NetworkServiceControlling
    private let bundleURL: URL

    init(
        rootURL: URL,
        networkService: any NetworkServiceControlling = SMNetworkServiceController(
            plistName: DomainResolverManager.modernPlistName
        ),
        bundleURL: URL = Bundle.main.bundleURL
    ) {
        self.rootURL = rootURL
        self.networkService = networkService
        self.bundleURL = bundleURL
    }

    private var routingConfigurationURL: URL {
        rootURL.appendingPathComponent("Runtime/network-helper.conf")
    }

    func state(tld: String) -> DomainResolverState {
        guard Self.isValid(tld: tld) else { return .missing }
        let resolver = URL(fileURLWithPath: "/etc/resolver", isDirectory: true).appendingPathComponent(tld)
        guard let contents = try? String(contentsOf: resolver, encoding: .utf8) else { return .missing }
        return Self.state(contents: contents, port: Self.dnsPort)
    }

    @discardableResult
    func install(tld: String, replacingExternal: Bool = false) throws -> Bool {
        guard Self.isValid(tld: tld) else { throw DomainResolverError.invalidTLD }
        let currentState = state(tld: tld)
        if currentState == .managed, isModernNetworkHelperRunning(), isNetworkHelperCurrent() {
            return false
        }
        if currentState == .external, !replacingExternal {
            throw DomainResolverError.ownedByAnotherApplication
        }

        let bundledHelper = bundleURL
            .appendingPathComponent("Contents/Helpers/herdme-network-helper")
        guard FileManager.default.isExecutableFile(atPath: bundledHelper.path) else {
            throw DomainResolverError.helperMissing
        }
        let bundledManifest = bundleURL
            .appendingPathComponent("Contents/Library/LaunchDaemons/")
            .appendingPathComponent(Self.modernPlistName)
        guard Self.modernServiceManifestIsValid(at: bundledManifest) else {
            throw DomainResolverError.serviceManifestMissing
        }
        let existingPorts = routingPorts() ?? (http: 8_080, https: nil)
        try updateNetworkRouting(
            httpPort: existingPorts.http,
            httpsPort: existingPorts.https,
            tld: tld
        )

        do {
            try registerModernService(restart: networkService.status() == .enabled)
        } catch DomainResolverError.serviceRequiresApproval {
            networkService.openApprovalSettings()
            throw DomainResolverError.serviceRequiresApproval
        } catch {
            throw DomainResolverError.authorizationFailed(error.localizedDescription)
        }

        for _ in 0..<50 {
            if state(tld: tld) == .managed,
               isNetworkHelperCurrent(),
               isModernNetworkHelperRunning() {
                return true
            }
            Thread.sleep(forTimeInterval: 0.1)
        }
        guard state(tld: tld) == .managed, isNetworkHelperCurrent(), isModernNetworkHelperRunning() else {
            throw DomainResolverError.installationVerificationFailed
        }
        return true
    }

    func updateNetworkRouting(httpPort: Int, httpsPort: Int?, tld: String) throws {
        guard Self.isValid(tld: tld),
              (1_024...65_535).contains(httpPort),
              httpsPort.map({ (1_024...65_535).contains($0) }) ?? true else {
            throw DomainResolverError.invalidRoutingPorts
        }
        let directory = routingConfigurationURL.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let contents = "http=\(httpPort)\nhttps=\(httpsPort ?? 0)\ntld=\(tld)\n"
        try contents.write(to: routingConfigurationURL, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o644],
            ofItemAtPath: routingConfigurationURL.path
        )
    }

    func isNetworkHelperRunning() -> Bool {
        isModernNetworkHelperRunning() || launchdJobIsRunning(label: Self.helperLabel)
    }

    func isModernNetworkHelperRunning() -> Bool {
        networkService.status() == .enabled
            && launchdJobIsRunning(label: Self.modernHelperLabel)
    }

    private func launchdJobIsRunning(label: String) -> Bool {
        do {
            let result = try ProcessRunner.run(
                URL(fileURLWithPath: "/bin/launchctl"),
                arguments: ["print", "system/" + label],
                timeout: 10
            )
            return result.status == 0 && result.output.contains("state = running")
        } catch {
            return false
        }
    }

    func isNetworkHelperCurrent(
        bundledHelperURL: URL? = nil,
        installedHelperURL: URL = URL(fileURLWithPath: Self.helperDestination),
        installedDaemonURL: URL = URL(fileURLWithPath: Self.launchDaemonDestination)
    ) -> Bool {
        if networkService.status() == .enabled {
            return Self.modernServiceManifestIsValid(
                at: bundleURL
                    .appendingPathComponent("Contents/Library/LaunchDaemons/")
                    .appendingPathComponent(Self.modernPlistName)
            ) && isModernNetworkHelperRunning()
        }
        let bundledHelperURL = bundledHelperURL ?? bundleURL
            .appendingPathComponent("Contents/Helpers/herdme-network-helper")
        guard let bundledHelper = try? Data(contentsOf: bundledHelperURL),
              let installedHelper = try? Data(contentsOf: installedHelperURL),
              bundledHelper == installedHelper,
              let installedDaemon = try? Data(contentsOf: installedDaemonURL) else {
            return false
        }

        let expectedDaemon = Data(Self.launchDaemonPlist(
            configurationPath: routingConfigurationURL.path,
            uid: getuid(),
            gid: getgid()
        ).utf8)
        guard let expectedPropertyList = try? PropertyListSerialization.propertyList(
            from: expectedDaemon,
            format: nil
        ) as? NSDictionary,
              let installedPropertyList = try? PropertyListSerialization.propertyList(
                from: installedDaemon,
                format: nil
              ) as? NSDictionary else {
            return false
        }
        return expectedPropertyList.isEqual(installedPropertyList)
    }

    static func modernServiceManifestIsValid(at url: URL) -> Bool {
        guard let data = try? Data(contentsOf: url),
              let manifest = try? PropertyListSerialization.propertyList(
                from: data,
                format: nil
              ) as? [String: Any],
              manifest["Label"] as? String == modernHelperLabel,
              manifest["BundleProgram"] as? String == "Contents/Helpers/herdme-network-helper",
              manifest["AssociatedBundleIdentifiers"] as? [String] == ["app.herdme.desktop"],
              manifest["ProgramArguments"] as? [String] == [
                "herdme-network-helper", "--managed"
              ],
              manifest["RunAtLoad"] as? Bool == true,
              let keepAlive = manifest["KeepAlive"] as? [String: Any],
              keepAlive["SuccessfulExit"] as? Bool == false else {
            return false
        }
        return true
    }

    static func state(contents: String, port: Int) -> DomainResolverState {
        let normalized = contents.replacingOccurrences(of: "\r\n", with: "\n")
        return normalized.contains("# Managed by HerdMe")
            && normalized.contains("nameserver 127.0.0.1")
            && normalized.contains("port \(port)") ? .managed : .external
    }

    static func contents(port: Int) -> String {
        """
        # Managed by HerdMe
        nameserver 127.0.0.1
        port \(port)
        search_order 1
        timeout 1

        """
    }

    static func isValid(tld: String) -> Bool {
        guard !tld.isEmpty, tld.count <= 63,
              tld.first != "-", tld.last != "-" else { return false }
        return tld.unicodeScalars.allSatisfy {
            CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-")).contains($0)
        }
    }

    static func launchDaemonPlist(configurationPath: String, uid: uid_t, gid: gid_t) -> String {
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>AssociatedBundleIdentifiers</key>
            <array>
                <string>app.herdme.desktop</string>
            </array>
            <key>Label</key>
            <string>\(helperLabel)</string>
            <key>ProgramArguments</key>
            <array>
                <string>\(helperDestination)</string>
                <string>--config</string>
                <string>\(xmlEscape(configurationPath))</string>
                <string>--uid</string>
                <string>\(uid)</string>
                <string>--gid</string>
                <string>\(gid)</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
            <key>KeepAlive</key>
            <true/>
            <key>ProcessType</key>
            <string>Background</string>
            <key>ThrottleInterval</key>
            <integer>10</integer>
        </dict>
        </plist>
        """
    }

    func registerModernService(restart: Bool) throws {
        var currentStatus = networkService.status()
        if currentStatus == .requiresApproval {
            throw DomainResolverError.serviceRequiresApproval
        }

        if restart, currentStatus == .enabled {
            do {
                try networkService.unregister()
            } catch {
                if networkService.status() == .requiresApproval {
                    throw DomainResolverError.serviceRequiresApproval
                }
                throw error
            }
            for _ in 0..<30 {
                currentStatus = networkService.status()
                if currentStatus != .enabled { break }
                Thread.sleep(forTimeInterval: 0.1)
            }
            if currentStatus == .requiresApproval {
                throw DomainResolverError.serviceRequiresApproval
            }
            guard currentStatus != .enabled else {
                throw DomainResolverError.installationVerificationFailed
            }
        }

        currentStatus = networkService.status()
        if currentStatus != .enabled {
            do {
                try networkService.register()
            } catch {
                if networkService.status() == .requiresApproval {
                    throw DomainResolverError.serviceRequiresApproval
                }
                throw error
            }
        }

        currentStatus = networkService.status()
        if currentStatus == .requiresApproval {
            throw DomainResolverError.serviceRequiresApproval
        }
        guard currentStatus == .enabled else {
            throw DomainResolverError.installationVerificationFailed
        }
    }

    private func routingPorts() -> (http: Int, https: Int?)? {
        guard let contents = try? String(contentsOf: routingConfigurationURL, encoding: .utf8) else {
            return nil
        }
        var values: [String: String] = [:]
        for line in contents.split(whereSeparator: \.isNewline) {
            let parts = line.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
            guard parts.count == 2 else { return nil }
            values[String(parts[0])] = String(parts[1])
        }
        guard let httpValue = values["http"], let http = Int(httpValue),
              (1_024...65_535).contains(http),
              let httpsValue = values["https"], let rawHTTPS = Int(httpsValue),
              rawHTTPS == 0 || (1_024...65_535).contains(rawHTTPS) else {
            return nil
        }
        return (http, rawHTTPS == 0 ? nil : rawHTTPS)
    }

    private static func xmlEscape(_ value: String) -> String {
        value
            .replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
            .replacingOccurrences(of: "\"", with: "&quot;")
            .replacingOccurrences(of: "'", with: "&apos;")
    }
}
