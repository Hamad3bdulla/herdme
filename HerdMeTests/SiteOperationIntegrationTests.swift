import Foundation
import XCTest

@testable import HerdMe

final class SiteOperationIntegrationTests: XCTestCase {
    private var fixtures: [URL] = []

    override func tearDown() {
        for fixture in fixtures { try? FileManager.default.removeItem(at: fixture) }
        fixtures.removeAll()
        super.tearDown()
    }

    func testUpdateRunsManagedToolsAndKeepsARecoverableArchive() async throws {
        let (root, site) = try makeProject()
        let result = try await run(.update, root: root, site: site)
        let commands = try String(contentsOf: site.path.appendingPathComponent("commands.log"), encoding: .utf8)
        for command in ["update --no-interaction", "update --no-audit", "run build", "migrate --force", "optimize:clear"] {
            XCTAssertTrue(commands.contains(command), commands)
        }
        let archive = try XCTUnwrap(result.artifactURL)
        let inventory = try ProcessRunner.run(URL(fileURLWithPath: "/usr/bin/unzip"), arguments: ["-Z", "-1", archive.path])
        XCTAssertEqual(inventory.status, 0)
        XCTAssertTrue(inventory.output.contains("composer.json"))
        XCTAssertTrue(inventory.output.contains(".env.example"))
        XCTAssertFalse(inventory.output.contains("commands.log"))
    }

    func testCleanRebuildsDependenciesAndRollsBackFailedCommands() async throws {
        let (root, site) = try makeProject()
        for name in ["vendor", "node_modules"] { try write("old", to: site.path.appendingPathComponent("\(name)/original")) }
        _ = try await run(.clean, root: root, site: site)
        for name in ["vendor", "node_modules"] {
            XCTAssertTrue(FileManager.default.fileExists(atPath: site.path.appendingPathComponent("\(name)/rebuilt").path))
            XCTAssertFalse(FileManager.default.fileExists(atPath: site.path.appendingPathComponent("\(name)/original").path))
            try write("preserve", to: site.path.appendingPathComponent("\(name)/original"))
        }
        try write("fail", to: site.path.appendingPathComponent(".fail-command"))
        do {
            _ = try await run(.clean, root: root, site: site)
            XCTFail("A failed dependency install must fail the clean operation.")
        } catch {
            XCTAssertTrue(error.localizedDescription.contains("composer install"))
        }
        for name in ["vendor", "node_modules"] {
            XCTAssertEqual(try String(contentsOf: site.path.appendingPathComponent("\(name)/original"), encoding: .utf8), "preserve")
        }
    }

    func testRepairCreatesEnvironmentAndStorageBeforeArtisanCommands() async throws {
        let (root, site) = try makeProject()
        _ = try await run(.repair, root: root, site: site)
        XCTAssertTrue(try String(contentsOf: site.path.appendingPathComponent(".env"), encoding: .utf8).contains("APP_NAME=Fixture"))
        for name in ["cache", "sessions", "views"] {
            XCTAssertTrue(FileManager.default.fileExists(atPath: site.path.appendingPathComponent("storage/framework/\(name)").path))
        }
        let commands = try String(contentsOf: site.path.appendingPathComponent("commands.log"), encoding: .utf8)
        for command in ["install --no-interaction", "key:generate", "storage:link", "optimize:clear"] {
            XCTAssertTrue(commands.contains(command), commands)
        }
        _ = try await run(.reset, root: root, site: site)
        XCTAssertTrue(try String(contentsOf: site.path.appendingPathComponent("commands.log"), encoding: .utf8).contains("migrate:fresh"))
    }

    func testFailedDependencyRestorePreservesTheStagedOriginals() async throws {
        let (root, site) = try makeProject()
        try write("original dependencies", to: site.path.appendingPathComponent("vendor/original"))
        try write("fail", to: site.path.appendingPathComponent(".fail-command"))
        do {
            _ = try await SiteWorkflowRunner(rootURL: root, fileManager: RestoreFailureFileManager()).run(
                .clean, site: site, defaultPHP: "8.4", smtpPort: 2525,
                cancellation: SiteOperationCancellation(), progress: { _ in }
            )
            XCTFail("A failed install must preserve the original error.")
        } catch {
            XCTAssertTrue(error.localizedDescription.contains("composer install"))
        }
        let cache = root.appendingPathComponent("Cache")
        let staging = try FileManager.default.contentsOfDirectory(at: cache, includingPropertiesForKeys: nil)
            .filter { $0.lastPathComponent.hasPrefix("site-clean-") }
        XCTAssertEqual(staging.count, 1)
        let retained = try XCTUnwrap(staging.first).appendingPathComponent("vendor/original")
        XCTAssertEqual(try String(contentsOf: retained, encoding: .utf8), "original dependencies")
    }

    func testAuditReportsFailuresAndRepeatedExportsHaveDistinctArchives() async throws {
        let (root, site) = try makeProject()
        let good = try await run(.audit, root: root, site: site)
        XCTAssertTrue(good.output.contains("[OK] composer validate"))
        XCTAssertTrue(good.output.contains("[OK] npm audit"))
        XCTAssertTrue(good.output.contains("[OK] artisan test"))
        try write("fail", to: site.path.appendingPathComponent(".fail-command"))
        let bad = try await run(.audit, root: root, site: site)
        XCTAssertTrue(bad.output.contains("[FAIL] composer audit"))
        XCTAssertTrue(bad.output.contains("fixture failure"))
        let first = try await run(.export, root: root, site: site)
        let second = try await run(.export, root: root, site: site)
        XCTAssertNotEqual(first.artifactURL, second.artifactURL)
        XCTAssertTrue(FileManager.default.fileExists(atPath: try XCTUnwrap(first.artifactURL).path))
    }

    func testToolchainRejectsMissingToolsAndAcceptsAnExactNodeVersion() throws {
        let (root, original) = try makeProject()
        var site = original
        site.nodeVersion = "v22.23.1"
        let tools = SiteToolchain(rootURL: root)
        XCTAssertEqual(try tools.npm(site: site, arguments: ["audit"]).executable.lastPathComponent, "node")
        try FileManager.default.removeItem(at: site.path.appendingPathComponent("artisan"))
        XCTAssertThrowsError(try tools.artisan(site: site, defaultPHP: "8.4", arguments: ["list"]))
        try FileManager.default.removeItem(at: root.appendingPathComponent("Composer/composer.phar"))
        XCTAssertThrowsError(try tools.composer(site: site, defaultPHP: "8.4", arguments: ["install"]))
        site.nodeVersion = "99"
        XCTAssertThrowsError(try tools.npm(site: site, arguments: ["install"]))
    }

    func testDevelopmentServerUsesFrontendDirectoryAndStopsItsListener() async throws {
        let (root, original) = try makeProject(developmentServer: true)
        try FileManager.default.moveItem(
            at: original.path.appendingPathComponent("package.json"),
            to: original.path.appendingPathComponent("frontend-package.json")
        )
        let frontend = original.path.appendingPathComponent("frontend")
        try write(#"{"scripts":{"dev":"vite"}}"#, to: frontend.appendingPathComponent("package.json"))
        try write("fixture", to: frontend.appendingPathComponent("node_modules/fixture"))
        let site = SiteProject(path: original.path, name: "Frontend", framework: "Python", isLinked: true, nodeVersion: "22")
        let server = SiteDevelopmentServer(rootURL: root)
        do {
            let port = try await server.start(site: site)
            XCTAssertTrue(server.isRunning)
            XCTAssertTrue(server.proxiesSiteTraffic)
            let repeatedPort = try await server.start(site: site)
            XCTAssertEqual(repeatedPort, port)
            let workingDirectory = try String(contentsOf: frontend.appendingPathComponent("started-directory"), encoding: .utf8)
            XCTAssertEqual(URL(fileURLWithPath: workingDirectory).resolvingSymlinksInPath(), frontend.resolvingSymlinksInPath())
            await server.stop()
            XCTAssertFalse(LocalEnvironmentEngine.canConnect(port: port))
            XCTAssertFalse(server.isRunning)
        } catch {
            await server.stop()
            throw error
        }
    }

    func testLaravelDevelopmentServerPublishesAndRemovesItsHotFile() async throws {
        let (root, site) = try makeProject(developmentServer: true)
        try write("fixture", to: site.path.appendingPathComponent("node_modules/fixture"))
        try write("stale", to: site.path.appendingPathComponent("public/hot"))
        let server = SiteDevelopmentServer(rootURL: root)
        do {
            let port = try await server.start(site: site)
            XCTAssertFalse(server.proxiesSiteTraffic)
            let hot = try String(contentsOf: site.path.appendingPathComponent("public/hot"), encoding: .utf8)
            XCTAssertEqual(hot, "http://127.0.0.1:\(port)")
            await server.stop()
            XCTAssertFalse(FileManager.default.fileExists(atPath: site.path.appendingPathComponent("public/hot").path))
            XCTAssertFalse(LocalEnvironmentEngine.canConnect(port: port))
        } catch {
            await server.stop()
            throw error
        }
    }

    func testDevelopmentServerRejectsMissingDependenciesAndCancellation() async throws {
        let (root, site) = try makeProject(developmentServer: true)
        let server = SiteDevelopmentServer(rootURL: root)
        do {
            _ = try await server.start(site: site)
            XCTFail("Missing dependencies must be reported.")
        } catch let error as SiteDevelopmentServerError {
            XCTAssertEqual(error, .dependenciesMissing)
        }
        try write("fixture", to: site.path.appendingPathComponent("node_modules/fixture"))
        let cancelled = SiteOperationCancellation()
        cancelled.cancel()
        do {
            _ = try await server.start(site: site, cancellationToken: cancelled)
            XCTFail("A cancelled startup must not launch a server.")
        } catch is CancellationError {}
        XCTAssertFalse(server.isRunning)
    }

    private func run(_ operation: SiteWorkflowOperation, root: URL, site: SiteProject) async throws -> SiteWorkflowResult {
        try await SiteWorkflowRunner(rootURL: root).run(
            operation, site: site, defaultPHP: "8.4", smtpPort: 2525,
            cancellation: SiteOperationCancellation(), progress: { _ in }
        )
    }

    func testCancellationDuringServerStartupStopsTheChildProcess() async throws {
        let (root, site) = try makeProject(developmentServer: true)
        try write("fixture", to: site.path.appendingPathComponent("node_modules/fixture"))
        let delayed = #"""
            #!/bin/sh
            if [ "$1" = "--version" ]; then printf 'v22.23.1'; exit 0; fi
            touch started-directory
            exec /bin/sleep 30
            """#
        try write(delayed, to: root.appendingPathComponent("Runtimes/node/22.23.1/bin/node"), executable: true)
        let server = SiteDevelopmentServer(rootURL: root)
        let cancellation = SiteOperationCancellation()
        let operation = Task { try await server.start(site: site, cancellationToken: cancellation) }
        let marker = site.path.appendingPathComponent("started-directory")
        for _ in 0..<200 {
            if FileManager.default.fileExists(atPath: marker.path) { break }
            try await Task.sleep(for: .milliseconds(20))
        }
        cancellation.cancel()
        do {
            _ = try await operation.value
            XCTFail("Cancelled startup must fail.")
        } catch is CancellationError {}
        await server.stop()
        XCTAssertTrue(FileManager.default.fileExists(atPath: marker.path))
        XCTAssertNil(server.port)
        XCTAssertFalse(server.isRunning)
    }

    private func makeProject(developmentServer: Bool = false) throws -> (URL, SiteProject) {
        let fixture = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        fixtures.append(fixture)
        let root = fixture.appendingPathComponent("Support")
        let project = fixture.appendingPathComponent("Project")
        let commandScript = #"""
            #!/bin/sh
            if [ "$1" = "--version" ]; then printf 'v22.23.1'; exit 0; fi
            printf '%s\n' "$*" >> commands.log
            case "$*" in
              *install*) mkdir -p vendor node_modules; touch vendor/rebuilt node_modules/rebuilt ;;
            esac
            if [ -f .fail-command ]; then printf 'fixture failure'; exit 3; fi
            printf 'fixture success'
            """#
        let serverScript = #"""
            #!/bin/sh
            if [ "$1" = "--version" ]; then printf 'v22.23.1'; exit 0; fi
            exec /usr/bin/ruby -rsocket -e '
              port = ARGV.last.to_i
              File.write("started-directory", Dir.pwd)
              File.write("public/hot", "http://127.0.0.1:#{port}") if Dir.exist?("public")
              server = TCPServer.new("127.0.0.1", port)
              puts "fixture server ready"
              STDOUT.flush
              trap("TERM") { exit }
              loop { socket = server.accept; socket.close }
            ' "$@"
            """#
        try write(commandScript, to: root.appendingPathComponent("Runtimes/php/8.4/bin/php"), executable: true)
        try write(
            developmentServer ? serverScript : commandScript,
            to: root.appendingPathComponent("Runtimes/node/22.23.1/bin/node"), executable: true
        )
        try write("fixture", to: root.appendingPathComponent("Runtimes/node/22.23.1/lib/node_modules/npm/bin/npm-cli.js"))
        try write("fixture", to: root.appendingPathComponent("Composer/composer.phar"))
        try write("fixture", to: project.appendingPathComponent("artisan"))
        try write("{}", to: project.appendingPathComponent("composer.json"))
        try write(#"{"scripts":{"dev":"vite","build":"vite build"}}"#, to: project.appendingPathComponent("package.json"))
        try write("APP_NAME=Fixture\n", to: project.appendingPathComponent(".env.example"))
        return (root, SiteProject(path: project, name: "Fixture", framework: "Laravel", isLinked: true, nodeVersion: "22"))
    }

    private func write(_ value: String, to url: URL, executable: Bool = false) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(value.utf8).write(to: url)
        if executable { try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: url.path) }
    }
}

private final class RestoreFailureFileManager: FileManager, @unchecked Sendable {
    override func moveItem(at source: URL, to destination: URL) throws {
        if source.path.contains("/Cache/site-clean-") && source.lastPathComponent == "vendor" {
            throw CocoaError(.fileWriteNoPermission)
        }
        try super.moveItem(at: source, to: destination)
    }
}
