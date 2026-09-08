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
}
