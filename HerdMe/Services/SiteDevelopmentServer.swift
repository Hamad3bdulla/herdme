import Foundation

enum SiteDevelopmentMode: String, Sendable {
    case primaryProxy
    case laravelAssets
}

/// Owns a project's long-running npm development process.
/// Laravel keeps PHP as the primary site handler while Vite publishes its hot file;
/// standalone Node and frontend projects are routed through the development server.
final class SiteDevelopmentServer: @unchecked Sendable {
    private let rootURL: URL
    private let fileManager: FileManager
    private let lock = NSLock()
    private var task: Task<Void, Never>?
    private var cancellation: SiteOperationCancellation?
    private var backendTask: Task<Void, Never>?
    private var backendCancellation: SiteOperationCancellation?
    private var logURL: URL?
    private var managedHotPath: URL?

    private(set) var port: Int?
    private(set) var mode: SiteDevelopmentMode?

    init(rootURL: URL, fileManager: FileManager = .default) {
        self.rootURL = rootURL
        self.fileManager = fileManager
    }

    var isRunning: Bool {
        lock.withLock { port.map(LocalEnvironmentEngine.canConnect(port:)) == true && task != nil }
    }

    var proxiesSiteTraffic: Bool {
        lock.withLock { mode == .primaryProxy }
    }

    static func mode(for site: SiteProject, fileManager: FileManager = .default) -> SiteDevelopmentMode? {
        let root = site.path.standardizedFileURL
        if isLaravelProject(site, root: root, fileManager: fileManager),
            hasDevScript(at: root, fileManager: fileManager)
        {
            return .laravelAssets
        }
        if site.framework.caseInsensitiveCompare("Node.js") == .orderedSame,
            hasDevScript(at: root, fileManager: fileManager)
        {
            return .primaryProxy
        }
        let frontend = root.appendingPathComponent("frontend", isDirectory: true)
        return hasDevScript(at: frontend, fileManager: fileManager) ? .primaryProxy : nil
    }

    static func projectDirectory(for site: SiteProject, fileManager: FileManager = .default) -> URL? {
        let root = site.path.standardizedFileURL
        guard let mode = mode(for: site, fileManager: fileManager) else { return nil }
        if mode == .laravelAssets || hasDevScript(at: root, fileManager: fileManager) {
            return root
        }
        let frontend = root.appendingPathComponent("frontend", isDirectory: true)
        return hasDevScript(at: frontend, fileManager: fileManager) ? frontend : nil
    }

    static func isDevelopmentSite(_ site: SiteProject, fileManager: FileManager = .default) -> Bool {
        projectDirectory(for: site, fileManager: fileManager) != nil
    }

    func start(site: SiteProject, cancellationToken: SiteOperationCancellation? = nil) async throws -> Int {
        if let port = lock.withLock({ port }), isRunning { return port }
        await stop()

        guard let selectedMode = Self.mode(for: site, fileManager: fileManager),
            let project = Self.projectDirectory(for: site, fileManager: fileManager)
        else {
            throw SiteDevelopmentServerError.noDevScript
        }
        guard fileManager.fileExists(atPath: project.appendingPathComponent("node_modules", isDirectory: true).path) else {
            throw SiteDevelopmentServerError.dependenciesMissing
        }

        if let cancellationToken, cancellationToken.isCancelled { throw CancellationError() }
        try await ensureNodeRuntime(for: site)
        let selectedPort = try Self.availablePort()
        let invocation = try SiteToolchain(rootURL: rootURL, fileManager: fileManager)
            .npm(
                site: site,
                arguments: Self.developmentArguments(for: project, port: selectedPort),
                timeout: 24 * 60 * 60
            )
        let logDirectory = rootURL.appendingPathComponent("Log/development", isDirectory: true)
        try fileManager.createDirectory(at: logDirectory, withIntermediateDirectories: true)
        let log = logDirectory.appendingPathComponent(Self.safeName(site.name) + ".log")
        try LogRotation.rotateIfNeeded(log)
        if !fileManager.fileExists(atPath: log.path) { fileManager.createFile(atPath: log.path, contents: nil) }

        let processCancellation = SiteOperationCancellation()
        let hotPath =
            selectedMode == .laravelAssets
            ? Self.laravelHotFile(for: site)
            : nil
        if selectedMode == .laravelAssets {
            _ = Self.removeLaravelHotFile(for: site, fileManager: fileManager)
        }
        lock.withLock {
            mode = selectedMode
            port = selectedPort
            logURL = log
            managedHotPath = hotPath
            cancellation = processCancellation
        }

        do {
            try await startCameraBackendIfNeeded(site: site, cancellation: processCancellation)

            let appendOutput: @Sendable (Data) -> Void = { [weak self] data in
                self?.appendLog(data)
            }
            let task = Task.detached(priority: .userInitiated) {
                do {
                    let result = try await SiteCommandRunner.run(
                        SiteToolInvocation(
                            executable: invocation.executable,
                            arguments: invocation.arguments,
                            projectDirectory: invocation.projectDirectory,
                            environment: invocation.environment,
                            timeout: invocation.timeout
                        ),
                        cancellation: processCancellation
                    ) { data in
                        appendOutput(data)
                    }
                    guard !processCancellation.isCancelled else { return }
                    if result.status != 0 {
                        appendOutput(Data(("\nDevelopment server exited with status \(result.status).\n").utf8))
                    }
                } catch {
                    guard !processCancellation.isCancelled else { return }
                    appendOutput(Data(("\nDevelopment server failed: \(error.localizedDescription)\n").utf8))
                }
            }
            lock.withLock { self.task = task }

            try await waitUntilReady(port: selectedPort, cancellation: processCancellation)
            if selectedMode == .laravelAssets {
                try await waitForLaravelHotFile(port: selectedPort, cancellation: processCancellation)
            }
            return selectedPort
        } catch {
            await stop()
            throw error
        }
    }

    func stop() async {
        let current = lock.withLock {
            () -> (Task<Void, Never>?, SiteOperationCancellation?, Task<Void, Never>?, SiteOperationCancellation?, URL?) in
            let value = (task, cancellation, backendTask, backendCancellation, managedHotPath)
            task = nil
            cancellation = nil
            backendTask = nil
            backendCancellation = nil
            port = nil
            mode = nil
            logURL = nil
            managedHotPath = nil
            return value
        }
        current.1?.cancel()
        current.0?.cancel()
        current.3?.cancel()
        current.2?.cancel()
        _ = await current.0?.result
        _ = await current.2?.result
        if let hotPath = current.4 { try? fileManager.removeItem(at: hotPath) }
    }

    nonisolated static func availablePort() throws -> Int {
        guard let port = LocalEnvironmentEngine.availablePort(startingAt: 30_000) else {
            throw LocalEnvironmentError.noAvailablePort
        }
        return port
    }

    private func ensureNodeRuntime(for site: SiteProject) async throws {
        let inspector = RuntimeInspector(managedRoot: rootURL)
        let installed = inspector.nodeVersions().filter(\.isInstalled)
        let requested = site.nodeVersion?.trimmingCharacters(in: .whitespacesAndNewlines)
        if let requested, !requested.isEmpty {
            let normalized = requested.trimmingCharacters(in: CharacterSet(charactersIn: "v"))
            let hasRequested = installed.contains {
                $0.cycle == normalized || $0.installedVersion == normalized
            }
            if !hasRequested {
                let major = normalized.split(separator: ".", maxSplits: 1).first.map(String.init) ?? normalized
                guard Int(major).map({ (1...99).contains($0) }) == true else {
                    throw SiteDevelopmentServerError.invalidNodeVersion(requested)
                }
                _ = try await RuntimeInstaller(rootURL: rootURL).installNode(cycle: major)
            }
            try? await RuntimeInstaller(rootURL: rootURL).activateNode(
                cycle: normalized.split(separator: ".", maxSplits: 1).first.map(String.init) ?? normalized)
            return
        }
        guard !installed.isEmpty else {
            _ = try await RuntimeInstaller(rootURL: rootURL).installNode(cycle: RuntimeCatalog.defaultNodeMajor)
            return
        }
        try? await RuntimeInstaller(rootURL: rootURL).activateNode(cycle: installed[0].cycle)
    }

    private func startCameraBackendIfNeeded(
        site: SiteProject,
        cancellation: SiteOperationCancellation
    ) async throws {
        let backend = site.path.appendingPathComponent("backend", isDirectory: true)
        let python = backend.appendingPathComponent(".venv/bin/python")
        let main = backend.appendingPathComponent("app/main.py")
        guard fileManager.isExecutableFile(atPath: python.path), fileManager.isReadableFile(atPath: main.path) else {
            return
        }
        guard !LocalEnvironmentEngine.canConnect(port: 8_000) else { return }

        let backendCancellation = SiteOperationCancellation()
        let environment = ProcessInfo.processInfo.environment.merging([
            "PYTHONUNBUFFERED": "1",
            "PATH": [rootURL.appendingPathComponent("bin").path, "/usr/bin", "/bin", "/usr/sbin", "/sbin"].joined(separator: ":")
        ]) { _, new in new }
        let appendOutput: @Sendable (Data) -> Void = { [weak self] data in self?.appendLog(data) }
        let task = Task.detached(priority: .userInitiated) {
            let invocation = SiteToolInvocation(
                executable: python,
                arguments: ["-m", "uvicorn", "app.main:app", "--host", "127.0.0.1", "--port", "8000"],
                projectDirectory: backend,
                environment: environment,
                timeout: 24 * 60 * 60
            )
            _ = try? ProcessRunner.run(
                invocation.executable,
                arguments: invocation.arguments,
                currentDirectory: invocation.projectDirectory,
                environment: invocation.environment,
                timeout: invocation.timeout,
                cancellationRequested: { backendCancellation.isCancelled || Task.isCancelled },
                outputReceived: appendOutput
            )
        }
        lock.withLock {
            self.backendTask = task
            self.backendCancellation = backendCancellation
        }
        let deadline = ProcessInfo.processInfo.systemUptime + 15
        while ProcessInfo.processInfo.systemUptime < deadline {
            if cancellation.isCancelled { throw CancellationError() }
            if LocalEnvironmentEngine.canConnect(port: 8_000) { return }
            try await Task.sleep(for: .milliseconds(100))
        }
        throw SiteDevelopmentServerError.cameraBackendTimedOut
    }

    static func developmentArguments(for project: URL, port: Int) -> [String] {
        let package = (try? Data(contentsOf: project.appendingPathComponent("package.json")))
            .flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
        let dependencyGroups = ["dependencies", "devDependencies"]
        let isNext = dependencyGroups.contains { group in
            guard let dependencies = package?[group] as? [String: Any] else { return false }
            return dependencies.keys.contains {
                $0.caseInsensitiveCompare("next") == .orderedSame
            }
        }
        return ["run", "dev", "--", isNext ? "--hostname" : "--host", "127.0.0.1", "--port", String(port)]
    }

    static func laravelHotFile(for site: SiteProject) -> URL {
        site.path.appendingPathComponent("public/hot")
    }

    @discardableResult
    static func removeLaravelHotFile(
        for site: SiteProject,
        fileManager: FileManager = .default
    ) -> Bool {
        let hotFile = laravelHotFile(for: site)
        guard fileManager.fileExists(atPath: hotFile.path) else { return true }
        do {
            try fileManager.removeItem(at: hotFile)
            return true
        } catch {
            return false
        }
    }

    private func waitUntilReady(port: Int, cancellation: SiteOperationCancellation) async throws {
        // Replace the placeholder only after selecting the port so the command is deterministic.
        let deadline = ProcessInfo.processInfo.systemUptime + 15
        while ProcessInfo.processInfo.systemUptime < deadline {
            try Task.checkCancellation()
            if cancellation.isCancelled { throw CancellationError() }
            if LocalEnvironmentEngine.canConnect(port: port) { return }
            try await Task.sleep(for: .milliseconds(100))
        }
        throw SiteDevelopmentServerError.startTimedOut(port)
    }

    private func waitForLaravelHotFile(port: Int, cancellation: SiteOperationCancellation) async throws {
        guard let hotPath = lock.withLock({ managedHotPath }) else { return }
        let deadline = ProcessInfo.processInfo.systemUptime + 15
        while ProcessInfo.processInfo.systemUptime < deadline {
            try Task.checkCancellation()
            if cancellation.isCancelled { throw CancellationError() }
            if let value = try? String(contentsOf: hotPath, encoding: .utf8),
                let url = URL(string: value.trimmingCharacters(in: .whitespacesAndNewlines)),
                url.port == port
            {
                return
            }
            try await Task.sleep(for: .milliseconds(100))
        }
        throw SiteDevelopmentServerError.hotFileTimedOut
    }

    private func appendLog(_ data: Data) {
        guard !data.isEmpty, let logURL = lock.withLock({ logURL }) else { return }
        let message = String(decoding: data, as: UTF8.self)
        guard !message.isEmpty else { return }
        let lock = Self.logLock
        lock.lock()
        defer { lock.unlock() }
        guard let handle = try? FileHandle(forWritingTo: logURL) else { return }
        defer { try? handle.close() }
        _ = try? handle.seekToEnd()
        try? handle.write(contentsOf: Data("[\(ISO8601DateFormatter().string(from: Date()))] \(message)".utf8))
    }

    private static let logLock = NSLock()

    private static func isLaravelProject(_ site: SiteProject, root: URL, fileManager: FileManager) -> Bool {
        site.framework.caseInsensitiveCompare("Laravel") == .orderedSame
            || fileManager.fileExists(atPath: root.appendingPathComponent("artisan").path)
    }

    private static func hasDevScript(at directory: URL, fileManager: FileManager) -> Bool {
        guard fileManager.fileExists(atPath: directory.appendingPathComponent("package.json").path) else { return false }
        return (try? NPMScriptCatalog.scripts(in: directory).contains { $0.name == "dev" }) == true
    }

    private static func safeName(_ value: String) -> String {
        String(value.unicodeScalars.map { CharacterSet.alphanumerics.contains($0) || $0 == "-" || $0 == "_" ? Character($0) : "_" })
    }
}

enum SiteDevelopmentServerError: LocalizedError, Equatable {
    case noDevScript
    case dependenciesMissing
    case invalidNodeVersion(String)
    case startTimedOut(Int)
    case hotFileTimedOut
    case cameraBackendTimedOut

    var errorDescription: String? {
        switch self {
        case .noDevScript: String(localized: "This site has no runnable npm dev script.")
        case .dependenciesMissing: String(localized: "Install the frontend dependencies before starting development mode.")
        case .invalidNodeVersion(let version):
            String.localizedStringWithFormat(String(localized: "The requested Node.js version %@ is invalid."), version)
        case .startTimedOut(let port):
            String.localizedStringWithFormat(String(localized: "The development server did not open port %lld."), Int64(port))
        case .hotFileTimedOut: String(localized: "Laravel Vite did not publish its hot-file endpoint.")
        case .cameraBackendTimedOut: String(localized: "The detected camera backend did not open port 8000.")
        }
    }
}

extension NSLock {
    fileprivate func withLock<T>(_ body: () -> T) -> T {
        lock()
        defer { unlock() }
        return body()
    }
}
