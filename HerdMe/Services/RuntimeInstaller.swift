import CryptoKit
import Foundation

struct NodeRelease: Decodable, Sendable {
    let version: String
    let files: [String]
}

private struct PackagistPackageIndex: Decodable {
    let packages: [String: [PackagistPackageRelease]]
}

private struct PackagistPackageRelease: Decodable {
    let version: String
}

private struct ComposerReleaseIndex: Decodable {
    let stable: [ComposerRelease]
}

private struct ComposerRelease: Decodable {
    let version: String
    let minimumPHP: Int

    private enum CodingKeys: String, CodingKey {
        case version
        case minimumPHP = "min-php"
    }
}

private struct HomebrewFormulaIndex: Decodable {
    let formulae: [HomebrewFormulaRelease]
}

private struct HomebrewFormulaRelease: Decodable {
    let name: String
    let versions: HomebrewFormulaVersions
}

private struct HomebrewFormulaVersions: Decodable {
    let stable: String
}

enum CommandFailureReporter {
    private static let lock = NSLock()

    static func recordAndSummarize(_ output: String, operation: String, rootURL: URL) -> String {
        let trimmed = output.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return "" }
        append(trimmed, operation: operation, rootURL: rootURL)

        let recentLines = trimmed.split(whereSeparator: \Character.isNewline).suffix(12)
        var summary = recentLines.joined(separator: "\n")
        if summary.count > 1_500 {
            summary = String(summary.suffix(1_500))
        }
        if summary == trimmed { return summary }
        return "Recent command output:\n\(summary)\n\nFull output is available in Logs/homebrew.log."
    }

    private static func append(_ output: String, operation: String, rootURL: URL) {
        lock.lock()
        defer { lock.unlock() }

        let logDirectory = rootURL.appendingPathComponent("Log", isDirectory: true)
        let logURL = logDirectory.appendingPathComponent("homebrew.log")
        try? FileManager.default.createDirectory(at: logDirectory, withIntermediateDirectories: true)
        if !FileManager.default.fileExists(atPath: logURL.path) {
            FileManager.default.createFile(atPath: logURL.path, contents: nil)
        }
        guard let handle = try? FileHandle(forWritingTo: logURL) else { return }
        defer { try? handle.close() }
        _ = try? handle.seekToEnd()
        let entry = "\n[\(ISO8601DateFormatter().string(from: Date()))] \(operation)\n\(output)\n"
        try? handle.write(contentsOf: Data(entry.utf8))
    }
}

enum HomebrewFormulaTrust {
    static func target(from output: String, expectedFormula: String) -> String? {
        guard output.localizedCaseInsensitiveContains("untrusted tap"),
              let match = output.range(
                of: #"brew trust --formula [`']?([A-Za-z0-9._+/@-]+)"#,
                options: .regularExpression
              ) else { return nil }
        let command = String(output[match])
        guard let target = command.split(separator: " ").last.map(String.init)?
            .trimmingCharacters(in: CharacterSet(charactersIn: "`'")),
              target == expectedFormula || target.hasPrefix(expectedFormula + "@") else {
            return nil
        }
        return target
    }
}

enum RuntimeInstallationError: LocalizedError {
    case unsupportedArchitecture
    case releaseNotFound(String)
    case invalidResponse
    case archiveFailed(String)
    case runtimeNotInstalled(name: String, cycle: String)
    case packageManagerMissing
    case integrityCheckFailed(component: String)
    case commandFailed(String)

    var errorDescription: String? {
        switch self {
        case .unsupportedArchitecture:
            "This Mac architecture is not supported by the selected runtime."
        case let .releaseNotFound(cycle):
            "No compatible Node.js " + cycle + " release was found."
        case .invalidResponse:
            "The runtime server returned an invalid response."
        case let .archiveFailed(output):
            output.isEmpty ? "The downloaded runtime could not be unpacked." : output
        case let .runtimeNotInstalled(name, cycle):
            name + " " + cycle + " is not installed by HerdMe."
        case .packageManagerMissing:
            "Homebrew is required to install PHP on macOS."
        case let .integrityCheckFailed(component):
            component + " did not match its official checksum."
        case let .commandFailed(output):
            output.isEmpty ? "The runtime package manager failed." : output
        }
    }
}

actor RuntimeInstaller {
    let rootURL: URL
    private let fileManager = FileManager.default

    init(rootURL: URL) {
        self.rootURL = rootURL
    }

    func installNode(cycle: String) async throws -> String {
        let archiveKey = try nodeArchiveKey()
        let indexURL = URL(string: "https://nodejs.org/dist/index.json")!
        let (indexData, response) = try await URLSession.shared.data(from: indexURL)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw RuntimeInstallationError.invalidResponse
        }
        let releases = try JSONDecoder().decode([NodeRelease].self, from: indexData)
        guard let release = Self.release(for: cycle, archiveKey: archiveKey, in: releases) else {
            throw RuntimeInstallationError.releaseNotFound(cycle)
        }

        let version = String(release.version.dropFirst())
        let runtimesRoot = rootURL.appendingPathComponent("Runtimes/node", isDirectory: true)
        let destination = runtimesRoot.appendingPathComponent(version, isDirectory: true)
        if !fileManager.fileExists(atPath: destination.path) {
            try fileManager.createDirectory(at: runtimesRoot, withIntermediateDirectories: true)
            let staging = runtimesRoot.appendingPathComponent(".install-\(UUID().uuidString)", isDirectory: true)
            try fileManager.createDirectory(at: staging, withIntermediateDirectories: true)
            defer { try? fileManager.removeItem(at: staging) }

            let platform = archiveKey.hasPrefix("osx-arm64") ? "darwin-arm64" : "darwin-x64"
            let archiveName = "node-\(release.version)-\(platform).tar.gz"
            let downloadURL = URL(string: "https://nodejs.org/dist/\(release.version)/\(archiveName)")!
            let checksumsURL = URL(string: "https://nodejs.org/dist/\(release.version)/SHASUMS256.txt")!
            async let archiveRequest = URLSession.shared.download(from: downloadURL)
            async let checksumsRequest = URLSession.shared.data(from: checksumsURL)
            let ((temporaryArchive, downloadResponse), (checksumsData, checksumsResponse)) = try await (
                archiveRequest,
                checksumsRequest
            )
            guard (downloadResponse as? HTTPURLResponse)?.statusCode == 200,
                  (checksumsResponse as? HTTPURLResponse)?.statusCode == 200,
                  let checksums = String(data: checksumsData, encoding: .utf8),
                  let expectedChecksum = Self.nodeChecksum(
                      for: archiveName,
                      in: checksums
                  ) else {
                throw RuntimeInstallationError.invalidResponse
            }
            guard try Self.sha256(of: temporaryArchive) == expectedChecksum else {
                throw RuntimeInstallationError.integrityCheckFailed(component: "Node.js archive")
            }
            try unpack(archive: temporaryArchive, into: staging)
            try fileManager.moveItem(at: staging, to: destination)
        }

        try activateNode(version: version)
        return version
    }

    func latestNodeVersions(cycles: [String]) async throws -> [String: String] {
        let archiveKey = try nodeArchiveKey()
        let indexURL = URL(string: "https://nodejs.org/dist/index.json")!
        let (indexData, response) = try await URLSession.shared.data(from: indexURL)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw RuntimeInstallationError.invalidResponse
        }
        let releases = try JSONDecoder().decode([NodeRelease].self, from: indexData)
        return Dictionary(uniqueKeysWithValues: Set(cycles).compactMap { cycle in
            guard let release = Self.release(for: cycle, archiveKey: archiveKey, in: releases) else {
                return nil
            }
            return (cycle, Self.normalizedVersion(release.version))
        })
    }

    func latestPHPVersions(cycles: [String]) throws -> [String: String] {
        let requestedCycles = Set(cycles)
        guard !requestedCycles.isEmpty else { return [:] }
        guard let brew = brewURL() else { throw RuntimeInstallationError.packageManagerMissing }
        let formulae = requestedCycles.sorted().map { "php@\($0)" }
        let result = try run(
            brew,
            arguments: ["info", "--json=v2"] + formulae,
            environment: homebrewEnvironment
        )
        guard result.status == 0 else {
            throw RuntimeInstallationError.commandFailed(CommandFailureReporter.recordAndSummarize(
                result.output,
                operation: "brew info " + formulae.joined(separator: " "),
                rootURL: rootURL
            ))
        }
        return try Self.phpVersions(
            fromHomebrewInfoOutput: result.output,
            cycles: requestedCycles
        )
    }

    func activateNode(cycle: String) throws {
        guard let version = installedNodeVersions().first(where: { $0.hasPrefix("\(cycle).") || $0 == cycle }) else {
            throw RuntimeInstallationError.runtimeNotInstalled(name: "Node.js", cycle: cycle)
        }
        try activateNode(version: version)
    }

    func installPHP(cycle: String) throws -> String {
        guard let brew = brewURL() else { throw RuntimeInstallationError.packageManagerMissing }
        let formula = "php@\(cycle)"
        let managedExecutable = rootURL.appendingPathComponent("Runtimes/php/\(cycle)/bin/php")
        let command = fileManager.isExecutableFile(atPath: managedExecutable.path) ? "upgrade" : "install"
        var install = try run(brew, arguments: [command, formula], environment: homebrewEnvironment)
        if install.status != 0,
           let trustTarget = Self.phpFormulaTrustTarget(from: install.output, cycle: cycle) {
            let trust = try run(
                brew,
                arguments: ["trust", "--formula", trustTarget],
                environment: homebrewEnvironment
            )
            guard trust.status == 0 else {
                _ = CommandFailureReporter.recordAndSummarize(
                    trust.output,
                    operation: "brew trust --formula \(trustTarget)",
                    rootURL: rootURL
                )
                throw RuntimeInstallationError.commandFailed(
                    "HerdMe could not approve the verified PHP \(cycle) formula. Full output is available in Logs/homebrew.log."
                )
            }
            install = try run(
                brew,
                arguments: [command, formula],
                environment: homebrewEnvironment
            )
        }
        guard install.status == 0 else {
            _ = CommandFailureReporter.recordAndSummarize(
                install.output,
                operation: "brew \(command) \(formula)",
                rootURL: rootURL
            )
            throw RuntimeInstallationError.commandFailed(
                "PHP \(cycle) could not be installed. Full output is available in Logs/homebrew.log."
            )
        }
        let prefix = try run(brew, arguments: ["--prefix", formula], environment: homebrewEnvironment)
        guard prefix.status == 0 else {
            _ = CommandFailureReporter.recordAndSummarize(
                prefix.output,
                operation: "brew --prefix \(formula)",
                rootURL: rootURL
            )
            throw RuntimeInstallationError.commandFailed(
                "PHP \(cycle) was installed, but HerdMe could not locate it. Full output is available in Logs/homebrew.log."
            )
        }
        let source = URL(fileURLWithPath: prefix.output.trimmingCharacters(in: .whitespacesAndNewlines), isDirectory: true)
        var isDirectory: ObjCBool = false
        guard fileManager.fileExists(atPath: source.appendingPathComponent("bin").path, isDirectory: &isDirectory),
              isDirectory.boolValue else {
            throw RuntimeInstallationError.commandFailed("Homebrew did not return a valid PHP runtime path.")
        }
        let runtimeRoot = rootURL.appendingPathComponent("Runtimes/php", isDirectory: true)
        try fileManager.createDirectory(at: runtimeRoot, withIntermediateDirectories: true)
        let destination = runtimeRoot.appendingPathComponent(cycle, isDirectory: true)
        if fileManager.fileExists(atPath: destination.path) || (try? destination.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true {
            try? fileManager.removeItem(at: destination)
        }
        try fileManager.createSymbolicLink(at: destination, withDestinationURL: source)
        try activatePHP(cycle: cycle)
        let version = try run(
            source.appendingPathComponent("bin/php"),
            arguments: ["-r", "echo PHP_VERSION;"]
        )
        let installedVersion = version.output.trimmingCharacters(in: .whitespacesAndNewlines)
        guard version.status == 0, !installedVersion.isEmpty else {
            throw RuntimeInstallationError.commandFailed("The installed PHP runtime did not report its version.")
        }
        try PHPRuntimeValidator().validate(executable: source.appendingPathComponent("bin/php"))
        return installedVersion
    }

    nonisolated static func phpFormulaTrustTarget(from output: String, cycle: String) -> String? {
        guard cycle.range(of: #"^[0-9]+\.[0-9]+$"#, options: .regularExpression) != nil else {
            return nil
        }
        let expectedFormula = "shivammathur/php/php@\(cycle)"
        guard let target = HomebrewFormulaTrust.target(
            from: output,
            expectedFormula: expectedFormula
        ), target == expectedFormula else { return nil }
        return target
    }

    func activatePHP(cycle: String) throws {
        let source = rootURL.appendingPathComponent("Runtimes/php/\(cycle)/bin/php")
        guard fileManager.isExecutableFile(atPath: source.path) else {
            throw RuntimeInstallationError.runtimeNotInstalled(name: "PHP", cycle: cycle)
        }
        let managedBin = rootURL.appendingPathComponent("bin", isDirectory: true)
        try fileManager.createDirectory(at: managedBin, withIntermediateDirectories: true)
        for name in ["php", "phpize", "php-config", "php-fpm"] {
            let candidate = rootURL.appendingPathComponent(
                "Runtimes/php/\(cycle)/" + Self.phpRelativePath(for: name)
            )
            guard fileManager.fileExists(atPath: candidate.path) else { continue }
            let link = managedBin.appendingPathComponent(name)
            if fileManager.fileExists(atPath: link.path) || (try? link.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true {
                try fileManager.removeItem(at: link)
            }
            try fileManager.createSymbolicLink(at: link, withDestinationURL: candidate)
        }
    }

    func laravelInstallerVersion(cycle: String) -> String? {
        let php = rootURL.appendingPathComponent("Runtimes/php/\(cycle)/bin/php")
        let installer = laravelInstallerURL
        guard fileManager.isExecutableFile(atPath: php.path),
              fileManager.isReadableFile(atPath: installer.path),
              let result = try? run(
                  php,
                  arguments: [installer.path, "--version", "--no-ansi"],
                  environment: composerEnvironment
              ),
              result.status == 0 else {
            return nil
        }
        let output = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let range = output.range(
            of: "[0-9]+\\.[0-9]+\\.[0-9]+",
            options: .regularExpression
        ) else {
            return nil
        }
        return String(output[range])
    }

    func composerVersion(cycle: String) -> String? {
        let php = rootURL.appendingPathComponent("Runtimes/php/\(cycle)/bin/php")
        guard fileManager.isExecutableFile(atPath: php.path),
              fileManager.isReadableFile(atPath: composerURL.path),
              let result = try? run(
                  php,
                  arguments: [composerURL.path, "--version", "--no-ansi"],
                  environment: composerEnvironment
              ),
              result.status == 0 else {
            return nil
        }
        return Self.composerVersion(from: result.output)
    }

    func latestComposerVersion(cycle: String) async throws -> String {
        let php = rootURL.appendingPathComponent("Runtimes/php/\(cycle)/bin/php")
        guard fileManager.isExecutableFile(atPath: php.path) else {
            throw RuntimeInstallationError.runtimeNotInstalled(name: "PHP", cycle: cycle)
        }
        let versionID = try run(
            php,
            arguments: ["-r", "echo PHP_VERSION_ID;"],
            environment: composerEnvironment
        )
        guard versionID.status == 0,
              let phpVersionID = Int(versionID.output.trimmingCharacters(in: .whitespacesAndNewlines)) else {
            throw RuntimeInstallationError.invalidResponse
        }
        let indexURL = URL(string: "https://getcomposer.org/versions")!
        let (data, response) = try await URLSession.shared.data(from: indexURL)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw RuntimeInstallationError.invalidResponse
        }
        return try Self.compatibleComposerVersion(from: data, phpVersionID: phpVersionID)
    }

    func updateComposer(cycle: String) async throws -> String {
        let php = rootURL.appendingPathComponent("Runtimes/php/\(cycle)/bin/php")
        guard fileManager.isExecutableFile(atPath: php.path) else {
            throw RuntimeInstallationError.runtimeNotInstalled(name: "PHP", cycle: cycle)
        }
        let composer = try await ensureComposer(php: php)
        let result = try run(
            php,
            arguments: [
                composer.path,
                "self-update", "--stable", "--no-interaction", "--no-progress", "--no-ansi"
            ],
            environment: composerEnvironment
        )
        guard result.status == 0 else {
            _ = CommandFailureReporter.recordAndSummarize(
                result.output,
                operation: "composer self-update",
                rootURL: rootURL
            )
            throw RuntimeInstallationError.commandFailed(
                "Composer could not be updated. Full output is available in Logs/homebrew.log."
            )
        }
        guard let version = composerVersion(cycle: cycle) else {
            throw RuntimeInstallationError.commandFailed(
                "Composer was updated, but HerdMe could not read its version."
            )
        }
        return version
    }

    func latestLaravelInstallerVersion() async throws -> String {
        let indexURL = URL(string: "https://repo.packagist.org/p2/laravel/installer.json")!
        let (data, response) = try await URLSession.shared.data(from: indexURL)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw RuntimeInstallationError.invalidResponse
        }
        let index = try JSONDecoder().decode(PackagistPackageIndex.self, from: data)
        guard let release = index.packages["laravel/installer"]?.first(where: {
            Self.isStableVersion($0.version)
        }) else {
            throw RuntimeInstallationError.invalidResponse
        }
        return Self.normalizedVersion(release.version)
    }

    func ensureLaravelInstaller(cycle: String) async throws -> String {
        if let version = laravelInstallerVersion(cycle: cycle) { return version }
        return try await updateLaravelInstaller(cycle: cycle)
    }

    func prepareLaravelInstallerForProjectCreation(cycle: String) async throws {
        if isLaravelInstallerReadyForProjectCreation() { return }
        _ = try await ensureLaravelInstaller(cycle: cycle)
    }

    func isLaravelInstallerReadyForProjectCreation() -> Bool {
        let activePHP = rootURL.appendingPathComponent("bin/php")
        return fileManager.isExecutableFile(atPath: activePHP.path)
            && fileManager.isReadableFile(atPath: composerURL.path)
            && fileManager.isReadableFile(atPath: laravelInstallerURL.path)
    }

    func updateLaravelInstaller(cycle: String) async throws -> String {
        let php = rootURL.appendingPathComponent("Runtimes/php/\(cycle)/bin/php")
        guard fileManager.isExecutableFile(atPath: php.path) else {
            throw RuntimeInstallationError.runtimeNotInstalled(name: "PHP", cycle: cycle)
        }
        let composer = try await ensureComposer(php: php)
        let result = try run(
            php,
            arguments: [
                composer.path,
                "global", "require", "laravel/installer:^5",
                "--no-interaction", "--no-progress", "--no-ansi"
            ],
            environment: composerEnvironment
        )
        guard result.status == 0 else {
            throw RuntimeInstallationError.commandFailed(CommandFailureReporter.recordAndSummarize(
                result.output,
                operation: "composer update laravel/installer",
                rootURL: rootURL
            ))
        }
        guard let version = laravelInstallerVersion(cycle: cycle) else {
            throw RuntimeInstallationError.commandFailed("Laravel Installer was not available after Composer completed.")
        }
        return version
    }

    nonisolated static func phpRelativePath(for executable: String) -> String {
        executable == "php-fpm" ? "sbin/php-fpm" : "bin/" + executable
    }

    func removeNode(cycle: String) throws {
        let versions = installedNodeVersions().filter { $0.hasPrefix("\(cycle).") || $0 == cycle }
        guard !versions.isEmpty else {
            throw RuntimeInstallationError.runtimeNotInstalled(name: "Node.js", cycle: cycle)
        }
        let activeNode = rootURL.appendingPathComponent("bin/node").resolvingSymlinksInPath()
        let activeVersion = versions.first { activeNode.path.contains("/Runtimes/node/\($0)/") }

        for version in versions {
            let destination = rootURL.appendingPathComponent("Runtimes/node/\(version)", isDirectory: true)
            try fileManager.removeItem(at: destination)
        }
        if activeVersion != nil {
            for name in ["node", "npm", "npx", "corepack"] {
                let link = rootURL.appendingPathComponent("bin/\(name)")
                if fileManager.fileExists(atPath: link.path) || (try? link.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true {
                    try? fileManager.removeItem(at: link)
                }
            }
        }
    }

    nonisolated static func release(for cycle: String, archiveKey: String, in releases: [NodeRelease]) -> NodeRelease? {
        releases.first { release in
            release.version.hasPrefix("v\(cycle).") && release.files.contains(archiveKey)
        }
    }

    nonisolated static func nodeChecksum(for archiveName: String, in manifest: String) -> String? {
        for line in manifest.split(whereSeparator: \Character.isNewline) {
            let fields = line.split(whereSeparator: \Character.isWhitespace)
            guard fields.count == 2 else { continue }
            let listedName = fields[1].hasPrefix("*") ? fields[1].dropFirst() : fields[1][...]
            let checksum = String(fields[0]).lowercased()
            guard listedName == archiveName,
                  checksum.count == 64,
                  checksum.allSatisfy({ $0.isHexDigit }) else {
                continue
            }
            return checksum
        }
        return nil
    }

    nonisolated static func sha256(of data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    nonisolated static func phpVersions(
        fromHomebrewInfoOutput output: String,
        cycles: Set<String>
    ) throws -> [String: String] {
        guard let start = output.firstIndex(of: "{"),
              let end = output.lastIndex(of: "}") else {
            throw RuntimeInstallationError.invalidResponse
        }
        let payload = Data(output[start...end].utf8)
        let index = try JSONDecoder().decode(HomebrewFormulaIndex.self, from: payload)
        return Dictionary(uniqueKeysWithValues: index.formulae.compactMap { formula in
            guard formula.name.hasPrefix("php@") else { return nil }
            let cycle = String(formula.name.dropFirst("php@".count))
            guard cycles.contains(cycle), !formula.versions.stable.isEmpty else { return nil }
            return (cycle, formula.versions.stable)
        })
    }

    nonisolated static func isNewerVersion(_ candidate: String, than current: String) -> Bool {
        normalizedVersion(candidate).compare(normalizedVersion(current), options: .numeric) == .orderedDescending
    }

    nonisolated static func normalizedVersion(_ version: String) -> String {
        version.hasPrefix("v") ? String(version.dropFirst()) : version
    }

    nonisolated static func isStableVersion(_ version: String) -> Bool {
        version.range(of: #"^v?[0-9]+\.[0-9]+\.[0-9]+$"#, options: .regularExpression) != nil
    }

    nonisolated static func composerVersion(from output: String) -> String? {
        guard output.localizedCaseInsensitiveContains("composer"),
              let range = output.range(
                  of: #"[0-9]+\.[0-9]+\.[0-9]+"#,
                  options: .regularExpression
              ) else { return nil }
        return String(output[range])
    }

    nonisolated static func compatibleComposerVersion(
        from data: Data,
        phpVersionID: Int
    ) throws -> String {
        let index = try JSONDecoder().decode(ComposerReleaseIndex.self, from: data)
        guard let release = index.stable
            .filter({ $0.minimumPHP <= phpVersionID && isStableVersion($0.version) })
            .max(by: { isNewerVersion($1.version, than: $0.version) }) else {
            throw RuntimeInstallationError.invalidResponse
        }
        return normalizedVersion(release.version)
    }

    private func installedNodeVersions() -> [String] {
        let root = rootURL.appendingPathComponent("Runtimes/node", isDirectory: true)
        return ((try? fileManager.contentsOfDirectory(at: root, includingPropertiesForKeys: [.isDirectoryKey])) ?? [])
            .filter { !$0.lastPathComponent.hasPrefix(".") && fileManager.fileExists(atPath: $0.appendingPathComponent("bin/node").path) }
            .map(\.lastPathComponent)
            .sorted(by: >)
    }

    private func activateNode(version: String) throws {
        let runtimeBin = rootURL.appendingPathComponent("Runtimes/node/\(version)/bin", isDirectory: true)
        let managedBin = rootURL.appendingPathComponent("bin", isDirectory: true)
        try fileManager.createDirectory(at: managedBin, withIntermediateDirectories: true)

        for name in ["node", "npm", "npx", "corepack"] {
            let source = runtimeBin.appendingPathComponent(name)
            guard fileManager.fileExists(atPath: source.path) else { continue }
            let link = managedBin.appendingPathComponent(name)
            if fileManager.fileExists(atPath: link.path) || (try? link.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true {
                try fileManager.removeItem(at: link)
            }
            try fileManager.createSymbolicLink(at: link, withDestinationURL: source)
        }
    }

    private var composerURL: URL {
        rootURL.appendingPathComponent("bin/composer")
    }

    private var laravelInstallerURL: URL {
        rootURL.appendingPathComponent("Composer/vendor/bin/laravel")
    }

    private var composerEnvironment: [String: String] {
        var environment = ProcessInfo.processInfo.environment
        let temporaryDirectory = rootURL.appendingPathComponent("Cache/tmp", isDirectory: true)
        try? fileManager.createDirectory(at: temporaryDirectory, withIntermediateDirectories: true)
        environment["COMPOSER_HOME"] = rootURL.appendingPathComponent("Composer", isDirectory: true).path
        environment["COMPOSER_CACHE_DIR"] = rootURL.appendingPathComponent("Cache/composer", isDirectory: true).path
        environment["TMPDIR"] = temporaryDirectory.path + "/"
        environment["TMP"] = temporaryDirectory.path
        environment["TEMP"] = temporaryDirectory.path
        environment["PATH"] = [
            rootURL.appendingPathComponent("bin", isDirectory: true).path,
            "/usr/bin", "/bin", "/usr/sbin", "/sbin"
        ].joined(separator: ":")
        return environment
    }

    private func ensureComposer(php: URL) async throws -> URL {
        if fileManager.isReadableFile(atPath: composerURL.path),
           let result = try? run(
               php,
               arguments: [composerURL.path, "--version", "--no-ansi"],
               environment: composerEnvironment
           ),
           result.status == 0 {
            return composerURL
        }

        let installerURL = URL(string: "https://getcomposer.org/installer")!
        let signatureURL = URL(string: "https://composer.github.io/installer.sig")!
        async let installerRequest = URLSession.shared.data(from: installerURL)
        async let signatureRequest = URLSession.shared.data(from: signatureURL)
        let ((installerData, installerResponse), (signatureData, signatureResponse)) = try await (
            installerRequest,
            signatureRequest
        )
        guard (installerResponse as? HTTPURLResponse)?.statusCode == 200,
              (signatureResponse as? HTTPURLResponse)?.statusCode == 200,
              let expectedSignature = String(data: signatureData, encoding: .utf8)?
                .trimmingCharacters(in: .whitespacesAndNewlines),
              !expectedSignature.isEmpty else {
            throw RuntimeInstallationError.invalidResponse
        }
        let actualSignature = SHA384.hash(data: installerData)
            .map { String(format: "%02x", $0) }
            .joined()
        guard actualSignature.caseInsensitiveCompare(expectedSignature) == .orderedSame else {
            throw RuntimeInstallationError.integrityCheckFailed(component: "Composer installer")
        }

        let managedBin = rootURL.appendingPathComponent("bin", isDirectory: true)
        try fileManager.createDirectory(at: managedBin, withIntermediateDirectories: true)
        try fileManager.createDirectory(
            at: rootURL.appendingPathComponent("Composer", isDirectory: true),
            withIntermediateDirectories: true
        )
        try fileManager.createDirectory(
            at: rootURL.appendingPathComponent("Cache/composer", isDirectory: true),
            withIntermediateDirectories: true
        )
        let staging = rootURL.appendingPathComponent("Cache/composer-installer-\(UUID().uuidString).php")
        try installerData.write(to: staging, options: .atomic)
        defer { try? fileManager.removeItem(at: staging) }

        let install = try run(
            php,
            arguments: [
                staging.path,
                "--install-dir=\(managedBin.path)",
                "--filename=composer",
                "--quiet"
            ],
            environment: composerEnvironment
        )
        guard install.status == 0, fileManager.isReadableFile(atPath: composerURL.path) else {
            throw RuntimeInstallationError.commandFailed(CommandFailureReporter.recordAndSummarize(
                install.output,
                operation: "install Composer",
                rootURL: rootURL
            ))
        }
        try fileManager.setAttributes([.posixPermissions: 0o755], ofItemAtPath: composerURL.path)
        return composerURL
    }

    private func nodeArchiveKey() throws -> String {
#if arch(arm64)
        return "osx-arm64-tar"
#elseif arch(x86_64)
        return "osx-x64-tar"
#else
        throw RuntimeInstallationError.unsupportedArchitecture
#endif
    }

    private nonisolated static func sha256(of fileURL: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: fileURL)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 1_048_576), !chunk.isEmpty {
            hasher.update(data: chunk)
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    private func brewURL() -> URL? {
        ["/opt/homebrew/bin/brew", "/usr/local/bin/brew"]
            .map { URL(fileURLWithPath: $0) }
            .first { fileManager.isExecutableFile(atPath: $0.path) }
    }

    private var homebrewEnvironment: [String: String] {
        var environment = ProcessInfo.processInfo.environment
        let home = fileManager.homeDirectoryForCurrentUser.path
        environment["HOME"] = home
        environment["USER"] = NSUserName()
        environment["LOGNAME"] = NSUserName()
        environment["PATH"] = [
            "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin"
        ].joined(separator: ":")
        return environment
    }

    private func run(
        _ executable: URL,
        arguments: [String],
        environment: [String: String]? = nil
    ) throws -> (status: Int32, output: String) {
        let result = try ProcessRunner.run(
            executable,
            arguments: arguments,
            environment: environment
        )
        return (result.status, result.output)
    }

    private func unpack(archive: URL, into destination: URL) throws {
        let result = try ProcessRunner.run(
            URL(fileURLWithPath: "/usr/bin/tar"),
            arguments: ["-xzf", archive.path, "-C", destination.path, "--strip-components", "1"],
            timeout: 2 * 60
        )
        guard result.status == 0 else {
            throw RuntimeInstallationError.archiveFailed(result.output)
        }
    }
}
