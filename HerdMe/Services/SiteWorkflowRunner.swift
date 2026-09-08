import Foundation

enum SiteWorkflowOperation: String, CaseIterable, Identifiable, Sendable {
    case update
    case clean
    case reset
    case export
    case configureMail
    case audit
    case repair

    var id: String { rawValue }

    var title: String {
        switch self {
        case .update: String(localized: "Update Laravel Project")
        case .clean: String(localized: "Clean and Rebuild")
        case .reset: String(localized: "Reset Local Project")
        case .export: String(localized: "Export Transferable Package")
        case .configureMail: String(localized: "Configure Local Mail")
        case .audit: String(localized: "Run Handoff Audit")
        case .repair: String(localized: "Repair Site")
        }
    }

    var systemImage: String {
        switch self {
        case .update: "arrow.triangle.2.circlepath"
        case .clean: "sparkles"
        case .reset: "arrow.counterclockwise"
        case .export: "archivebox"
        case .configureMail: "envelope"
        case .audit: "checkmark.shield"
        case .repair: "wrench.and.screwdriver"
        }
    }

    var isDestructive: Bool { self == .reset }
}

struct SiteWorkflowResult: Sendable {
    let title: String
    let output: String
    let artifactURL: URL?
}

struct SiteToolInvocation: Sendable {
    let executable: URL
    let arguments: [String]
    let projectDirectory: URL
    let environment: [String: String]
    let timeout: TimeInterval
}

final class SiteOperationCancellation: @unchecked Sendable {
    private let lock = NSLock()
    private var cancelled = false

    var isCancelled: Bool {
        lock.lock()
        defer { lock.unlock() }
        return cancelled
    }

    func cancel() {
        lock.lock()
        cancelled = true
        lock.unlock()
    }
}

enum SiteCommandRunner {
    private static let processQueue = DispatchQueue(
        label: "app.herdme.site-command-runner",
        qos: .userInitiated,
        attributes: .concurrent
    )

    static func run(
        _ invocation: SiteToolInvocation,
        cancellation: SiteOperationCancellation,
        outputReceived: @escaping @Sendable (Data) -> Void = { _ in }
    ) async throws -> ProcessResult {
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                processQueue.async {
                    continuation.resume(
                        with: Result {
                            try ProcessRunner.run(
                                invocation.executable,
                                arguments: invocation.arguments,
                                currentDirectory: invocation.projectDirectory,
                                environment: invocation.environment,
                                timeout: invocation.timeout,
                                cancellationRequested: { cancellation.isCancelled },
                                outputReceived: outputReceived
                            )
                        }
                    )
                }
            }
        } onCancel: {
            cancellation.cancel()
        }
    }
}

struct SiteToolchain: @unchecked Sendable {
    let rootURL: URL
    let fileManager: FileManager

    init(rootURL: URL, fileManager: FileManager = .default) {
        self.rootURL = rootURL
        self.fileManager = fileManager
    }

    func artisan(site: SiteProject, defaultPHP: String, arguments: [String], timeout: TimeInterval = 30 * 60) throws -> SiteToolInvocation {
        let artisan = site.path.appendingPathComponent("artisan")
        guard fileManager.isReadableFile(atPath: artisan.path) else { throw SiteWorkflowError.artisanMissing }
        return SiteToolInvocation(
            executable: try php(site: site, defaultPHP: defaultPHP),
            arguments: [artisan.path] + arguments,
            projectDirectory: site.path,
            environment: phpEnvironment,
            timeout: timeout
        )
    }

    func composer(site: SiteProject, defaultPHP: String, arguments: [String], timeout: TimeInterval = 45 * 60) throws -> SiteToolInvocation
    {
        let composer = rootURL.appendingPathComponent("Composer/composer.phar")
        guard fileManager.isReadableFile(atPath: composer.path) else { throw SiteWorkflowError.composerMissing }
        return SiteToolInvocation(
            executable: try php(site: site, defaultPHP: defaultPHP),
            arguments: [composer.path] + arguments,
            projectDirectory: site.path,
            environment: phpEnvironment,
            timeout: timeout
        )
    }

    func npm(site: SiteProject, arguments: [String], timeout: TimeInterval = 45 * 60) throws -> SiteToolInvocation {
        let runtimes = RuntimeInspector(managedRoot: rootURL).nodeVersions()
        let runtime: RuntimeVersion?
        if let cycle = site.nodeVersion {
            runtime = runtimes.first { $0.cycle == cycle && $0.isInstalled }
        } else {
            runtime = runtimes.first(where: \.isActive) ?? runtimes.first(where: \.isInstalled)
        }
        guard let runtime, let version = runtime.installedVersion else { throw SiteWorkflowError.nodeMissing }
        let runtimeDirectory = rootURL.appendingPathComponent("Runtimes/node/\(version)", isDirectory: true)
        let bin = runtimeDirectory.appendingPathComponent("bin", isDirectory: true)
        let node = bin.appendingPathComponent("node")
        let npmCLI = runtimeDirectory.appendingPathComponent("lib/node_modules/npm/bin/npm-cli.js")
        guard fileManager.isExecutableFile(atPath: node.path), fileManager.isReadableFile(atPath: npmCLI.path) else {
            throw SiteWorkflowError.nodeMissing
        }
        var environment = baseEnvironment
        environment["PATH"] = [bin.path, rootURL.appendingPathComponent("bin").path, "/usr/bin", "/bin", "/usr/sbin", "/sbin"].joined(
            separator: ":")
        environment["npm_config_cache"] = rootURL.appendingPathComponent("Cache/npm", isDirectory: true).path
        environment["npm_config_update_notifier"] = "false"
        return SiteToolInvocation(
            executable: node,
            arguments: [npmCLI.path] + arguments,
            projectDirectory: site.path,
            environment: environment,
            timeout: timeout
        )
    }

    private func php(site: SiteProject, defaultPHP: String) throws -> URL {
        let cycle = site.phpVersion ?? defaultPHP
        let executable = rootURL.appendingPathComponent("Runtimes/php/\(cycle)/bin/php")
        guard fileManager.isExecutableFile(atPath: executable.path) else {
            throw RuntimeInstallationError.runtimeNotInstalled(name: "PHP", cycle: cycle)
        }
        return executable
    }

    private var baseEnvironment: [String: String] {
        var environment = ProcessInfo.processInfo.environment
        for key in environment.keys where key.hasPrefix("HERD_") { environment.removeValue(forKey: key) }
        environment.removeValue(forKey: "PHPRC")
        environment.removeValue(forKey: "PHP_INI_SCAN_DIR")
        environment["NO_COLOR"] = "1"
        return environment
    }

    private var phpEnvironment: [String: String] {
        var environment = baseEnvironment
        environment["COMPOSER_HOME"] = rootURL.appendingPathComponent("Composer", isDirectory: true).path
        environment["COMPOSER_CACHE_DIR"] = rootURL.appendingPathComponent("Cache/composer", isDirectory: true).path
        environment["PATH"] = [rootURL.appendingPathComponent("bin").path, "/usr/bin", "/bin", "/usr/sbin", "/sbin"].joined(separator: ":")
        return environment
    }
}

struct SiteWorkflowRunner: @unchecked Sendable {
    let rootURL: URL
    let fileManager: FileManager

    init(rootURL: URL, fileManager: FileManager = .default) {
        self.rootURL = rootURL
        self.fileManager = fileManager
    }

    func run(
        _ operation: SiteWorkflowOperation,
        site: SiteProject,
        defaultPHP: String,
        smtpPort: Int,
        cancellation: SiteOperationCancellation,
        progress: @escaping @Sendable (String) -> Void
    ) async throws -> SiteWorkflowResult {
        try Task.checkCancellation()
        let tools = SiteToolchain(rootURL: rootURL, fileManager: fileManager)
        switch operation {
        case .update:
            let backup = try await archive(site: site, label: "before-update", cancellation: cancellation, progress: progress)
            if fileManager.fileExists(atPath: site.path.appendingPathComponent("composer.json").path) {
                try await execute(
                    try tools.composer(
                        site: site, defaultPHP: defaultPHP, arguments: ["update", "--no-interaction", "--with-all-dependencies"]),
                    name: "composer update", cancellation: cancellation, progress: progress)
            }
            if fileManager.fileExists(atPath: site.path.appendingPathComponent("package.json").path) {
                try await execute(
                    try tools.npm(site: site, arguments: ["update", "--no-audit", "--no-fund"]), name: "npm update",
                    cancellation: cancellation, progress: progress)
                if hasNPMScript("build", at: site.path) {
                    try await execute(
                        try tools.npm(site: site, arguments: ["run", "build"]), name: "npm run build", cancellation: cancellation,
                        progress: progress)
                }
            }
            if fileManager.isReadableFile(atPath: site.path.appendingPathComponent("artisan").path) {
                try await execute(
                    try tools.artisan(site: site, defaultPHP: defaultPHP, arguments: ["migrate", "--force", "--no-interaction"]),
                    name: "artisan migrate", cancellation: cancellation, progress: progress)
                try await execute(
                    try tools.artisan(site: site, defaultPHP: defaultPHP, arguments: ["optimize:clear", "--no-interaction"]),
                    name: "artisan optimize:clear", cancellation: cancellation, progress: progress)
            }
            return SiteWorkflowResult(
                title: operation.title, output: "Project updated successfully.\nBackup: \(backup.path)", artifactURL: backup)

        case .clean:
            let backup = try await archive(site: site, label: "before-clean", cancellation: cancellation, progress: progress)
            try await cleanAndRebuild(site: site, defaultPHP: defaultPHP, tools: tools, cancellation: cancellation, progress: progress)
            return SiteWorkflowResult(
                title: operation.title, output: "Project dependencies were rebuilt.\nBackup: \(backup.path)", artifactURL: backup)

        case .reset:
            let backup = try await archive(site: site, label: "before-reset", cancellation: cancellation, progress: progress)
            try await execute(
                try tools.artisan(
                    site: site, defaultPHP: defaultPHP, arguments: ["migrate:fresh", "--seed", "--force", "--no-interaction"]),
                name: "artisan migrate:fresh", cancellation: cancellation, progress: progress)
            try await execute(
                try tools.artisan(site: site, defaultPHP: defaultPHP, arguments: ["optimize:clear", "--no-interaction"]),
                name: "artisan optimize:clear", cancellation: cancellation, progress: progress)
            return SiteWorkflowResult(
                title: operation.title, output: "The local database was reset and seeded.\nBackup: \(backup.path)", artifactURL: backup)

        case .export:
            let archiveURL = try await archive(site: site, label: "export", cancellation: cancellation, progress: progress)
            return SiteWorkflowResult(
                title: operation.title, output: "Transferable package created at:\n\(archiveURL.path)", artifactURL: archiveURL)

        case .configureMail:
            let environmentURL = try configureMail(site: site, smtpPort: smtpPort)
            progress("[OK] Updated \(environmentURL.path)\n")
            return SiteWorkflowResult(
                title: operation.title, output: "Local mail capture is configured in .env.", artifactURL: environmentURL)

        case .audit:
            var report: [String] = []
            if fileManager.fileExists(atPath: site.path.appendingPathComponent("composer.json").path) {
                report.append(
                    try await audit(
                        try tools.composer(site: site, defaultPHP: defaultPHP, arguments: ["validate", "--no-interaction"]),
                        name: "composer validate", cancellation: cancellation, progress: progress))
                report.append(
                    try await audit(
                        try tools.composer(site: site, defaultPHP: defaultPHP, arguments: ["audit", "--no-interaction"]),
                        name: "composer audit", cancellation: cancellation, progress: progress))
            }
            if fileManager.fileExists(atPath: site.path.appendingPathComponent("package.json").path) {
                report.append(
                    try await audit(
                        try tools.npm(site: site, arguments: ["audit", "--audit-level=high"]), name: "npm audit",
                        cancellation: cancellation, progress: progress))
            }
            if fileManager.isReadableFile(atPath: site.path.appendingPathComponent("artisan").path) {
                report.append(
                    try await audit(
                        try tools.artisan(site: site, defaultPHP: defaultPHP, arguments: ["test", "--no-interaction"], timeout: 60 * 60),
                        name: "artisan test", cancellation: cancellation, progress: progress))
            }
            return SiteWorkflowResult(
                title: operation.title, output: report.isEmpty ? "No supported audit tools were found." : report.joined(separator: "\n"),
                artifactURL: nil)

        case .repair:
            try await repair(site: site, defaultPHP: defaultPHP, tools: tools, cancellation: cancellation, progress: progress)
            return SiteWorkflowResult(
                title: operation.title, output: "Site files, dependencies, application key, storage link, and caches were repaired.",
                artifactURL: nil)
        }
    }

    private func execute(
        _ invocation: SiteToolInvocation, name: String, cancellation: SiteOperationCancellation,
        progress: @escaping @Sendable (String) -> Void
    ) async throws {
        progress("Running \(name)...\n")
        let result = try await SiteCommandRunner.run(invocation, cancellation: cancellation) { data in
            progress(String(decoding: data, as: UTF8.self))
        }
        guard result.status == 0 else { throw SiteWorkflowError.commandFailed(name, result.status, result.output) }
        progress("[OK] \(name)\n")
    }

    private func audit(
        _ invocation: SiteToolInvocation, name: String, cancellation: SiteOperationCancellation,
        progress: @escaping @Sendable (String) -> Void
    ) async throws -> String {
        progress("Running \(name)...\n")
        let result = try await SiteCommandRunner.run(invocation, cancellation: cancellation)
        let summary = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
        progress("[\(result.status == 0 ? "OK" : "FAIL")] \(name)\n")
        return "[\(result.status == 0 ? "OK" : "FAIL")] \(name)\n\(summary.prefix(4_000))"
    }

    private func cleanAndRebuild(
        site: SiteProject, defaultPHP: String, tools: SiteToolchain, cancellation: SiteOperationCancellation,
        progress: @escaping @Sendable (String) -> Void
    ) async throws {
        let staging = rootURL.appendingPathComponent("Cache/site-clean-\(UUID().uuidString)", isDirectory: true)
        try fileManager.createDirectory(at: staging, withIntermediateDirectories: true)
        var moved: [(URL, URL)] = []
        do {
            for name in ["vendor", "node_modules"] {
                let source = site.path.appendingPathComponent(name, isDirectory: true)
                guard fileManager.fileExists(atPath: source.path) else { continue }
                let target = staging.appendingPathComponent(name, isDirectory: true)
                try fileManager.moveItem(at: source, to: target)
                moved.append((source, target))
            }
            if fileManager.fileExists(atPath: site.path.appendingPathComponent("composer.json").path) {
                try await execute(
                    try tools.composer(site: site, defaultPHP: defaultPHP, arguments: ["install", "--no-interaction", "--prefer-dist"]),
                    name: "composer install", cancellation: cancellation, progress: progress)
            }
            if fileManager.fileExists(atPath: site.path.appendingPathComponent("package.json").path) {
                try await execute(
                    try tools.npm(site: site, arguments: ["install", "--no-audit", "--no-fund"]), name: "npm install",
                    cancellation: cancellation, progress: progress)
                if hasNPMScript("build", at: site.path) {
                    try await execute(
                        try tools.npm(site: site, arguments: ["run", "build"]), name: "npm run build", cancellation: cancellation,
                        progress: progress)
                }
            }
            if fileManager.isReadableFile(atPath: site.path.appendingPathComponent("artisan").path) {
                try await execute(
                    try tools.artisan(site: site, defaultPHP: defaultPHP, arguments: ["optimize:clear", "--no-interaction"]),
                    name: "artisan optimize:clear", cancellation: cancellation, progress: progress)
            }
            try? fileManager.removeItem(at: staging)
        } catch {
            for (source, target) in moved.reversed() where fileManager.fileExists(atPath: target.path) {
                try? fileManager.removeItem(at: source)
                try? fileManager.moveItem(at: target, to: source)
            }
            try? fileManager.removeItem(at: staging)
            throw error
        }
    }

    private func repair(
        site: SiteProject, defaultPHP: String, tools: SiteToolchain, cancellation: SiteOperationCancellation,
        progress: @escaping @Sendable (String) -> Void
    ) async throws {
        let envURL = site.path.appendingPathComponent(".env")
        if !fileManager.fileExists(atPath: envURL.path) {
            let example = site.path.appendingPathComponent(".env.example")
            let data = fileManager.fileExists(atPath: example.path) ? try Data(contentsOf: example) : Data()
            try data.write(to: envURL, options: .atomic)
            progress("[OK] Created .env\n")
        }
        for relative in ["storage/framework/cache", "storage/framework/sessions", "storage/framework/views", "storage/logs"] {
            try fileManager.createDirectory(
                at: site.path.appendingPathComponent(relative, isDirectory: true), withIntermediateDirectories: true)
        }
        if fileManager.fileExists(atPath: site.path.appendingPathComponent("composer.json").path),
            !fileManager.fileExists(atPath: site.path.appendingPathComponent("vendor/autoload.php").path)
        {
            try await execute(
                try tools.composer(site: site, defaultPHP: defaultPHP, arguments: ["install", "--no-interaction", "--prefer-dist"]),
                name: "composer install", cancellation: cancellation, progress: progress)
        }
        guard fileManager.isReadableFile(atPath: site.path.appendingPathComponent("artisan").path) else { return }
        let contents = (try? String(contentsOf: envURL, encoding: .utf8)) ?? ""
        if SiteHealthInspector.environmentValue(contents, key: "APP_KEY")?.isEmpty != false {
            try await execute(
                try tools.artisan(site: site, defaultPHP: defaultPHP, arguments: ["key:generate", "--force", "--no-interaction"]),
                name: "artisan key:generate", cancellation: cancellation, progress: progress)
        }
        if !fileManager.fileExists(atPath: site.path.appendingPathComponent("public/storage").path) {
            try await execute(
                try tools.artisan(site: site, defaultPHP: defaultPHP, arguments: ["storage:link", "--no-interaction"]),
                name: "artisan storage:link", cancellation: cancellation, progress: progress)
        }
        try await execute(
            try tools.artisan(site: site, defaultPHP: defaultPHP, arguments: ["optimize:clear", "--no-interaction"]),
            name: "artisan optimize:clear", cancellation: cancellation, progress: progress)
    }

    private func configureMail(site: SiteProject, smtpPort: Int) throws -> URL {
        let envURL = site.path.appendingPathComponent(".env")
        if (try? envURL.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true {
            throw ServiceEnvironmentError.symbolicLink
        }
        let exampleURL = site.path.appendingPathComponent(".env.example")
        let original: String
        if fileManager.fileExists(atPath: envURL.path) {
            original = try String(contentsOf: envURL, encoding: .utf8)
        } else if fileManager.fileExists(atPath: exampleURL.path) {
            original = try String(contentsOf: exampleURL, encoding: .utf8)
        } else {
            original = ""
        }
        let merged = ServiceEnvironmentFile.merging(
            original,
            variables: [
                ServiceEnvironmentVariable(key: "MAIL_MAILER", value: "smtp"),
                ServiceEnvironmentVariable(key: "MAIL_HOST", value: "127.0.0.1"),
                ServiceEnvironmentVariable(key: "MAIL_PORT", value: String(smtpPort)),
                ServiceEnvironmentVariable(key: "MAIL_USERNAME", value: "null"),
                ServiceEnvironmentVariable(key: "MAIL_PASSWORD", value: "null"),
                ServiceEnvironmentVariable(key: "MAIL_ENCRYPTION", value: "null")
            ], serviceName: "HerdMe Mail")
        try Data(merged.contents.utf8).write(to: envURL, options: .atomic)
        return envURL
    }

    private func archive(
        site: SiteProject, label: String, cancellation: SiteOperationCancellation, progress: @escaping @Sendable (String) -> Void
    ) async throws -> URL {
        let directory = rootURL.appendingPathComponent("Backups", isDirectory: true)
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        let name = SiteProject.dnsLabel(for: site.name)
        let target = directory.appendingPathComponent("\(name)-\(label)-\(formatter.string(from: Date())).zip")
        progress("Creating backup...\n")
        let invocation = SiteToolInvocation(
            executable: URL(fileURLWithPath: "/usr/bin/ditto"),
            arguments: ["-c", "-k", "--sequesterRsrc", "--keepParent", site.path.path, target.path],
            projectDirectory: site.path.deletingLastPathComponent(),
            environment: ProcessInfo.processInfo.environment,
            timeout: 60 * 60
        )
        let result = try await SiteCommandRunner.run(invocation, cancellation: cancellation)
        guard result.status == 0 else { throw SiteWorkflowError.commandFailed("backup", result.status, result.output) }
        progress("[OK] Backup: \(target.path)\n")
        return target
    }

    private func hasNPMScript(_ name: String, at directory: URL) -> Bool {
        (try? NPMScriptCatalog.scripts(in: directory).contains { $0.name == name }) == true
    }
}

enum SiteWorkflowError: LocalizedError {
    case artisanMissing
    case composerMissing
    case nodeMissing
    case commandFailed(String, Int32, String)

    var errorDescription: String? {
        switch self {
        case .artisanMissing:
            return String(localized: "This site does not contain Laravel Artisan.")
        case .composerMissing:
            return String(localized: "Install Composer from the PHP page before running this command.")
        case .nodeMissing:
            return String(localized: "Install the selected Node.js runtime before running this command.")
        case .commandFailed(let name, let status, let output):
            let detail = output.trimmingCharacters(in: .whitespacesAndNewlines)
            return "\(name) failed (exit \(status))." + (detail.isEmpty ? "" : "\n" + detail)
        }
    }
}
