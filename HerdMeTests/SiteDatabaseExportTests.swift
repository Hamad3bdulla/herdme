import Foundation
import XCTest

@testable import HerdMe

final class SiteDatabaseExportTests: XCTestCase {
    func testExportSeparatesLargeDiagnosticsFromSQL() throws {
        try withFixture { root, target, manager in
            try manager.export(
                executable: URL(fileURLWithPath: "/bin/sh"),
                arguments: ["-c", "dd if=/dev/zero bs=1024 count=128 >&2; printf 'SELECT 1;'"],
                environment: ProcessInfo.processInfo.environment,
                target: target,
                cancellation: SiteOperationCancellation()
            )
            XCTAssertEqual(try String(contentsOf: target, encoding: .utf8), "SELECT 1;")
            XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: root.path), [target.lastPathComponent])
            let attributes = try FileManager.default.attributesOfItem(atPath: target.path)
            XCTAssertEqual((attributes[.posixPermissions] as? NSNumber)?.intValue, 0o600)
        }
    }

    func testFailedExportPreservesExistingBackupAndRemovesStagingFile() throws {
        try withFixture { root, target, manager in
            try Data("original".utf8).write(to: target)
            XCTAssertThrowsError(
                try manager.export(
                    executable: URL(fileURLWithPath: "/bin/sh"),
                    arguments: ["-c", "printf partial; printf failure >&2; exit 1"],
                    environment: ProcessInfo.processInfo.environment,
                    target: target,
                    cancellation: SiteOperationCancellation()
                )
            )
            XCTAssertEqual(try String(contentsOf: target, encoding: .utf8), "original")
            XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: root.path), [target.lastPathComponent])
        }
    }

    func testSuccessfulDumpDoesNotOverwriteAnExistingBackup() throws {
        try withFixture { root, target, manager in
            try Data("original".utf8).write(to: target)
            XCTAssertThrowsError(
                try manager.export(
                    executable: URL(fileURLWithPath: "/bin/sh"),
                    arguments: ["-c", "printf replacement"],
                    environment: ProcessInfo.processInfo.environment,
                    target: target,
                    cancellation: SiteOperationCancellation()
                )
            )
            XCTAssertEqual(try String(contentsOf: target, encoding: .utf8), "original")
            XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: root.path), [target.lastPathComponent])
        }
    }

    func testCancelledExportDoesNotCreateOrReplaceFiles() throws {
        try withFixture { root, target, manager in
            let cancellation = SiteOperationCancellation()
            cancellation.cancel()
            XCTAssertThrowsError(
                try manager.export(
                    executable: URL(fileURLWithPath: "/bin/sh"),
                    arguments: ["-c", "printf unexpected"],
                    environment: ProcessInfo.processInfo.environment,
                    target: target,
                    cancellation: cancellation
                )
            )
            XCTAssertTrue(try FileManager.default.contentsOfDirectory(atPath: root.path).isEmpty)
        }
    }

    func testCancellationDuringExportRemovesPartialOutput() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let target = root.appendingPathComponent("backup.sql")
        let started = root.appendingPathComponent("started")
        let cancellation = SiteOperationCancellation()
        let manager = SiteDatabaseManager(rootURL: root)
        let operation = Task.detached {
            try manager.export(
                executable: URL(fileURLWithPath: "/bin/sh"),
                arguments: ["-c", "printf partial; touch \"$1\"; sleep 5; printf complete", "sh", started.path],
                environment: ProcessInfo.processInfo.environment,
                target: target,
                cancellation: cancellation
            )
        }
        for _ in 0..<100 {
            if FileManager.default.fileExists(atPath: started.path) { break }
            try await Task.sleep(for: .milliseconds(20))
        }
        cancellation.cancel()
        do {
            try await operation.value
            XCTFail("A cancelled export must fail.")
        } catch let error as ProcessRunnerError {
            guard case .cancelled = error else { return XCTFail("Unexpected process error: \(error)") }
        }
        XCTAssertTrue(FileManager.default.fileExists(atPath: started.path))
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: root.path), ["started"])
    }

    private func withFixture(_ check: (URL, URL, SiteDatabaseManager) throws -> Void) throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try check(root, root.appendingPathComponent("backup.sql"), SiteDatabaseManager(rootURL: root))
    }

    func testSupportedDatabaseClientsProvisionInspectAndBackUpWithoutPasswordArguments() async throws {
        for definition in ["mysql", "mariadb", "postgresql"] {
            let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
            defer { try? FileManager.default.removeItem(at: root) }
            let client = root.appendingPathComponent("client")
            let script = #"""
                #!/bin/sh
                test -n "$MYSQL_PWD$PGPASSWORD" || exit 77
                printf '%s\n' "$*" >> "$(dirname "$0")/arguments.log"
                case "$*" in
                  *information_schema*) printf 'Fixture Database\n4\n2048\n' ;;
                  *pg_database*) exit 0 ;;
                esac
                """#
            try Data(script.utf8).write(to: client)
            try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: client.path)
            for name in ["mysqldump", "mariadb-dump", "pg_dump"] {
                let dump = root.appendingPathComponent(name)
                try Data("#!/bin/sh\nprintf 'SELECT 1;'\n".utf8).write(to: dump)
                try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: dump.path)
            }
            let site = SiteProject(path: root, name: "Database fixture", framework: "Laravel", isLinked: true)
            let instance = ServiceInstance(
                id: UUID(), definitionID: definition, name: definition, version: "fixture", port: 54321, isRunning: true
            )
            let credentials = ServiceCredentials(username: "fixture_user", secret: "fixture-password")
            let manager = SiteDatabaseManager(rootURL: root)
            let provisioning = try await manager.provision(site: site, instance: instance, credentials: credentials, client: client)
            let inspection = try await manager.inspect(provisioning: provisioning, credentials: credentials, client: client)
            XCTAssertEqual(inspection.serverVersion, "Fixture Database")
            XCTAssertEqual(inspection.tableCount, 4)
            XCTAssertEqual(inspection.sizeBytes, 2048)
            XCTAssertGreaterThanOrEqual(inspection.responseMilliseconds, 0)
            let backup = try await manager.backup(
                site: site, provisioning: provisioning, credentials: credentials, client: client,
                cancellation: SiteOperationCancellation()
            )
            XCTAssertEqual(try String(contentsOf: backup, encoding: .utf8), "SELECT 1;")
            let connection = try XCTUnwrap(manager.connectionURL(provisioning: provisioning, credentials: credentials))
            XCTAssertEqual(connection.host, "127.0.0.1")
            XCTAssertEqual(connection.port, 54321)
            XCTAssertEqual(connection.user, "fixture_user")
            let arguments = try String(contentsOf: root.appendingPathComponent("arguments.log"), encoding: .utf8)
            XCTAssertFalse(arguments.contains(credentials.secret))
            XCTAssertTrue(arguments.contains("CREATE DATABASE"))
            let environment = try String(contentsOf: root.appendingPathComponent(".env"), encoding: .utf8)
            XCTAssertTrue(environment.contains("DB_CONNECTION=\(definition == "postgresql" ? "pgsql" : "mysql")"))
        }
    }

    func testDatabaseInspectionRejectsInvalidOutputAndFailedCommands() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let client = root.appendingPathComponent("client")
        let instance = ServiceInstance(id: UUID(), definitionID: "mysql", name: "Fixture", version: "1", port: 3306, isRunning: true)
        let provisioning = SiteDatabaseProvisioning(
            service: instance, databaseName: "fixture", username: "fixture", environmentURL: root.appendingPathComponent(".env")
        )
        let manager = SiteDatabaseManager(rootURL: root)
        for script in ["#!/bin/sh\nprintf invalid\n", "#!/bin/sh\nprintf rejected; exit 1\n"] {
            try Data(script.utf8).write(to: client)
            try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: client.path)
            do {
                _ = try await manager.inspect(
                    provisioning: provisioning, credentials: ServiceCredentials(username: "fixture", secret: "fixture"), client: client
                )
                XCTFail("Invalid inspection output must fail.")
            } catch let error as SiteDatabaseError {
                XCTAssertFalse(error.localizedDescription.isEmpty)
            }
        }
    }
}
