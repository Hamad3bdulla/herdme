import Foundation

struct SiteDatabaseProvisioning: Sendable {
    let service: ServiceInstance
    let databaseName: String
    let username: String
    let environmentURL: URL
}

struct SiteDatabaseInspection: Sendable {
    let serverVersion: String
    let tableCount: Int
    let sizeBytes: Int64
    let responseMilliseconds: Double
}

enum SiteDatabaseError: LocalizedError {
    case unsupported
    case serviceStopped
    case clientMissing
    case commandFailed(String)
    case invalidResponse

    var errorDescription: String? {
        switch self {
        case .unsupported: String(localized: "Select a MySQL, MariaDB, or PostgreSQL service.")
        case .serviceStopped: String(localized: "Start the selected database service first.")
        case .clientMissing: String(localized: "The selected database runtime does not contain its command-line client.")
        case .commandFailed(let detail): detail
        case .invalidResponse: String(localized: "The database returned an invalid inspection response.")
        }
    }
}

struct SiteDatabaseManager: @unchecked Sendable {
    private let rootURL: URL
    private let fileManager: FileManager

    init(rootURL: URL, fileManager: FileManager = .default) {
        self.rootURL = rootURL
        self.fileManager = fileManager
    }

    func provision(
        site: SiteProject,
        instance: ServiceInstance,
        credentials: ServiceCredentials,
        client: URL
    ) async throws -> SiteDatabaseProvisioning {
        let manager = self
        return try await Task.detached(priority: .userInitiated) {
            let databaseName = manager.databaseName(for: site)
            try manager.createDatabase(
                databaseName,
                instance: instance,
                credentials: credentials,
                client: client
            )
            let variables = manager.environmentVariables(
                databaseName: databaseName,
                instance: instance,
                credentials: credentials
            )
            let update = try ServiceEnvironmentFile.update(
                projectURL: site.path,
                variables: variables,
                serviceName: instance.name,
                fileManager: manager.fileManager
            )
            return SiteDatabaseProvisioning(
                service: instance,
                databaseName: databaseName,
                username: credentials.username,
                environmentURL: update.environmentURL
            )
        }.value
    }

    func inspect(
        provisioning: SiteDatabaseProvisioning,
        credentials: ServiceCredentials,
        client: URL
    ) async throws -> SiteDatabaseInspection {
        let manager = self
        return try await Task.detached(priority: .userInitiated) {
            let started = Date.timeIntervalSinceReferenceDate
            let result = try manager.runInspection(
                provisioning: provisioning,
                credentials: credentials,
                client: client
            )
            let elapsed = (Date.timeIntervalSinceReferenceDate - started) * 1_000
            let lines = result.output.components(separatedBy: .newlines)
                .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
                .filter { !$0.isEmpty }
            guard lines.count >= 3,
                let tableCount = Int(lines[lines.count - 2]),
                let sizeBytes = Int64(lines[lines.count - 1])
            else { throw SiteDatabaseError.invalidResponse }
            return SiteDatabaseInspection(
                serverVersion: lines.dropLast(2).joined(separator: " "),
                tableCount: tableCount,
                sizeBytes: sizeBytes,
                responseMilliseconds: elapsed
            )
        }.value
    }

    func backup(
        site: SiteProject,
        provisioning: SiteDatabaseProvisioning,
        credentials: ServiceCredentials,
        client: URL,
        cancellation: SiteOperationCancellation
    ) async throws -> URL {
        let manager = self
        return try await Task.detached(priority: .userInitiated) {
            let directory = manager.rootURL.appendingPathComponent("Backups/Databases", isDirectory: true)
            try manager.fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
            let formatter = DateFormatter()
            formatter.locale = Locale(identifier: "en_US_POSIX")
            formatter.dateFormat = "yyyyMMdd-HHmmss"
            let target = directory.appendingPathComponent(
                "\(SiteProject.dnsLabel(for: site.name))-\(formatter.string(from: Date())).sql"
            )
            let executable: URL
            let arguments: [String]
            var environment = ProcessInfo.processInfo.environment
            switch provisioning.service.definitionID {
            case "mysql", "mariadb":
                let names = provisioning.service.definitionID == "mariadb" ? ["mariadb-dump", "mysqldump"] : ["mysqldump"]
                guard let dump = names.map({ client.deletingLastPathComponent().appendingPathComponent($0) })
                    .first(where: { manager.fileManager.isExecutableFile(atPath: $0.path) })
                else { throw SiteDatabaseError.clientMissing }
                executable = dump
                arguments = [
                    "--no-defaults", "--protocol=TCP", "--host=127.0.0.1",
                    "--port=\(provisioning.service.port)", "--user=\(credentials.username)",
                    "--single-transaction", "--routines", "--triggers", provisioning.databaseName
                ]
                environment["MYSQL_PWD"] = credentials.secret
            case "postgresql":
                let dump = client.deletingLastPathComponent().appendingPathComponent("pg_dump")
                guard manager.fileManager.isExecutableFile(atPath: dump.path) else { throw SiteDatabaseError.clientMissing }
                executable = dump
                arguments = [
                    "--host=127.0.0.1", "--port=\(provisioning.service.port)",
                    "--username=\(credentials.username)", "--no-password", "--format=plain",
                    "--no-owner", provisioning.databaseName
                ]
                environment["PGPASSWORD"] = credentials.secret
            default:
                throw SiteDatabaseError.unsupported
            }
            do {
                try manager.export(
                    executable: executable,
                    arguments: arguments,
                    environment: environment,
                    target: target,
                    cancellation: cancellation
                )
                return target
            } catch {
                try? manager.fileManager.removeItem(at: target)
                throw error
            }
        }.value
    }

    func connectionURL(
        provisioning: SiteDatabaseProvisioning,
        credentials: ServiceCredentials
    ) -> URL? {
        var components = URLComponents()
        switch provisioning.service.definitionID {
        case "mysql": components.scheme = "mysql"
        case "mariadb": components.scheme = "mariadb"
        case "postgresql": components.scheme = "postgresql"
        default: return nil
        }
        components.host = "127.0.0.1"
        components.port = provisioning.service.port
        components.user = credentials.username
        components.password = credentials.secret
        components.path = "/" + provisioning.databaseName
        return components.url
    }

    private func createDatabase(
        _ name: String,
        instance: ServiceInstance,
        credentials: ServiceCredentials,
        client: URL
    ) throws {
        let result: ProcessResult
        switch instance.definitionID {
        case "mysql", "mariadb":
            var environment = ProcessInfo.processInfo.environment
            environment["MYSQL_PWD"] = credentials.secret
            result = try ProcessRunner.run(
                client,
                arguments: [
                    "--no-defaults", "--protocol=TCP", "--host=127.0.0.1",
                    "--port=\(instance.port)", "--user=\(credentials.username)", "--batch",
                    "--execute=CREATE DATABASE IF NOT EXISTS `\(name)` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci"
                ],
                environment: environment,
                cancellationRequested: { Task.isCancelled }
            )
        case "postgresql":
            var environment = ProcessInfo.processInfo.environment
            environment["PGPASSWORD"] = credentials.secret
            let exists = try ProcessRunner.run(
                client,
                arguments: [
                    "--host=127.0.0.1", "--port=\(instance.port)",
                    "--username=\(credentials.username)", "--dbname=postgres", "--no-password",
                    "--tuples-only", "--no-align",
                    "--command=SELECT 1 FROM pg_database WHERE datname = '\(name)'"
                ],
                environment: environment,
                cancellationRequested: { Task.isCancelled }
            )
            guard exists.status == 0 else {
                throw SiteDatabaseError.commandFailed(exists.output.trimmingCharacters(in: .whitespacesAndNewlines))
            }
            if exists.output.split(whereSeparator: \.isWhitespace).contains("1") { return }
            result = try ProcessRunner.run(
                client,
                arguments: [
                    "--host=127.0.0.1", "--port=\(instance.port)",
                    "--username=\(credentials.username)", "--dbname=postgres", "--no-password",
                    "--set=ON_ERROR_STOP=1",
                    "--command=CREATE DATABASE \"\(name)\" OWNER \"\(credentials.username)\""
                ],
                environment: environment,
                cancellationRequested: { Task.isCancelled }
            )
        default:
            throw SiteDatabaseError.unsupported
        }
        guard result.status == 0 else {
            throw SiteDatabaseError.commandFailed(result.output.trimmingCharacters(in: .whitespacesAndNewlines))
        }
    }

    private func runInspection(
        provisioning: SiteDatabaseProvisioning,
        credentials: ServiceCredentials,
        client: URL
    ) throws -> ProcessResult {
        var environment = ProcessInfo.processInfo.environment
        let arguments: [String]
        switch provisioning.service.definitionID {
        case "mysql", "mariadb":
            environment["MYSQL_PWD"] = credentials.secret
            arguments = [
                "--no-defaults", "--protocol=TCP", "--host=127.0.0.1",
                "--port=\(provisioning.service.port)", "--user=\(credentials.username)",
                "--database=\(provisioning.databaseName)", "--batch", "--skip-column-names", "--raw",
                "--execute=SELECT VERSION(); SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE(); SELECT COALESCE(SUM(data_length + index_length), 0) FROM information_schema.tables WHERE table_schema = DATABASE();"
            ]
        case "postgresql":
            environment["PGPASSWORD"] = credentials.secret
            arguments = [
                "--host=127.0.0.1", "--port=\(provisioning.service.port)",
                "--username=\(credentials.username)", "--dbname=\(provisioning.databaseName)",
                "--no-password", "--tuples-only", "--no-align",
                "--command=SELECT version(); SELECT COUNT(*) FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog','information_schema'); SELECT COALESCE(pg_database_size(current_database()),0);"
            ]
        default:
            throw SiteDatabaseError.unsupported
        }
        let result = try ProcessRunner.run(
            client,
            arguments: arguments,
            environment: environment,
            cancellationRequested: { Task.isCancelled }
        )
        guard result.status == 0 else {
            throw SiteDatabaseError.commandFailed(result.output.trimmingCharacters(in: .whitespacesAndNewlines))
        }
        return result
    }

    private func environmentVariables(
        databaseName: String,
        instance: ServiceInstance,
        credentials: ServiceCredentials
    ) -> [ServiceEnvironmentVariable] {
        [
            ServiceEnvironmentVariable(key: "DB_CONNECTION", value: instance.definitionID == "postgresql" ? "pgsql" : "mysql"),
            ServiceEnvironmentVariable(key: "DB_HOST", value: "127.0.0.1"),
            ServiceEnvironmentVariable(key: "DB_PORT", value: String(instance.port)),
            ServiceEnvironmentVariable(key: "DB_DATABASE", value: databaseName),
            ServiceEnvironmentVariable(key: "DB_USERNAME", value: credentials.username),
            ServiceEnvironmentVariable(key: "DB_PASSWORD", value: credentials.secret)
        ]
    }

    private func databaseName(for site: SiteProject) -> String {
        let label = SiteProject.dnsLabel(for: site.name)
            .replacingOccurrences(of: "-", with: "_")
        return "herdme_" + label.prefix(48)
    }

    private func export(
        executable: URL,
        arguments: [String],
        environment: [String: String],
        target: URL,
        cancellation: SiteOperationCancellation
    ) throws {
        _ = fileManager.createFile(atPath: target.path, contents: nil)
        let output = try FileHandle(forWritingTo: target)
        defer { try? output.close() }
        let process = Process()
        let errorPipe = Pipe()
        process.executableURL = executable
        process.arguments = arguments
        process.environment = environment
        process.standardOutput = output
        process.standardError = errorPipe
        try process.run()
        while process.isRunning {
            if cancellation.isCancelled || Task.isCancelled {
                process.terminate()
                throw CancellationError()
            }
            Thread.sleep(forTimeInterval: 0.05)
        }
        let errorOutput = String(decoding: errorPipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        guard process.terminationStatus == 0 else {
            throw SiteDatabaseError.commandFailed(errorOutput.trimmingCharacters(in: .whitespacesAndNewlines))
        }
    }
}
