import Darwin
import Foundation

enum DomainResolverState: Equatable {
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
    case invalidRoutingPorts
    case authorizationFailed(String)

    var errorDescription: String? {
        switch self {
        case .invalidTLD:
            "The top-level domain may only contain letters, numbers, and hyphens."
        case .ownedByAnotherApplication:
            "The resolver for this top-level domain is managed outside HerdMe and was not changed."
        case .helperMissing:
            "This HerdMe build does not contain its local network helper."
        case .invalidRoutingPorts:
            "HerdMe could not prepare valid internal ports for local domains."
        case let .authorizationFailed(message):
            message.isEmpty ? "The local domain resolver could not be installed." : message
        }
    }
}

struct DomainResolverManager {
    static let dnsPort = 53
    static let helperLabel = "app.herdme.network-helper"
    static let helperDestination = "/Library/PrivilegedHelperTools/app.herdme.network-helper"
    static let launchDaemonDestination = "/Library/LaunchDaemons/app.herdme.network-helper.plist"
    let rootURL: URL

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
        if currentState == .managed, isNetworkHelperRunning(), isNetworkHelperCurrent() { return false }
        if currentState == .external, !replacingExternal {
            throw DomainResolverError.ownedByAnotherApplication
        }

        let bundledHelper = Bundle.main.bundleURL
            .appendingPathComponent("Contents/Helpers/herdme-network-helper")
        guard FileManager.default.isExecutableFile(atPath: bundledHelper.path) else {
            throw DomainResolverError.helperMissing
        }
        if !FileManager.default.fileExists(atPath: routingConfigurationURL.path) {
            try updateNetworkRouting(httpPort: 8_080, httpsPort: 8_443, tld: tld)
        }

        let stagingDirectory = rootURL.appendingPathComponent("Runtime/resolver", isDirectory: true)
        try FileManager.default.createDirectory(at: stagingDirectory, withIntermediateDirectories: true)
        let resolverSource = stagingDirectory.appendingPathComponent(tld)
        let daemonSource = stagingDirectory.appendingPathComponent(Self.helperLabel + ".plist")
        try Self.contents(port: Self.dnsPort).write(
            to: resolverSource,
            atomically: true,
            encoding: .utf8
        )
        try Self.launchDaemonPlist(
            configurationPath: routingConfigurationURL.path,
            uid: getuid(),
            gid: getgid()
        ).write(to: daemonSource, atomically: true, encoding: .utf8)

        let destination = "/etc/resolver/" + tld
        do {
            try PrivilegedCommandRunner.execute(
                URL(fileURLWithPath: "/bin/sh"),
                arguments: ["-c", Self.installScript(
                    resolverSourcePath: resolverSource.path,
                    resolverDestinationPath: destination,
                    helperSourcePath: bundledHelper.path,
                    helperDestinationPath: Self.helperDestination,
                    daemonSourcePath: daemonSource.path,
                    daemonDestinationPath: Self.launchDaemonDestination
                )]
            )
        } catch {
            throw DomainResolverError.authorizationFailed(error.localizedDescription)
        }
        return true
    }

    func updateNetworkRouting(httpPort: Int, httpsPort: Int, tld: String) throws {
        guard Self.isValid(tld: tld),
              (1_024...65_535).contains(httpPort),
              (1_024...65_535).contains(httpsPort) else {
            throw DomainResolverError.invalidRoutingPorts
        }
        let directory = routingConfigurationURL.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let contents = "http=\(httpPort)\nhttps=\(httpsPort)\ntld=\(tld)\n"
        try contents.write(to: routingConfigurationURL, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o644],
            ofItemAtPath: routingConfigurationURL.path
        )
    }

    func isNetworkHelperRunning() -> Bool {
        let process = Process()
        let output = Pipe()
        process.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        process.arguments = ["print", "system/" + Self.helperLabel]
        process.standardOutput = output
        process.standardError = Pipe()
        do {
            try process.run()
            process.waitUntilExit()
            let text = String(
                decoding: output.fileHandleForReading.readDataToEndOfFile(),
                as: UTF8.self
            )
            return process.terminationStatus == 0 && text.contains("state = running")
        } catch {
            return false
        }
    }

    func isNetworkHelperCurrent(
        bundledHelperURL: URL? = nil,
        installedHelperURL: URL = URL(fileURLWithPath: Self.helperDestination),
        installedDaemonURL: URL = URL(fileURLWithPath: Self.launchDaemonDestination)
    ) -> Bool {
        let bundledHelperURL = bundledHelperURL ?? Bundle.main.bundleURL
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

    static func installScript(
        resolverSourcePath: String,
        resolverDestinationPath: String,
        helperSourcePath: String,
        helperDestinationPath: String,
        daemonSourcePath: String,
        daemonDestinationPath: String
    ) -> String {
        """
        /bin/launchctl bootout system/\(helperLabel) >/dev/null 2>&1 || true
        /usr/bin/install -d -o root -g wheel -m 0755 /etc/resolver /Library/PrivilegedHelperTools /Library/LaunchDaemons
        /usr/bin/install -o root -g wheel -m 0755 \(shellQuote(helperSourcePath)) \(shellQuote(helperDestinationPath))
        /usr/bin/install -o root -g wheel -m 0644 \(shellQuote(daemonSourcePath)) \(shellQuote(daemonDestinationPath))
        /usr/bin/install -o root -g wheel -m 0644 \(shellQuote(resolverSourcePath)) \(shellQuote(resolverDestinationPath))
        /bin/launchctl bootstrap system \(shellQuote(daemonDestinationPath))
        /bin/launchctl enable system/\(helperLabel)
        /bin/launchctl kickstart -k system/\(helperLabel)
        """
    }

    private static func shellQuote(_ value: String) -> String {
        "'" + value.replacingOccurrences(of: "'", with: "'\\''") + "'"
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
