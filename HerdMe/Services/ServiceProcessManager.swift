import Darwin
import Foundation

private struct HomebrewOutdatedIndex: Decodable {
    let formulae: [HomebrewOutdatedFormula]
}

private struct HomebrewOutdatedFormula: Decodable {
    let name: String
}

enum ServiceRuntimeState: String, Sendable {
    case notInstalled = "Not Installed"
    case stopped = "Stopped"
    case running = "Running"

    var isRunning: Bool { self == .running }
}

enum ServiceRuntimeError: LocalizedError {
    case unsupported(String)
    case packageManagerMissing
    case notInstalled(String)
    case portUnavailable(Int)
    case commandFailed(String)
    case processFailed(String)
    case readinessTimedOut(String, Int)

    var errorDescription: String? {
        switch self {
        case let .unsupported(name):
            "Automatic runtime management is not available for \(name) yet."
        case .packageManagerMissing:
            "Homebrew is required to install local service runtimes on macOS."
        case let .notInstalled(name):
            "Install the \(name) runtime before starting this service."
        case let .portUnavailable(port):
            "Port \(port) is already in use. Choose another port for this service."
        case let .commandFailed(output):
            output.isEmpty ? "The service package command failed." : output
        case let .processFailed(name):
            "The \(name) service exited before it became ready. Check its log for details."
        case let .readinessTimedOut(name, port):
            "The \(name) service did not become ready on port \(port). Check its log for details."
        }
    }
}

final class ServiceProcessManager: @unchecked Sendable {
    private struct Descriptor {
        let formula: String?
        let executable: String
    }

    private struct ManagedProcessHandles {
        let process: Process?
        let adoptedPID: pid_t?
        let outputHandle: FileHandle?
    }

    struct DatabaseConflictRecoveryPlan: Equatable {
        let installingFormula: String
        let conflictingFormula: String
        let packageCommand: String

        init(
            installingFormula: String,
            conflictingFormula: String,
            packageCommand: String = "install"
        ) {
            self.installingFormula = installingFormula
            self.conflictingFormula = conflictingFormula
            self.packageCommand = packageCommand
        }

        var unlinkConflictArguments: [String] { ["unlink", conflictingFormula] }
        var retryInstallArguments: [String] { [packageCommand, installingFormula] }
        var restoreArguments: [[String]] {
            [["unlink", installingFormula], ["link", conflictingFormula]]
        }
    }

    let rootURL: URL
    private let fileManager: FileManager
    private let executableOverrides: [String: URL]
    private let credentialStore: ServiceCredentialStore
    private let readinessProbe: @Sendable (Int) -> Bool
    private let readinessTimeout: TimeInterval
    private let operationGate = AsyncOperationGate()
    private let lifecycleLock = NSRecursiveLock()
    private let lock = NSLock()
    private var processes: [UUID: Process] = [:]
    private var adoptedProcessIDs: [UUID: pid_t] = [:]
    private var outputHandles: [UUID: FileHandle] = [:]
    private var consolePorts: [UUID: Int] = [:]

    init(
        rootURL: URL,
        fileManager: FileManager = .default,
        executableOverrides: [String: URL] = [:],
        credentialStore: ServiceCredentialStore = ServiceCredentialStore(),
        readinessProbe: @escaping @Sendable (Int) -> Bool = {
            LocalEnvironmentEngine.canConnect(port: $0)
        },
        readinessTimeout: TimeInterval = 30
    ) {
        self.rootURL = rootURL
        self.fileManager = fileManager
        self.executableOverrides = executableOverrides
        self.credentialStore = credentialStore
        self.readinessProbe = readinessProbe
        self.readinessTimeout = max(0.05, readinessTimeout)
    }

    func state(for instance: ServiceInstance) -> ServiceRuntimeState {
        lifecycleLock.lock()
        defer { lifecycleLock.unlock() }
        lock.lock()
        let running = processes[instance.id]?.isRunning == true
        let adoptedPID = adoptedProcessIDs[instance.id]
        lock.unlock()
        if running { return .running }

        if let adoptedPID, isManagedProcess(adoptedPID, for: instance) {
            return .running
        }
        if adoptedPID != nil {
            lock.lock()
            adoptedProcessIDs[instance.id] = nil
            lock.unlock()
        }

        if let persistedPID = persistedProcessID(for: instance),
           isManagedProcess(persistedPID, for: instance) {
            lock.lock()
            adoptedProcessIDs[instance.id] = persistedPID
            lock.unlock()
            return .running
        }
        removePIDFile(for: instance)
        return executableURL(for: instance.definitionID) == nil ? .notInstalled : .stopped
    }

    func install(definitionID: String) async throws -> String {
        try await Task.detached(priority: .userInitiated) { [self] in
            try installSynchronously(definitionID: definitionID)
        }.value
    }

    func outdatedDefinitionIDs() async throws -> Set<String> {
        try await Task.detached(priority: .utility) {
            try Self.outdatedDefinitionIDsSynchronously()
        }.value
    }

    func start(
        _ instance: ServiceInstance,
        allowCredentialInteraction: Bool = true
    ) async throws {
        await operationGate.acquire()
        do {
            try Task.checkCancellation()
            try await startManaged(
                instance,
                allowCredentialInteraction: allowCredentialInteraction
            )
            await operationGate.release()
        } catch {
            await operationGate.release()
            recordStartFailure(instance: instance, error: error)
            throw error
        }
    }

    func stop(_ instance: ServiceInstance) async {
        await operationGate.acquire()
        await stop(id: instance.id)
        await operationGate.release()
    }

    func stopAll() async {
        await operationGate.acquire()
        let identifiers = activeIdentifiers()
        for identifier in identifiers {
            await stop(id: identifier)
        }
        await operationGate.release()
    }

    func stopAllImmediately() {
        lock.lock()
        let activeProcesses = processes
        let adopted = adoptedProcessIDs
        let handles = outputHandles
        processes.removeAll()
        adoptedProcessIDs.removeAll()
        outputHandles.removeAll()
        consolePorts.removeAll()
        lock.unlock()

        activeProcesses.forEach { identifier, process in
            AsyncProcessLifecycle.terminateImmediately(process)
            removeRuntimeArtifacts(id: identifier)
        }
        adopted.forEach { identifier, pid in
            AsyncProcessLifecycle.terminateImmediately(pid: pid)
            removeRuntimeArtifacts(id: identifier)
        }
        handles.values.forEach { try? $0.close() }
    }

    func dataDirectory(for instance: ServiceInstance) -> URL {
        rootURL.appendingPathComponent("Services/\(instance.id.uuidString)/data", isDirectory: true)
    }

    func consoleURL(for instance: ServiceInstance) -> URL? {
        lifecycleLock.lock()
        defer { lifecycleLock.unlock() }
        guard instance.definitionID == "minio" || instance.definitionID == "rustfs" else {
            return nil
        }
        lock.lock()
        let knownPort = consolePorts[instance.id]
        let managedProcess = processes[instance.id]
        let adoptedProcessID = adoptedProcessIDs[instance.id]
        let processID = managedProcess?.isRunning == true
            ? managedProcess?.processIdentifier
            : adoptedProcessID
        if processID == nil {
            consolePorts[instance.id] = nil
        }
        lock.unlock()
        guard processID != nil else { return nil }
        if let knownPort {
            return URL(string: "http://127.0.0.1:\(knownPort)")
        }
        guard let processID,
              let arguments = Self.processArguments(pid: processID),
              let port = Self.consolePort(from: arguments) else {
            return nil
        }
        lock.lock()
        consolePorts[instance.id] = port
        lock.unlock()
        return URL(string: "http://127.0.0.1:\(port)")
    }

    private func installSynchronously(definitionID: String) throws -> String {
        guard let descriptor = Self.descriptor(for: definitionID), let formula = descriptor.formula else {
            throw ServiceRuntimeError.unsupported(Self.displayName(for: definitionID))
        }
        guard let brew = Self.brewURL() else { throw ServiceRuntimeError.packageManagerMissing }
        let packageCommand = executableURL(for: definitionID) == nil ? "install" : "upgrade"
        var install = try Self.run(
            brew,
            arguments: [packageCommand, formula],
            environment: Self.homebrewEnvironment
        )
        if install.status != 0,
           let trustTarget = Self.formulaTrustTarget(from: install.output, expectedFormula: formula) {
            let trust = try Self.run(
                brew,
                arguments: ["trust", "--formula", trustTarget],
                environment: Self.homebrewEnvironment
            )
            guard trust.status == 0 else {
                throw ServiceRuntimeError.commandFailed(CommandFailureReporter.recordAndSummarize(
                    trust.output,
                    operation: "brew trust --formula \(trustTarget)",
                    rootURL: rootURL
                ))
            }
            install = try Self.run(
                brew,
                arguments: [packageCommand, formula],
                environment: Self.homebrewEnvironment
            )
        }
        if install.status != 0,
           let recovery = Self.databaseConflictRecoveryPlan(
               from: install.output,
               installing: formula,
               packageCommand: packageCommand
           ) {
            install = try installResolvingDatabaseConflict(
                recovery,
                initialOutput: install.output,
                brew: brew
            )
        }
        guard install.status == 0 else {
            throw ServiceRuntimeError.commandFailed(CommandFailureReporter.recordAndSummarize(
                install.output,
                operation: "brew \(packageCommand) \(formula)",
                rootURL: rootURL
            ))
        }
        let prefix = try Self.run(brew, arguments: ["--prefix", formula], environment: Self.homebrewEnvironment)
        guard prefix.status == 0 else {
            throw ServiceRuntimeError.commandFailed(CommandFailureReporter.recordAndSummarize(
                prefix.output,
                operation: "brew --prefix \(formula)",
                rootURL: rootURL
            ))
        }

        let source = URL(
            fileURLWithPath: prefix.output.trimmingCharacters(in: .whitespacesAndNewlines),
            isDirectory: true
        )
        guard fileManager.isExecutableFile(atPath: source.appendingPathComponent(descriptor.executable).path) else {
            throw ServiceRuntimeError.commandFailed("Homebrew did not return a usable service runtime.")
        }

        let runtimeRoot = rootURL.appendingPathComponent("Runtimes/services", isDirectory: true)
        try fileManager.createDirectory(at: runtimeRoot, withIntermediateDirectories: true)
        let destination = runtimeRoot.appendingPathComponent(definitionID, isDirectory: true)
        if fileManager.fileExists(atPath: destination.path)
            || (try? destination.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true {
            try fileManager.removeItem(at: destination)
        }
        try fileManager.createSymbolicLink(at: destination, withDestinationURL: source)
        let version = try Self.run(
            brew,
            arguments: ["list", "--versions", formula],
            environment: Self.homebrewEnvironment
        )
        guard version.status == 0,
              let installedVersion = version.output.split(whereSeparator: \.isWhitespace).last.map(String.init) else {
            throw ServiceRuntimeError.commandFailed("Homebrew did not report the installed service version.")
        }
        return installedVersion
    }

    private func installResolvingDatabaseConflict(
        _ plan: DatabaseConflictRecoveryPlan,
        initialOutput: String,
        brew: URL
    ) throws -> (status: Int32, output: String) {
        let environment = Self.homebrewEnvironment
        _ = CommandFailureReporter.recordAndSummarize(
            initialOutput,
            operation: "brew \(plan.packageCommand) \(plan.installingFormula)",
            rootURL: rootURL
        )

        let unlink = try Self.run(brew, arguments: plan.unlinkConflictArguments, environment: environment)
        guard unlink.status == 0 else {
            let relink = try? Self.run(
                brew,
                arguments: plan.restoreArguments[1],
                environment: environment
            )
            throw conciseCommandFailure(
                output: [unlink.output, relink?.output]
                    .compactMap { $0 }
                    .filter { !$0.isEmpty }
                    .joined(separator: "\n"),
                operation: "brew unlink \(plan.conflictingFormula)",
                message: "HerdMe could not temporarily unlink \(plan.conflictingFormula)."
            )
        }

        var retry: (status: Int32, output: String)?
        var retryError: Error?
        do {
            retry = try Self.run(brew, arguments: plan.retryInstallArguments, environment: environment)
        } catch {
            retryError = error
        }

        let unlinkInstalled = try? Self.run(
            brew,
            arguments: plan.restoreArguments[0],
            environment: environment
        )
        let relinkOriginal: (status: Int32, output: String)?
        var relinkError: Error?
        do {
            relinkOriginal = try Self.run(
                brew,
                arguments: plan.restoreArguments[1],
                environment: environment
            )
        } catch {
            relinkOriginal = nil
            relinkError = error
        }

        guard relinkOriginal?.status == 0 else {
            let restorationOutput = [
                unlinkInstalled?.output,
                relinkOriginal?.output,
                retry?.output,
                retryError?.localizedDescription,
                relinkError?.localizedDescription
            ].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: "\n")
            throw conciseCommandFailure(
                output: restorationOutput,
                operation: "restore Homebrew link for \(plan.conflictingFormula)",
                message: "HerdMe could not restore \(plan.conflictingFormula)'s Homebrew links. Run brew unlink \(plan.installingFormula), then brew link \(plan.conflictingFormula)."
            )
        }

        if let retryError {
            throw conciseCommandFailure(
                output: retryError.localizedDescription,
                operation: "brew \(plan.packageCommand) \(plan.installingFormula)",
                message: "\(Self.displayName(for: plan.installingFormula)) could not be installed. \(Self.displayName(for: plan.conflictingFormula)) was linked again."
            )
        }
        guard let retry, retry.status == 0 else {
            throw conciseCommandFailure(
                output: retry?.output ?? "",
                operation: "brew \(plan.packageCommand) \(plan.installingFormula)",
                message: "\(Self.displayName(for: plan.installingFormula)) could not be installed. \(Self.displayName(for: plan.conflictingFormula)) was linked again."
            )
        }
        return retry
    }

    private func conciseCommandFailure(output: String, operation: String, message: String) -> ServiceRuntimeError {
        _ = CommandFailureReporter.recordAndSummarize(output, operation: operation, rootURL: rootURL)
        return .commandFailed(message + " Full output is available in Logs/homebrew.log.")
    }

    private func startManaged(
        _ instance: ServiceInstance,
        allowCredentialInteraction: Bool
    ) async throws {
        if state(for: instance) == .running { return }
        guard instance.port > 0, instance.port <= 65_535,
              LocalEnvironmentEngine.canBind(port: instance.port) else {
            throw ServiceRuntimeError.portUnavailable(instance.port)
        }
        guard let executable = executableURL(for: instance.definitionID) else {
            throw ServiceRuntimeError.notInstalled(instance.name)
        }

        let dataURL = dataDirectory(for: instance)
        let logsURL = rootURL.appendingPathComponent("Log/services", isDirectory: true)
        try fileManager.createDirectory(at: dataURL, withIntermediateDirectories: true)
        try fileManager.createDirectory(at: logsURL, withIntermediateDirectories: true)
        let credentials = try credentialsIfRequired(
            for: instance,
            allowInteraction: allowCredentialInteraction
        )
        try prepareDataIfNeeded(
            instance: instance,
            executable: executable,
            dataURL: dataURL,
            credentials: credentials
        )
        if instance.definitionID == "mysql" || instance.definitionID == "mariadb" {
            try? fileManager.removeItem(atPath: Self.databaseSocketPath(for: instance.id))
        }

        let logURL = logsURL.appendingPathComponent(instance.id.uuidString + ".log")
        try LogRotation.rotateIfNeeded(logURL)
        if !fileManager.fileExists(atPath: logURL.path) {
            fileManager.createFile(atPath: logURL.path, contents: nil)
        }
        let outputHandle = try FileHandle(forWritingTo: logURL)
        try outputHandle.seekToEnd()
        try outputHandle.write(contentsOf: Data("\n[HerdMe] Starting \(instance.name) on port \(instance.port)\n".utf8))

        let process = Process()
        process.executableURL = executable
        process.currentDirectoryURL = dataURL
        let peeringPort: Int?
        if instance.definitionID == "typesense" {
            guard let preferredPort = Self.typesensePeeringPort(apiPort: instance.port),
                  LocalEnvironmentEngine.canBind(port: preferredPort) else {
                try? outputHandle.close()
                throw ServiceRuntimeError.portUnavailable(
                    Self.typesensePeeringPort(apiPort: instance.port) ?? instance.port
                )
            }
            peeringPort = preferredPort
        } else {
            peeringPort = nil
        }
        let consolePort: Int?
        if instance.definitionID == "minio" || instance.definitionID == "rustfs" {
            let preferredPort = instance.port < 65_535 ? instance.port + 1 : 49_152
            guard let available = LocalEnvironmentEngine.availablePort(startingAt: preferredPort) else {
                try? outputHandle.close()
                throw ServiceRuntimeError.commandFailed("No loopback port is available for the service console.")
            }
            consolePort = available
        } else {
            consolePort = nil
        }
        process.arguments = arguments(
            for: instance,
            dataURL: dataURL,
            consolePort: consolePort,
            peeringPort: peeringPort,
            credentials: credentials
        )
        var environment = ProcessInfo.processInfo.environment
        let runtimeBin = executable.deletingLastPathComponent().path
        environment["PATH"] = runtimeBin + ":" + rootURL.appendingPathComponent("bin").path
            + ":" + (environment["PATH"] ?? "")
        if instance.definitionID == "minio" {
            environment["MINIO_ROOT_USER"] = credentials?.username
            environment["MINIO_ROOT_PASSWORD"] = credentials?.secret
        } else if instance.definitionID == "rustfs" {
            environment["RUSTFS_ACCESS_KEY"] = credentials?.username
            environment["RUSTFS_SECRET_KEY"] = credentials?.secret
        }
        process.environment = environment
        process.standardOutput = outputHandle
        process.standardError = outputHandle
        try process.run()
        let becameReady: Bool
        do {
            becameReady = try await waitUntilReady(process: process, port: instance.port)
        } catch {
            await AsyncProcessLifecycle.terminate(process)
            try? outputHandle.close()
            throw error
        }
        guard becameReady else {
            let exitedEarly = !process.isRunning
            await AsyncProcessLifecycle.terminate(process)
            try? outputHandle.close()
            throw exitedEarly
                ? ServiceRuntimeError.processFailed(instance.name)
                : ServiceRuntimeError.readinessTimedOut(instance.name, instance.port)
        }

        if let credentials, DatabaseServiceAuthenticator.protectedDefinitions.contains(instance.definitionID) {
            do {
                try await DatabaseServiceAuthenticator.secure(
                    instance: instance,
                    executable: executable,
                    dataURL: dataURL,
                    credentials: credentials,
                    fileManager: fileManager
                )
            } catch {
                await AsyncProcessLifecycle.terminate(process)
                try? outputHandle.close()
                throw error
            }
        }

        do {
            try Data("\(process.processIdentifier)\n".utf8).write(
                to: pidFileURL(for: instance),
                options: .atomic
            )
        } catch {
            await AsyncProcessLifecycle.terminate(process)
            try? outputHandle.close()
            throw error
        }

        register(
            process: process,
            outputHandle: outputHandle,
            consolePort: consolePort,
            for: instance.id
        )
    }

    private func waitUntilReady(process: Process, port: Int) async throws -> Bool {
        let deadline = ProcessInfo.processInfo.systemUptime + readinessTimeout
        while process.isRunning {
            try Task.checkCancellation()
            if readinessProbe(port) { return true }
            if ProcessInfo.processInfo.systemUptime >= deadline { return false }
            try await Task.sleep(for: .milliseconds(100))
        }
        return false
    }

    private func recordStartFailure(instance: ServiceInstance, error: Error) {
        let logsURL = rootURL.appendingPathComponent("Log/services", isDirectory: true)
        let logURL = logsURL.appendingPathComponent(instance.id.uuidString + ".log")
        let message = "[HerdMe] Failed to start \(instance.name): \(error.localizedDescription)\n"
        do {
            try fileManager.createDirectory(at: logsURL, withIntermediateDirectories: true)
            try LogRotation.rotateIfNeeded(logURL)
            if !fileManager.fileExists(atPath: logURL.path) {
                try Data(message.utf8).write(to: logURL, options: .atomic)
                return
            }
            let handle = try FileHandle(forWritingTo: logURL)
            defer { try? handle.close() }
            try handle.seekToEnd()
            try handle.write(contentsOf: Data(message.utf8))
        } catch {
            return
        }
    }

    private func prepareDataIfNeeded(
        instance: ServiceInstance,
        executable: URL,
        dataURL: URL,
        credentials: ServiceCredentials?
    ) throws {
        let command: (URL, [String])?
        switch instance.definitionID {
        case "mysql":
            guard !fileManager.fileExists(atPath: dataURL.appendingPathComponent("mysql").path) else { return }
            command = (executable, ["--no-defaults", "--initialize-insecure", "--datadir=\(dataURL.path)"])
        case "mariadb":
            guard !fileManager.fileExists(atPath: dataURL.appendingPathComponent("mysql").path) else { return }
            let installer = executable.deletingLastPathComponent().appendingPathComponent("mariadb-install-db")
            command = fileManager.isExecutableFile(atPath: installer.path)
                ? (installer, ["--no-defaults", "--auth-root-authentication-method=normal", "--datadir=\(dataURL.path)"])
                : nil
        case "postgresql":
            guard !fileManager.fileExists(atPath: dataURL.appendingPathComponent("PG_VERSION").path),
                  let credentials else { return }
            let initdb = executable.deletingLastPathComponent().appendingPathComponent("initdb")
            let passwordURL = dataURL.deletingLastPathComponent()
                .appendingPathComponent(".herdme-initdb-\(UUID().uuidString).password")
            try Data((credentials.secret + "\n").utf8).write(to: passwordURL, options: [.atomic])
            try? fileManager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: passwordURL.path)
            defer { try? fileManager.removeItem(at: passwordURL) }
            let result = try Self.run(initdb, arguments: [
                "-D", dataURL.path,
                "--username=\(credentials.username)",
                "--pwfile=\(passwordURL.path)",
                "--auth-local=scram-sha-256",
                "--auth-host=scram-sha-256",
                "--encoding=UTF8"
            ])
            guard result.status == 0 else { throw ServiceRuntimeError.commandFailed(result.output) }
            return
        default:
            command = nil
        }
        guard let command else { return }
        let result = try Self.run(command.0, arguments: command.1)
        guard result.status == 0 else { throw ServiceRuntimeError.commandFailed(result.output) }
    }

    func arguments(
        for instance: ServiceInstance,
        dataURL: URL,
        consolePort: Int?,
        peeringPort: Int?,
        credentials: ServiceCredentials? = nil
    ) -> [String] {
        switch instance.definitionID {
        case "redis", "valkey":
            return [
                "--bind", "127.0.0.1", "--port", String(instance.port), "--dir", dataURL.path,
                "--protected-mode", "yes", "--daemonize", "no", "--appendonly", "yes"
            ]
        case "mysql":
            return [
                "--no-defaults", "--datadir=\(dataURL.path)", "--port=\(instance.port)",
                "--bind-address=127.0.0.1",
                "--mysqlx=0",
                "--socket=\(Self.databaseSocketPath(for: instance.id))",
                "--pid-file=\(dataURL.appendingPathComponent("mysql.pid").path)"
            ]
        case "mariadb":
            return [
                "--no-defaults", "--datadir=\(dataURL.path)", "--port=\(instance.port)",
                "--bind-address=127.0.0.1",
                "--socket=\(Self.databaseSocketPath(for: instance.id))",
                "--pid-file=\(dataURL.appendingPathComponent("mysql.pid").path)"
            ]
        case "postgresql":
            return ["-D", dataURL.path, "-h", "127.0.0.1", "-p", String(instance.port)]
        case "mongodb":
            return ["--dbpath", dataURL.path, "--bind_ip", "127.0.0.1", "--port", String(instance.port)]
        case "meilisearch":
            return [
                "--http-addr", "127.0.0.1:\(instance.port)", "--db-path", dataURL.path,
                "--no-analytics", "true"
            ]
        case "typesense":
            guard let peeringPort, let credentials else { return [] }
            return [
                "--data-dir", dataURL.path, "--api-key", credentials.secret,
                "--api-address", "127.0.0.1", "--api-port", String(instance.port),
                "--peering-address", "127.0.0.1", "--peering-port", String(peeringPort),
                "--enable-cors"
            ]
        case "minio":
            guard let consolePort else { return [] }
            return [
                "server", dataURL.path, "--address", "127.0.0.1:\(instance.port)",
                "--console-address", "127.0.0.1:\(consolePort)"
            ]
        case "rustfs":
            guard let consolePort else { return [] }
            return [
                "server", dataURL.path, "--address", "127.0.0.1:\(instance.port)",
                "--console-address", "127.0.0.1:\(consolePort)"
            ]
        default:
            return []
        }
    }

    private func credentialsIfRequired(
        for instance: ServiceInstance,
        allowInteraction: Bool
    ) throws -> ServiceCredentials? {
        guard Self.requiresCredentials(definitionID: instance.definitionID) else { return nil }
        return try credentialStore.credentials(
            for: instance.id,
            allowInteraction: allowInteraction
        )
    }

    static func requiresCredentials(definitionID: String) -> Bool {
        ["mysql", "mariadb", "postgresql", "typesense", "minio", "rustfs"]
            .contains(definitionID)
    }

    private func executableURL(for definitionID: String) -> URL? {
        if let override = executableOverrides[definitionID], fileManager.isExecutableFile(atPath: override.path) {
            return override
        }
        guard let descriptor = Self.descriptor(for: definitionID) else { return nil }
        let candidate = rootURL.appendingPathComponent("Runtimes/services/\(definitionID)/\(descriptor.executable)")
        return fileManager.isExecutableFile(atPath: candidate.path) ? candidate.resolvingSymlinksInPath() : nil
    }

    private func stop(id: UUID) async {
        let handles = takeProcessHandles(id: id)
        defer { try? fileManager.removeItem(atPath: Self.databaseSocketPath(for: id)) }

        if let process = handles.process {
            await AsyncProcessLifecycle.terminate(process)
        } else if let adoptedPID = handles.adoptedPID {
            await AsyncProcessLifecycle.terminate(pid: adoptedPID)
        }
        try? handles.outputHandle?.close()
        removePIDFile(id: id)
    }

    private func activeIdentifiers() -> [UUID] {
        lock.lock()
        defer { lock.unlock() }
        return Array(Set(processes.keys).union(adoptedProcessIDs.keys))
    }

    private func register(
        process: Process,
        outputHandle: FileHandle,
        consolePort: Int?,
        for id: UUID
    ) {
        lock.lock()
        processes[id] = process
        adoptedProcessIDs[id] = nil
        outputHandles[id] = outputHandle
        consolePorts[id] = consolePort
        lock.unlock()
    }

    private func takeProcessHandles(id: UUID) -> ManagedProcessHandles {
        lock.lock()
        defer { lock.unlock() }
        let handles = ManagedProcessHandles(
            process: processes.removeValue(forKey: id),
            adoptedPID: adoptedProcessIDs.removeValue(forKey: id),
            outputHandle: outputHandles.removeValue(forKey: id)
        )
        consolePorts[id] = nil
        return handles
    }

    private func pidFileURL(for instance: ServiceInstance) -> URL {
        dataDirectory(for: instance).appendingPathComponent(".herdme.pid")
    }

    private func persistedProcessID(for instance: ServiceInstance) -> pid_t? {
        guard let value = try? String(contentsOf: pidFileURL(for: instance), encoding: .utf8),
              let pid = pid_t(value.trimmingCharacters(in: .whitespacesAndNewlines)),
              pid > 1 else {
            return nil
        }
        return pid
    }

    private func removePIDFile(for instance: ServiceInstance) {
        try? fileManager.removeItem(at: pidFileURL(for: instance))
    }

    private func removePIDFile(id: UUID) {
        let url = rootURL.appendingPathComponent("Services/\(id.uuidString)/data/.herdme.pid")
        try? fileManager.removeItem(at: url)
    }

    private func isManagedProcess(_ pid: pid_t, for instance: ServiceInstance) -> Bool {
        guard Darwin.kill(pid, 0) == 0,
              let executable = executableURL(for: instance.definitionID),
              let actualExecutable = Self.processExecutablePath(pid: pid),
              let currentDirectory = Self.processCurrentDirectory(pid: pid) else {
            return false
        }

        let expectedExecutable = executable.resolvingSymlinksInPath().standardizedFileURL.path
        let normalizedActualExecutable = URL(fileURLWithPath: actualExecutable)
            .resolvingSymlinksInPath().standardizedFileURL.path
        let scriptArguments = Self.processArguments(pid: pid) ?? []
        let executableMatches = normalizedActualExecutable == expectedExecutable
            || scriptArguments.prefix(2).contains { argument in
                URL(fileURLWithPath: argument).resolvingSymlinksInPath().standardizedFileURL.path
                    == expectedExecutable
            }
        let expectedDataPath = dataDirectory(for: instance).standardizedFileURL.path
        let actualDataPath = URL(fileURLWithPath: currentDirectory).standardizedFileURL.path
        return executableMatches && actualDataPath == expectedDataPath
    }

    private func removeRuntimeArtifacts(id: UUID) {
        try? fileManager.removeItem(atPath: Self.databaseSocketPath(for: id))
        removePIDFile(id: id)
    }

    private static func processArguments(pid: pid_t) -> [String]? {
        var mib = [CTL_KERN, KERN_PROCARGS2, pid]
        var size = 0
        guard sysctl(&mib, u_int(mib.count), nil, &size, nil, 0) == 0,
              size > MemoryLayout<Int32>.size else {
            return nil
        }

        var bytes = [UInt8](repeating: 0, count: size)
        guard sysctl(&mib, u_int(mib.count), &bytes, &size, nil, 0) == 0 else {
            return nil
        }
        bytes = Array(bytes.prefix(size))

        var argumentCount: Int32 = 0
        withUnsafeMutableBytes(of: &argumentCount) { destination in
            bytes.withUnsafeBytes { source in
                destination.copyBytes(from: source.prefix(MemoryLayout<Int32>.size))
            }
        }
        guard argumentCount > 0 else { return nil }

        var offset = MemoryLayout<Int32>.size
        while offset < bytes.count, bytes[offset] != 0 { offset += 1 }
        while offset < bytes.count, bytes[offset] == 0 { offset += 1 }

        var arguments: [String] = []
        for _ in 0..<Int(argumentCount) where offset < bytes.count {
            let start = offset
            while offset < bytes.count, bytes[offset] != 0 { offset += 1 }
            arguments.append(String(decoding: bytes[start..<offset], as: UTF8.self))
            while offset < bytes.count, bytes[offset] == 0 { offset += 1 }
        }
        return arguments
    }

    private static func processExecutablePath(pid: pid_t) -> String? {
        var buffer = [CChar](repeating: 0, count: Int(MAXPATHLEN) * 4)
        let length = proc_pidpath(pid, &buffer, UInt32(buffer.count))
        guard length > 0 else { return nil }
        return String(
            decoding: buffer.prefix(Int(length)).map { UInt8(bitPattern: $0) },
            as: UTF8.self
        )
    }

    private static func processCurrentDirectory(pid: pid_t) -> String? {
        var info = proc_vnodepathinfo()
        let expectedSize = Int32(MemoryLayout<proc_vnodepathinfo>.size)
        let actualSize = withUnsafeMutablePointer(to: &info) {
            proc_pidinfo(pid, PROC_PIDVNODEPATHINFO, 0, $0, expectedSize)
        }
        guard actualSize == expectedSize else { return nil }
        return withUnsafePointer(to: &info.pvi_cdir.vip_path) { path in
            path.withMemoryRebound(to: CChar.self, capacity: Int(MAXPATHLEN)) {
                let bytes = UnsafeBufferPointer(start: $0, count: Int(MAXPATHLEN))
                    .prefix { $0 != 0 }
                    .map { UInt8(bitPattern: $0) }
                return String(decoding: bytes, as: UTF8.self)
            }
        }
    }

    static func databaseSocketPath(for id: UUID) -> String {
        "/tmp/herdme-\(id.uuidString.lowercased()).sock"
    }

    static func typesensePeeringPort(apiPort: Int) -> Int? {
        guard apiPort > 0, apiPort <= 65_535 else { return nil }
        return apiPort == 1 ? 2 : apiPort - 1
    }

    static func consolePort(from arguments: [String]) -> Int? {
        guard let index = arguments.firstIndex(of: "--console-address"),
              arguments.indices.contains(index + 1) else { return nil }
        let endpoint = arguments[index + 1].split(separator: ":", omittingEmptySubsequences: false)
        guard endpoint.count == 2,
              endpoint[0] == "127.0.0.1",
              let port = Int(endpoint[1]),
              port > 0,
              port <= 65_535 else { return nil }
        return port
    }

    private static func descriptor(for identifier: String) -> Descriptor? {
        switch identifier {
        case "mariadb": Descriptor(formula: "mariadb", executable: "bin/mariadbd")
        case "mysql": Descriptor(formula: "mysql", executable: "bin/mysqld")
        case "postgresql": Descriptor(formula: "postgresql@18", executable: "bin/postgres")
        case "mongodb": Descriptor(formula: "mongodb/brew/mongodb-community@7.0", executable: "bin/mongod")
        case "redis": Descriptor(formula: "redis", executable: "bin/redis-server")
        case "valkey": Descriptor(formula: "valkey", executable: "bin/valkey-server")
        case "meilisearch": Descriptor(formula: "meilisearch", executable: "bin/meilisearch")
        case "typesense": Descriptor(formula: "typesense/tap/typesense-server", executable: "bin/typesense-server")
        case "minio": Descriptor(formula: "minio/stable/minio", executable: "bin/minio")
        case "rustfs": Descriptor(formula: "rustfs/tap/rustfs", executable: "bin/rustfs")
        default: nil
        }
    }

    private static func displayName(for identifier: String) -> String {
        ServiceCatalog.all.first(where: { $0.id == identifier })?.name ?? identifier
    }

    static func supports(definitionID: String) -> Bool {
        descriptor(for: definitionID)?.formula != nil
    }

    static func formulaTrustTarget(from output: String, expectedFormula: String) -> String? {
        HomebrewFormulaTrust.target(from: output, expectedFormula: expectedFormula)
    }

    static func databaseConflictRecoveryPlan(
        from output: String,
        installing formula: String,
        packageCommand: String = "install"
    ) -> DatabaseConflictRecoveryPlan? {
        let conflicting: String
        switch formula {
        case "mysql": conflicting = "mariadb"
        case "mariadb": conflicting = "mysql"
        default: return nil
        }
        let normalized = output
            .replacingOccurrences(
                of: #"\x{1B}\[[0-?]*[ -/]*[@-~]"#,
                with: "",
                options: .regularExpression
            )
            .lowercased()
        guard normalized.contains("cannot install \(formula)"),
              normalized.contains("conflicting formula"),
              normalized.contains("brew unlink \(conflicting)") else {
            return nil
        }
        return DatabaseConflictRecoveryPlan(
            installingFormula: formula,
            conflictingFormula: conflicting,
            packageCommand: packageCommand
        )
    }

    static func outdatedFormulaNames(from output: String) throws -> Set<String> {
        guard let start = output.firstIndex(of: "{"),
              let end = output.lastIndex(of: "}") else {
            throw ServiceRuntimeError.commandFailed("Homebrew returned an invalid update response.")
        }
        let payload = Data(output[start...end].utf8)
        let index = try JSONDecoder().decode(HomebrewOutdatedIndex.self, from: payload)
        return Set(index.formulae.map(\.name))
    }

    private static func outdatedDefinitionIDsSynchronously() throws -> Set<String> {
        guard let brew = brewURL() else { throw ServiceRuntimeError.packageManagerMissing }
        let result = try run(
            brew,
            arguments: ["outdated", "--json=v2"],
            environment: homebrewEnvironment
        )
        guard result.status == 0 else {
            throw ServiceRuntimeError.commandFailed(result.output)
        }
        let names = try outdatedFormulaNames(from: result.output)
        return Set(ServiceCatalog.all.compactMap { definition in
            guard let formula = descriptor(for: definition.id)?.formula else { return nil }
            let shortName = formula.split(separator: "/").last.map(String.init) ?? formula
            return names.contains(formula) || names.contains(shortName) ? definition.id : nil
        })
    }

    private static func brewURL() -> URL? {
        ["/opt/homebrew/bin/brew", "/usr/local/bin/brew"]
            .map { URL(fileURLWithPath: $0) }
            .first { FileManager.default.isExecutableFile(atPath: $0.path) }
    }

    private static var homebrewEnvironment: [String: String] {
        var environment = ProcessInfo.processInfo.environment
        environment["HOME"] = FileManager.default.homeDirectoryForCurrentUser.path
        environment["USER"] = NSUserName()
        environment["LOGNAME"] = NSUserName()
        environment["PATH"] = [
            "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin"
        ].joined(separator: ":")
        return environment
    }

    private static func run(
        _ executable: URL,
        arguments: [String],
        environment: [String: String]? = nil
    ) throws -> (status: Int32, output: String) {
        let result = try ProcessRunner.run(
            executable,
            arguments: arguments,
            environment: environment
        )
        return (
            result.status,
            result.output.trimmingCharacters(in: .whitespacesAndNewlines)
        )
    }
}
