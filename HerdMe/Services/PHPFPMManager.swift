import Darwin
import Foundation

enum PHPFPMError: LocalizedError {
    case executableMissing
    case startupFailed(String)

    var errorDescription: String? {
        switch self {
        case .executableMissing:
            "Install a HerdMe-managed PHP runtime with PHP-FPM before starting sites."
        case let .startupFailed(logPath):
            "PHP-FPM could not be started. Check \(logPath) for details."
        }
    }
}

final class PHPFPMManager: @unchecked Sendable {
    private struct Runtime {
        let process: Process
        let outputHandle: FileHandle
        let port: Int
    }

    private let rootURL: URL
    private let fileManager: FileManager
    private let lock = NSLock()
    private var runtimes: [String: Runtime] = [:]

    init(rootURL: URL, fileManager: FileManager = .default) {
        self.rootURL = rootURL
        self.fileManager = fileManager
        cleanupStaleProcesses()
    }

    var isRunning: Bool {
        lock.lock()
        defer { lock.unlock() }
        return runtimes.values.contains(where: { $0.process.isRunning })
    }

    func start(
        executable: URL,
        identifier: String,
        preferredPort: Int,
        phpOptions: [String: String] = [:]
    ) throws -> Int {
        guard fileManager.isExecutableFile(atPath: executable.path) else {
            throw PHPFPMError.executableMissing
        }

        let key = Self.safeName(identifier)
        lock.lock()
        if let runtime = runtimes[key], runtime.process.isRunning {
            lock.unlock()
            return runtime.port
        }
        let stale = runtimes.removeValue(forKey: key)
        lock.unlock()
        try? stale?.outputHandle.close()

        guard let port = LocalEnvironmentEngine.availablePort(startingAt: preferredPort) else {
            throw LocalEnvironmentError.noAvailablePort
        }

        let configurationDirectory = rootURL.appendingPathComponent("Runtime/fpm", isDirectory: true)
        let logDirectory = rootURL.appendingPathComponent("Log/fpm", isDirectory: true)
        try fileManager.createDirectory(at: configurationDirectory, withIntermediateDirectories: true)
        try fileManager.createDirectory(at: logDirectory, withIntermediateDirectories: true)
        if let xdebugLog = phpOptions["xdebug.log"] {
            try fileManager.createDirectory(
                at: URL(fileURLWithPath: xdebugLog).deletingLastPathComponent(),
                withIntermediateDirectories: true
            )
        }

        let configurationURL = configurationDirectory.appendingPathComponent("\(key).conf")
        let pidURL = configurationDirectory.appendingPathComponent("\(key).pid")
        let errorLogURL = logDirectory.appendingPathComponent("\(key).log")
        let outputLogURL = logDirectory.appendingPathComponent("\(key)-output.log")
        let configuration = Self.configuration(
            port: port,
            pidURL: pidURL,
            errorLogURL: errorLogURL
        )
        try configuration.write(to: configurationURL, atomically: true, encoding: .utf8)
        if !fileManager.fileExists(atPath: outputLogURL.path) {
            fileManager.createFile(atPath: outputLogURL.path, contents: nil)
        }
        let outputHandle = try FileHandle(forWritingTo: outputLogURL)
        try outputHandle.seekToEnd()
        try outputHandle.write(contentsOf: Data("\n[HerdMe] Starting PHP-FPM on 127.0.0.1:\(port)\n".utf8))

        let process = Process()
        process.executableURL = executable
        process.currentDirectoryURL = rootURL
        var arguments: [String] = []
        for (name, value) in phpOptions.sorted(by: { $0.key < $1.key }) {
            arguments.append(contentsOf: ["-d", "\(name)=\(value)"])
        }
        arguments.append(contentsOf: ["--nodaemonize", "--fpm-config", configurationURL.path])
        process.arguments = arguments
        var environment = ProcessInfo.processInfo.environment
        environment["PATH"] = rootURL.appendingPathComponent("bin").path
            + ":" + (environment["PATH"] ?? "")
        process.environment = environment
        process.standardOutput = outputHandle
        process.standardError = outputHandle

        do {
            try process.run()
            for _ in 0..<150 {
                if Self.canConnect(port: port) { break }
                if !process.isRunning { break }
                usleep(20_000)
            }
            guard process.isRunning, Self.canConnect(port: port) else {
                if process.isRunning { process.terminate() }
                try? outputHandle.close()
                throw PHPFPMError.startupFailed(outputLogURL.path)
            }
        } catch {
            if process.isRunning { process.terminate() }
            try? outputHandle.close()
            throw error
        }

        lock.lock()
        runtimes[key] = Runtime(process: process, outputHandle: outputHandle, port: port)
        lock.unlock()
        return port
    }

    func stopAll() {
        lock.lock()
        let active = Array(runtimes.values)
        runtimes.removeAll()
        lock.unlock()

        for runtime in active {
            if runtime.process.isRunning {
                runtime.process.terminate()
                for _ in 0..<80 where runtime.process.isRunning { usleep(25_000) }
                if runtime.process.isRunning {
                    Darwin.kill(runtime.process.processIdentifier, SIGKILL)
                }
            }
            try? runtime.outputHandle.close()
        }
    }

    private func cleanupStaleProcesses() {
        let configurationDirectory = rootURL.appendingPathComponent("Runtime/fpm", isDirectory: true)
        let pidFiles = ((try? fileManager.contentsOfDirectory(
            at: configurationDirectory,
            includingPropertiesForKeys: nil
        )) ?? []).filter { $0.pathExtension == "pid" }

        for pidURL in pidFiles {
            defer { try? fileManager.removeItem(at: pidURL) }
            guard let text = try? String(contentsOf: pidURL, encoding: .utf8),
                  let pid = Int32(text.trimmingCharacters(in: .whitespacesAndNewlines)),
                  pid > 1 else {
                continue
            }
            let command = Self.command(for: pid)
            guard command.contains(configurationDirectory.path), command.contains("php-fpm") else {
                continue
            }
            Darwin.kill(pid, SIGTERM)
            for _ in 0..<40 where Darwin.kill(pid, 0) == 0 { usleep(25_000) }
            if Darwin.kill(pid, 0) == 0 { Darwin.kill(pid, SIGKILL) }
        }
    }

    private static func command(for pid: Int32) -> String {
        let process = Process()
        let output = Pipe()
        process.executableURL = URL(fileURLWithPath: "/bin/ps")
        process.arguments = ["-p", String(pid), "-o", "command="]
        process.standardOutput = output
        process.standardError = Pipe()
        do {
            try process.run()
            let data = output.fileHandleForReading.readDataToEndOfFile()
            process.waitUntilExit()
            return process.terminationStatus == 0 ? String(decoding: data, as: UTF8.self) : ""
        } catch {
            return ""
        }
    }

    private static func configuration(port: Int, pidURL: URL, errorLogURL: URL) -> String {
        let pid = quoted(pidURL.path)
        let errorLog = quoted(errorLogURL.path)
        return """
        [global]
        pid = \(pid)
        error_log = \(errorLog)
        daemonize = no
        log_level = notice

        [herdme]
        listen = 127.0.0.1:\(port)
        listen.allowed_clients = 127.0.0.1
        pm = dynamic
        pm.max_children = 12
        pm.start_servers = 2
        pm.min_spare_servers = 1
        pm.max_spare_servers = 4
        pm.max_requests = 500
        clear_env = no
        catch_workers_output = yes
        decorate_workers_output = no
        security.limit_extensions = .php
        php_admin_flag[log_errors] = on
        php_admin_value[error_log] = \(errorLog)
        """
    }

    private static func quoted(_ value: String) -> String {
        "\"" + value
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"") + "\""
    }

    private static func safeName(_ value: String) -> String {
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-_."))
        let mapped = value.unicodeScalars.map { allowed.contains($0) ? Character(String($0)) : "-" }
        return String(mapped)
    }

    private static func canConnect(port: Int) -> Bool {
        let descriptor = socket(AF_INET, SOCK_STREAM, 0)
        guard descriptor >= 0 else { return false }
        defer { Darwin.close(descriptor) }
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = in_port_t(UInt16(port).bigEndian)
        address.sin_addr = in_addr(s_addr: inet_addr("127.0.0.1"))
        return withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.connect(descriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) == 0
            }
        }
    }
}
