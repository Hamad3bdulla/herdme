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

enum PHPRuntimeSupport {
    static let installableCycles = RuntimeCatalog.installablePHPCycles

    static func isInstallable(_ cycle: String) -> Bool {
        installableCycles.contains(cycle)
    }
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
        try? LogRotation.rotateIfNeeded(logURL)
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

enum RuntimeInstallationError: LocalizedError {
    case unsupportedArchitecture
    case unsupportedPHPCycle(String)
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
            String(localized: "This Mac architecture is not supported by the selected runtime.")
        case .unsupportedPHPCycle(let cycle):
            String.localizedStringWithFormat(
                String(localized: "PHP %@ is not available for new HerdMe installations. Install PHP 8.0 through 8.5 instead."),
                cycle
            )
        case .releaseNotFound(let cycle):
            String.localizedStringWithFormat(
                String(localized: "No compatible Node.js %@ release was found."),
                cycle
            )
        case .invalidResponse:
            String(localized: "The runtime server returned an invalid response.")
        case .archiveFailed(let output):
            output.isEmpty ? String(localized: "The downloaded runtime could not be unpacked.") : output
        case .runtimeNotInstalled(let name, let cycle):
            String.localizedStringWithFormat(
                String(localized: "%1$@ %2$@ is not installed by HerdMe."),
                name,
                cycle
            )
        case .packageManagerMissing:
            String(localized: "Homebrew is required to install PHP on macOS.")
        case .integrityCheckFailed(let component):
            String.localizedStringWithFormat(
                String(localized: "%@ did not match its official checksum."),
                component
            )
        case .commandFailed(let output):
            output.isEmpty ? String(localized: "The runtime package manager failed.") : output
        }
    }
}

protocol RuntimeInstalling: Sendable {
    func activatePHP(cycle: String) async throws
    func installPHP(cycle: String) async throws -> String
    func installNode(cycle: String) async throws -> String
    func activateNode(cycle: String) async throws
    func removeNode(cycle: String) async throws
    func composerVersion(cycle: String) async -> String?
    func latestComposerVersion(cycle: String) async throws -> String
    func updateComposer(cycle: String) async throws -> String
    func laravelInstallerVersion(cycle: String) async -> String?
    func latestLaravelInstallerVersion() async throws -> String
    func updateLaravelInstaller(cycle: String) async throws -> String
    func prepareLaravelInstallerForProjectCreation(cycle: String) async throws
    func latestPHPVersions(cycles: [String]) async throws -> [String: String]
    func latestNodeVersions(cycles: [String]) async throws -> [String: String]
}

actor RuntimeInstaller: RuntimeInstalling {
    let rootURL: URL
    private let fileManager = FileManager.default

    init(rootURL: URL) {
        self.rootURL = rootURL
    }

    nonisolated static func officialURL(_ value: String, expectedHost: String) throws -> URL {
        guard let url = URL(string: value),
            url.scheme == "https",
            url.host?.lowercased() == expectedHost.lowercased(),
            url.port == nil || url.port == 443,
            url.user == nil,
            url.password == nil
        else {
            throw RuntimeInstallationError.invalidResponse
        }
        return url
    }

    func installNode(cycle: String) async throws -> String {
        let archiveKey = try nodeArchiveKey()
        let indexURL = try Self.officialURL(
            "https://nodejs.org/dist/index.json",
            expectedHost: "nodejs.org"
        )
        let (indexData, response) = try await ManagedDownloadClient.data(from: indexURL)
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
            let downloadURL = try Self.officialURL(
                "https://nodejs.org/dist/\(release.version)/\(archiveName)",
                expectedHost: "nodejs.org"
            )
            let checksumsURL = try Self.officialURL(
                "https://nodejs.org/dist/\(release.version)/SHASUMS256.txt",
                expectedHost: "nodejs.org"
            )
            async let archiveRequest = ManagedDownloadClient.download(from: downloadURL)
            async let checksumsRequest = ManagedDownloadClient.data(from: checksumsURL)
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
                )
            else {
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
        let indexURL = try Self.officialURL(
            "https://nodejs.org/dist/index.json",
            expectedHost: "nodejs.org"
        )
        let (indexData, response) = try await ManagedDownloadClient.data(from: indexURL)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw RuntimeInstallationError.invalidResponse
        }
        let releases = try JSONDecoder().decode([NodeRelease].self, from: indexData)
        return Dictionary(
            uniqueKeysWithValues: Set(cycles).compactMap { cycle in
                guard let release = Self.release(for: cycle, archiveKey: archiveKey, in: releases) else {
                    return nil
                }
                return (cycle, Self.normalizedVersion(release.version))
            })
    }

    func latestPHPVersions(cycles: [String]) throws -> [String: String] {
        let requestedCycles = Set(cycles)
        guard !requestedCycles.isEmpty else { return [:] }
        if let unsupportedCycle = requestedCycles.first(where: { !PHPRuntimeSupport.isInstallable($0) }) {
            throw RuntimeInstallationError.unsupportedPHPCycle(unsupportedCycle)
        }
        guard let homebrew = HomebrewCLI(fileManager: fileManager) else {
            throw RuntimeInstallationError.packageManagerMissing
        }
        let formulae = requestedCycles.sorted().map { "php@\($0)" }
        let result = try homebrew.run(arguments: ["info", "--json=v2"] + formulae)
        guard result.status == 0 else {
            throw RuntimeInstallationError.commandFailed(
                CommandFailureReporter.recordAndSummarize(
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
        guard PHPRuntimeSupport.isInstallable(cycle) else {
            throw RuntimeInstallationError.unsupportedPHPCycle(cycle)
        }
        guard let homebrew = HomebrewCLI(fileManager: fileManager) else {
            throw RuntimeInstallationError.packageManagerMissing
        }
        let formula = "php@\(cycle)"
        let managedExecutable = rootURL.appendingPathComponent("Runtimes/php/\(cycle)/bin/php")
        let command = fileManager.isExecutableFile(atPath: managedExecutable.path) ? "upgrade" : "install"
        var install = try homebrew.run(arguments: [command, formula])
        if install.status != 0,
            let trustTarget = Self.phpFormulaTrustTarget(from: install.output, cycle: cycle)
        {
            let trust = try homebrew.run(arguments: ["trust", "--formula", trustTarget])
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
            install = try homebrew.run(arguments: [command, formula])
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
        let prefix = try homebrew.run(arguments: ["--prefix", formula])
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
            isDirectory.boolValue
        else {
            throw RuntimeInstallationError.commandFailed("Homebrew did not return a valid PHP runtime path.")
        }
        let runtimeRoot = rootURL.appendingPathComponent("Runtimes/php", isDirectory: true)
        try fileManager.createDirectory(at: runtimeRoot, withIntermediateDirectories: true)
        let destination = runtimeRoot.appendingPathComponent(cycle, isDirectory: true)
        if fileManager.fileExists(atPath: destination.path)
            || (try? destination.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true
        {
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
        guard PHPRuntimeSupport.isInstallable(cycle) else {
            return nil
        }
        let expectedFormula = "shivammathur/php/php@\(cycle)"
        guard
            let target = HomebrewCLI.formulaTrustTarget(
                from: output,
                expectedFormula: expectedFormula
            ), target == expectedFormula
        else { return nil }
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
            if fileManager.fileExists(atPath: link.path) || (try? link.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true
            {
                try fileManager.removeItem(at: link)
            }
            try fileManager.createSymbolicLink(at: link, withDestinationURL: candidate)
        }
        try repairManagedToolLaunchers()
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
            result.status == 0
        else {
            return nil
        }
        let output = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
        guard
            let range = output.range(
                of: "[0-9]+\\.[0-9]+\\.[0-9]+",
                options: .regularExpression
            )
        else {
            return nil
        }
        return String(output[range])
    }

    func composerVersion(cycle: String) -> String? {
        let php = rootURL.appendingPathComponent("Runtimes/php/\(cycle)/bin/php")
        try? repairManagedToolLaunchers()
        guard fileManager.isExecutableFile(atPath: php.path),
            fileManager.isReadableFile(atPath: composerPHARURL.path),
            let result = try? run(
                php,
                arguments: [composerPHARURL.path, "--version", "--no-ansi"],
                environment: composerEnvironment
            ),
            result.status == 0
        else {
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
            let phpVersionID = Int(versionID.output.trimmingCharacters(in: .whitespacesAndNewlines))
        else {
            throw RuntimeInstallationError.invalidResponse
        }
        let indexURL = try Self.officialURL(
            "https://getcomposer.org/versions",
            expectedHost: "getcomposer.org"
        )
        let (data, response) = try await ManagedDownloadClient.data(from: indexURL)
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
        try repairManagedToolLaunchers()
        guard let version = composerVersion(cycle: cycle) else {
            throw RuntimeInstallationError.commandFailed(
                "Composer was updated, but HerdMe could not read its version."
            )
        }
        return version
    }

    func latestLaravelInstallerVersion() async throws -> String {
        let indexURL = try Self.officialURL(
            "https://repo.packagist.org/p2/laravel/installer.json",
            expectedHost: "repo.packagist.org"
        )
        let (data, response) = try await ManagedDownloadClient.data(from: indexURL)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw RuntimeInstallationError.invalidResponse
        }
        let index = try JSONDecoder().decode(PackagistPackageIndex.self, from: data)
        guard
            let release = index.packages["laravel/installer"]?.first(where: {
                Self.isStableVersion($0.version)
            })
        else {
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
        try? repairManagedToolLaunchers()
        return fileManager.isExecutableFile(atPath: activePHP.path)
            && fileManager.isReadableFile(atPath: composerPHARURL.path)
            && fileManager.isExecutableFile(atPath: composerLauncherURL.path)
            && fileManager.isReadableFile(atPath: laravelInstallerURL.path)
            && fileManager.isExecutableFile(atPath: laravelLauncherURL.path)
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
            throw RuntimeInstallationError.commandFailed(
                CommandFailureReporter.recordAndSummarize(
                    result.output,
                    operation: "composer update laravel/installer",
                    rootURL: rootURL
                ))
        }
        try repairManagedToolLaunchers()
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
                if fileManager.fileExists(atPath: link.path)
                    || (try? link.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true
                {
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
                checksum.allSatisfy({ $0.isHexDigit })
            else {
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
            let end = output.lastIndex(of: "}")
        else {
            throw RuntimeInstallationError.invalidResponse
        }
        let payload = Data(output[start...end].utf8)
        let index = try JSONDecoder().decode(HomebrewFormulaIndex.self, from: payload)
        return Dictionary(
            uniqueKeysWithValues: index.formulae.compactMap { formula in
                guard formula.name.hasPrefix("php@") else { return nil }
                let cycle = String(formula.name.dropFirst("php@".count))
                guard cycles.contains(cycle), !formula.versions.stable.isEmpty else { return nil }
                return (cycle, formula.versions.stable)
            })
    }

    nonisolated static func isNewerVersion(_ candidate: String, than current: String) -> Bool {
        VersionComparison.isNewer(candidate, than: current)
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
            )
        else { return nil }
        return String(output[range])
    }

    nonisolated static func compatibleComposerVersion(
        from data: Data,
        phpVersionID: Int
    ) throws -> String {
        let index = try JSONDecoder().decode(ComposerReleaseIndex.self, from: data)
        guard
            let release = index.stable
                .filter({ $0.minimumPHP <= phpVersionID && isStableVersion($0.version) })
                .max(by: { isNewerVersion($1.version, than: $0.version) })
        else {
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
            if fileManager.fileExists(atPath: link.path) || (try? link.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true
            {
                try fileManager.removeItem(at: link)
            }
            try fileManager.createSymbolicLink(at: link, withDestinationURL: source)
        }
    }

    private var composerLauncherURL: URL {
        rootURL.appendingPathComponent("bin/composer")
    }

    private var composerPHARURL: URL {
        rootURL.appendingPathComponent("Composer/composer.phar")
    }

    private var laravelInstallerURL: URL {
        rootURL.appendingPathComponent("Composer/vendor/bin/laravel")
    }

    private var laravelLauncherURL: URL {
        rootURL.appendingPathComponent("bin/laravel")
    }

    private var composerEnvironment: [String: String] {
        var environment = ProcessInfo.processInfo.environment
        for key in environment.keys.filter({ $0.hasPrefix("HERD_") }) {
            environment.removeValue(forKey: key)
        }
        environment.removeValue(forKey: "PHPRC")
        environment.removeValue(forKey: "PHP_INI_SCAN_DIR")
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
        try repairManagedToolLaunchers()
        if fileManager.isReadableFile(atPath: composerPHARURL.path),
            let result = try? run(
                php,
                arguments: [composerPHARURL.path, "--version", "--no-ansi"],
                environment: composerEnvironment
            ),
            result.status == 0
        {
            return composerPHARURL
        }

        let installerURL = try Self.officialURL(
            "https://getcomposer.org/installer",
            expectedHost: "getcomposer.org"
        )
        let signatureURL = try Self.officialURL(
            "https://composer.github.io/installer.sig",
            expectedHost: "composer.github.io"
        )
        async let installerRequest = ManagedDownloadClient.data(from: installerURL)
        async let signatureRequest = ManagedDownloadClient.data(from: signatureURL)
        let ((installerData, installerResponse), (signatureData, signatureResponse)) = try await (
            installerRequest,
            signatureRequest
        )
        guard (installerResponse as? HTTPURLResponse)?.statusCode == 200,
            (signatureResponse as? HTTPURLResponse)?.statusCode == 200,
            let expectedSignature = String(data: signatureData, encoding: .utf8)?
                .trimmingCharacters(in: .whitespacesAndNewlines),
            !expectedSignature.isEmpty
        else {
            throw RuntimeInstallationError.invalidResponse
        }
        let actualSignature = SHA384.hash(data: installerData)
            .map { String(format: "%02x", $0) }
            .joined()
        guard actualSignature.caseInsensitiveCompare(expectedSignature) == .orderedSame else {
            throw RuntimeInstallationError.integrityCheckFailed(component: "Composer installer")
        }

        let composerDirectory = rootURL.appendingPathComponent("Composer", isDirectory: true)
        try fileManager.createDirectory(at: composerDirectory, withIntermediateDirectories: true)
        try fileManager.createDirectory(
            at: rootURL.appendingPathComponent("Cache/composer", isDirectory: true),
            withIntermediateDirectories: true
        )
        let staging = rootURL.appendingPathComponent("Cache/composer-installer-\(UUID().uuidString).php")
        let installationDirectory = rootURL.appendingPathComponent(
            "Cache/composer-install-\(UUID().uuidString)",
            isDirectory: true
        )
        try fileManager.createDirectory(at: installationDirectory, withIntermediateDirectories: true)
        try installerData.write(to: staging, options: .atomic)
        defer {
            try? fileManager.removeItem(at: staging)
            try? fileManager.removeItem(at: installationDirectory)
        }

        let install = try run(
            php,
            arguments: [
                staging.path,
                "--install-dir=\(installationDirectory.path)",
                "--filename=composer.phar",
                "--quiet"
            ],
            environment: composerEnvironment
        )
        let installedPHAR = installationDirectory.appendingPathComponent("composer.phar")
        guard install.status == 0, fileManager.isReadableFile(atPath: installedPHAR.path) else {
            throw RuntimeInstallationError.commandFailed(
                CommandFailureReporter.recordAndSummarize(
                    install.output,
                    operation: "install Composer",
                    rootURL: rootURL
                ))
        }
        let verification = try run(
            php,
            arguments: [installedPHAR.path, "--version", "--no-ansi"],
            environment: composerEnvironment
        )
        guard verification.status == 0, Self.composerVersion(from: verification.output) != nil else {
            throw RuntimeInstallationError.commandFailed(
                "Composer was downloaded, but the managed PHP runtime could not execute it."
            )
        }
        if fileManager.fileExists(atPath: composerPHARURL.path) {
            _ = try fileManager.replaceItemAt(composerPHARURL, withItemAt: installedPHAR)
        } else {
            try fileManager.moveItem(at: installedPHAR, to: composerPHARURL)
        }
        try fileManager.setAttributes([.posixPermissions: 0o644], ofItemAtPath: composerPHARURL.path)
        try repairManagedToolLaunchers()
        return composerPHARURL
    }

    func repairManagedToolLaunchers() throws {
        let managedBin = rootURL.appendingPathComponent("bin", isDirectory: true)
        let composerDirectory = rootURL.appendingPathComponent("Composer", isDirectory: true)
        try fileManager.createDirectory(at: managedBin, withIntermediateDirectories: true)
        try fileManager.createDirectory(at: composerDirectory, withIntermediateDirectories: true)
        try migrateLegacyComposerPHARIfNeeded()

        if fileManager.isReadableFile(atPath: composerPHARURL.path) {
            try writeManagedLauncher(
                Self.managedToolLauncher(target: "Composer/composer.phar"),
                to: composerLauncherURL
            )
        }
        if fileManager.isReadableFile(atPath: laravelInstallerURL.path) {
            try writeManagedLauncher(
                Self.managedToolLauncher(target: "Composer/vendor/bin/laravel"),
                to: laravelLauncherURL
            )
        }
    }

    nonisolated static func managedToolLauncher(target: String) -> String {
        """
        #!/bin/sh
        set -eu

        HERDME_BIN=$(CDPATH= cd "$(dirname "$0")" && pwd -P)
        HERDME_ROOT=$(CDPATH= cd "$HERDME_BIN/.." && pwd -P)
        export COMPOSER_HOME="$HERDME_ROOT/Composer"
        export COMPOSER_CACHE_DIR="$HERDME_ROOT/Cache/composer"
        export PATH="$HERDME_BIN:${PATH:-/usr/bin:/bin:/usr/sbin:/sbin}"
        unset PHPRC PHP_INI_SCAN_DIR
        exec "$HERDME_BIN/php" "$HERDME_ROOT/\(target)" "$@"

        """
    }

    private func migrateLegacyComposerPHARIfNeeded() throws {
        guard !fileManager.fileExists(atPath: composerPHARURL.path),
            fileManager.isReadableFile(atPath: composerLauncherURL.path),
            let handle = try? FileHandle(forReadingFrom: composerLauncherURL)
        else { return }
        defer { try? handle.close() }
        let prefix = try handle.read(upToCount: 64) ?? Data()
        guard Self.isComposerPHARPrefix(prefix) else { return }
        try fileManager.moveItem(at: composerLauncherURL, to: composerPHARURL)
        try fileManager.setAttributes([.posixPermissions: 0o644], ofItemAtPath: composerPHARURL.path)
    }

    nonisolated static func isComposerPHARPrefix(_ data: Data) -> Bool {
        guard let prefix = String(data: data, encoding: .utf8) else { return false }
        return prefix.hasPrefix("#!/usr/bin/env php\n<?php") || prefix.hasPrefix("<?php")
    }

    private func writeManagedLauncher(_ contents: String, to url: URL) throws {
        try Data(contents.utf8).write(to: url, options: .atomic)
        try fileManager.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
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

    private func run(
        _ executable: URL,
        arguments: [String],
        environment: [String: String]? = nil
    ) throws -> (status: Int32, output: String) {
        let result = try ProcessRunner.run(
            executable,
            arguments: arguments,
            environment: environment,
            cancellationRequested: { Task<Never, Never>.isCancelled }
        )
        return (result.status, result.output)
    }

    private func unpack(archive: URL, into destination: URL) throws {
        let tar = URL(fileURLWithPath: "/usr/bin/tar")
        let names = try ProcessRunner.run(
            tar,
            arguments: ["-tzf", archive.path],
            timeout: 2 * 60,
            cancellationRequested: { Task<Never, Never>.isCancelled }
        )
        guard names.status == 0 else { throw RuntimeInstallationError.archiveFailed(names.output) }
        let details = try ProcessRunner.run(
            tar,
            arguments: ["-tvzf", archive.path],
            timeout: 2 * 60,
            cancellationRequested: { Task<Never, Never>.isCancelled }
        )
        guard details.status == 0 else { throw RuntimeInstallationError.archiveFailed(details.output) }
        try TarArchivePolicy.validate(nameListing: names.output, verboseListing: details.output)

        let result = try ProcessRunner.run(
            tar,
            arguments: [
                "-xzf", archive.path, "-C", destination.path, "--strip-components", "1",
                "--no-same-owner", "--no-same-permissions"
            ],
            timeout: 2 * 60,
            cancellationRequested: { Task<Never, Never>.isCancelled }
        )
        guard result.status == 0 else {
            throw RuntimeInstallationError.archiveFailed(result.output)
        }
        try TarArchivePolicy.validateExtractedTree(at: destination)
    }
}
