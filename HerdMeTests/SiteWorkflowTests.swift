import Foundation
import XCTest

@testable import HerdMe

final class SiteWorkflowTests: XCTestCase {
    private var temporaryDirectories: [URL] = []

    override func tearDown() {
        for directory in temporaryDirectories { try? FileManager.default.removeItem(at: directory) }
        temporaryDirectories.removeAll()
        super.tearDown()
    }

    func testCommandFavoritesStayInApplicationSupportAndDeduplicate() throws {
        let root = try temporaryDirectory()
        let project = try temporaryDirectory()
        let store = SiteCommandFavoritesStore(rootURL: root)

        try store.add(site: project, tool: "composer", command: "  install  ")
        try store.add(site: project, tool: "composer", command: "INSTALL")
        try store.add(site: project, tool: "artisan", command: "route:list")

        let favorites = try store.load(site: project, tool: "composer")
        XCTAssertEqual(favorites.map(\.command), ["INSTALL"])
        XCTAssertFalse(FileManager.default.fileExists(atPath: project.appendingPathComponent("command-favorites.json").path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: root.appendingPathComponent("Config/command-favorites.json").path))

        try store.remove(site: project, tool: "composer", command: "INSTALL")
        XCTAssertTrue(try store.load(site: project, tool: "composer").isEmpty)
    }

    func testArbitraryServiceEnvironmentUpdatePreservesOtherValues() throws {
        let project = try temporaryDirectory()
        let env = project.appendingPathComponent(".env")
        try Data("APP_NAME=Example\nDB_DATABASE=old\n".utf8).write(to: env)

        let update = try ServiceEnvironmentFile.update(
            projectURL: project,
            variables: [
                ServiceEnvironmentVariable(key: "DB_DATABASE", value: "herdme_example"),
                ServiceEnvironmentVariable(key: "DB_HOST", value: "127.0.0.1")
            ],
            serviceName: "Test Database"
        )

        let contents = try String(contentsOf: env, encoding: .utf8)
        XCTAssertTrue(contents.contains("APP_NAME=Example"))
        XCTAssertTrue(contents.contains("DB_DATABASE=herdme_example"))
        XCTAssertTrue(contents.contains("DB_HOST=127.0.0.1"))
        XCTAssertEqual(update.updatedKeys, 1)
        XCTAssertEqual(update.addedKeys, 1)
    }

    func testConfigureMailWorkflowUsesConfiguredCapturePort() async throws {
        let root = try temporaryDirectory()
        let project = try temporaryDirectory()
        try Data("APP_NAME=Example\n".utf8).write(to: project.appendingPathComponent(".env"))
        let site = SiteProject(path: project, name: "example", framework: "Laravel", isLinked: true)
        let cancellation = SiteOperationCancellation()

        _ = try await SiteWorkflowRunner(rootURL: root).run(
            .configureMail,
            site: site,
            defaultPHP: "8.4",
            smtpPort: 2526,
            cancellation: cancellation,
            progress: { _ in }
        )

        let contents = try String(contentsOf: project.appendingPathComponent(".env"), encoding: .utf8)
        XCTAssertTrue(contents.contains("MAIL_MAILER=smtp"))
        XCTAssertTrue(contents.contains("MAIL_HOST=127.0.0.1"))
        XCTAssertTrue(contents.contains("MAIL_PORT=2526"))
        XCTAssertTrue(contents.contains("APP_NAME=Example"))
    }

    func testHealthInspectorReportsRepairableLaravelGaps() throws {
        let root = try temporaryDirectory()
        let project = try temporaryDirectory()
        try Data().write(to: project.appendingPathComponent("artisan"))
        try Data("APP_KEY=\n".utf8).write(to: project.appendingPathComponent(".env"))
        let site = SiteProject(path: project, name: "example", framework: "Laravel", isLinked: true)

        let report = SiteHealthInspector.inspect(
            site: site,
            defaultPHP: "8.4",
            environmentStatus: .stopped,
            rootURL: root
        )

        XCTAssertFalse(report.isHealthy)
        XCTAssertEqual(report.checks.first(where: { $0.id == "environment" })?.isHealthy, true)
        XCTAssertEqual(report.checks.first(where: { $0.id == "app-key" })?.isHealthy, false)
        XCTAssertEqual(report.checks.first(where: { $0.id == "composer" })?.repairable, true)
        XCTAssertEqual(report.checks.first(where: { $0.id == "environment-running" })?.isHealthy, false)
    }

    func testDatabaseProvisioningCreatesDedicatedNameAndUpdatesEnvironment() async throws {
        let root = try temporaryDirectory()
        let project = try temporaryDirectory()
        let client = root.appendingPathComponent("mysql")
        try Data("#!/bin/sh\nexit 0\n".utf8).write(to: client)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: client.path)
        let instance = ServiceInstance(
            id: UUID(),
            definitionID: "mysql",
            name: "MySQL",
            version: "test",
            port: 3306,
            isRunning: true
        )
        let credentials = ServiceCredentials(
            username: "herdme_test_user",
            secret: String(repeating: "a", count: 32)
        )
        let site = SiteProject(path: project, name: "My Demo", framework: "Laravel", isLinked: true)

        let provisioning = try await SiteDatabaseManager(rootURL: root).provision(
            site: site,
            instance: instance,
            credentials: credentials,
            client: client
        )

        XCTAssertTrue(provisioning.databaseName.hasPrefix("herdme_my_demo"))
        let contents = try String(contentsOf: project.appendingPathComponent(".env"), encoding: .utf8)
        XCTAssertTrue(contents.contains("DB_DATABASE=\(provisioning.databaseName)"))
        XCTAssertTrue(contents.contains("DB_USERNAME=herdme_test_user"))
        XCTAssertTrue(contents.contains("DB_PORT=3306"))
    }

    func testDevelopmentServerDetectsLaravelAssetModeAndHotFile() throws {
        let project = try temporaryDirectory()
        try Data().write(to: project.appendingPathComponent("artisan"))
        try writePackage(scripts: ["dev": "vite"], to: project)
        let publicDirectory = project.appendingPathComponent("public", isDirectory: true)
        try FileManager.default.createDirectory(at: publicDirectory, withIntermediateDirectories: true)
        let site = SiteProject(
            path: project,
            name: "laravel-app",
            framework: "Laravel",
            isLinked: true
        )

        XCTAssertEqual(SiteDevelopmentServer.mode(for: site), .laravelAssets)
        XCTAssertEqual(SiteDevelopmentServer.projectDirectory(for: site), project.standardizedFileURL)

        let hotFile = SiteDevelopmentServer.laravelHotFile(for: site)
        try Data("http://127.0.0.1:5173".utf8).write(to: hotFile)
        XCTAssertTrue(SiteDevelopmentServer.removeLaravelHotFile(for: site))
        XCTAssertFalse(FileManager.default.fileExists(atPath: hotFile.path))
    }

    func testDevelopmentServerDetectsNodeAndNestedFrontendProjects() throws {
        let nodeProject = try temporaryDirectory()
        try writePackage(scripts: ["dev": "next dev"], to: nodeProject)
        let nodeSite = SiteProject(
            path: nodeProject,
            name: "node-app",
            framework: "Node.js",
            isLinked: true
        )
        XCTAssertEqual(SiteDevelopmentServer.mode(for: nodeSite), .primaryProxy)
        XCTAssertEqual(
            SiteDevelopmentServer.projectDirectory(for: nodeSite),
            nodeProject.standardizedFileURL
        )

        let apiProject = try temporaryDirectory()
        let frontend = apiProject.appendingPathComponent("frontend", isDirectory: true)
        try FileManager.default.createDirectory(at: frontend, withIntermediateDirectories: true)
        try writePackage(scripts: ["dev": "vite"], to: frontend)
        let apiSite = SiteProject(
            path: apiProject,
            name: "camera-app",
            framework: "Python",
            isLinked: true
        )
        XCTAssertEqual(SiteDevelopmentServer.mode(for: apiSite), .primaryProxy)
        XCTAssertEqual(SiteDevelopmentServer.projectDirectory(for: apiSite), frontend)
    }

    func testDevelopmentServerIgnoresPackagesWithoutDevScript() throws {
        let project = try temporaryDirectory()
        try writePackage(scripts: ["build": "vite build"], to: project)
        let site = SiteProject(
            path: project,
            name: "static-app",
            framework: "Node.js",
            isLinked: true
        )

        XCTAssertNil(SiteDevelopmentServer.mode(for: site))
        XCTAssertNil(SiteDevelopmentServer.projectDirectory(for: site))
        XCTAssertFalse(SiteDevelopmentServer.isDevelopmentSite(site))
    }

    func testDevelopmentArgumentsRecognizeNextInDevDependencies() throws {
        let project = try temporaryDirectory()
        try writePackage(
            scripts: ["dev": "next dev"],
            devDependencies: ["next": "latest"],
            to: project
        )

        XCTAssertEqual(
            SiteDevelopmentServer.developmentArguments(for: project, port: 30_001),
            ["run", "dev", "--", "--hostname", "127.0.0.1", "--port", "30001"]
        )
    }

    private func writePackage(
        scripts: [String: String],
        devDependencies: [String: String] = [:],
        to directory: URL
    ) throws {
        let package: [String: Any] = [
            "scripts": scripts,
            "devDependencies": devDependencies
        ]
        try JSONSerialization.data(withJSONObject: package, options: [.sortedKeys])
            .write(to: directory.appendingPathComponent("package.json"))
    }

    private func temporaryDirectory() throws -> URL {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("HerdMe-SiteWorkflowTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        temporaryDirectories.append(directory)
        return directory
    }
}
