import CryptoKit
import Foundation

struct DebuggerSettings: Equatable, Sendable {
    static let enabledKey = "debuggerEnabled"
    static let detectBreakpointsKey = "debuggerDetectBreakpoints"
    static let portKey = "debuggerPort"
    static let ideKeyKey = "debuggerIdeKey"

    var enabled: Bool
    var detectBreakpoints: Bool
    var port: Int
    var ideKey: String

    var startOnlyOnTrigger: Bool {
        get { detectBreakpoints }
        set { detectBreakpoints = newValue }
    }

    static let disabled = DebuggerSettings(
        enabled: false,
        detectBreakpoints: true,
        port: 9_003,
        ideKey: "VSCODE"
    )

    static func load(defaults: UserDefaults = .standard) -> DebuggerSettings {
        let storedPort = defaults.integer(forKey: portKey)
        let storedIDEKey = defaults.string(forKey: ideKeyKey)?
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return DebuggerSettings(
            enabled: defaults.bool(forKey: enabledKey),
            detectBreakpoints: defaults.object(forKey: detectBreakpointsKey) == nil
                ? true
                : defaults.bool(forKey: detectBreakpointsKey),
            port: (1...65_535).contains(storedPort) ? storedPort : 9_003,
            ideKey: storedIDEKey.flatMap { $0.isEmpty ? nil : $0 } ?? "VSCODE"
        )
    }

    func save(defaults: UserDefaults = .standard) {
        defaults.set(enabled, forKey: Self.enabledKey)
        defaults.set(detectBreakpoints, forKey: Self.detectBreakpointsKey)
        defaults.set(port, forKey: Self.portKey)
        defaults.set(ideKey, forKey: Self.ideKeyKey)
    }

    var normalized: DebuggerSettings {
        var value = self
        value.port = min(max(value.port, 1), 65_535)
        value.ideKey = value.ideKey
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .filter { $0.isLetter || $0.isNumber || "-_.".contains($0) }
        if value.ideKey.isEmpty { value.ideKey = "VSCODE" }
        return value
    }
}

struct PHPRequestSettings: Equatable, Sendable {
    static let uploadKey = "phpMaxUpload"
    static let memoryKey = "phpMemoryLimit"

    var maxUploadMegabytes: Int
    var memoryLimitMegabytes: Int

    static let `default` = PHPRequestSettings(
        maxUploadMegabytes: 100,
        memoryLimitMegabytes: 512
    )

    static func load(defaults: UserDefaults = .standard) -> PHPRequestSettings {
        let upload = defaults.integer(forKey: uploadKey)
        let memory = defaults.integer(forKey: memoryKey)
        return PHPRequestSettings(
            maxUploadMegabytes: upload > 0 ? upload : Self.default.maxUploadMegabytes,
            memoryLimitMegabytes: memory > 0 ? memory : Self.default.memoryLimitMegabytes
        ).normalized
    }

    var normalized: PHPRequestSettings {
        PHPRequestSettings(
            maxUploadMegabytes: min(max(maxUploadMegabytes, 1), 100_000),
            memoryLimitMegabytes: min(max(memoryLimitMegabytes, 16), 100_000)
        )
    }

    var phpOptions: [String: String] {
        let value = normalized
        return [
            "memory_limit": "\(value.memoryLimitMegabytes)M",
            "upload_max_filesize": "\(value.maxUploadMegabytes)M",
            "post_max_size": "\(value.maxUploadMegabytes)M"
        ]
    }
}

struct XdebugInstallation: Equatable, Sendable {
    let version: String
    let extensionURL: URL
}

struct XdebugSourceRelease: Equatable, Sendable {
    let version: String
    let archiveURL: URL
    let sha256: String
}

enum XdebugInstallationError: LocalizedError {
    case invalidRelease
    case checksumMismatch
    case unsafeArchive
    case runtimeMissing(String)
    case buildToolMissing(String)
    case commandFailed(String)
    case extensionInvalid

    var errorDescription: String? {
        switch self {
        case .invalidRelease:
            String(localized: "The Xdebug release service returned an invalid version.")
        case .checksumMismatch:
            String(localized: "The downloaded Xdebug archive did not match its official SHA-256 checksum.")
        case .unsafeArchive:
            String(localized: "The Xdebug archive contains an unsafe path and was rejected.")
        case .runtimeMissing(let cycle):
            String.localizedStringWithFormat(
                String(localized: "Install HerdMe PHP %@ before installing Xdebug."),
                cycle
            )
        case .buildToolMissing(let tool):
            String.localizedStringWithFormat(
                String(localized: "Xdebug requires %@. Install the Xcode Command Line Tools and try again."),
                tool
            )
        case .commandFailed(let output):
            output.isEmpty ? String(localized: "Xdebug could not be built.") : output
        case .extensionInvalid:
            String(localized: "The built Xdebug extension could not be loaded by the selected PHP runtime.")
        }
    }
}

actor XdebugManager {
    private let rootURL: URL
    private let fileManager: FileManager

    init(rootURL: URL, fileManager: FileManager = .default) {
        self.rootURL = rootURL
        self.fileManager = fileManager
    }

    nonisolated static func extensionURL(rootURL: URL, cycle: String) -> URL {
        rootURL.appendingPathComponent("Extensions/php/\(cycle)/xdebug.so")
    }

    nonisolated static func phpOptions(
        rootURL: URL,
        cycle: String,
        debugger: DebuggerSettings,
        request: PHPRequestSettings
    ) -> [String: String] {
        var options = request.phpOptions
        let settings = debugger.normalized
        guard settings.enabled else { return options }
        let extensionURL = extensionURL(rootURL: rootURL, cycle: cycle)
        guard FileManager.default.isReadableFile(atPath: extensionURL.path) else { return options }
        let logURL = rootURL.appendingPathComponent("Log/xdebug/xdebug.log")
        options.merge([
            "zend_extension": extensionURL.path,
            "xdebug.mode": "debug,develop",
            "xdebug.start_with_request": settings.startOnlyOnTrigger ? "trigger" : "yes",
            "xdebug.client_host": "127.0.0.1",
            "xdebug.client_port": String(settings.port),
            "xdebug.idekey": settings.ideKey,
            "xdebug.trigger_value": settings.ideKey,
            "xdebug.discover_client_host": "0",
            "xdebug.log": logURL.path,
            "xdebug.log_level": "1"
        ]) { _, new in new }
        return options
    }

    func installed(cycle: String, php: URL) -> XdebugInstallation? {
        let extensionURL = Self.extensionURL(rootURL: rootURL, cycle: cycle)
        guard fileManager.isReadableFile(atPath: extensionURL.path),
            let result = try? run(
                php,
                arguments: [
                    "-n", "-d", "zend_extension=\(extensionURL.path)",
                    "-r", "echo phpversion('xdebug') ?: '';"
                ]
            ),
            result.status == 0
        else {
            return nil
        }
        let version = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
        return version.isEmpty ? nil : XdebugInstallation(version: version, extensionURL: extensionURL)
    }

    func install(cycle: String) async throws -> XdebugInstallation {
        try Task.checkCancellation()
        let runtimeURL = rootURL.appendingPathComponent("Runtimes/php/\(cycle)", isDirectory: true)
        let php = runtimeURL.appendingPathComponent("bin/php")
        let phpize = runtimeURL.appendingPathComponent("bin/phpize")
        let phpConfig = runtimeURL.appendingPathComponent("bin/php-config")
        guard fileManager.isExecutableFile(atPath: php.path),
            fileManager.isExecutableFile(atPath: phpize.path),
            fileManager.isExecutableFile(atPath: phpConfig.path)
        else {
            throw XdebugInstallationError.runtimeMissing(cycle)
        }
        guard fileManager.isExecutableFile(atPath: "/usr/bin/make") else {
            throw XdebugInstallationError.buildToolMissing("make")
        }

        guard let releasesURL = URL(string: "https://xdebug.org/download") else {
            throw XdebugInstallationError.invalidRelease
        }
        let (releaseData, releaseResponse) = try await ManagedDownloadClient.data(from: releasesURL)
        try Task.checkCancellation()
        guard (releaseResponse as? HTTPURLResponse)?.statusCode == 200,
            let releaseHTML = String(data: releaseData, encoding: .utf8),
            let release = Self.sourceRelease(from: releaseHTML)
        else {
            throw XdebugInstallationError.invalidRelease
        }

        let (downloadedArchive, archiveResponse) = try await ManagedDownloadClient.download(
            from: release.archiveURL
        )
        try Task.checkCancellation()
        guard (archiveResponse as? HTTPURLResponse)?.statusCode == 200 else {
            throw XdebugInstallationError.invalidRelease
        }
        guard try Self.sha256(of: downloadedArchive) == release.sha256 else {
            throw XdebugInstallationError.checksumMismatch
        }

        let stagingRoot = fileManager.temporaryDirectory.appendingPathComponent(
            "herdme-xdebug-\(UUID().uuidString)",
            isDirectory: true
        )
        let sourceURL = stagingRoot.appendingPathComponent("source", isDirectory: true)
        let buildRuntimeURL = stagingRoot.appendingPathComponent("php-runtime", isDirectory: true)
        try fileManager.createDirectory(at: sourceURL, withIntermediateDirectories: true)
        try fileManager.createSymbolicLink(at: buildRuntimeURL, withDestinationURL: runtimeURL)
        defer { try? fileManager.removeItem(at: stagingRoot) }
        try Task.checkCancellation()

        let archiveListing = try run(
            URL(fileURLWithPath: "/usr/bin/tar"),
            arguments: ["-tzf", downloadedArchive.path]
        )
        try requireSuccess(archiveListing)
        guard Self.archiveEntriesAreSafe(archiveListing.output) else {
            throw XdebugInstallationError.unsafeArchive
        }
        let verboseArchiveListing = try run(
            URL(fileURLWithPath: "/usr/bin/tar"),
            arguments: ["-tvzf", downloadedArchive.path]
        )
        try requireSuccess(verboseArchiveListing)
        do {
            try TarArchivePolicy.validate(
                nameListing: archiveListing.output,
                verboseListing: verboseArchiveListing.output
            )
        } catch {
            throw XdebugInstallationError.unsafeArchive
        }

        try requireSuccess(
            run(
                URL(fileURLWithPath: "/usr/bin/tar"),
                arguments: [
                    "-xzf", downloadedArchive.path,
                    "-C", sourceURL.path,
                    "--strip-components", "1",
                    "--no-same-owner", "--no-same-permissions"
                ]
            ))
        do {
            try TarArchivePolicy.validateExtractedTree(at: sourceURL)
        } catch {
            throw XdebugInstallationError.unsafeArchive
        }

        let environment = buildEnvironment(runtimeURL: buildRuntimeURL)
        try requireSuccess(
            run(
                buildRuntimeURL.appendingPathComponent("bin/phpize"),
                currentDirectory: sourceURL,
                environment: environment
            ))
        try requireSuccess(
            run(
                sourceURL.appendingPathComponent("configure"),
                arguments: ["--with-php-config=\(buildRuntimeURL.appendingPathComponent("bin/php-config").path)"],
                currentDirectory: sourceURL,
                environment: environment
            ))
        try requireSuccess(
            run(
                URL(fileURLWithPath: "/usr/bin/make"),
                arguments: ["-j\(max(ProcessInfo.processInfo.activeProcessorCount, 2))"],
                currentDirectory: sourceURL,
                environment: environment
            ))
        try Task.checkCancellation()

        let builtExtension = sourceURL.appendingPathComponent("modules/xdebug.so")
        guard fileManager.isReadableFile(atPath: builtExtension.path) else {
            throw XdebugInstallationError.extensionInvalid
        }
        let destination = Self.extensionURL(rootURL: rootURL, cycle: cycle)
        try fileManager.createDirectory(
            at: destination.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        let candidate = destination.deletingLastPathComponent()
            .appendingPathComponent(".xdebug-\(UUID().uuidString).so")
        try fileManager.copyItem(at: builtExtension, to: candidate)
        if fileManager.fileExists(atPath: destination.path) {
            try fileManager.removeItem(at: destination)
        }
        try fileManager.moveItem(at: candidate, to: destination)

        guard let installation = installed(cycle: cycle, php: php),
            installation.version == release.version
        else {
            throw XdebugInstallationError.extensionInvalid
        }
        try Data((release.version + "\n").utf8).write(
            to: destination.deletingLastPathComponent().appendingPathComponent("VERSION"),
            options: .atomic
        )
        return installation
    }

    nonisolated static func isValid(version: String) -> Bool {
        version.range(of: "^[0-9]+\\.[0-9]+\\.[0-9]+$", options: .regularExpression) != nil
    }

    nonisolated static func sourceRelease(from html: String) -> XdebugSourceRelease? {
        guard
            let anchorPattern = try? NSRegularExpression(
                pattern: #"<a\b[^>]*>"#,
                options: [.caseInsensitive]
            )
        else { return nil }
        let fullRange = NSRange(html.startIndex..<html.endIndex, in: html)
        for match in anchorPattern.matches(in: html, range: fullRange) {
            guard let range = Range(match.range, in: html) else { continue }
            let anchor = String(html[range])
            guard let href = attribute("href", in: anchor),
                let title = attribute("title", in: anchor),
                let version = capture(
                    pattern: #"^/files/xdebug-([0-9]+\.[0-9]+\.[0-9]+)\.tgz$"#,
                    in: href
                ),
                isValid(version: version),
                let checksum = capture(
                    pattern: #"^SHA256:\s*([0-9a-fA-F]{64})$"#,
                    in:
                        title
                        .replacingOccurrences(of: "&nbsp;", with: " ")
                        .replacingOccurrences(of: "\u{00A0}", with: " ")
                )?.lowercased(),
                let archiveURL = URL(string: href, relativeTo: URL(string: "https://xdebug.org"))?
                    .absoluteURL,
                archiveURL.scheme == "https",
                archiveURL.host == "xdebug.org"
            else {
                continue
            }
            return XdebugSourceRelease(
                version: version,
                archiveURL: archiveURL,
                sha256: checksum
            )
        }
        return nil
    }

    nonisolated static func sha256(of data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    nonisolated static func archiveEntriesAreSafe(_ listing: String) -> Bool {
        let entries = listing.split(whereSeparator: \.isNewline)
        guard !entries.isEmpty, entries.count <= 20_000 else { return false }
        return entries.allSatisfy { entry in
            let path = String(entry)
            guard !path.isEmpty, !path.hasPrefix("/"), !path.contains("\\") else {
                return false
            }
            return !path.split(separator: "/", omittingEmptySubsequences: false).contains("..")
        }
    }

    private nonisolated static func attribute(_ name: String, in anchor: String) -> String? {
        capture(
            pattern: #"\b"# + NSRegularExpression.escapedPattern(for: name)
                + #"\s*=\s*[\"']([^\"']+)[\"']"#, in: anchor)
    }

    private nonisolated static func capture(pattern: String, in value: String) -> String? {
        guard let expression = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive]),
            let match = expression.firstMatch(
                in: value,
                range: NSRange(value.startIndex..<value.endIndex, in: value)
            ),
            match.numberOfRanges > 1,
            let range = Range(match.range(at: 1), in: value)
        else {
            return nil
        }
        return String(value[range])
    }

    private nonisolated static func sha256(of fileURL: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: fileURL)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let data = try handle.read(upToCount: 1_048_576), !data.isEmpty {
            if Task<Never, Never>.isCancelled { throw CancellationError() }
            hasher.update(data: data)
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    private func buildEnvironment(runtimeURL: URL) -> [String: String] {
        var environment = ProcessInfo.processInfo.environment
        let path = [
            runtimeURL.appendingPathComponent("bin").path,
            "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin"
        ].joined(separator: ":")
        environment["PATH"] = path
        environment["PHP_PEAR_PHP_BIN"] = runtimeURL.appendingPathComponent("bin/php").path
        return environment
    }

    private func requireSuccess(_ result: (status: Int32, output: String)) throws {
        guard result.status == 0 else {
            throw XdebugInstallationError.commandFailed(result.output)
        }
    }

    private func run(
        _ executable: URL,
        arguments: [String] = [],
        currentDirectory: URL? = nil,
        environment: [String: String]? = nil
    ) throws -> (status: Int32, output: String) {
        let result = try ProcessRunner.run(
            executable,
            arguments: arguments,
            currentDirectory: currentDirectory,
            environment: environment,
            cancellationRequested: { Task<Never, Never>.isCancelled }
        )
        return (result.status, result.output)
    }
}
