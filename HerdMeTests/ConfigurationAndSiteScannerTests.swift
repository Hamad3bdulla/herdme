import Darwin
import Security
import XCTest
@testable import HerdMe

final class ConfigurationAndSiteScannerTests: XCTestCase {
    func testSingleInstanceGuardRejectsASecondLock() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let lockURL = root.appendingPathComponent("herdme.lock")
        defer { try? FileManager.default.removeItem(at: root) }

        var first: SingleInstanceGuard? = SingleInstanceGuard(lockURL: lockURL)
        XCTAssertTrue(try XCTUnwrap(first).acquired)
        XCTAssertFalse(SingleInstanceGuard(lockURL: lockURL).acquired)

        first = nil
        XCTAssertTrue(SingleInstanceGuard(lockURL: lockURL).acquired)
    }

    func testProjectCreationDefaultsToIndependentHerdMeFolder() {
        XCTAssertEqual(ProjectCreator.defaultProjectsDirectory.lastPathComponent, "HerdMe")
        XCTAssertEqual(
            ProjectCreator.defaultProjectsDirectory.deletingLastPathComponent(),
            FileManager.default.homeDirectoryForCurrentUser
        )
    }

    func testProjectCreationProgressIncludesOnlySelectedOptionalSteps() {
        XCTAssertEqual(
            ProjectCreationStage.stages(installBoost: true, initializeGit: true),
            [
                .validatingRequest,
                .preparingLaravelInstaller,
                .creatingLaravelProject,
                .installingLaravelBoost,
                .initializingGitRepository,
                .verifyingProject,
                .registeringSite,
                .completed
            ]
        )
        XCTAssertEqual(
            ProjectCreationStage.stages(installBoost: false, initializeGit: false),
            [
                .validatingRequest,
                .preparingLaravelInstaller,
                .creatingLaravelProject,
                .verifyingProject,
                .registeringSite,
                .completed
            ]
        )
        XCTAssertEqual(
            ProjectCreationStage.stages(
                installBoost: false,
                buildFrontendAssets: true,
                initializeGit: false
            ),
            [
                .validatingRequest,
                .preparingLaravelInstaller,
                .creatingLaravelProject,
                .preparingNodeRuntime,
                .installingFrontendDependencies,
                .buildingFrontendAssets,
                .verifyingProject,
                .registeringSite,
                .completed
            ]
        )
    }

    func testCustomStarterKitUsesLaravelInstallerPackageOption() throws {
        let parent = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let request = NewProjectRequest(
            name: "custom-project",
            parentDirectory: parent,
            starterKit: .custom,
            customStarterKit: "vendor/community-kit:^2.0",
            testingFramework: "PHPUnit",
            installBoost: false,
            initializeGit: false
        )
        XCTAssertNoThrow(try ProjectCreator.validate(request))
        XCTAssertEqual(
            ProjectCreator.laravelArguments(for: request),
            [
                "new", "custom-project", "--no-interaction", "--phpunit",
                "--using=vendor/community-kit:^2.0", "--npm"
            ]
        )

        XCTAssertThrowsError(try ProjectCreator.validate(NewProjectRequest(
            name: "custom-project",
            parentDirectory: parent,
            starterKit: .custom,
            customStarterKit: "not a package",
            testingFramework: "Pest",
            installBoost: false,
            initializeGit: false
        )))
    }

    func testLaravelInstallerJSONFailureUsesConcisePresentation() {
        let failure = ErrorPresentation(
            #"{"success":false,"directory":"/Users/demo/HerdMe/demo-app","log":"/tmp/laravel-installer.log","tail":"UnexpectedValueException at vendor/monolog/monolog/src/Monolog/Handler/StreamHandler.php:164: The stream could not be opened in append mode."}"#,
            fallback: "Laravel Installer could not finish creating the site."
        )

        XCTAssertTrue(failure.message.contains("could not write"))
        XCTAssertFalse(failure.message.hasPrefix("{"))
        XCTAssertTrue(failure.technicalDetails?.contains("Project folder:") == true)
        XCTAssertTrue(failure.technicalDetails?.contains("Installer log:") == true)
    }

    func testDiskSpaceFailureExplainsHowToRecover() {
        let failure = ErrorPresentation("write failed: No space left on device")

        XCTAssertTrue(failure.message.contains("free disk space"))
        XCTAssertNotNil(failure.technicalDetails)
    }

    func testProjectCreationReusesInstalledLaravelInstallerWithoutLaunchingPHP() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let php = root.appendingPathComponent("bin/php")
        let composer = root.appendingPathComponent("bin/composer")
        let laravel = root.appendingPathComponent("Composer/vendor/bin/laravel")
        let invocationMarker = root.appendingPathComponent("php-was-launched")
        defer { try? FileManager.default.removeItem(at: root) }

        for directory in [php.deletingLastPathComponent(), laravel.deletingLastPathComponent()] {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        }
        try Data(
            "#!/bin/sh\ntouch \"\(invocationMarker.path)\"\nexit 1\n".utf8
        ).write(to: php)
        try Data("managed composer".utf8).write(to: composer)
        try Data("managed laravel installer".utf8).write(to: laravel)
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o755],
            ofItemAtPath: php.path
        )

        let installer = RuntimeInstaller(rootURL: root)
        let isReady = await installer.isLaravelInstallerReadyForProjectCreation()
        XCTAssertTrue(isReady)
        try await installer.prepareLaravelInstallerForProjectCreation(cycle: "8.4")
        XCTAssertFalse(FileManager.default.fileExists(atPath: invocationMarker.path))
    }

    @MainActor
    func testProjectCreatorBuildsFrontendAssetsForStarterKits() async throws {
        let fixture = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let root = fixture.appendingPathComponent("support", isDirectory: true)
        let parent = fixture.appendingPathComponent("projects", isDirectory: true)
        let php = root.appendingPathComponent("bin/php")
        let npm = root.appendingPathComponent("bin/npm")
        let laravel = root.appendingPathComponent("Composer/vendor/bin/laravel")
        try FileManager.default.createDirectory(
            at: php.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try FileManager.default.createDirectory(
            at: laravel.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try FileManager.default.createDirectory(at: parent, withIntermediateDirectories: true)
        try Data("fake laravel".utf8).write(to: laravel)
        try Data(
            """
            #!/bin/sh
            mkdir -p "$PWD/$3/vendor"
            touch "$PWD/$3/artisan"
            touch "$PWD/$3/vendor/autoload.php"
            printf '{"scripts":{"build":"vite build"}}' > "$PWD/$3/package.json"
            touch "$PWD/$3/vite.config.ts"
            exit 0
            """.utf8
        ).write(to: php)
        try Data(
            """
            #!/bin/sh
            echo "$@" >> "$PWD/npm-invocations.log"
            if [ "$1" = "run" ] && [ "$2" = "build" ]; then
                mkdir -p "$PWD/public/build"
                printf '{}' > "$PWD/public/build/manifest.json"
            fi
            exit 0
            """.utf8
        ).write(to: npm)
        for executable in [php, npm] {
            try FileManager.default.setAttributes(
                [.posixPermissions: 0o755],
                ofItemAtPath: executable.path
            )
        }
        defer { try? FileManager.default.removeItem(at: fixture) }

        var stages: [ProjectCreationStage] = []
        let destination = try await ProjectCreator(rootURL: root).create(
            NewProjectRequest(
                name: "frontend-project",
                parentDirectory: parent,
                starterKit: .react,
                testingFramework: "Pest",
                installBoost: false,
                initializeGit: false
            )
        ) { stage in
            stages.append(stage)
        }

        XCTAssertEqual(
            stages,
            [
                .creatingLaravelProject,
                .preparingNodeRuntime,
                .installingFrontendDependencies,
                .buildingFrontendAssets,
                .verifyingProject
            ]
        )
        XCTAssertTrue(FileManager.default.fileExists(
            atPath: destination.appendingPathComponent("public/build/manifest.json").path
        ))
        let invocations = try String(
            contentsOf: destination.appendingPathComponent("npm-invocations.log"),
            encoding: .utf8
        )
        XCTAssertTrue(invocations.contains("install --no-audit --no-fund --no-progress"))
        XCTAssertTrue(invocations.contains("run build"))
    }

    @MainActor
    func testProjectCreatorReportsRealOptionalStagesAndInitializesGit() async throws {
        let fixture = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let root = fixture.appendingPathComponent("support", isDirectory: true)
        let parent = fixture.appendingPathComponent("projects", isDirectory: true)
        let php = root.appendingPathComponent("bin/php")
        let composer = root.appendingPathComponent("bin/composer")
        let laravel = root.appendingPathComponent("Composer/vendor/bin/laravel")
        try FileManager.default.createDirectory(
            at: php.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try FileManager.default.createDirectory(
            at: laravel.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try FileManager.default.createDirectory(at: parent, withIntermediateDirectories: true)
        try Data("fake composer".utf8).write(to: composer)
        try Data("fake laravel".utf8).write(to: laravel)
        try Data(
            """
            #!/bin/sh
            if [ "$2" = "new" ]; then
                mkdir -p "$PWD/$3/vendor"
                touch "$PWD/$3/artisan"
                touch "$PWD/$3/vendor/autoload.php"
            fi
            exit 0
            """.utf8
        ).write(to: php)
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o755],
            ofItemAtPath: php.path
        )
        defer { try? FileManager.default.removeItem(at: fixture) }

        var stages: [ProjectCreationStage] = []
        let destination = try await ProjectCreator(rootURL: root).create(
            NewProjectRequest(
                name: "progress-test",
                parentDirectory: parent,
                starterKit: .none,
                testingFramework: "Pest",
                installBoost: true,
                initializeGit: true
            )
        ) { stage in
            stages.append(stage)
        }

        XCTAssertEqual(
            stages,
            [
                .creatingLaravelProject,
                .installingLaravelBoost,
                .initializingGitRepository,
                .verifyingProject
            ]
        )
        XCTAssertTrue(FileManager.default.fileExists(atPath: destination.appendingPathComponent("artisan").path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: destination.appendingPathComponent("vendor/autoload.php").path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: destination.appendingPathComponent(".git").path))
    }

    @MainActor
    func testProjectCreatorRejectsIncompleteLaravelOutputDuringVerification() async throws {
        let fixture = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let root = fixture.appendingPathComponent("support", isDirectory: true)
        let parent = fixture.appendingPathComponent("projects", isDirectory: true)
        let php = root.appendingPathComponent("bin/php")
        let laravel = root.appendingPathComponent("Composer/vendor/bin/laravel")
        try FileManager.default.createDirectory(
            at: php.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try FileManager.default.createDirectory(
            at: laravel.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try FileManager.default.createDirectory(at: parent, withIntermediateDirectories: true)
        try Data("fake laravel".utf8).write(to: laravel)
        try Data(
            """
            #!/bin/sh
            mkdir -p "$PWD/$3"
            touch "$PWD/$3/artisan"
            exit 0
            """.utf8
        ).write(to: php)
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o755],
            ofItemAtPath: php.path
        )
        defer { try? FileManager.default.removeItem(at: fixture) }

        var stages: [ProjectCreationStage] = []
        do {
            _ = try await ProjectCreator(rootURL: root).create(
                NewProjectRequest(
                    name: "incomplete-project",
                    parentDirectory: parent,
                    starterKit: .none,
                    testingFramework: "Pest",
                    installBoost: false,
                    initializeGit: false
                )
            ) { stage in
                stages.append(stage)
            }
            XCTFail("Incomplete Laravel output must not be registered as a successful site.")
        } catch {
            XCTAssertTrue(error.localizedDescription.contains("vendor/autoload.php"))
        }
        XCTAssertEqual(stages, [.creatingLaravelProject, .verifyingProject])
    }

    func testScannerFindsLaravelAndNodeProjects() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let laravel = root.appendingPathComponent("laravel-app")
        let node = root.appendingPathComponent("node-app")
        try FileManager.default.createDirectory(at: laravel, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: node, withIntermediateDirectories: true)
        try Data().write(to: laravel.appendingPathComponent("artisan"))
        try Data("8.4\n".utf8).write(to: laravel.appendingPathComponent(".herdme-php"))
        try Data("{}".utf8).write(to: node.appendingPathComponent("package.json"))
        defer { try? FileManager.default.removeItem(at: root) }

        let sites = SiteScanner().scan(paths: [root.path])

        XCTAssertEqual(sites.count, 2)
        XCTAssertEqual(sites.first(where: { $0.name == "laravel-app" })?.framework, "Laravel")
        XCTAssertEqual(sites.first(where: { $0.name == "laravel-app" })?.phpVersion, "8.4")
        XCTAssertEqual(sites.first(where: { $0.name == "node-app" })?.framework, "Node.js")
    }

    func testUnlinkRemovesOnlyRegistrationSymlink() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let project = root.appendingPathComponent("source-project")
        let links = root.appendingPathComponent("links")
        let registration = links.appendingPathComponent("linked-project")
        try FileManager.default.createDirectory(at: project, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: links, withIntermediateDirectories: true)
        try Data().write(to: project.appendingPathComponent("artisan"))
        try FileManager.default.createSymbolicLink(at: registration, withDestinationURL: project)
        defer { try? FileManager.default.removeItem(at: root) }

        let site = try XCTUnwrap(SiteScanner().scan(paths: [links.path]).first)
        XCTAssertTrue(site.isLinked)
        XCTAssertEqual(site.registrationPath?.lastPathComponent, registration.lastPathComponent)
        XCTAssertEqual(
            site.registrationPath?.deletingLastPathComponent().resolvingSymlinksInPath(),
            links.resolvingSymlinksInPath()
        )

        try SiteLinkManager.unlink(site)

        XCTAssertFalse(FileManager.default.fileExists(atPath: registration.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: project.appendingPathComponent("artisan").path))
    }

    func testDefaultConfigurationUsesTestTLD() {
        let configuration = AppConfiguration.default

        XCTAssertEqual(configuration.tld, "test")
        XCTAssertEqual(configuration.selectedPHP, "8.4")
        XCTAssertEqual(configuration.smtpPort, 2525)
        XCTAssertEqual(URL(fileURLWithPath: configuration.parkPaths[0]).lastPathComponent, "HerdMe")
    }

    func testIndependentPathPolicyRejectsOtherHerdFoldersAndDescendants() {
        let home = URL(fileURLWithPath: "/Users/test", isDirectory: true)

        XCTAssertTrue(IndependentPathPolicy.belongsToOtherHerd(
            URL(fileURLWithPath: "/Users/test/Herd"),
            homeDirectory: home
        ))
        XCTAssertTrue(IndependentPathPolicy.belongsToOtherHerd(
            URL(fileURLWithPath: "/Users/test/Herd/project"),
            homeDirectory: home
        ))
        XCTAssertTrue(IndependentPathPolicy.belongsToOtherHerd(
            URL(fileURLWithPath: "/users/TEST/herd/project"),
            homeDirectory: home
        ))
        XCTAssertTrue(IndependentPathPolicy.belongsToOtherHerd(
            URL(fileURLWithPath: "/Users/test/Library/Application Support/Herd/bin/php"),
            homeDirectory: home
        ))
        XCTAssertFalse(IndependentPathPolicy.belongsToOtherHerd(
            URL(fileURLWithPath: "/Users/test/HerdMe/project"),
            homeDirectory: home
        ))
        XCTAssertEqual(
            IndependentPathPolicy.removingOtherHerdPaths(
                from: [
                    "/Users/test/Herd",
                    "/Users/test/Herd/project",
                    "/Users/test/HerdMe",
                    "/Users/test/Projects"
                ],
                homeDirectory: home
            ),
            ["/Users/test/HerdMe", "/Users/test/Projects"]
        )
    }

    func testScannerIgnoresLinksIntoOtherHerdFolders() throws {
        let home = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let otherHerd = home.appendingPathComponent("Herd", isDirectory: true)
        let project = otherHerd.appendingPathComponent("laravel-app", isDirectory: true)
        let links = home.appendingPathComponent("Links", isDirectory: true)
        try FileManager.default.createDirectory(at: project, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: links, withIntermediateDirectories: true)
        try Data().write(to: project.appendingPathComponent("artisan"))
        try FileManager.default.createSymbolicLink(
            at: links.appendingPathComponent("laravel-app"),
            withDestinationURL: project
        )
        defer { try? FileManager.default.removeItem(at: home) }

        XCTAssertTrue(IndependentPathPolicy.belongsToOtherHerd(project, homeDirectory: home))
        XCTAssertTrue(SiteScanner(homeDirectory: home).scan(paths: [links.path]).isEmpty)
    }

    func testConfigurationDecodesOlderFilesWithoutLosingSettings() throws {
        let data = Data(
            """
            {
              "parkPaths": ["/tmp/projects"],
              "tld": "local",
              "selectedPHP": "8.3",
              "theme": "Dark"
            }
            """.utf8
        )

        let configuration = try JSONDecoder().decode(AppConfiguration.self, from: data)

        XCTAssertEqual(configuration.parkPaths, ["/tmp/projects"])
        XCTAssertEqual(configuration.tld, "local")
        XCTAssertEqual(configuration.selectedPHP, "8.3")
        XCTAssertEqual(configuration.theme, "Dark")
        XCTAssertFalse(configuration.startAutomatically)
        XCTAssertTrue(configuration.automaticUpdates)
        XCTAssertEqual(configuration.smtpPort, 2525)
        XCTAssertEqual(configuration.independenceMigrationVersion, 0)
    }

    func testLegacyOtherHerdRootMigratesToIndependentProjectsOnce() throws {
        let home = URL(fileURLWithPath: "/Users/test", isDirectory: true)
        let data = Data(
            """
            {
              "parkPaths": ["/Users/test/Herd"],
              "tld": "test",
              "selectedPHP": "8.4"
            }
            """.utf8
        )
        let legacy = try JSONDecoder().decode(AppConfiguration.self, from: data)

        var migrated = ConfigurationStore.migratingIndependentPaths(
            in: legacy,
            homeDirectory: home
        )

        XCTAssertEqual(migrated.parkPaths, ["/Users/test/HerdMe"])
        XCTAssertEqual(
            migrated.independenceMigrationVersion,
            ConfigurationStore.currentIndependenceMigrationVersion
        )

        migrated.parkPaths.removeAll()
        XCTAssertTrue(ConfigurationStore.migratingIndependentPaths(
            in: migrated,
            homeDirectory: home
        ).parkPaths.isEmpty)
    }

    func testServiceCatalogUsesUniqueIdentifiers() {
        let identifiers = ServiceCatalog.all.map(\.id)

        XCTAssertEqual(Set(identifiers).count, identifiers.count)
        XCTAssertTrue(identifiers.contains("mysql"))
        XCTAssertTrue(identifiers.contains("redis"))
        XCTAssertFalse(identifiers.contains("reverb"))
        XCTAssertTrue(identifiers.allSatisfy { ServiceProcessManager.supports(definitionID: $0) })
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

    func testTerminalCommandsShellQuoteProjectPaths() {
        XCTAssertEqual(TerminalCommandLauncher.shellQuote("/tmp/O'Brien App"), "'/tmp/O'\\''Brien App'")
    }

    func testSidebarExposesTheImplementedLogViewer() {
        XCTAssertTrue(SidebarPage.visibleCases.contains(.logs))
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

    func testServiceProcessManagerStartsAndStopsManagedProcess() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let executable = root.appendingPathComponent("test-service")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try Data("#!/bin/sh\ntrap 'exit 0' TERM INT\nwhile :; do sleep 1; done\n".utf8).write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: executable.path)
        defer { try? FileManager.default.removeItem(at: root) }

        let manager = ServiceProcessManager(rootURL: root, executableOverrides: ["redis": executable])
        let instance = ServiceInstance(
            id: UUID(),
            definitionID: "redis",
            name: "Test Redis",
            version: "test",
            port: try XCTUnwrap(LocalEnvironmentEngine.availablePort(startingAt: 32_000)),
            isRunning: false
        )
        defer { manager.stopAll() }

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

        manager.stop(instance)
        XCTAssertEqual(manager.state(for: instance), .stopped)
        XCTAssertFalse(FileManager.default.fileExists(atPath: pidFile.path))
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
        let original = ServiceProcessManager(rootURL: root, executableOverrides: ["redis": executable])
        defer { original.stopAll() }
        try await original.start(instance)

        let recovered = ServiceProcessManager(rootURL: root, executableOverrides: ["redis": executable])
        XCTAssertEqual(recovered.state(for: instance), .running)
        recovered.stop(instance)
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
        XCTAssertTrue(contents.contains("Port \(blocker.port) is already in use"))
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
        XCTAssertFalse(manager.arguments(
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
            peeringPort: 8_107
        )
        func value(after flag: String) -> String? {
            guard let index = typesenseArguments.firstIndex(of: flag),
                  typesenseArguments.indices.contains(index + 1) else { return nil }
            return typesenseArguments[index + 1]
        }
        XCTAssertEqual(value(after: "--api-address"), "127.0.0.1")
        XCTAssertEqual(value(after: "--api-port"), "8108")
        XCTAssertEqual(value(after: "--peering-address"), "127.0.0.1")
        XCTAssertEqual(value(after: "--peering-port"), "8107")
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
        let manager = ServiceProcessManager(rootURL: root, executableOverrides: ["minio": executable])
        defer { manager.stopAll() }

        try await manager.start(instance)
        let consoleURL = try XCTUnwrap(manager.consoleURL(for: instance))
        XCTAssertEqual(consoleURL.host, "127.0.0.1")
        XCTAssertNotEqual(consoleURL.port, instance.port)

        manager.stop(instance)
        XCTAssertNil(manager.consoleURL(for: instance))
    }

    func testPortPresentationNeverUsesThousandsSeparators() {
        XCTAssertEqual(PortPresentation.number(9_003), "9003")
        XCTAssertEqual(
            PortPresentation.endpoint(host: "127.0.0.1", port: 9_003),
            "127.0.0.1:9003"
        )
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

    func testRuntimeUpdateComparisonOnlyAcceptsNewerStableVersions() {
        XCTAssertTrue(RuntimeInstaller.isNewerVersion("v22.24.0", than: "22.23.1"))
        XCTAssertFalse(RuntimeInstaller.isNewerVersion("22.23.1", than: "22.23.1"))
        XCTAssertFalse(RuntimeInstaller.isNewerVersion("5.30.0", than: "5.31.0"))
        XCTAssertTrue(RuntimeInstaller.isStableVersion("v5.31.0"))
        XCTAssertFalse(RuntimeInstaller.isStableVersion("v5.32.0-beta.1"))
    }

    func testHomebrewPHPInfoSelectsRequestedStableVersions() throws {
        let output = """
        Warning: ignored diagnostic before JSON
        {
          "formulae": [
            { "name": "php@8.4", "versions": { "stable": "8.4.23" } },
            { "name": "php@8.3", "versions": { "stable": "8.3.32" } },
            { "name": "unrelated", "versions": { "stable": "1.0.0" } }
          ]
        }
        """

        XCTAssertEqual(
            try RuntimeInstaller.phpVersions(
                fromHomebrewInfoOutput: output,
                cycles: ["8.4"]
            ),
            ["8.4": "8.4.23"]
        )
    }

    func testRuntimeInspectorReadsFullVersionsForInactivePHP() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        for (cycle, version) in [("8.4", "8.4.23"), ("8.3", "8.3.32")] {
            let executable = root.appendingPathComponent("Runtimes/php/\(cycle)/bin/php")
            try FileManager.default.createDirectory(
                at: executable.deletingLastPathComponent(),
                withIntermediateDirectories: true
            )
            try Data("#!/bin/sh\nprintf '\(version)'\n".utf8).write(to: executable)
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: executable.path)
        }
        let managedBin = root.appendingPathComponent("bin", isDirectory: true)
        try FileManager.default.createDirectory(at: managedBin, withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(
            at: managedBin.appendingPathComponent("php"),
            withDestinationURL: root.appendingPathComponent("Runtimes/php/8.4/bin/php")
        )

        let runtimes = RuntimeInspector(managedRoot: root).phpVersions(activeCycle: "8.4")

        XCTAssertEqual(runtimes.first(where: { $0.cycle == "8.4" })?.installedVersion, "8.4.23")
        XCTAssertEqual(runtimes.first(where: { $0.cycle == "8.3" })?.installedVersion, "8.3.32")
        XCTAssertTrue(runtimes.first(where: { $0.cycle == "8.4" })?.isActive == true)
        XCTAssertTrue(runtimes.first(where: { $0.cycle == "8.3" })?.isActive == false)
    }

    func testLongCommandFailuresShowOnlyRecentOutput() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let output = (1...30).map { "line \($0)" }.joined(separator: "\n")

        let summary = CommandFailureReporter.recordAndSummarize(
            output,
            operation: "test command",
            rootURL: root
        )

        XCTAssertFalse(summary.contains("line 1\n"))
        XCTAssertTrue(summary.contains("line 30"))
        XCTAssertTrue(summary.contains("Logs/homebrew.log"))
        XCTAssertTrue(FileManager.default.fileExists(atPath: root.appendingPathComponent("Log/homebrew.log").path))
    }

    func testEnvironmentUsesPublicDocumentRootAndLaravelRouter() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let publicDirectory = root.appendingPathComponent("public")
        try FileManager.default.createDirectory(at: publicDirectory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let site = SiteProject(path: root, name: "demo", framework: "Laravel", isLinked: false)

        XCTAssertEqual(
            LocalEnvironmentEngine.documentRoot(for: site).standardizedFileURL.path,
            publicDirectory.standardizedFileURL.path
        )
        let router = LocalEnvironmentEngine.routerScript(documentRoot: publicDirectory)
        XCTAssertTrue(router.contains(publicDirectory.path))
        XCTAssertTrue(router.contains("index.php"))
    }

    func testSiteDomainNormalizesInvalidFolderNamesWithoutCollisions() {
        XCTAssertEqual(SiteProject.dnsLabel(for: "app"), "app")
        let normalized = SiteProject.dnsLabel(for: "s2-")

        XCTAssertTrue(normalized.hasPrefix("s2-"))
        XCTAssertNotEqual(normalized, SiteProject.dnsLabel(for: "s2"))
        XCTAssertFalse(normalized.hasSuffix("-"))
    }

    func testSiteDisplayAddressOmitsInternalProxyPort() throws {
        XCTAssertEqual(
            AppModel.siteDisplayAddress(
                domain: "memo.test",
                navigationURL: try XCTUnwrap(URL(string: "https://memo.test:8443"))
            ),
            "https://memo.test"
        )
        XCTAssertEqual(
            AppModel.siteDisplayAddress(
                domain: "memo.test",
                navigationURL: try XCTUnwrap(URL(string: "http://127.0.0.1:9001"))
            ),
            "http://memo.test"
        )
    }

    func testSiteRuntimeStorePersistsHerdMeOverridesWithoutChangingNVMRC() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let project = root.appendingPathComponent("demo")
        try FileManager.default.createDirectory(at: project, withIntermediateDirectories: true)
        try Data("20\n".utf8).write(to: project.appendingPathComponent(".nvmrc"))
        defer { try? FileManager.default.removeItem(at: root) }
        let site = SiteProject(path: project, name: "demo", framework: "Laravel", isLinked: false)
        let store = SiteRuntimeStore()

        try store.set("8.4", kind: .php, for: site)
        try store.set("22", kind: .node, for: site)
        var scanned = try XCTUnwrap(SiteScanner().scan(paths: [root.path]).first)
        XCTAssertEqual(scanned.phpVersion, "8.4")
        XCTAssertEqual(scanned.nodeVersion, "22")

        try store.set(nil, kind: .node, for: site)
        scanned = try XCTUnwrap(SiteScanner().scan(paths: [root.path]).first)
        XCTAssertEqual(scanned.nodeVersion, "20")
        XCTAssertEqual(try String(contentsOf: project.appendingPathComponent(".nvmrc"), encoding: .utf8), "20\n")
    }

    func testCapturedMailParsesHeadersAndBody() {
        let raw = "From: Sender <sender@example.com>\r\nTo: test@example.com\r\nSubject: Welcome\r\n\r\nHello from HerdMe."

        let message = CapturedMail.parse(sender: "envelope@example.com", recipients: ["test@example.com"], raw: raw)

        XCTAssertEqual(message.sender, "Sender <sender@example.com>")
        XCTAssertEqual(message.subject, "Welcome")
        XCTAssertEqual(message.body, "Hello from HerdMe.")
    }

    func testCapturedMailDecodesMultipartHTMLAndQuotedPrintableText() {
        let raw = """
        From: sender@example.com
        To: recipient@example.com
        Subject: =?UTF-8?B?2YXYsdit2KjYpw==?=
        Content-Type: multipart/alternative; boundary="herdme-boundary"
        
        --herdme-boundary
        Content-Type: text/plain; charset=utf-8
        Content-Transfer-Encoding: quoted-printable
        
        Hello=20from=20HerdMe
        --herdme-boundary
        Content-Type: text/html; charset=utf-8
        Content-Transfer-Encoding: base64
        
        PGgxPkhlbGxvPC9oMT48cD5Gcm9tIEhlcmRNZTwvcD4=
        --herdme-boundary--
        """

        let message = CapturedMail.parse(
            sender: "sender@example.com",
            recipients: ["recipient@example.com"],
            raw: raw
        )

        XCTAssertEqual(message.subject, "مرحبا")
        XCTAssertEqual(message.body, "Hello from HerdMe")
        XCTAssertEqual(message.htmlBody, "<h1>Hello</h1><p>From HerdMe</p>")
        XCTAssertTrue(MailMIMEParser.safeHTMLDocument(message.htmlBody ?? "").contains("default-src 'none'"))
    }

    func testAppUpdateManagerSelectsStableAndBetaChannels() async throws {
        let manifestURL = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
            .appendingPathExtension("json")
        let manifest = AppUpdateManifest(releases: [
            AppUpdateRelease(
                version: "0.2.0",
                build: 2,
                channel: "stable",
                notes: "Stable release",
                downloadURL: URL(string: "https://example.test/stable.zip")
            ),
            AppUpdateRelease(
                version: "0.3.0",
                build: 1,
                channel: "beta",
                notes: "Beta release",
                downloadURL: URL(string: "https://example.test/beta.zip")
            )
        ])
        try JSONEncoder().encode(manifest).write(to: manifestURL, options: .atomic)
        defer { try? FileManager.default.removeItem(at: manifestURL) }

        let manager = AppUpdateManager(
            feedURL: manifestURL,
            currentVersion: "0.1.0",
            currentBuild: 1
        )
        let stableResult = try await manager.check(channel: "Stable")
        let betaResult = try await manager.check(channel: "Beta")

        XCTAssertEqual(
            stableResult,
            .available(try XCTUnwrap(manifest.releases.first(where: { $0.channel == "stable" })))
        )
        XCTAssertEqual(
            betaResult,
            .available(try XCTUnwrap(manifest.releases.first(where: { $0.channel == "beta" })))
        )
    }

    func testAppUpdateManagerUsesBuildForEqualVersions() async throws {
        let manifestURL = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
            .appendingPathExtension("json")
        let release = AppUpdateRelease(
            version: "0.1.0",
            build: 2,
            channel: "stable",
            notes: "Rebuilt release",
            downloadURL: nil
        )
        try JSONEncoder().encode(AppUpdateManifest(releases: [release]))
            .write(to: manifestURL, options: .atomic)
        defer { try? FileManager.default.removeItem(at: manifestURL) }

        let available = try await AppUpdateManager(
            feedURL: manifestURL,
            currentVersion: "0.1.0",
            currentBuild: 1
        ).check(channel: "Stable")
        let current = try await AppUpdateManager(
            feedURL: manifestURL,
            currentVersion: "0.1.0",
            currentBuild: 2
        ).check(channel: "Stable")

        XCTAssertEqual(available, .available(release))
        XCTAssertEqual(current, .upToDate(version: "0.1.0"))
    }

    func testPHPSerializationParserRendersArrays() throws {
        let serialized = Data("a:2:{s:5:\"value\";i:42;s:4:\"name\";s:6:\"HerdMe\";}".utf8)
        var parser = PHPSerializationParser(data: serialized)

        let rendered = try parser.parse().rendered()

        XCTAssertTrue(rendered.contains("value: 42"))
        XCTAssertTrue(rendered.contains("name: \"HerdMe\""))
    }

    func testRuntimeErrorsIdentifyTheRequestedRuntime() {
        let phpError = RuntimeInstallationError.runtimeNotInstalled(name: "PHP", cycle: "8.4")
        let nodeError = RuntimeInstallationError.runtimeNotInstalled(name: "Node.js", cycle: "22")

        XCTAssertEqual(phpError.errorDescription, "PHP 8.4 is not installed by HerdMe.")
        XCTAssertEqual(nodeError.errorDescription, "Node.js 22 is not installed by HerdMe.")
    }

    func testPHPFPMUsesHomebrewSbinLayout() {
        XCTAssertEqual(RuntimeInstaller.phpRelativePath(for: "php"), "bin/php")
        XCTAssertEqual(RuntimeInstaller.phpRelativePath(for: "php-fpm"), "sbin/php-fpm")
    }

    func testPHPRequestSettingsProduceBoundedFPMValues() {
        let settings = PHPRequestSettings(maxUploadMegabytes: -4, memoryLimitMegabytes: 200_000)

        XCTAssertEqual(settings.phpOptions["upload_max_filesize"], "1M")
        XCTAssertEqual(settings.phpOptions["post_max_size"], "1M")
        XCTAssertEqual(settings.phpOptions["memory_limit"], "100000M")
    }

    func testDebuggerSettingsNormalizePortAndIDEKey() {
        let settings = DebuggerSettings(
            enabled: true,
            detectBreakpoints: true,
            port: 90_003,
            ideKey: " VS CODE!\n"
        ).normalized

        XCTAssertEqual(settings.port, 65_535)
        XCTAssertEqual(settings.ideKey, "VSCODE")
    }

    func testXdebugFPMOptionsStayInsideHerdMeData() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let extensionURL = XdebugManager.extensionURL(rootURL: root, cycle: "8.4")
        try FileManager.default.createDirectory(
            at: extensionURL.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try Data("test".utf8).write(to: extensionURL)
        defer { try? FileManager.default.removeItem(at: root) }
        let debugger = DebuggerSettings(
            enabled: true,
            detectBreakpoints: true,
            port: 9_003,
            ideKey: "VSCODE"
        )

        let options = XdebugManager.phpOptions(
            rootURL: root,
            cycle: "8.4",
            debugger: debugger,
            request: .default
        )

        XCTAssertEqual(options["zend_extension"], extensionURL.path)
        XCTAssertEqual(options["xdebug.start_with_request"], "trigger")
        XCTAssertEqual(options["xdebug.client_port"], "9003")
        XCTAssertEqual(options["xdebug.trigger_value"], "VSCODE")
        XCTAssertTrue(options["xdebug.log"]?.hasPrefix(root.path) == true)
        XCTAssertTrue(XdebugManager.isValid(version: "3.5.3"))
        XCTAssertFalse(XdebugManager.isValid(version: "latest"))
    }

    func testLaravelExtensionReportIdentifiesMissingModules() {
        let modules = PHPRuntimeValidator.laravelRequiredExtensions
            .filter { $0 != "curl" }
            .joined(separator: "\n")

        let report = PHPRuntimeValidator.report(moduleOutput: "[PHP Modules]\n" + modules)

        XCTAssertEqual(report.missing, ["curl"])
        XCTAssertFalse(report.isLaravelCompatible)
    }

    func testManagedPHPContainsLaravel13RequiredExtensions() throws {
        let php = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/HerdMe/Runtimes/php/8.4/bin/php")
        guard FileManager.default.isExecutableFile(atPath: php.path) else {
            throw XCTSkip("HerdMe-managed PHP 8.4 is not installed on this machine.")
        }

        let report = try PHPRuntimeValidator().report(executable: php)

        XCTAssertTrue(report.isLaravelCompatible, "Missing: \(report.missing.joined(separator: ", "))")
        XCTAssertTrue(Set(PHPRuntimeValidator.laravelRequiredExtensions).isSubset(of: report.loaded))
    }

    func testListenerErrorsIdentifyTheService() {
        let error = LocalListenerError.invalidPort(service: "dump listener")

        XCTAssertEqual(error.errorDescription, "The configured dump listener port is invalid.")
    }

    func testDumpCaptureServerReportsPortConflicts() async throws {
        let occupiedPort = try TestHTTPBackend(responseBody: "occupied")
        let server = DumpCaptureServer()
        let failed = expectation(description: "dump listener reports the occupied port")
        defer {
            server.stop()
            occupiedPort.stop()
        }

        try server.start(
            port: occupiedPort.port,
            onStateChange: { running, errorMessage in
                if !running, errorMessage != nil { failed.fulfill() }
            },
            onDump: { _ in }
        )

        await fulfillment(of: [failed], timeout: 3)
        XCTAssertFalse(server.isRunning)
    }

    func testHTTPProxyExtractsAndNormalizesHost() {
        let request = Data("GET / HTTP/1.1\r\nHost: Demo.TEST:8080\r\nConnection: close\r\n\r\n".utf8)

        XCTAssertEqual(LocalHTTPProxy.host(in: request), "demo.test")
    }

    func testHTTPProxyReplacesForwardedHeadersAndPreservesBody() {
        let request = Data(
            "POST /submit HTTP/1.1\r\nHost: demo.test\r\nX-Forwarded-Proto: untrusted\r\nContent-Length: 5\r\n\r\nhello".utf8
        )

        let forwarded = LocalHTTPProxy.addingForwardedHeaders(to: request, secure: true)
        let rendered = String(decoding: forwarded, as: UTF8.self)

        XCTAssertTrue(rendered.contains("X-Forwarded-Proto: https\r\n"))
        XCTAssertTrue(rendered.contains("X-Forwarded-For: 127.0.0.1\r\n"))
        XCTAssertFalse(rendered.contains("untrusted"))
        XCTAssertTrue(rendered.hasSuffix("\r\n\r\nhello"))
    }

    func testHTTPProxyRoutesRequestToMatchingBackend() throws {
        let backend = try TestHTTPBackend(responseBody: "proxied-by-herdme")
        let proxy = LocalHTTPProxy()
        defer {
            proxy.stop()
            backend.stop()
        }
        let proxyPort = try proxy.start(routes: ["demo.test": backend.port])

        let response = try Self.sendHTTPRequest(
            port: proxyPort,
            request: "GET / HTTP/1.1\r\nHost: demo.test\r\nConnection: close\r\n\r\n"
        )

        XCTAssertTrue(response.contains("200 OK"))
        XCTAssertTrue(response.contains("proxied-by-herdme"))
    }

    func testHTTPSProxyUsesGeneratedCertificateForLocalDomain() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let certificateManager = LocalCertificateManager(rootURL: root)
        let identity = try certificateManager.prepareIdentity(tld: "test", domains: ["demo.test"])
        var importedCertificate: SecCertificate?
        XCTAssertEqual(SecIdentityCopyCertificate(identity, &importedCertificate), errSecSuccess)
        let importedSubject = importedCertificate.flatMap { SecCertificateCopySubjectSummary($0) as String? }
        XCTAssertEqual(importedSubject, "*.test")
        let certificateURL = root.appendingPathComponent("Certificates/herdme-ca.pem")
        let leafCertificateURL = root.appendingPathComponent("Certificates/local-sites.pem")
        let privateKeyURL = root.appendingPathComponent("Certificates/herdme-ca.key")
        XCTAssertTrue(FileManager.default.fileExists(atPath: certificateURL.path))
        let attributes = try FileManager.default.attributesOfItem(atPath: privateKeyURL.path)
        XCTAssertEqual((attributes[.posixPermissions] as? NSNumber)?.intValue, 0o600)
        let inspection = try Self.runProcess(
            executable: "/usr/bin/openssl",
            arguments: ["x509", "-in", leafCertificateURL.path, "-noout", "-text"]
        )
        XCTAssertEqual(inspection.status, 0, inspection.output)
        XCTAssertTrue(inspection.output.contains("DNS:*.test"), inspection.output)
        XCTAssertTrue(inspection.output.contains("DNS:demo.test"), inspection.output)

        let backend = try TestHTTPBackend(responseBody: "secured-by-herdme")
        let proxy = LocalHTTPProxy()
        defer {
            proxy.stop()
            backend.stop()
        }
        let preferredPort = try XCTUnwrap(LocalEnvironmentEngine.availablePort(startingAt: 9_443))
        let proxyPort = try proxy.start(
            routes: ["demo.test": backend.port],
            identity: identity,
            preferredPort: preferredPort,
            fallbackPort: 9_543
        )

        let process = Process()
        let output = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/curl")
        process.arguments = [
            "--silent", "--show-error", "--fail", "--max-time", "5",
            "--retry", "20", "--retry-connrefused", "--retry-delay", "0",
            "--noproxy", "*", "--cacert", certificateURL.path,
            "--resolve", "demo.test:\(proxyPort):127.0.0.1",
            "https://demo.test:\(proxyPort)/"
        ]
        process.standardOutput = output
        process.standardError = output
        try process.run()
        process.waitUntilExit()
        let response = String(decoding: output.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)

        XCTAssertEqual(process.terminationStatus, 0, response)
        XCTAssertEqual(response, "secured-by-herdme")
    }

    func testEnvironmentServesPHPThroughFPMOverHTTPAndHTTPS() throws {
        let managedRoot = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/HerdMe")
        let php = managedRoot.appendingPathComponent("Runtimes/php/8.4/bin/php")
        let fpm = managedRoot.appendingPathComponent("Runtimes/php/8.4/sbin/php-fpm")
        guard FileManager.default.isExecutableFile(atPath: php.path),
              FileManager.default.isExecutableFile(atPath: fpm.path) else {
            throw XCTSkip("HerdMe-managed PHP-FPM 8.4 is not installed on this machine.")
        }

        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let app = root.appendingPathComponent("fpm-demo")
        let publicDirectory = app.appendingPathComponent("public")
        let engineRoot = root.appendingPathComponent("HerdMeData")
        try FileManager.default.createDirectory(at: publicDirectory, withIntermediateDirectories: true)
        try Data(
            """
            <?php
            header('Content-Type: application/json');
            echo json_encode([
                'sapi' => PHP_SAPI,
                'method' => $_SERVER['REQUEST_METHOD'],
                'uri' => $_SERVER['REQUEST_URI'],
                'https' => $_SERVER['HTTPS'] ?? '',
                'body' => file_get_contents('php://input'),
            ]);
            """.utf8
        ).write(to: publicDirectory.appendingPathComponent("index.php"))
        try Data("static-from-herdme".utf8).write(to: publicDirectory.appendingPathComponent("asset.txt"))
        defer { try? FileManager.default.removeItem(at: root) }

        let certificateManager = LocalCertificateManager(rootURL: engineRoot)
        let engine = LocalEnvironmentEngine(rootURL: engineRoot, certificateManager: certificateManager)
        defer { engine.stopAll() }
        let site = SiteProject(path: app, name: "fpm-demo", framework: "Laravel", isLinked: false)
        let routes = try engine.start(sites: [site], defaultPHP: php, tld: "test")
        XCTAssertNotNil(routes[site.id])

        let httpPort = try XCTUnwrap(engine.proxyPort)
        let getResponse = try Self.sendHTTPRequest(
            port: httpPort,
            request: "GET /hello?name=HerdMe HTTP/1.1\r\nHost: fpm-demo.test\r\nConnection: close\r\n\r\n"
        )
        XCTAssertTrue(getResponse.contains("200 OK"), getResponse)
        XCTAssertTrue(getResponse.contains("\"sapi\":\"fpm-fcgi\""), getResponse)
        XCTAssertTrue(getResponse.contains("hello?name=HerdMe"), getResponse)

        let postBody = "laravel=13"
        let postResponse = try Self.sendHTTPRequest(
            port: httpPort,
            request: "POST /submit HTTP/1.1\r\nHost: fpm-demo.test\r\nContent-Type: application/x-www-form-urlencoded\r\nContent-Length: \(postBody.utf8.count)\r\nConnection: close\r\n\r\n\(postBody)"
        )
        XCTAssertTrue(postResponse.contains("\"method\":\"POST\""), postResponse)
        XCTAssertTrue(postResponse.contains("\"body\":\"laravel=13\""), postResponse)

        let staticResponse = try Self.sendHTTPRequest(
            port: httpPort,
            request: "GET /asset.txt HTTP/1.1\r\nHost: fpm-demo.test\r\nConnection: close\r\n\r\n"
        )
        XCTAssertTrue(staticResponse.contains("static-from-herdme"), staticResponse)

        let httpsPort = try XCTUnwrap(engine.httpsProxyPort)
        let certificateURL = engineRoot.appendingPathComponent("Certificates/herdme-ca.pem")
        let secure = try Self.runProcess(
            executable: "/usr/bin/curl",
            arguments: [
                "--silent", "--show-error", "--fail", "--max-time", "8",
                "--retry", "30", "--retry-connrefused", "--retry-delay", "0",
                "--noproxy", "*", "--cacert", certificateURL.path,
                "--resolve", "fpm-demo.test:\(httpsPort):127.0.0.1",
                "https://fpm-demo.test:\(httpsPort)/secure"
            ]
        )
        XCTAssertEqual(secure.status, 0, secure.output)
        XCTAssertTrue(secure.output.contains("\"https\":\"on\""), secure.output)
        XCTAssertTrue(secure.output.contains("\"sapi\":\"fpm-fcgi\""), secure.output)
    }

    func testEnvironmentLoadsInstalledXdebugAndPHPSettings() throws {
        let managedRoot = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/HerdMe")
        let php = managedRoot.appendingPathComponent("Runtimes/php/8.4/bin/php")
        let extensionURL = XdebugManager.extensionURL(rootURL: managedRoot, cycle: "8.4")
        guard FileManager.default.isExecutableFile(atPath: php.path),
              FileManager.default.isReadableFile(atPath: extensionURL.path) else {
            throw XCTSkip("HerdMe-managed Xdebug for PHP 8.4 is not installed on this machine.")
        }

        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let app = root.appendingPathComponent("xdebug-demo")
        let publicDirectory = app.appendingPathComponent("public")
        let engineRoot = root.appendingPathComponent("HerdMeData")
        let testExtension = XdebugManager.extensionURL(rootURL: engineRoot, cycle: "8.4")
        try FileManager.default.createDirectory(
            at: testExtension.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try FileManager.default.copyItem(at: extensionURL, to: testExtension)
        try FileManager.default.createDirectory(at: publicDirectory, withIntermediateDirectories: true)
        try Data(
            """
            <?php
            header('Content-Type: application/json');
            echo json_encode([
                'xdebug' => phpversion('xdebug') ?: '',
                'memory' => ini_get('memory_limit'),
                'upload' => ini_get('upload_max_filesize'),
            ]);
            """.utf8
        ).write(to: publicDirectory.appendingPathComponent("index.php"))
        defer { try? FileManager.default.removeItem(at: root) }

        let engine = LocalEnvironmentEngine(rootURL: engineRoot)
        defer { engine.stopAll() }
        let site = SiteProject(path: app, name: "xdebug-demo", framework: "PHP", isLinked: false)
        _ = try engine.start(
            sites: [site],
            defaultPHP: php,
            defaultPHPCycle: "8.4",
            tld: "test",
            debuggerSettings: DebuggerSettings(
                enabled: true,
                detectBreakpoints: true,
                port: 9_003,
                ideKey: "HERDME_TEST"
            ),
            phpRequestSettings: PHPRequestSettings(
                maxUploadMegabytes: 17,
                memoryLimitMegabytes: 321
            )
        )

        let response = try Self.sendHTTPRequest(
            port: try XCTUnwrap(engine.proxyPort),
            request: "GET /?XDEBUG_TRIGGER=OTHER HTTP/1.1\r\nHost: xdebug-demo.test\r\nConnection: close\r\n\r\n"
        )
        XCTAssertTrue(response.contains("\"xdebug\":\"3."), response)
        XCTAssertTrue(response.contains("\"memory\":\"321M\""), response)
        XCTAssertTrue(response.contains("\"upload\":\"17M\""), response)
    }

    @MainActor
    func testExistingLaravelProjectThroughFPMWhenRequested() async throws {
        let managedRoot = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/HerdMe")
        let php = managedRoot.appendingPathComponent("Runtimes/php/8.4/bin/php")
        let fpm = managedRoot.appendingPathComponent("Runtimes/php/8.4/sbin/php-fpm")
        guard FileManager.default.isExecutableFile(atPath: php.path),
              FileManager.default.isExecutableFile(atPath: fpm.path) else {
            throw XCTSkip("HerdMe-managed PHP-FPM 8.4 is not installed on this machine.")
        }

        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let app = root.appendingPathComponent("real-laravel")
        let engineRoot = root.appendingPathComponent("HerdMeData")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }

        let environment = ProcessInfo.processInfo.environment
        let source: URL
        if let sourcePath = environment["HERDME_LARAVEL_PROJECT"], !sourcePath.isEmpty {
            source = URL(fileURLWithPath: sourcePath).standardizedFileURL
        } else if environment["HERDME_CREATE_LARAVEL_INTEGRATION"] == "1" {
            let parent = root.appendingPathComponent("created", isDirectory: true)
            try FileManager.default.createDirectory(at: parent, withIntermediateDirectories: true)
            var stages: [ProjectCreationStage] = []
            source = try await ProjectCreator(rootURL: managedRoot).create(
                NewProjectRequest(
                    name: "laravel-live-" + String(UUID().uuidString.prefix(8)).lowercased(),
                    parentDirectory: parent,
                    starterKit: .react,
                    testingFramework: "Pest",
                    installBoost: false,
                    initializeGit: false
                )
            ) { stage in
                stages.append(stage)
            }
            XCTAssertEqual(
                stages,
                [
                    .creatingLaravelProject,
                    .preparingNodeRuntime,
                    .installingFrontendDependencies,
                    .buildingFrontendAssets,
                    .verifyingProject
                ]
            )
            XCTAssertTrue(FileManager.default.fileExists(
                atPath: source.appendingPathComponent("public/build/manifest.json").path
            ))
        } else {
            throw XCTSkip(
                "Set HERDME_LARAVEL_PROJECT for a read-only project or "
                    + "HERDME_CREATE_LARAVEL_INTEGRATION=1 to create a temporary Laravel project."
            )
        }

        guard FileManager.default.fileExists(atPath: source.appendingPathComponent("artisan").path),
              FileManager.default.fileExists(atPath: source.appendingPathComponent("vendor/autoload.php").path),
              FileManager.default.fileExists(atPath: source.appendingPathComponent("public/index.php").path) else {
            XCTFail("The Laravel integration source must contain artisan, vendor/autoload.php, and public/index.php.")
            return
        }

        let copied = try Self.runProcess(
            executable: "/bin/cp",
            arguments: ["-cR", source.path, app.path]
        )
        XCTAssertEqual(copied.status, 0, copied.output)

        let version = try Self.runProcess(
            executable: php.path,
            arguments: ["artisan", "--version"],
            currentDirectory: app
        )
        XCTAssertEqual(version.status, 0, version.output)
        XCTAssertTrue(version.output.contains("Laravel Framework 13."), version.output)
        try Data("herdme-static-ok".utf8).write(
            to: app.appendingPathComponent("public/herdme-static.txt")
        )

        let certificateManager = LocalCertificateManager(rootURL: engineRoot)
        let engine = LocalEnvironmentEngine(rootURL: engineRoot, certificateManager: certificateManager)
        defer { engine.stopAll() }
        let site = SiteProject(path: app, name: "real-laravel", framework: "Laravel", isLinked: false)
        _ = try engine.start(sites: [site], defaultPHP: php, tld: "test")

        let httpPort = try XCTUnwrap(engine.proxyPort)
        let http = try Self.runProcess(
            executable: "/usr/bin/curl",
            arguments: [
                "--silent", "--show-error", "--fail", "--max-time", "15",
                "--retry", "30", "--retry-connrefused", "--retry-delay", "0", "--noproxy", "*",
                "--resolve", "real-laravel.test:\(httpPort):127.0.0.1",
                "http://real-laravel.test:\(httpPort)/"
            ]
        )
        XCTAssertEqual(http.status, 0, http.output)
        XCTAssertFalse(http.output.isEmpty)

        let staticAsset = try Self.runProcess(
            executable: "/usr/bin/curl",
            arguments: [
                "--silent", "--show-error", "--fail", "--max-time", "8", "--noproxy", "*",
                "--resolve", "real-laravel.test:\(httpPort):127.0.0.1",
                "http://real-laravel.test:\(httpPort)/herdme-static.txt"
            ]
        )
        XCTAssertEqual(staticAsset.status, 0, staticAsset.output)
        XCTAssertEqual(staticAsset.output, "herdme-static-ok")

        let httpsPort = try XCTUnwrap(engine.httpsProxyPort)
        let certificateURL = engineRoot.appendingPathComponent("Certificates/herdme-ca.pem")
        let https = try Self.runProcess(
            executable: "/usr/bin/curl",
            arguments: [
                "--silent", "--show-error", "--fail", "--max-time", "15",
                "--retry", "30", "--retry-connrefused", "--retry-delay", "0", "--noproxy", "*",
                "--cacert", certificateURL.path,
                "--resolve", "real-laravel.test:\(httpsPort):127.0.0.1",
                "https://real-laravel.test:\(httpsPort)/"
            ]
        )
        XCTAssertEqual(https.status, 0, https.output)
        XCTAssertFalse(https.output.isEmpty)
    }

    func testDomainResolverRecognizesOnlyHerdMeConfiguration() {
        XCTAssertEqual(
            DomainResolverManager.state(contents: DomainResolverManager.contents(port: 53), port: 53),
            .managed
        )
        XCTAssertEqual(
            DomainResolverManager.state(contents: "nameserver 127.0.0.1", port: 53),
            .external
        )
    }

    func testDomainResolverReadinessRequiresCurrentRunningHelper() {
        XCTAssertTrue(AppModel.domainResolverIsReady(
            state: .managed,
            helperRunning: true,
            helperNeedsUpdate: false
        ))
        XCTAssertFalse(AppModel.domainResolverIsReady(
            state: .managed,
            helperRunning: false,
            helperNeedsUpdate: false
        ))
        XCTAssertFalse(AppModel.domainResolverIsReady(
            state: .managed,
            helperRunning: true,
            helperNeedsUpdate: true
        ))
        XCTAssertFalse(AppModel.domainResolverIsReady(
            state: .external,
            helperRunning: true,
            helperNeedsUpdate: false
        ))
    }

    func testDomainResolverInstallScriptQuotesPathsWithoutSudo() {
        let script = DomainResolverManager.installScript(
            resolverSourcePath: "/tmp/O'Brien resolver",
            resolverDestinationPath: "/etc/resolver/local-test",
            helperSourcePath: "/Applications/HerdMe App/HerdMe Helper",
            helperDestinationPath: DomainResolverManager.helperDestination,
            daemonSourcePath: "/tmp/HerdMe daemon.plist",
            daemonDestinationPath: DomainResolverManager.launchDaemonDestination
        )
        XCTAssertTrue(script.contains("'/tmp/O'\\''Brien resolver' '/etc/resolver/local-test'"))
        XCTAssertTrue(script.contains("'/Applications/HerdMe App/HerdMe Helper'"))
        XCTAssertTrue(script.contains("launchctl bootstrap system"))
        XCTAssertTrue(script.contains("launchctl kickstart -k system/app.herdme.network-helper"))
        XCTAssertFalse(script.contains("sudo"))
    }

    func testNetworkHelperConfigurationAndLaunchDaemonAreBounded() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let manager = DomainResolverManager(rootURL: root)
        try manager.updateNetworkRouting(httpPort: 8_081, httpsPort: 8_444, tld: "local-test")
        let configuration = try String(
            contentsOf: root.appendingPathComponent("Runtime/network-helper.conf"),
            encoding: .utf8
        )
        XCTAssertEqual(configuration, "http=8081\nhttps=8444\ntld=local-test\n")
        XCTAssertThrowsError(
            try manager.updateNetworkRouting(httpPort: 80, httpsPort: 443, tld: "local-test")
        )

        let plist = DomainResolverManager.launchDaemonPlist(
            configurationPath: "/Users/Demo & QA/HerdMe.conf",
            uid: 501,
            gid: 20
        )
        let decoded = try PropertyListSerialization.propertyList(
            from: Data(plist.utf8),
            format: nil
        ) as? [String: Any]
        let arguments = try XCTUnwrap(decoded?["ProgramArguments"] as? [String])
        XCTAssertEqual(decoded?["AssociatedBundleIdentifiers"] as? [String], ["app.herdme.desktop"])
        XCTAssertEqual(arguments.first, DomainResolverManager.helperDestination)
        XCTAssertTrue(arguments.contains("/Users/Demo & QA/HerdMe.conf"))
        XCTAssertTrue(arguments.contains("501"))
        XCTAssertTrue(arguments.contains("20"))
    }

    func testNetworkHelperUpdateDetectionComparesBinaryAndDaemonIdentity() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let manager = DomainResolverManager(rootURL: root)
        let bundledHelper = root.appendingPathComponent("bundled-helper")
        let installedHelper = root.appendingPathComponent("installed-helper")
        let installedDaemon = root.appendingPathComponent("installed-daemon.plist")
        let helper = Data([0x48, 0x65, 0x72, 0x64, 0x4D, 0x65])
        try helper.write(to: bundledHelper)
        try helper.write(to: installedHelper)
        try DomainResolverManager.launchDaemonPlist(
            configurationPath: root.appendingPathComponent("Runtime/network-helper.conf").path,
            uid: getuid(),
            gid: getgid()
        ).write(to: installedDaemon, atomically: true, encoding: .utf8)

        XCTAssertTrue(manager.isNetworkHelperCurrent(
            bundledHelperURL: bundledHelper,
            installedHelperURL: installedHelper,
            installedDaemonURL: installedDaemon
        ))

        try Data([0x6F, 0x6C, 0x64]).write(to: installedHelper)
        XCTAssertFalse(manager.isNetworkHelperCurrent(
            bundledHelperURL: bundledHelper,
            installedHelperURL: installedHelper,
            installedDaemonURL: installedDaemon
        ))
    }

    func testDNSResponseMapsTestDomainToLoopback() throws {
        let query = Data([
            0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x04, 0x64, 0x65, 0x6D, 0x6F,
            0x04, 0x74, 0x65, 0x73, 0x74,
            0x00, 0x00, 0x01, 0x00, 0x01
        ])

        let response = try XCTUnwrap(DNSMessage.response(to: query, tld: "test"))

        XCTAssertEqual(response.prefix(2), query.prefix(2))
        XCTAssertEqual(response[7], 1)
        XCTAssertEqual(response.suffix(4), Data([127, 0, 0, 1]))
    }

    private static func sendHTTPRequest(port: Int, request: String) throws -> String {
        var lastError = POSIXError(.ECONNREFUSED)
        for _ in 0..<100 {
            let descriptor = socket(AF_INET, SOCK_STREAM, 0)
            guard descriptor >= 0 else { throw POSIXError(.EIO) }
            var address = sockaddr_in()
            address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
            address.sin_family = sa_family_t(AF_INET)
            address.sin_port = in_port_t(UInt16(port).bigEndian)
            address.sin_addr = in_addr(s_addr: inet_addr("127.0.0.1"))
            let connected = withUnsafePointer(to: &address) { pointer in
                pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    Darwin.connect(descriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
            if connected == 0 {
                defer { Darwin.close(descriptor) }
                try request.withCString { pointer in
                    var remaining = request.utf8.count
                    var offset = 0
                    while remaining > 0 {
                        let written = Darwin.write(descriptor, pointer + offset, remaining)
                        guard written > 0 else { throw POSIXError(.EIO) }
                        offset += written
                        remaining -= written
                    }
                }
                var response = Data()
                var buffer = [UInt8](repeating: 0, count: 4_096)
                while true {
                    let count = Darwin.read(descriptor, &buffer, buffer.count)
                    if count <= 0 { break }
                    response.append(contentsOf: buffer.prefix(count))
                }
                return String(decoding: response, as: UTF8.self)
            }
            lastError = POSIXError(.ECONNREFUSED)
            Darwin.close(descriptor)
            usleep(10_000)
        }
        throw lastError
    }

    private static func runProcess(
        executable: String,
        arguments: [String],
        currentDirectory: URL? = nil
    ) throws -> (status: Int32, output: String) {
        let process = Process()
        let output = Pipe()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        process.currentDirectoryURL = currentDirectory
        process.standardOutput = output
        process.standardError = output
        try process.run()
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        return (process.terminationStatus, String(decoding: data, as: UTF8.self))
    }
}

private final class TestHTTPBackend: @unchecked Sendable {
    let port: Int
    private let descriptor: Int32

    init(responseBody: String) throws {
        let listenerDescriptor = socket(AF_INET, SOCK_STREAM, 0)
        guard listenerDescriptor >= 0 else { throw POSIXError(.EIO) }
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = 0
        address.sin_addr = in_addr(s_addr: inet_addr("127.0.0.1"))
        let bound = withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.bind(listenerDescriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard bound == 0, listen(listenerDescriptor, 1) == 0 else {
            Darwin.close(listenerDescriptor)
            throw POSIXError(.EADDRINUSE)
        }
        var socketAddress = sockaddr_in()
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let resolved = withUnsafeMutablePointer(to: &socketAddress) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                getsockname(listenerDescriptor, $0, &length)
            }
        }
        guard resolved == 0 else {
            Darwin.close(listenerDescriptor)
            throw POSIXError(.EIO)
        }
        descriptor = listenerDescriptor
        port = Int(UInt16(bigEndian: socketAddress.sin_port))

        let response = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: \(responseBody.utf8.count)\r\nConnection: close\r\n\r\n\(responseBody)"
        let listener = listenerDescriptor
        DispatchQueue.global(qos: .userInitiated).async {
            let client = accept(listener, nil, nil)
            guard client >= 0 else { return }
            defer { Darwin.close(client) }
            var buffer = [UInt8](repeating: 0, count: 4_096)
            _ = Darwin.read(client, &buffer, buffer.count)
            response.withCString { pointer in
                _ = Darwin.write(client, pointer, response.utf8.count)
            }
        }
    }

    func stop() {
        Darwin.close(descriptor)
    }
}
