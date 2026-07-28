import Combine
import CryptoKit
import Darwin
import Security
import SwiftUI
import XCTest

@testable import HerdMe

extension ConfigurationAndSiteScannerTests {
    func testServiceCatalogUsesUniqueIdentifiers() {
        let identifiers = ServiceCatalog.all.map(\.id)

        XCTAssertEqual(Set(identifiers).count, identifiers.count)
        XCTAssertTrue(identifiers.contains("mysql"))
        XCTAssertTrue(identifiers.contains("redis"))
        XCTAssertFalse(identifiers.contains("reverb"))
        XCTAssertTrue(identifiers.allSatisfy { ServiceProcessManager.supports(definitionID: $0) })
    }

    func testCredentialProtectedServicesAreIdentifiedForAutomaticStartup() {
        for identifier in ["mysql", "mariadb", "postgresql", "typesense", "minio", "rustfs"] {
            XCTAssertTrue(ServiceProcessManager.requiresCredentials(definitionID: identifier))
        }
        for identifier in ["redis", "valkey", "meilisearch"] {
            XCTAssertFalse(ServiceProcessManager.requiresCredentials(definitionID: identifier))
        }
    }

    func testServiceEnvironmentConfigurationUsesManagedConnectionDetails() throws {
        let credentials = ServiceCredentials(
            username: "herdme_testuser",
            secret: "test_secret_0123456789_ABCDEFGHIJKLMNOP"
        )
        let mysql = ServiceInstance(
            id: UUID(),
            definitionID: "mysql",
            name: "MySQL",
            version: "9.7",
            port: 3_307,
            isRunning: true
        )
        let typesense = ServiceInstance(
            id: UUID(),
            definitionID: "typesense",
            name: "Typesense",
            version: "30.2",
            port: 8_108,
            isRunning: true
        )
        let rustFS = ServiceInstance(
            id: UUID(),
            definitionID: "rustfs",
            name: "RustFS",
            version: "1.0",
            port: 9_000,
            isRunning: true
        )

        let mysqlValues = Dictionary(
            uniqueKeysWithValues:
                ServiceEnvironmentConfiguration.variables(
                    for: mysql,
                    credentials: credentials
                ).map { ($0.key, $0.value) }
        )
        XCTAssertEqual(mysqlValues["DB_CONNECTION"], "mysql")
        XCTAssertEqual(mysqlValues["DB_HOST"], "127.0.0.1")
        XCTAssertEqual(mysqlValues["DB_PORT"], "3307")
        XCTAssertEqual(mysqlValues["DB_USERNAME"], credentials.username)
        XCTAssertEqual(mysqlValues["DB_PASSWORD"], credentials.secret)

        let postgreSQL = ServiceInstance(
            id: UUID(),
            definitionID: "postgresql",
            name: "PostgreSQL",
            version: "18",
            port: 5_432,
            isRunning: true
        )
        let postgreSQLValues = Dictionary(
            uniqueKeysWithValues:
                ServiceEnvironmentConfiguration.variables(
                    for: postgreSQL,
                    credentials: credentials
                ).map { ($0.key, $0.value) }
        )
        XCTAssertEqual(postgreSQLValues["DB_USERNAME"], credentials.username)
        XCTAssertEqual(postgreSQLValues["DB_PASSWORD"], credentials.secret)

        let typesenseValues = Dictionary(
            uniqueKeysWithValues:
                ServiceEnvironmentConfiguration.variables(
                    for: typesense,
                    credentials: credentials
                ).map { ($0.key, $0.value) }
        )
        XCTAssertEqual(typesenseValues["TYPESENSE_API_KEY"], credentials.secret)
        XCTAssertEqual(typesenseValues["TYPESENSE_PROTOCOL"], "http")

        let storageValues = Dictionary(
            uniqueKeysWithValues:
                ServiceEnvironmentConfiguration.variables(
                    for: rustFS,
                    credentials: credentials
                ).map { ($0.key, $0.value) }
        )
        XCTAssertEqual(storageValues["AWS_ACCESS_KEY_ID"], credentials.username)
        XCTAssertEqual(storageValues["AWS_SECRET_ACCESS_KEY"], credentials.secret)
        XCTAssertEqual(storageValues["AWS_ENDPOINT"], "http://127.0.0.1:9000")

        for definition in ServiceCatalog.all {
            let instance = ServiceInstance(
                id: UUID(),
                definitionID: definition.id,
                name: definition.name,
                version: definition.latestVersion,
                port: definition.defaultPort,
                isRunning: false
            )
            XCTAssertFalse(
                ServiceEnvironmentConfiguration.variables(
                    for: instance,
                    credentials: credentials
                ).isEmpty,
                "Missing .env variables for \(definition.id)"
            )
        }
    }

    func testServiceEnvironmentFileUpdatesWithoutDuplicatingKeys() throws {
        let project = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: project) }
        try FileManager.default.createDirectory(at: project, withIntermediateDirectories: true)
        try Data("APP_NAME=Example\r\n# Keep this comment\r\nDB_HOST=localhost\r\n".utf8)
            .write(to: project.appendingPathComponent(".env.example"))

        var instance = ServiceInstance(
            id: UUID(),
            definitionID: "mysql",
            name: "Local MySQL",
            version: "9.7",
            port: 3_306,
            isRunning: false
        )
        let credentials = ServiceCredentials(
            username: "herdme_testuser",
            secret: "test_secret_0123456789_ABCDEFGHIJKLMNOP"
        )
        let first = try ServiceEnvironmentFile.update(
            projectURL: project,
            instance: instance,
            credentials: credentials
        )
        XCTAssertTrue(first.createdFile)
        XCTAssertEqual(first.updatedKeys, 1)
        XCTAssertEqual(first.addedKeys, 5)

        instance.port = 3_307
        let second = try ServiceEnvironmentFile.update(
            projectURL: project,
            instance: instance,
            credentials: credentials
        )
        let contents = try String(
            contentsOf: project.appendingPathComponent(".env"),
            encoding: .utf8
        )
        XCTAssertFalse(second.createdFile)
        XCTAssertEqual(second.updatedKeys, 6)
        XCTAssertEqual(second.addedKeys, 0)
        XCTAssertTrue(contents.contains("# Keep this comment\r\n"))
        XCTAssertTrue(contents.contains("DB_PORT=3307\r\n"))
        XCTAssertEqual(
            contents.components(separatedBy: "\r\n").filter { $0.hasPrefix("DB_HOST=") }.count,
            1
        )
    }

    func testProjectEnvironmentFileCreatesFromExampleAndPersistsSecurely() throws {
        let project = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: project) }
        try FileManager.default.createDirectory(at: project, withIntermediateDirectories: true)
        try Data("APP_NAME=Example\nAPP_ENV=local\n".utf8)
            .write(to: project.appendingPathComponent(".env.example"))

        let draft = try ProjectEnvironmentFile.load(projectURL: project)
        XCTAssertFalse(draft.exists)
        XCTAssertTrue(draft.loadedFromExample)
        XCTAssertEqual(draft.revision, .missing)
        XCTAssertEqual(draft.contents, "APP_NAME=Example\nAPP_ENV=local\n")

        let saved = try ProjectEnvironmentFile.save(
            draft.contents + "APP_DEBUG=true\n",
            projectURL: project,
            expectedRevision: draft.revision
        )
        XCTAssertTrue(saved.exists)
        XCTAssertFalse(saved.loadedFromExample)
        XCTAssertNotNil(saved.revision.digest)
        XCTAssertEqual(try ProjectEnvironmentFile.load(projectURL: project), saved)

        let permissions =
            try FileManager.default.attributesOfItem(
                atPath: project.appendingPathComponent(".env").path
            )[.posixPermissions] as? NSNumber
        XCTAssertEqual(permissions?.intValue, 0o600)
    }

    func testProjectEnvironmentFileRejectsExternalChangesWithoutOverwritingThem() throws {
        let project = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: project) }
        try FileManager.default.createDirectory(at: project, withIntermediateDirectories: true)
        let environmentURL = project.appendingPathComponent(".env")
        try Data("APP_NAME=First\n".utf8).write(to: environmentURL)
        let loaded = try ProjectEnvironmentFile.load(projectURL: project)

        try Data("APP_NAME=External\n".utf8).write(to: environmentURL, options: .atomic)
        XCTAssertThrowsError(
            try ProjectEnvironmentFile.save(
                "APP_NAME=HerdMe\n",
                projectURL: project,
                expectedRevision: loaded.revision
            )
        ) { error in
            XCTAssertEqual(error as? ProjectEnvironmentFileError, .changedExternally)
        }
        XCTAssertEqual(
            try String(contentsOf: environmentURL, encoding: .utf8),
            "APP_NAME=External\n"
        )
    }

    func testProjectEnvironmentFileRejectsUnsafeInputs() throws {
        let project = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: project) }
        try FileManager.default.createDirectory(at: project, withIntermediateDirectories: true)
        let environmentURL = project.appendingPathComponent(".env")
        try Data([0xFF, 0xFE, 0xFD]).write(to: environmentURL)
        XCTAssertThrowsError(try ProjectEnvironmentFile.load(projectURL: project)) { error in
            XCTAssertEqual(error as? ProjectEnvironmentFileError, .invalidFile)
        }

        try FileManager.default.removeItem(at: environmentURL)
        let oversized = String(
            repeating: "x",
            count: ProjectEnvironmentFile.maximumFileBytes + 1
        )
        XCTAssertThrowsError(
            try ProjectEnvironmentFile.save(
                oversized,
                projectURL: project,
                expectedRevision: .missing
            )
        ) { error in
            XCTAssertEqual(error as? ProjectEnvironmentFileError, .fileTooLarge)
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: environmentURL.path))
    }

    func testOlderServiceInstancesPreserveAutomaticStartupBehavior() throws {
        let id = UUID()
        let data = Data(
            """
            {
              "id": "\(id.uuidString)",
              "definitionID": "redis",
              "name": "Redis",
              "version": "latest",
              "port": 6379,
              "isRunning": false
            }
            """.utf8
        )

        let instance = try JSONDecoder().decode(ServiceInstance.self, from: data)

        XCTAssertTrue(instance.startAutomatically)
    }

    func testManagedServiceCredentialsAreRandomAndStablePerInstance() throws {
        let backend = TestServiceCredentialBackend()
        let store = ServiceCredentialStore(backend: backend)
        let firstIdentifier = UUID()
        let secondIdentifier = UUID()

        let first = try store.credentials(for: firstIdentifier)
        let restored = try store.credentials(for: firstIdentifier)
        let second = try store.credentials(for: secondIdentifier)

        XCTAssertTrue(first.isValid)
        XCTAssertEqual(first, restored)
        XCTAssertNotEqual(first.secret, second.secret)
        XCTAssertNotEqual(first.username, second.username)
        XCTAssertFalse(first.secret.contains("herdme-local-service"))

        try store.delete(for: firstIdentifier)
        XCTAssertNotEqual(try store.credentials(for: firstIdentifier).secret, first.secret)
    }

    func testManagedServiceCredentialReadRespectsNonInteractiveStartup() throws {
        let backend = TestServiceCredentialBackend()
        let store = ServiceCredentialStore(backend: backend)
        let identifier = UUID()

        _ = try store.credentials(for: identifier)
        _ = try store.credentials(for: identifier, allowInteraction: false)

        XCTAssertEqual(backend.readAllowsInteraction, [true, false])
    }

    func testManagedServiceCredentialReadDoesNotCreateASecretDuringNonInteractiveStartup() {
        let backend = TestServiceCredentialBackend()
        let store = ServiceCredentialStore(backend: backend)

        XCTAssertThrowsError(try store.credentials(for: UUID(), allowInteraction: false)) { error in
            guard case ServiceCredentialError.interactionRequired = error else {
                return XCTFail("Unexpected error: \(error)")
            }
        }
        XCTAssertEqual(backend.readAllowsInteraction, [false])
    }

    func testTerminalCommandsShellQuoteProjectPaths() {
        XCTAssertEqual(TerminalCommandLauncher.shellQuote("/tmp/O'Brien App"), "'/tmp/O'\\''Brien App'")
    }

    func testSidebarExposesTheImplementedLogViewer() {
        XCTAssertTrue(SidebarPage.visibleCases.contains(.logs))
    }

    @MainActor
    func testDashboardIsTheFirstAndDefaultApplicationPage() {
        XCTAssertEqual(SidebarPage.visibleCases.first, .dashboard)
        XCTAssertEqual(AppNavigation().selectedPage, .dashboard)
    }

    @MainActor
    func testPrimaryDetailPagesRenderWithApplicationEnvironment() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let store = ConfigurationStore(
            rootURL: root.appendingPathComponent("support"),
            projectsURL: root.appendingPathComponent("projects")
        )
        defer { try? FileManager.default.removeItem(at: root) }
        var configuration = AppConfiguration.default
        configuration.onboardingCompleted = true
        try store.save(configuration)
        let model = AppModel(configurationStore: store)

        for page in [SidebarPage.sites, .about] {
            model.navigation.selectedPage = page
            let rootView = AnyView(
                RootView()
                    .environmentObject(model)
                    .environmentObject(model.applicationSettings)
                    .environmentObject(model.navigation)
                    .environmentObject(model.mail)
                    .environmentObject(model.dumpsCoordinator)
                    .environmentObject(model.services)
                    .environmentObject(model.runtime)
                    .environmentObject(model.sitesCoordinator)
                    .environmentObject(model.environment)
                    .environmentObject(model.security)
            )
            let hostingView = NSHostingView(rootView: rootView)
            hostingView.frame = NSRect(x: 0, y: 0, width: 1_100, height: 720)
            hostingView.layoutSubtreeIfNeeded()

            XCTAssertGreaterThan(hostingView.fittingSize.width, 0)
            XCTAssertGreaterThan(hostingView.fittingSize.height, 0)
        }

        await model.shutdown()
    }

    @MainActor
    func testAppModelAddsParkPathFromCallerAndPersistsItOnce() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let support = root.appendingPathComponent("support")
        let projects = root.appendingPathComponent("projects")
        let additional = root.appendingPathComponent("additional")
        try FileManager.default.createDirectory(at: additional, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }

        let store = ConfigurationStore(rootURL: support, projectsURL: projects)
        let model = AppModel(configurationStore: store)

        XCTAssertTrue(model.addParkPath(additional))
        XCTAssertFalse(model.addParkPath(additional))
        XCTAssertEqual(model.configuration.parkPaths.filter { $0 == additional.path }.count, 1)
        XCTAssertEqual(store.load().parkPaths.filter { $0 == additional.path }.count, 1)
    }

    @MainActor
    func testSiteLogsNavigationPreservesTheRequestedLaravelProject() {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let store = ConfigurationStore(
            rootURL: root.appendingPathComponent("support"),
            projectsURL: root.appendingPathComponent("projects")
        )
        defer { try? FileManager.default.removeItem(at: root) }
        let site = SiteProject(
            path: root.appendingPathComponent("projects/demo"),
            name: "demo",
            framework: "Laravel",
            isLinked: false
        )
        let model = AppModel(configurationStore: store)
        model.sites = [site]

        model.showLogs(for: site)

        XCTAssertEqual(model.selectedPage, .logs)
        XCTAssertEqual(model.selectedSiteID, site.id)
        XCTAssertEqual(model.selectedLogSiteID, site.id)

        model.showApplicationLogs()
        XCTAssertNil(model.selectedLogSiteID)
    }

    @MainActor
    func testNavigationChangesDoNotInvalidateTheApplicationModel() {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let store = ConfigurationStore(
            rootURL: root.appendingPathComponent("support"),
            projectsURL: root.appendingPathComponent("projects")
        )
        defer { try? FileManager.default.removeItem(at: root) }
        let navigation = AppNavigation()
        let model = AppModel(configurationStore: store, navigation: navigation)
        var modelChangeCount = 0
        let observation = model.objectWillChange.sink { modelChangeCount += 1 }

        navigation.selectedPage = .sites
        navigation.selectedSiteID = "/tmp/example"

        XCTAssertEqual(model.selectedPage, .sites)
        XCTAssertEqual(model.selectedSiteID, "/tmp/example")
        XCTAssertEqual(modelChangeCount, 0)
        withExtendedLifetime(observation) {}
    }

    @MainActor
    func testApplicationSettingsCoordinatorOwnsUpdateStateWithoutInvalidatingAppModel() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let store = ConfigurationStore(
            rootURL: root.appendingPathComponent("support"),
            projectsURL: root.appendingPathComponent("projects")
        )
        defer { try? FileManager.default.removeItem(at: root) }
        let release = AppUpdateRelease(
            version: "1.2.3",
            build: 12,
            channel: "Beta",
            notes: "Coordinator update",
            downloadURL: URL(string: "https://example.test/herdme.dmg")
        )
        let checker = TestAppUpdateChecker(result: .success(.available(release)))
        let launchManager = TestLaunchAtLoginManager(
            status: LaunchAtLoginStatus(isEnabled: false, requiresApproval: false)
        )
        let coordinator = ApplicationSettingsCoordinator(
            appUpdateManager: checker,
            launchAtLoginManager: launchManager
        )
        let model = AppModel(configurationStore: store, applicationSettings: coordinator)
        var modelChangeCount = 0
        let observation = model.objectWillChange.sink { modelChangeCount += 1 }

        coordinator.checkForUpdates(channel: "Beta", userInitiated: true)
        coordinator.checkForUpdates(channel: "Stable", userInitiated: true)
        XCTAssertTrue(coordinator.isCheckingForUpdates)
        await coordinator.waitForUpdateCheck()

        let channels = await checker.channels
        XCTAssertEqual(channels, ["Beta"])
        XCTAssertFalse(coordinator.isCheckingForUpdates)
        XCTAssertEqual(coordinator.updateNotice?.title, "HerdMe 1.2.3 is available")
        XCTAssertEqual(coordinator.updateNotice?.message, "Coordinator update")
        XCTAssertEqual(model.updateNotice?.downloadURL, release.platformDownloadURL)
        XCTAssertEqual(modelChangeCount, 0)
        withExtendedLifetime(observation) {}
    }

    @MainActor
    func testApplicationSettingsCoordinatorHandlesLaunchStateAndUpdateFailures() async throws {
        let checker = TestAppUpdateChecker(result: .failure(.failed))
        let launchManager = TestLaunchAtLoginManager(
            status: LaunchAtLoginStatus(isEnabled: false, requiresApproval: true)
        )
        let coordinator = ApplicationSettingsCoordinator(
            appUpdateManager: checker,
            launchAtLoginManager: launchManager
        )

        XCTAssertEqual(
            coordinator.refreshLaunchAtLogin(),
            LaunchAtLoginStatus(isEnabled: false, requiresApproval: true)
        )
        XCTAssertTrue(coordinator.launchAtLoginRequiresApproval)
        XCTAssertEqual(
            try coordinator.setLaunchAtLogin(true),
            LaunchAtLoginStatus(isEnabled: true, requiresApproval: false)
        )
        coordinator.openLoginItemsSettings()
        XCTAssertEqual(launchManager.requestedStates, [true])
        XCTAssertEqual(launchManager.openSettingsCount, 1)

        coordinator.checkForUpdates(channel: "Stable", userInitiated: true)
        await coordinator.waitForUpdateCheck()
        XCTAssertEqual(coordinator.updateNotice?.title, "Update check failed")
        XCTAssertEqual(coordinator.updateNotice?.message, "Test update failure")

        let unavailable = ApplicationSettingsCoordinator(
            appUpdateManager: nil,
            launchAtLoginManager: launchManager
        )
        unavailable.checkForUpdates(channel: "Stable", userInitiated: true)
        XCTAssertEqual(unavailable.updateNotice?.title, "Updates unavailable")
    }

    @MainActor
    func testMailChangesDoNotInvalidateTheApplicationModel() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let support = root.appendingPathComponent("support")
        let store = ConfigurationStore(
            rootURL: support,
            projectsURL: root.appendingPathComponent("projects")
        )
        defer { try? FileManager.default.removeItem(at: root) }
        let message = CapturedMail(
            id: UUID(),
            sender: "sender@example.test",
            recipients: ["recipient@example.test"],
            subject: "Coordinator isolation",
            receivedAt: Date(timeIntervalSince1970: 1_000),
            body: "body",
            raw: "Subject: Coordinator isolation\r\n\r\nbody",
            htmlBody: nil
        )
        try await MailStore(rootURL: support).save(message)
        let model = AppModel(configurationStore: store)
        var modelChangeCount = 0
        let observation = model.objectWillChange.sink { modelChangeCount += 1 }

        await model.mail.load()

        XCTAssertEqual(model.mail.messages, [message.summary])
        XCTAssertEqual(model.mailMessages, [message.summary])
        XCTAssertEqual(modelChangeCount, 0)
        withExtendedLifetime(observation) {}
    }

    @MainActor
    func testDumpChangesDoNotInvalidateTheApplicationModel() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let support = root.appendingPathComponent("support")
        let store = ConfigurationStore(
            rootURL: support,
            projectsURL: root.appendingPathComponent("projects")
        )
        defer { try? FileManager.default.removeItem(at: root) }
        let dump = CapturedDump(
            id: UUID(),
            receivedAt: Date(timeIntervalSince1970: 1_000),
            source: "Coordinator isolation",
            summary: "dump value",
            payload: "payload"
        )
        try await DumpStore(rootURL: support).save(dump)
        let model = AppModel(configurationStore: store)
        var modelChangeCount = 0
        let observation = model.objectWillChange.sink { modelChangeCount += 1 }

        await model.dumpsCoordinator.load()

        XCTAssertEqual(model.dumpsCoordinator.dumps, [dump])
        XCTAssertEqual(model.dumps, [dump])
        XCTAssertEqual(modelChangeCount, 0)
        withExtendedLifetime(observation) {}
    }

    @MainActor
    func testServiceChangesDoNotInvalidateTheApplicationModel() {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let store = ConfigurationStore(
            rootURL: root.appendingPathComponent("support"),
            projectsURL: root.appendingPathComponent("projects")
        )
        defer { try? FileManager.default.removeItem(at: root) }
        let instance = ServiceInstance(
            id: UUID(),
            definitionID: "redis",
            name: "Redis",
            version: "8.0",
            port: 6_379,
            isRunning: false,
            startAutomatically: false
        )
        let model = AppModel(configurationStore: store)
        var modelChangeCount = 0
        let observation = model.objectWillChange.sink { modelChangeCount += 1 }

        model.services.refreshStates(for: [instance])
        XCTAssertTrue(model.services.beginOperation(for: instance.id))

        XCTAssertEqual(model.serviceStates, model.services.states)
        XCTAssertEqual(model.serviceOperation, instance.id)
        XCTAssertEqual(modelChangeCount, 0)
        model.services.endOperation(for: instance.id)
        withExtendedLifetime(observation) {}
    }

    @MainActor
    func testServicesCoordinatorRejectsOperationsAsSoonAsShutdownBegins() {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let coordinator = ServicesCoordinator(
            rootURL: root,
            credentialStore: ServiceCredentialStore(backend: TestServiceCredentialBackend())
        )
        let identifier = UUID()

        XCTAssertTrue(coordinator.beginOperation(for: identifier))
        coordinator.beginShutdown()

        XCTAssertNil(coordinator.operation)
        XCTAssertFalse(coordinator.beginOperation(for: UUID()))
    }

    @MainActor
    func testRuntimeChangesDoNotInvalidateTheApplicationModel() {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let store = ConfigurationStore(
            rootURL: root.appendingPathComponent("support"),
            projectsURL: root.appendingPathComponent("projects")
        )
        defer { try? FileManager.default.removeItem(at: root) }
        let version = RuntimeVersion(
            cycle: "8.4",
            installedVersion: "8.4.12",
            isActive: true
        )
        let model = AppModel(configurationStore: store)
        var modelChangeCount = 0
        let observation = model.objectWillChange.sink { modelChangeCount += 1 }

        model.runtime.phpVersions = [version]
        XCTAssertTrue(model.runtime.beginOperation("php-8.4"))
        XCTAssertFalse(model.runtime.beginOperation("node-22"))

        XCTAssertEqual(model.phpVersions, [version])
        XCTAssertEqual(model.runtimeOperation, "php-8.4")
        XCTAssertEqual(modelChangeCount, 0)
        model.runtime.endOperation("php-8.4")
        withExtendedLifetime(observation) {}
    }

    @MainActor
    func testSiteChangesDoNotInvalidateTheApplicationModel() {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let store = ConfigurationStore(
            rootURL: root.appendingPathComponent("support"),
            projectsURL: root.appendingPathComponent("projects")
        )
        defer { try? FileManager.default.removeItem(at: root) }
        let site = SiteProject(
            path: root.appendingPathComponent("projects/demo"),
            name: "demo",
            framework: "Laravel",
            isLinked: false
        )
        let model = AppModel(configurationStore: store)
        var modelChangeCount = 0
        let observation = model.objectWillChange.sink { modelChangeCount += 1 }

        model.sitesCoordinator.replaceSites([site])
        model.sitesCoordinator.replaceRuntimePorts([site.id: 8_001])

        XCTAssertEqual(model.sites, [site])
        XCTAssertEqual(model.siteRuntimePorts, [site.id: 8_001])
        XCTAssertEqual(modelChangeCount, 0)
        withExtendedLifetime(observation) {}
    }

    @MainActor
    func testEnvironmentChangesDoNotInvalidateTheApplicationModel() {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let store = ConfigurationStore(
            rootURL: root.appendingPathComponent("support"),
            projectsURL: root.appendingPathComponent("projects")
        )
        defer { try? FileManager.default.removeItem(at: root) }
        let model = AppModel(configurationStore: store)
        var modelChangeCount = 0
        let observation = model.objectWillChange.sink { modelChangeCount += 1 }

        model.environment.status = .running
        model.environment.apply(proxyPort: 8_080, httpsPort: 8_443)

        XCTAssertEqual(model.environmentStatus, .running)
        XCTAssertEqual(model.environmentProxyPort, 8_080)
        XCTAssertEqual(model.environmentHTTPSPort, 8_443)
        XCTAssertTrue(model.isHTTPSActive)
        XCTAssertEqual(modelChangeCount, 0)
        withExtendedLifetime(observation) {}
    }

    @MainActor
    func testEnvironmentCoordinatorUsesInjectedRunner() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let runner = TestLocalEnvironmentRunner()
        let certificateManager = LocalCertificateManager(rootURL: root)
        let coordinator = EnvironmentCoordinator(
            rootURL: root,
            certificateManager: certificateManager,
            engine: runner
        )
        let site = SiteProject(
            path: root.appendingPathComponent("demo"),
            name: "demo",
            framework: "Laravel",
            isLinked: false
        )

        let snapshot = try await coordinator.startEngine(
            sites: [site],
            defaultPHP: nil,
            defaultPHPCycle: "8.4",
            tld: "test",
            debuggerSettings: .disabled,
            phpRequestSettings: .default,
            enableHTTPS: true
        )

        XCTAssertEqual(snapshot.sitePorts, [site.id: 8_790])
        XCTAssertEqual(snapshot.proxyPort, 8_080)
        XCTAssertEqual(snapshot.httpsPort, 8_443)
        XCTAssertTrue(snapshot.isRunning)
        XCTAssertEqual(runner.startCount, 1)

        await coordinator.stopEngine()
        XCTAssertEqual(runner.stopCount, 1)
        XCTAssertFalse(runner.isRunning)

        coordinator.stopImmediately()
        XCTAssertEqual(runner.immediateStopCount, 1)
    }

    @MainActor
    func testRuntimeCoordinatorUsesInjectedInstaller() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let installer = TestRuntimeInstaller()
        let coordinator = RuntimeCoordinator(rootURL: root, installer: installer)

        try await coordinator.activatePHP(cycle: "8.4")
        let phpVersion = try await coordinator.installPHP(cycle: "8.4")
        let nodeVersion = try await coordinator.installNode(cycle: "22")
        try await coordinator.activateNode(cycle: "22")
        try await coordinator.removeNode(cycle: "20")
        let composerVersion = await coordinator.composerVersion(cycle: "8.4")
        let latestComposer = try await coordinator.latestComposerVersion(cycle: "8.4")
        let updatedComposer = try await coordinator.updateComposer(cycle: "8.4")
        let laravelVersion = await coordinator.laravelInstallerVersion(cycle: "8.4")
        let latestLaravel = try await coordinator.latestLaravelInstallerVersion()
        let updatedLaravel = try await coordinator.updateLaravelInstaller(cycle: "8.4")
        try await coordinator.prepareLaravelInstallerForProjectCreation(cycle: "8.4")
        let phpVersions = try await coordinator.latestPHPVersions(cycles: ["8.4"])
        let nodeVersions = try await coordinator.latestNodeVersions(cycles: ["22"])

        XCTAssertEqual(phpVersion, "8.4.test")
        XCTAssertEqual(nodeVersion, "22.test")
        XCTAssertEqual(composerVersion, "2.9.0")
        XCTAssertEqual(latestComposer, "2.9.1")
        XCTAssertEqual(updatedComposer, "2.9.1")
        XCTAssertEqual(laravelVersion, "5.20.0")
        XCTAssertEqual(latestLaravel, "5.21.0")
        XCTAssertEqual(updatedLaravel, "5.21.0")
        XCTAssertEqual(phpVersions, ["8.4": "8.4.test"])
        XCTAssertEqual(nodeVersions, ["22": "22.test"])
        XCTAssertEqual(coordinator.latestPHPVersions, ["8.4": "8.4.test"])
        XCTAssertEqual(coordinator.latestNodeVersions, ["22": "22.test"])
        XCTAssertEqual(coordinator.composerVersion, "2.9.1")
        XCTAssertEqual(coordinator.latestComposerVersion, "2.9.1")
        XCTAssertEqual(coordinator.laravelInstallerVersion, "5.21.0")
        XCTAssertEqual(coordinator.latestLaravelInstallerVersion, "5.21.0")
        let calls = await installer.calls
        XCTAssertEqual(
            calls,
            [
                "activate-php:8.4",
                "install-php:8.4",
                "install-node:22",
                "activate-node:22",
                "remove-node:20",
                "composer-version:8.4",
                "latest-composer:8.4",
                "update-composer:8.4",
                "laravel-version:8.4",
                "latest-laravel",
                "update-laravel:8.4",
                "prepare-laravel:8.4",
                "latest-php:8.4",
                "latest-node:22"
            ])
    }

    @MainActor
    func testSecuritySetupChangesDoNotInvalidateTheApplicationModel() {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let store = ConfigurationStore(
            rootURL: root.appendingPathComponent("support"),
            projectsURL: root.appendingPathComponent("projects")
        )
        defer { try? FileManager.default.removeItem(at: root) }
        let model = AppModel(configurationStore: store)
        var modelChangeCount = 0
        let observation = model.objectWillChange.sink { modelChangeCount += 1 }

        model.security.domainResolverState = .managed
        model.security.isDNSServerRunning = true
        model.security.certificateTrustState = .trusted
        model.security.onboardingStage = .completed

        XCTAssertEqual(model.domainResolverState, .managed)
        XCTAssertTrue(model.isDNSServerRunning)
        XCTAssertEqual(model.certificateTrustState, .trusted)
        XCTAssertEqual(model.onboardingStage, .completed)
        XCTAssertEqual(modelChangeCount, 0)
        withExtendedLifetime(observation) {}
    }

    func testLogStoreFindsNestedLogsAndReadsTheirTail() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let nested = root.appendingPathComponent("services", isDirectory: true)
        try FileManager.default.createDirectory(at: nested, withIntermediateDirectories: true)
        let log = nested.appendingPathComponent("database.log")
        try Data("head-tail".utf8).write(to: log)
        defer { try? FileManager.default.removeItem(at: root) }

        let store = LogStore(rootURL: root)
        let files = store.files()

        XCTAssertEqual(files.map(\.relativePath), ["services/database.log"])
        XCTAssertEqual(try store.contents(of: files[0], maximumBytes: 4), "tail")
    }

    func testLogStoreAppendsTimestampedApplicationEvents() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }

        let store = LogStore(rootURL: root)
        try store.append("Automatic SMTP startup failed", at: Date(timeIntervalSince1970: 0))
        try store.append("Automatic DNS startup failed", at: Date(timeIntervalSince1970: 1))

        let appLog = try XCTUnwrap(store.files().first(where: { $0.relativePath == "app.log" }))
        XCTAssertEqual(
            try store.contents(of: appLog),
            "[1970-01-01T00:00:00Z] Automatic SMTP startup failed\n"
                + "[1970-01-01T00:00:01Z] Automatic DNS startup failed\n"
        )
    }

    func testLogStoreRotatesOversizedApplicationLog() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let logURL = root.appendingPathComponent("app.log")
        try Data(repeating: 65, count: 64).write(to: logURL)

        let store = LogStore(rootURL: root, maximumLogBytes: 32, retainedLogFiles: 2)
        try store.append("new entry", at: Date(timeIntervalSince1970: 0))

        XCTAssertEqual(try Data(contentsOf: URL(fileURLWithPath: logURL.path + ".1")).count, 64)
        XCTAssertEqual(
            try String(contentsOf: logURL, encoding: .utf8),
            "[1970-01-01T00:00:00Z] new entry\n"
        )
    }

    func testMailStoreIndexesMetadataAndLoadsMessageBodiesOnDemand() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let bodyMarker = String(repeating: "lazy-body-marker-", count: 4_096)
        let message = CapturedMail(
            id: UUID(),
            sender: "sender@example.test",
            recipients: ["recipient@example.test"],
            subject: "Indexed message",
            receivedAt: Date(timeIntervalSince1970: 1_000),
            body: bodyMarker,
            raw: "Subject: Indexed message\r\n\r\n" + bodyMarker,
            htmlBody: nil
        )
        let store = MailStore(rootURL: root)

        try await store.save(message)
        let summaries = await store.loadSummaries()

        XCTAssertEqual(summaries, [message.summary])
        let indexContents = try String(contentsOf: store.indexURL, encoding: .utf8)
        XCTAssertFalse(indexContents.contains(bodyMarker))
        XCTAssertLessThan(indexContents.utf8.count, 2_048)
        let loadedMessage = try await store.message(id: message.id)
        XCTAssertEqual(loadedMessage, message)

        let reloadedStore = MailStore(rootURL: root)
        let reloadedSummaries = await reloadedStore.loadSummaries()
        XCTAssertEqual(reloadedSummaries, [message.summary])
        try await reloadedStore.delete(id: message.id)
        let emptySummaries = await reloadedStore.loadSummaries()
        XCTAssertTrue(emptySummaries.isEmpty)
    }

    func testCaptureRetentionPrunesExpiredAndExcessFiles() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let now = Date(timeIntervalSince1970: 1_000)
        for (name, age) in [("expired", 200.0), ("oldest", 30.0), ("middle", 20.0), ("newest", 10.0)] {
            let url = root.appendingPathComponent(name + ".json")
            try Data("{}".utf8).write(to: url)
            try FileManager.default.setAttributes(
                [.modificationDate: now.addingTimeInterval(-age)],
                ofItemAtPath: url.path
            )
        }

        try CaptureRetentionPolicy(itemLimit: 2, maximumAge: 100).prune(
            directoryURL: root,
            now: now
        )

        let remaining = try FileManager.default.contentsOfDirectory(atPath: root.path).sorted()
        XCTAssertEqual(remaining, ["middle.json", "newest.json"])
    }

    func testServiceProcessManagerStartsAndStopsManagedProcess() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let executable = root.appendingPathComponent("test-service")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try Data("#!/bin/sh\ntrap 'exit 0' TERM INT\nwhile :; do sleep 1; done\n".utf8).write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: executable.path)
        defer { try? FileManager.default.removeItem(at: root) }

        let manager = ServiceProcessManager(
            rootURL: root,
            executableOverrides: ["redis": executable],
            readinessProbe: { _ in true }
        )
        let instance = ServiceInstance(
            id: UUID(),
            definitionID: "redis",
            name: "Test Redis",
            version: "test",
            port: try XCTUnwrap(LocalEnvironmentEngine.availablePort(startingAt: 32_000)),
            isRunning: false
        )
        defer { manager.stopAllImmediately() }

        XCTAssertEqual(manager.state(for: instance), .stopped)
        try await manager.start(instance)
        XCTAssertEqual(manager.state(for: instance), .running)
        XCTAssertTrue(FileManager.default.fileExists(atPath: manager.dataDirectory(for: instance).path))
        XCTAssertTrue(
            FileManager.default.fileExists(
                atPath: root.appendingPathComponent("Log/services/\(instance.id.uuidString).log").path
            )
        )
        let pidFile = manager.dataDirectory(for: instance).appendingPathComponent(".herdme.pid")
        XCTAssertTrue(FileManager.default.fileExists(atPath: pidFile.path))

        await manager.stop(instance)
        XCTAssertEqual(manager.state(for: instance), .stopped)
        XCTAssertFalse(FileManager.default.fileExists(atPath: pidFile.path))
    }

    @MainActor
    func testServiceProcessManagerReadsProtectedCredentialsAwayFromMainThread() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let executable = root.appendingPathComponent("test-service")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try Data("#!/bin/sh\ntrap 'exit 0' TERM INT\nwhile :; do sleep 1; done\n".utf8)
            .write(to: executable)
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o755],
            ofItemAtPath: executable.path
        )
        defer { try? FileManager.default.removeItem(at: root) }

        let backend = ThreadRecordingServiceCredentialBackend()
        let manager = ServiceProcessManager(
            rootURL: root,
            executableOverrides: ["minio": executable],
            credentialStore: ServiceCredentialStore(backend: backend),
            readinessProbe: { _ in true }
        )
        let instance = ServiceInstance(
            id: UUID(),
            definitionID: "minio",
            name: "Protected Service",
            version: "test",
            port: try XCTUnwrap(LocalEnvironmentEngine.availablePort(startingAt: 32_020)),
            isRunning: false
        )
        defer { manager.stopAllImmediately() }

        try await manager.start(instance, allowCredentialInteraction: false)

        let result = try XCTUnwrap(backend.result())
        XCTAssertFalse(result.isMainThread)
        XCTAssertFalse(result.allowsInteraction)
    }

    func testServiceProcessManagerRejectsAProcessThatNeverBecomesReady() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let executable = root.appendingPathComponent("test-service")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try Data("#!/bin/sh\ntrap 'exit 0' TERM INT\nwhile :; do sleep 1; done\n".utf8)
            .write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: executable.path)
        defer { try? FileManager.default.removeItem(at: root) }

        let instance = ServiceInstance(
            id: UUID(),
            definitionID: "redis",
            name: "Unready Redis",
            version: "test",
            port: try XCTUnwrap(LocalEnvironmentEngine.availablePort(startingAt: 32_025)),
            isRunning: false
        )
        let manager = ServiceProcessManager(
            rootURL: root,
            executableOverrides: ["redis": executable],
            readinessProbe: { _ in false },
            readinessTimeout: 0.1
        )

        do {
            try await manager.start(instance)
            XCTFail("A service without a listening port must not be marked running")
        } catch let error as ServiceRuntimeError {
            guard case .readinessTimedOut(let name, let port) = error else {
                return XCTFail("Unexpected service error: \(error)")
            }
            XCTAssertEqual(name, instance.name)
            XCTAssertEqual(port, instance.port)
        }
        XCTAssertEqual(manager.state(for: instance), .stopped)
        XCTAssertFalse(
            FileManager.default.fileExists(
                atPath: manager.dataDirectory(for: instance).appendingPathComponent(".herdme.pid").path
            )
        )
        let logURL = root.appendingPathComponent("Log/services/\(instance.id.uuidString).log")
        let log = try String(contentsOf: logURL, encoding: .utf8)
        XCTAssertTrue(
            log.contains(
                ServiceRuntimeError.readinessTimedOut(
                    instance.name,
                    instance.port
                ).localizedDescription
            )
        )
    }

    func testServiceStartCancellationTerminatesUnreadyProcess() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let executable = root.appendingPathComponent("test-service")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try Data(
            "#!/bin/sh\necho $$ > process.pid\ntrap 'exit 0' TERM INT\nwhile :; do sleep 1; done\n".utf8
        ).write(to: executable)
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o755],
            ofItemAtPath: executable.path
        )
        defer { try? FileManager.default.removeItem(at: root) }

        let instance = ServiceInstance(
            id: UUID(),
            definitionID: "redis",
            name: "Cancelled Redis",
            version: "test",
            port: try XCTUnwrap(LocalEnvironmentEngine.availablePort(startingAt: 32_040)),
            isRunning: false
        )
        let manager = ServiceProcessManager(
            rootURL: root,
            executableOverrides: ["redis": executable],
            readinessProbe: { _ in false },
            readinessTimeout: 10
        )
        defer { manager.stopAllImmediately() }

        let operation = Task { try await manager.start(instance) }
        let processIDURL = manager.dataDirectory(for: instance).appendingPathComponent("process.pid")
        let appeared = try await AsyncProcessLifecycle.waitUntil(
            timeout: 2,
            interval: .milliseconds(20),
            condition: { FileManager.default.fileExists(atPath: processIDURL.path) }
        )
        XCTAssertTrue(appeared)
        operation.cancel()

        do {
            try await operation.value
            XCTFail("A cancelled service start must not succeed")
        } catch is CancellationError {
            // Expected.
        }
        let processID = try XCTUnwrap(
            Int32(
                String(contentsOf: processIDURL, encoding: .utf8)
                    .trimmingCharacters(in: .whitespacesAndNewlines)
            ))
        let exited = try await AsyncProcessLifecycle.waitUntil(
            timeout: 2,
            interval: .milliseconds(20),
            condition: { Darwin.kill(processID, 0) != 0 }
        )
        XCTAssertTrue(exited)
        XCTAssertEqual(manager.state(for: instance), .stopped)
        XCTAssertFalse(
            FileManager.default.fileExists(
                atPath: manager.dataDirectory(for: instance).appendingPathComponent(".herdme.pid").path
            ))
    }

    func testServiceProcessManagerSerializesConcurrentStarts() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let executable = root.appendingPathComponent("test-service")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try Data("#!/bin/sh\ntrap 'exit 0' TERM INT\nwhile :; do sleep 1; done\n".utf8)
            .write(to: executable)
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o755],
            ofItemAtPath: executable.path
        )
        defer { try? FileManager.default.removeItem(at: root) }

        let manager = ServiceProcessManager(
            rootURL: root,
            executableOverrides: ["redis": executable],
            readinessProbe: { _ in true }
        )
        let instance = ServiceInstance(
            id: UUID(),
            definitionID: "redis",
            name: "Concurrent Redis",
            version: "test",
            port: try XCTUnwrap(LocalEnvironmentEngine.availablePort(startingAt: 32_050)),
            isRunning: false
        )
        defer { manager.stopAllImmediately() }

        async let first: Void = manager.start(instance)
        async let second: Void = manager.start(instance)
        _ = try await (first, second)

        XCTAssertEqual(manager.state(for: instance), .running)
        let logURL = root.appendingPathComponent("Log/services/\(instance.id.uuidString).log")
        let log = try String(contentsOf: logURL, encoding: .utf8)
        XCTAssertEqual(log.components(separatedBy: "[HerdMe] Starting").count - 1, 1)
    }

    func testServiceProcessManagerRecoversAndStopsPersistedManagedProcess() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let executable = root.appendingPathComponent("test-service")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try Data("#!/bin/sh\ntrap 'exit 0' TERM INT\nwhile :; do sleep 1; done\n".utf8).write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: executable.path)
        defer { try? FileManager.default.removeItem(at: root) }

        let instance = ServiceInstance(
            id: UUID(),
            definitionID: "redis",
            name: "Recovered Redis",
            version: "test",
            port: try XCTUnwrap(LocalEnvironmentEngine.availablePort(startingAt: 32_100)),
            isRunning: false
        )
        let original = ServiceProcessManager(
            rootURL: root,
            executableOverrides: ["redis": executable],
            readinessProbe: { _ in true }
        )
        defer { original.stopAllImmediately() }
        try await original.start(instance)

        let recovered = ServiceProcessManager(rootURL: root, executableOverrides: ["redis": executable])
        XCTAssertEqual(recovered.state(for: instance), .running)
        await recovered.stop(instance)
        XCTAssertEqual(recovered.state(for: instance), .stopped)
        XCTAssertEqual(original.state(for: instance), .stopped)
    }

    func testServiceProcessManagerRejectsStalePIDForUnrelatedProcess() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let executable = root.appendingPathComponent("test-service")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try Data("#!/bin/sh\nexit 0\n".utf8).write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: executable.path)
        defer { try? FileManager.default.removeItem(at: root) }

        let instance = ServiceInstance(
            id: UUID(),
            definitionID: "redis",
            name: "Stale Redis",
            version: "test",
            port: 32_200,
            isRunning: false
        )
        let manager = ServiceProcessManager(rootURL: root, executableOverrides: ["redis": executable])
        let dataURL = manager.dataDirectory(for: instance)
        try FileManager.default.createDirectory(at: dataURL, withIntermediateDirectories: true)
        let pidFile = dataURL.appendingPathComponent(".herdme.pid")
        try Data("\(ProcessInfo.processInfo.processIdentifier)\n".utf8).write(to: pidFile)

        XCTAssertEqual(manager.state(for: instance), .stopped)
        XCTAssertFalse(FileManager.default.fileExists(atPath: pidFile.path))
    }

    func testServiceStartFailureIsLoggedWithoutLosingTheOriginalError() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let blocker = try TestHTTPBackend(responseBody: "occupied")
        defer {
            blocker.stop()
            try? FileManager.default.removeItem(at: root)
        }
        let instance = ServiceInstance(
            id: UUID(),
            definitionID: "redis",
            name: "Local Redis",
            version: "test",
            port: blocker.port,
            isRunning: false
        )
        let manager = ServiceProcessManager(rootURL: root)

        do {
            try await manager.start(instance)
            XCTFail("Starting on an occupied port must fail")
        } catch {
            XCTAssertEqual(error.localizedDescription, ServiceRuntimeError.portUnavailable(blocker.port).localizedDescription)
        }

        let log = root.appendingPathComponent("Log/services/\(instance.id.uuidString).log")
        let contents = try String(contentsOf: log, encoding: .utf8)
        XCTAssertTrue(contents.contains("Failed to start Local Redis"))
        XCTAssertTrue(
            contents.contains(
                ServiceRuntimeError.portUnavailable(blocker.port).localizedDescription
            )
        )
    }

    func testDatabaseServiceSocketFitsTheUnixPathLimit() {
        let first = ServiceProcessManager.databaseSocketPath(
            for: UUID(uuidString: "11111111-2222-3333-4444-555555555555")!
        )
        let second = ServiceProcessManager.databaseSocketPath(
            for: UUID(uuidString: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")!
        )

        XCTAssertTrue(first.hasPrefix("/tmp/herdme-"))
        XCTAssertLessThan(first.utf8.count, 104)
        XCTAssertNotEqual(first, second)
    }

    func testServiceLaunchArgumentsDisableAndBindAuxiliaryListeners() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let manager = ServiceProcessManager(rootURL: root)
        let dataURL = root.appendingPathComponent("data", isDirectory: true)

        let mysql = ServiceInstance(
            id: UUID(uuidString: "11111111-1111-1111-1111-111111111111")!,
            definitionID: "mysql",
            name: "MySQL",
            version: "test",
            port: 3_306,
            isRunning: false
        )
        let mysqlArguments = manager.arguments(
            for: mysql,
            dataURL: dataURL,
            consolePort: nil,
            peeringPort: nil
        )
        XCTAssertTrue(mysqlArguments.contains("--bind-address=127.0.0.1"))
        XCTAssertTrue(mysqlArguments.contains("--mysqlx=0"))

        let mariaDB = ServiceInstance(
            id: UUID(uuidString: "22222222-2222-2222-2222-222222222222")!,
            definitionID: "mariadb",
            name: "MariaDB",
            version: "test",
            port: 3_306,
            isRunning: false
        )
        XCTAssertFalse(
            manager.arguments(
                for: mariaDB,
                dataURL: dataURL,
                consolePort: nil,
                peeringPort: nil
            ).contains("--mysqlx=0"))

        let typesense = ServiceInstance(
            id: UUID(uuidString: "33333333-3333-3333-3333-333333333333")!,
            definitionID: "typesense",
            name: "Typesense",
            version: "test",
            port: 8_108,
            isRunning: false
        )
        let typesenseArguments = manager.arguments(
            for: typesense,
            dataURL: dataURL,
            consolePort: nil,
            peeringPort: 8_107,
            credentials: ServiceCredentials(
                username: "herdme_typesense",
                secret: "typesense_secret_0123456789_ABCDEFGHIJKL"
            )
        )
        func value(after flag: String) -> String? {
            guard let index = typesenseArguments.firstIndex(of: flag),
                typesenseArguments.indices.contains(index + 1)
            else { return nil }
            return typesenseArguments[index + 1]
        }
        XCTAssertEqual(value(after: "--api-address"), "127.0.0.1")
        XCTAssertEqual(value(after: "--api-port"), "8108")
        XCTAssertEqual(value(after: "--peering-address"), "127.0.0.1")
        XCTAssertEqual(value(after: "--peering-port"), "8107")
        XCTAssertEqual(value(after: "--api-key"), "typesense_secret_0123456789_ABCDEFGHIJKL")
        XCTAssertFalse(typesenseArguments.contains("--listen-address"))
        XCTAssertEqual(ServiceProcessManager.typesensePeeringPort(apiPort: 8_108), 8_107)
        XCTAssertEqual(ServiceProcessManager.typesensePeeringPort(apiPort: 1), 2)
        XCTAssertNil(ServiceProcessManager.typesensePeeringPort(apiPort: 0))
        XCTAssertNil(ServiceProcessManager.typesensePeeringPort(apiPort: 65_536))
    }

    func testAvailablePortRejectsValuesOutsideTheTCPRange() {
        XCTAssertNil(LocalEnvironmentEngine.availablePort(startingAt: 0))
        XCTAssertNil(LocalEnvironmentEngine.availablePort(startingAt: 65_536))
        XCTAssertFalse(LocalEnvironmentEngine.canBind(port: 65_536))
        XCTAssertFalse(LocalEnvironmentEngine.canConnect(port: 65_536))
    }

    func testServicePortSuggestionSkipsReservationsWithoutStoppingExternalOwner() throws {
        let blocker = try TestHTTPBackend(responseBody: "external service")
        defer { blocker.stop() }
        let reservedPort = blocker.port < 65_535 ? blocker.port + 1 : blocker.port - 1
        let suggestion = try XCTUnwrap(
            LocalEnvironmentEngine.availablePort(
                startingAt: blocker.port,
                excluding: [reservedPort]
            ))

        XCTAssertNotEqual(suggestion, blocker.port)
        XCTAssertNotEqual(suggestion, reservedPort)
        XCTAssertTrue(LocalEnvironmentEngine.canBind(port: suggestion))
        let response = try Self.sendHTTPRequest(
            port: blocker.port,
            request: "GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"
        )
        XCTAssertTrue(response.contains("external service"))
    }

    func testServiceConsolePortAcceptsOnlyLoopbackEndpoints() {
        XCTAssertEqual(
            ServiceProcessManager.consolePort(
                from: ["server", "data", "--console-address", "127.0.0.1:9002"]
            ),
            9_002
        )
        XCTAssertNil(ServiceProcessManager.consolePort(from: ["--console-address", "0.0.0.0:9002"]))
        XCTAssertNil(ServiceProcessManager.consolePort(from: ["--console-address", "127.0.0.1:65536"]))
    }

    func testStorageServiceConsoleURLTracksRunningProcess() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let executable = root.appendingPathComponent("test-storage")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try Data("#!/bin/sh\ntrap 'exit 0' TERM INT\nwhile :; do sleep 1; done\n".utf8).write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: executable.path)
        defer { try? FileManager.default.removeItem(at: root) }

        let instance = ServiceInstance(
            id: UUID(),
            definitionID: "minio",
            name: "Local MinIO",
            version: "test",
            port: try XCTUnwrap(LocalEnvironmentEngine.availablePort(startingAt: 32_300)),
            isRunning: false
        )
        let manager = ServiceProcessManager(
            rootURL: root,
            executableOverrides: ["minio": executable],
            credentialStore: ServiceCredentialStore(backend: TestServiceCredentialBackend()),
            readinessProbe: { _ in true }
        )
        defer { manager.stopAllImmediately() }

        try await manager.start(instance)
        let consoleURL = try XCTUnwrap(manager.consoleURL(for: instance))
        XCTAssertEqual(consoleURL.host, "127.0.0.1")
        XCTAssertNotEqual(consoleURL.port, instance.port)

        await manager.stop(instance)
        XCTAssertNil(manager.consoleURL(for: instance))
    }

    func testPortPresentationNeverUsesThousandsSeparators() {
        XCTAssertEqual(PortPresentation.number(9_003), "9003")
        XCTAssertEqual(
            PortPresentation.endpoint(host: "127.0.0.1", port: 9_003),
            "127.0.0.1:9003"
        )
    }

    func testTablePlusConnectionUsesManagedLoopbackCredentials() throws {
        func instance(_ definitionID: String, port: Int) -> ServiceInstance {
            ServiceInstance(
                id: UUID(),
                definitionID: definitionID,
                name: definitionID,
                version: "test",
                port: port,
                isRunning: true
            )
        }

        let credentials = ServiceCredentials(
            username: "herdme_tableplus",
            secret: "tableplus_secret_0123456789_ABCDEFGHIJKLM"
        )
        let mysql = try XCTUnwrap(
            TablePlusConnection.url(
                for: instance("mysql", port: 3_306),
                credentials: credentials
            ))
        XCTAssertEqual(
            TablePlusConnection.displayAddress(for: instance("mysql", port: 3_306)),
            "mysql://127.0.0.1:3306/mysql"
        )
        XCTAssertFalse(
            try XCTUnwrap(TablePlusConnection.displayAddress(for: instance("mysql", port: 3_306)))
                .contains(credentials.secret)
        )
        XCTAssertEqual(mysql.scheme, "mysql")
        XCTAssertEqual(mysql.user, credentials.username)
        XCTAssertEqual(mysql.password, credentials.secret)
        XCTAssertEqual(mysql.host, "127.0.0.1")
        XCTAssertEqual(mysql.port, 3_306)
        XCTAssertEqual(mysql.path, "/mysql")

        let mariaDB = try XCTUnwrap(
            TablePlusConnection.url(
                for: instance("mariadb", port: 3_307),
                credentials: credentials
            ))
        XCTAssertEqual(mariaDB.scheme, "mariadb")
        XCTAssertEqual(mariaDB.user, credentials.username)
        XCTAssertEqual(mariaDB.path, "/mysql")

        let postgreSQL = try XCTUnwrap(
            TablePlusConnection.url(
                for: instance("postgresql", port: 5_432),
                credentials: credentials
            ))
        XCTAssertEqual(postgreSQL.scheme, "postgresql")
        XCTAssertEqual(postgreSQL.user, credentials.username)
        XCTAssertEqual(postgreSQL.password, credentials.secret)
        XCTAssertEqual(postgreSQL.path, "/postgres")

        let mongoDB = try XCTUnwrap(TablePlusConnection.url(for: instance("mongodb", port: 27_017)))
        XCTAssertEqual(mongoDB.scheme, "mongodb")
        XCTAssertEqual(mongoDB.path, "/admin")

        let redis = try XCTUnwrap(TablePlusConnection.url(for: instance("redis", port: 6_379)))
        XCTAssertEqual(redis.scheme, "redis")
        XCTAssertEqual(redis.path, "/0")

        let valkey = try XCTUnwrap(TablePlusConnection.url(for: instance("valkey", port: 6_380)))
        XCTAssertEqual(valkey.scheme, "redis")
        XCTAssertEqual(valkey.path, "/0")
        XCTAssertNil(TablePlusConnection.url(for: instance("minio", port: 9_000)))
        XCTAssertNil(TablePlusConnection.displayAddress(for: instance("minio", port: 9_000)))
        XCTAssertNil(TablePlusConnection.url(for: instance("mysql", port: 65_536)))
        XCTAssertNil(TablePlusConnection.url(for: instance("mysql", port: 3_306)))
    }

    func testDatabaseAuthenticationContractsRemovePasswordlessAccess() {
        let credentials = ServiceCredentials(
            username: "herdme_database",
            secret: "database_secret_0123456789_ABCDEFGHIJKLM"
        )
        let sql = DatabaseServiceAuthenticator.mysqlProvisioningSQL(credentials: credentials)
        XCTAssertTrue(sql.contains("'herdme_database'@'127.0.0.1'"))
        XCTAssertTrue(sql.contains("IDENTIFIED BY '\(credentials.secret)'"))
        XCTAssertTrue(sql.contains("ALTER USER 'root'@'localhost'"))
        XCTAssertTrue(sql.contains("DELETE FROM mysql.user WHERE User = ''"))

        let original = """
            # PostgreSQL Client Authentication Configuration File
            local   all all                 trust
            host    all all 127.0.0.1/32    trust # managed loopback
            hostssl all all ::1/128         scram-sha-256
            host    all all 127.0.0.2/32    scram-sha-256 # trust remains in comments
            """
        let secured = DatabaseServiceAuthenticator.securedPostgreSQLHBA(original)
        XCTAssertTrue(secured.changed)
        XCTAssertFalse(secured.contents.contains("    trust"))
        XCTAssertTrue(secured.contents.contains("scram-sha-256 # managed loopback"))
        XCTAssertTrue(secured.contents.contains("scram-sha-256 # trust remains in comments"))
        XCTAssertTrue(secured.contents.contains("# PostgreSQL Client Authentication"))
    }

    func testInstalledMySQLCompatibleRuntimesEnforceManagedAuthentication() async throws {
        guard ProcessInfo.processInfo.environment["HERDME_DATABASE_AUTH_INTEGRATION"] == "1" else {
            throw XCTSkip("Set HERDME_DATABASE_AUTH_INTEGRATION=1 to test installed database runtimes.")
        }
        let candidates = [
            ("mysql", URL(fileURLWithPath: "/opt/homebrew/opt/mysql/bin/mysqld"), "mysql"),
            ("mariadb", URL(fileURLWithPath: "/opt/homebrew/opt/mariadb/bin/mariadbd"), "mariadb")
        ].filter { FileManager.default.isExecutableFile(atPath: $0.1.path) }
        guard !candidates.isEmpty else { throw XCTSkip("No supported Homebrew database runtime is installed.") }

        for (offset, candidate) in candidates.enumerated() {
            let root = FileManager.default.temporaryDirectory
                .appendingPathComponent("herdme-database-auth-\(UUID().uuidString)", isDirectory: true)
            let backend = TestServiceCredentialBackend()
            let store = ServiceCredentialStore(backend: backend)
            let instance = ServiceInstance(
                id: UUID(),
                definitionID: candidate.0,
                name: candidate.0,
                version: "integration",
                port: try XCTUnwrap(LocalEnvironmentEngine.availablePort(startingAt: 33_360 + offset * 20)),
                isRunning: false
            )
            let credentials = try store.credentials(for: instance.id)
            let manager = ServiceProcessManager(
                rootURL: root,
                executableOverrides: [candidate.0: candidate.1],
                credentialStore: store,
                readinessTimeout: 45
            )
            defer {
                manager.stopAllImmediately()
                try? FileManager.default.removeItem(at: root)
            }

            try await manager.start(instance)
            XCTAssertEqual(manager.state(for: instance), .running)
            XCTAssertTrue(
                FileManager.default.fileExists(
                    atPath: manager.dataDirectory(for: instance).appendingPathComponent(".herdme-auth-v1").path
                ))

            let client = candidate.1.deletingLastPathComponent().appendingPathComponent(candidate.2)
            let arguments = [
                "--no-defaults", "--protocol=TCP", "--host=127.0.0.1", "--port=\(instance.port)",
                "--user=\(credentials.username)", "--connect-timeout=5", "--batch", "--execute=SELECT 1"
            ]
            var authenticatedEnvironment = ProcessInfo.processInfo.environment
            authenticatedEnvironment["MYSQL_PWD"] = credentials.secret
            XCTAssertEqual(
                try ProcessRunner.run(
                    client,
                    arguments: arguments,
                    environment: authenticatedEnvironment,
                    timeout: 10
                ).status, 0)

            var passwordlessEnvironment = ProcessInfo.processInfo.environment
            passwordlessEnvironment["MYSQL_PWD"] = nil
            XCTAssertNotEqual(
                try ProcessRunner.run(
                    client,
                    arguments: arguments,
                    environment: passwordlessEnvironment,
                    timeout: 10
                ).status, 0)
            await manager.stop(instance)
        }
    }

    func testServiceFormulaTrustTargetIsLimitedToExpectedFormula() {
        let output = """
            Error: Refusing to load formula typesense/tap/typesense-server@30.2 from untrusted tap.
            Run `brew trust --formula typesense/tap/typesense-server@30.2` to trust it.
            """

        XCTAssertEqual(
            ServiceProcessManager.formulaTrustTarget(
                from: output,
                expectedFormula: "typesense/tap/typesense-server"
            ),
            "typesense/tap/typesense-server@30.2"
        )
        XCTAssertNil(
            ServiceProcessManager.formulaTrustTarget(
                from: output,
                expectedFormula: "someone/else/formula"
            )
        )
    }

    func testHomebrewCLISelectsTheFirstExecutableCandidate() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let first = root.appendingPathComponent("first-brew")
        let second = root.appendingPathComponent("second-brew")
        XCTAssertTrue(FileManager.default.createFile(atPath: first.path, contents: Data()))
        XCTAssertTrue(FileManager.default.createFile(atPath: second.path, contents: Data()))
        try FileManager.default.setAttributes([.posixPermissions: 0o644], ofItemAtPath: first.path)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: second.path)

        XCTAssertEqual(
            HomebrewCLI.executableURL(
                fileManager: .default,
                executablePaths: [first.path, second.path]
            ),
            second
        )

        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: first.path)
        XCTAssertEqual(
            HomebrewCLI.executableURL(
                fileManager: .default,
                executablePaths: [first.path, second.path]
            ),
            first
        )
    }

    func testHomebrewCLIConstructsConsistentEnvironment() {
        let environment = HomebrewCLI.environment(
            base: [
                "HOME": "/incorrect",
                "PATH": "/untrusted/bin",
                "LANG": "en_US.UTF-8"
            ],
            homeDirectory: URL(fileURLWithPath: "/Users/herdme-test", isDirectory: true),
            userName: "herdme-test"
        )

        XCTAssertEqual(environment["HOME"], "/Users/herdme-test")
        XCTAssertEqual(environment["USER"], "herdme-test")
        XCTAssertEqual(environment["LOGNAME"], "herdme-test")
        XCTAssertEqual(
            environment["PATH"],
            "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"
        )
        XCTAssertEqual(environment["LANG"], "en_US.UTF-8")
    }

    func testHomebrewCLITrimsCommandOutput() throws {
        let homebrew = HomebrewCLI(
            executableURL: URL(fileURLWithPath: "/bin/echo"),
            environment: [:]
        )

        let result = try homebrew.run(arguments: ["  ready  "])

        XCTAssertEqual(result.status, 0)
        XCTAssertEqual(result.output, "ready")
    }

    func testHomebrewCLICancelsBeforeLaunchingProcess() {
        let homebrew = HomebrewCLI(
            executableURL: URL(fileURLWithPath: "/usr/bin/true"),
            environment: [:]
        )

        XCTAssertThrowsError(try homebrew.run(arguments: [], cancellationRequested: { true })) { error in
            guard case ProcessRunnerError.cancelled = error else {
                return XCTFail("Unexpected error: \(error)")
            }
        }
    }

    func testPHPFormulaTrustTargetIsLimitedToTheExpectedVersionAndTap() {
        let output = """
            Error: Refusing to load formula shivammathur/php/php@8.0 from untrusted tap shivammathur/php.
            Run `brew trust --formula shivammathur/php/php@8.0` to trust it.
            """

        XCTAssertEqual(
            RuntimeInstaller.phpFormulaTrustTarget(from: output, cycle: "8.0"),
            "shivammathur/php/php@8.0"
        )
        XCTAssertNil(RuntimeInstaller.phpFormulaTrustTarget(from: output, cycle: "8.1"))
        XCTAssertNil(RuntimeInstaller.phpFormulaTrustTarget(from: output, cycle: "7.4"))
        XCTAssertNil(RuntimeInstaller.phpFormulaTrustTarget(from: output, cycle: "../8.0"))

        let unrelatedTap = """
            Error: Refusing to load formula example/php/php@8.0 from untrusted tap example/php.
            Run `brew trust --formula example/php/php@8.0` to trust it.
            """
        XCTAssertNil(RuntimeInstaller.phpFormulaTrustTarget(from: unrelatedTap, cycle: "8.0"))

        let suffixedFormula = """
            Error: Refusing to load formula shivammathur/php/php@8.0@preview from untrusted tap shivammathur/php.
            Run `brew trust --formula shivammathur/php/php@8.0@preview` to trust it.
            """
        XCTAssertNil(RuntimeInstaller.phpFormulaTrustTarget(from: suffixedFormula, cycle: "8.0"))
    }

    func testPHPInstallerRejectsUnsupportedCyclesBeforeHomebrew() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let installer = RuntimeInstaller(rootURL: root)

        do {
            _ = try await installer.installPHP(cycle: "7.4")
            XCTFail("An unsupported PHP cycle must not reach Homebrew.")
        } catch let error as RuntimeInstallationError {
            guard case .unsupportedPHPCycle(let cycle) = error else {
                return XCTFail("Unexpected error: \(error)")
            }
            XCTAssertEqual(cycle, "7.4")
        }

        do {
            _ = try await installer.latestPHPVersions(cycles: ["8.4", "7.4"])
            XCTFail("Update checks must reject unsupported PHP cycles.")
        } catch let error as RuntimeInstallationError {
            guard case .unsupportedPHPCycle(let cycle) = error else {
                return XCTFail("Unexpected error: \(error)")
            }
            XCTAssertEqual(cycle, "7.4")
        }
    }

    func testDatabaseFormulaConflictRecoveryOnlyAllowsMySQLAndMariaDBPair() throws {
        let mysqlOutput = """
            Error: Cannot install mysql because conflicting formulae are installed.
            mariadb: because both install the same binaries
            Please `brew unlink mariadb` before continuing.
            """
        let mysqlPlan = try XCTUnwrap(
            ServiceProcessManager.databaseConflictRecoveryPlan(from: mysqlOutput, installing: "mysql")
        )

        XCTAssertEqual(mysqlPlan.unlinkConflictArguments, ["unlink", "mariadb"])
        XCTAssertEqual(mysqlPlan.retryInstallArguments, ["install", "mysql"])
        XCTAssertEqual(mysqlPlan.restoreArguments, [["unlink", "mysql"], ["link", "mariadb"]])

        let mariaDBOutput = """
            Error: Cannot install mariadb because conflicting formulae are installed.
            mysql: because both install the same binaries
            Please `brew unlink mysql` before continuing.
            """
        XCTAssertEqual(
            ServiceProcessManager.databaseConflictRecoveryPlan(from: mariaDBOutput, installing: "mariadb"),
            ServiceProcessManager.DatabaseConflictRecoveryPlan(
                installingFormula: "mariadb",
                conflictingFormula: "mysql"
            )
        )
        XCTAssertNil(
            ServiceProcessManager.databaseConflictRecoveryPlan(from: mysqlOutput, installing: "redis")
        )
        XCTAssertNil(
            ServiceProcessManager.databaseConflictRecoveryPlan(
                from: mysqlOutput.replacingOccurrences(of: "unlink mariadb", with: "unlink redis"),
                installing: "mysql"
            )
        )
        XCTAssertEqual(
            ServiceProcessManager.databaseConflictRecoveryPlan(
                from: mysqlOutput,
                installing: "mysql",
                packageCommand: "upgrade"
            )?.retryInstallArguments,
            ["upgrade", "mysql"]
        )

        let decoratedOutput = """
            \u{001B}[31mError: Cannot install mysql because conflicting formulae are installed.\u{001B}[0m
            mariadb: because both install the same binaries
            Please brew unlink mariadb before continuing.
            """
        XCTAssertEqual(
            ServiceProcessManager.databaseConflictRecoveryPlan(
                from: decoratedOutput,
                installing: "mysql"
            ),
            mysqlPlan
        )
    }

    func testHomebrewOutdatedParserIgnoresDiagnosticsBeforeJSON() throws {
        let output = """
            Warning: ignored diagnostic before JSON
            {
              "formulae": [
                { "name": "mariadb", "installed_versions": ["12.3.1"], "current_version": "12.3.2" },
                { "name": "typesense-server", "installed_versions": ["30.1"], "current_version": "30.2" }
              ],
              "casks": []
            }
            """

        XCTAssertEqual(
            try ServiceProcessManager.outdatedFormulaNames(from: output),
            ["mariadb", "typesense-server"]
        )
    }

    func testExecutableLocatorRejectsOtherHerdFiles() {
        let herdPHP = URL(fileURLWithPath: "/Users/test/Library/Application Support/Herd/bin/php")
        let herdMePHP = URL(fileURLWithPath: "/Users/test/Library/Application Support/HerdMe/bin/php")

        XCTAssertTrue(ExecutableLocator.belongsToOtherHerd(herdPHP))
        XCTAssertFalse(ExecutableLocator.belongsToOtherHerd(herdMePHP))
    }

    func testNodeReleaseSelectionMatchesMajorAndArchitecture() {
        let releases = [
            NodeRelease(version: "v24.2.0", files: ["osx-x64-tar"]),
            NodeRelease(version: "v24.1.0", files: ["osx-arm64-tar"]),
            NodeRelease(version: "v22.9.0", files: ["osx-arm64-tar"])
        ]

        let selected = RuntimeInstaller.release(for: "24", archiveKey: "osx-arm64-tar", in: releases)

        XCTAssertEqual(selected?.version, "v24.1.0")
    }

    func testNodeChecksumSelectsOnlyTheExactArchive() {
        let manifest = """
            aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  node-v24.1.0-darwin-x64.tar.gz
            BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB *node-v24.1.0-darwin-arm64.tar.gz
            """

        XCTAssertEqual(
            RuntimeInstaller.nodeChecksum(
                for: "node-v24.1.0-darwin-arm64.tar.gz",
                in: manifest
            ),
            String(repeating: "b", count: 64)
        )
        XCTAssertNil(RuntimeInstaller.nodeChecksum(for: "node-v24.1.0.tar.gz", in: manifest))
        XCTAssertNil(
            RuntimeInstaller.nodeChecksum(
                for: "node-v24.1.0-darwin-arm64.tar.gz",
                in: "not-a-sha256  node-v24.1.0-darwin-arm64.tar.gz"
            ))
    }

    func testNodeSHA256MatchesKnownFixture() {
        XCTAssertEqual(
            RuntimeInstaller.sha256(of: Data("HerdMe".utf8)),
            "d0b0eeb2adcea192313b048f6b6cf8a04937c4e7f9cb98eda291aba97fc47296"
        )
    }

}
