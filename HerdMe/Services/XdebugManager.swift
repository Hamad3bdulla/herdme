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
            ideKey: storedIDEKey?.isEmpty == false ? storedIDEKey! : "VSCODE"
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

enum XdebugInstallationError: LocalizedError {
    case invalidRelease
    case runtimeMissing(String)
    case buildToolMissing(String)
    case commandFailed(String)
    case extensionInvalid

    var errorDescription: String? {
        switch self {
        case .invalidRelease:
            "The Xdebug release service returned an invalid version."
        case let .runtimeMissing(cycle):
            "Install HerdMe PHP \(cycle) before installing Xdebug."
        case let .buildToolMissing(tool):
            "Xdebug requires \(tool). Install the Xcode Command Line Tools and try again."
        case let .commandFailed(output):
            output.isEmpty ? "Xdebug could not be built." : output
        case .extensionInvalid:
            "The built Xdebug extension could not be loaded by the selected PHP runtime."
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
            "xdebug.start_with_request": settings.detectBreakpoints ? "trigger" : "yes",
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
              result.status == 0 else {
            return nil
        }
        let version = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
        return version.isEmpty ? nil : XdebugInstallation(version: version, extensionURL: extensionURL)
    }

    func install(cycle: String) async throws -> XdebugInstallation {
        let runtimeURL = rootURL.appendingPathComponent("Runtimes/php/\(cycle)", isDirectory: true)
        let php = runtimeURL.appendingPathComponent("bin/php")
        let phpize = runtimeURL.appendingPathComponent("bin/phpize")
        let phpConfig = runtimeURL.appendingPathComponent("bin/php-config")
        guard fileManager.isExecutableFile(atPath: php.path),
              fileManager.isExecutableFile(atPath: phpize.path),
              fileManager.isExecutableFile(atPath: phpConfig.path) else {
            throw XdebugInstallationError.runtimeMissing(cycle)
        }
        guard fileManager.isExecutableFile(atPath: "/usr/bin/make") else {
            throw XdebugInstallationError.buildToolMissing("make")
        }

        let latestURL = URL(string: "https://pecl.php.net/rest/r/xdebug/stable.txt")!
        let (versionData, versionResponse) = try await URLSession.shared.data(from: latestURL)
        guard (versionResponse as? HTTPURLResponse)?.statusCode == 200,
              let version = String(data: versionData, encoding: .utf8)?
                .trimmingCharacters(in: .whitespacesAndNewlines),
              Self.isValid(version: version) else {
            throw XdebugInstallationError.invalidRelease
        }

        let archiveURL = URL(string: "https://pecl.php.net/get/xdebug-\(version).tgz")!
        let (downloadedArchive, archiveResponse) = try await URLSession.shared.download(from: archiveURL)
        guard (archiveResponse as? HTTPURLResponse)?.statusCode == 200 else {
            throw XdebugInstallationError.invalidRelease
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

        try requireSuccess(run(
            URL(fileURLWithPath: "/usr/bin/tar"),
            arguments: ["-xzf", downloadedArchive.path, "-C", sourceURL.path, "--strip-components", "1"]
        ))

        let environment = buildEnvironment(runtimeURL: buildRuntimeURL)
        try requireSuccess(run(
            buildRuntimeURL.appendingPathComponent("bin/phpize"),
            currentDirectory: sourceURL,
            environment: environment
        ))
        try requireSuccess(run(
            sourceURL.appendingPathComponent("configure"),
            arguments: ["--with-php-config=\(buildRuntimeURL.appendingPathComponent("bin/php-config").path)"],
            currentDirectory: sourceURL,
            environment: environment
        ))
        try requireSuccess(run(
            URL(fileURLWithPath: "/usr/bin/make"),
            arguments: ["-j\(max(ProcessInfo.processInfo.activeProcessorCount, 2))"],
            currentDirectory: sourceURL,
            environment: environment
        ))

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
              installation.version == version else {
            throw XdebugInstallationError.extensionInvalid
        }
        try Data((version + "\n").utf8).write(
            to: destination.deletingLastPathComponent().appendingPathComponent("VERSION"),
            options: .atomic
        )
        return installation
    }

    nonisolated static func isValid(version: String) -> Bool {
        version.range(of: "^[0-9]+\\.[0-9]+\\.[0-9]+$", options: .regularExpression) != nil
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
        let process = Process()
        let output = Pipe()
        process.executableURL = executable
        process.arguments = arguments
        process.currentDirectoryURL = currentDirectory
        process.environment = environment
        process.standardOutput = output
        process.standardError = output
        try process.run()
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        return (process.terminationStatus, String(decoding: data, as: UTF8.self))
    }
}
