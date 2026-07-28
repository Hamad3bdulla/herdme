import Foundation

@MainActor
final class SiteToolsCoordinator {
    private let rootURL: URL
    private let fileManager: FileManager
    private let terminalCommandLauncher: any TerminalCommandLaunching
    private let runtimeInspector: any NodeRuntimeInspecting

    init(
        rootURL: URL,
        fileManager: FileManager = .default,
        terminalCommandLauncher: (any TerminalCommandLaunching)? = nil,
        runtimeInspector: (any NodeRuntimeInspecting)? = nil
    ) {
        self.rootURL = rootURL
        self.fileManager = fileManager
        self.terminalCommandLauncher =
            terminalCommandLauncher ?? TerminalCommandLauncher(rootURL: rootURL)
        self.runtimeInspector = runtimeInspector ?? RuntimeInspector(managedRoot: rootURL)
    }

    func openTerminal(for site: SiteProject) throws {
        try terminalCommandLauncher.open(
            command: "cd \(TerminalCommandLauncher.shellQuote(site.path.path))\n"
                + "exec \"${SHELL:-/bin/zsh}\" -l",
            title: site.name
        )
    }

    func openTinker(for site: SiteProject, defaultPHP: String) throws {
        let artisan = site.path.appendingPathComponent("artisan")
        guard fileManager.isReadableFile(atPath: artisan.path) else {
            throw RuntimeInstallationError.commandFailed(
                String(localized: "Tinker is available only for Laravel projects with an artisan executable.")
            )
        }
        let cycle = site.phpVersion ?? defaultPHP
        let php = managedPHPExecutable(cycle: cycle)
        guard fileManager.isExecutableFile(atPath: php.path) else {
            throw RuntimeInstallationError.runtimeNotInstalled(name: "PHP", cycle: cycle)
        }
        try terminalCommandLauncher.open(
            command: "cd \(TerminalCommandLauncher.shellQuote(site.path.path))\n"
                + "\(TerminalCommandLauncher.shellQuote(php.path)) artisan tinker\n"
                + "exec \"${SHELL:-/bin/zsh}\" -l",
            title: site.name + "-Tinker"
        )
    }

    func artisanInvocation(
        for site: SiteProject,
        defaultPHP: String,
        presetID: String,
        customCommand: String
    ) throws -> ArtisanInvocation {
        let artisan = site.path.appendingPathComponent("artisan")
        guard fileManager.isReadableFile(atPath: artisan.path) else {
            throw RuntimeInstallationError.commandFailed(
                String(localized: "Artisan is available only for Laravel projects with an artisan executable.")
            )
        }
        let cycle = site.phpVersion ?? defaultPHP
        let php = managedPHPExecutable(cycle: cycle)
        guard fileManager.isExecutableFile(atPath: php.path) else {
            throw RuntimeInstallationError.runtimeNotInstalled(name: "PHP", cycle: cycle)
        }
        let command = try ArtisanCommandParser.arguments(
            presetID: presetID,
            customCommand: customCommand
        )
        return ArtisanInvocation(
            phpExecutable: php,
            projectDirectory: site.path,
            arguments: command.arguments,
            environment: managedArtisanEnvironment(),
            timeout: command.timeout
        )
    }

    func npmInvocation(for site: SiteProject, scriptName: String) throws -> NPMScriptInvocation {
        try NPMScriptCatalog.validate(name: scriptName)
        let scripts = try NPMScriptCatalog.scripts(in: site.path)
        guard scripts.contains(where: { $0.name == scriptName }) else {
            throw NPMScriptCatalogError.scriptUnavailable
        }

        let runtimes = runtimeInspector.nodeVersions()
        let runtime: RuntimeVersion?
        if let cycle = site.nodeVersion {
            runtime = runtimes.first { $0.cycle == cycle && $0.isInstalled }
        } else {
            runtime =
                runtimes.first(where: { $0.isActive })
                ?? runtimes.first(where: {
                    $0.cycle == RuntimeCatalog.defaultNodeMajor && $0.isInstalled
                })
                ?? runtimes.first(where: \.isInstalled)
        }
        guard let runtime, let version = runtime.installedVersion else {
            throw RuntimeInstallationError.runtimeNotInstalled(
                name: "Node.js",
                cycle: site.nodeVersion ?? RuntimeCatalog.defaultNodeMajor
            )
        }

        let runtimeDirectory =
            rootURL
            .appendingPathComponent("Runtimes/node/\(version)", isDirectory: true)
        let runtimeBin = runtimeDirectory.appendingPathComponent("bin", isDirectory: true)
        let node = runtimeBin.appendingPathComponent("node")
        let npmCLI = runtimeDirectory.appendingPathComponent("lib/node_modules/npm/bin/npm-cli.js")
        guard fileManager.isExecutableFile(atPath: node.path) else {
            throw RuntimeInstallationError.runtimeNotInstalled(name: "Node.js", cycle: runtime.cycle)
        }
        guard fileManager.isReadableFile(atPath: npmCLI.path) else {
            throw RuntimeInstallationError.commandFailed(
                String(localized: "The selected managed Node.js runtime does not contain npm.")
            )
        }

        return NPMScriptInvocation(
            nodeExecutable: node,
            npmCLI: npmCLI,
            projectDirectory: site.path,
            scriptName: scriptName,
            environment: managedNPMEnvironment(runtimeBin: runtimeBin),
            timeout: NPMScriptCatalog.timeout(for: scriptName)
        )
    }

    private func managedPHPExecutable(cycle: String) -> URL {
        rootURL.appendingPathComponent("Runtimes/php/\(cycle)/bin/php")
    }

    private func managedArtisanEnvironment() -> [String: String] {
        var environment = ProcessInfo.processInfo.environment
        for key in environment.keys where key.hasPrefix("HERD_") {
            environment.removeValue(forKey: key)
        }
        environment.removeValue(forKey: "PHPRC")
        environment.removeValue(forKey: "PHP_INI_SCAN_DIR")

        let temporaryDirectory = rootURL.appendingPathComponent("Cache/tmp", isDirectory: true)
        try? fileManager.createDirectory(
            at: temporaryDirectory,
            withIntermediateDirectories: true
        )
        environment["COMPOSER_HOME"] =
            rootURL
            .appendingPathComponent("Composer", isDirectory: true).path
        environment["COMPOSER_CACHE_DIR"] =
            rootURL
            .appendingPathComponent("Cache/composer", isDirectory: true).path
        environment["TMPDIR"] = temporaryDirectory.path + "/"
        environment["TMP"] = temporaryDirectory.path
        environment["TEMP"] = temporaryDirectory.path
        environment["PATH"] = [
            rootURL.appendingPathComponent("bin", isDirectory: true).path,
            "/usr/bin", "/bin", "/usr/sbin", "/sbin"
        ].joined(separator: ":")
        return environment
    }

    private func managedNPMEnvironment(runtimeBin: URL) -> [String: String] {
        var environment = ProcessInfo.processInfo.environment
        for key in environment.keys where key.hasPrefix("HERD_") {
            environment.removeValue(forKey: key)
        }
        for key in ["NODE_OPTIONS", "NODE_PATH", "NPM_CONFIG_PREFIX", "npm_config_prefix"] {
            environment.removeValue(forKey: key)
        }

        let cache = rootURL.appendingPathComponent("Cache/npm", isDirectory: true)
        try? fileManager.createDirectory(at: cache, withIntermediateDirectories: true)
        environment["PATH"] = [
            runtimeBin.path,
            rootURL.appendingPathComponent("bin", isDirectory: true).path,
            "/usr/bin", "/bin", "/usr/sbin", "/sbin"
        ].joined(separator: ":")
        environment["npm_config_cache"] = cache.path
        environment["npm_config_update_notifier"] = "false"
        environment["NO_COLOR"] = "1"
        return environment
    }
}
