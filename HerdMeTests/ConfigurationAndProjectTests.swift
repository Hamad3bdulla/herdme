import Combine
import CryptoKit
import Darwin
import Security
import XCTest

@testable import HerdMe

extension ConfigurationAndSiteScannerTests {
    func testRuntimeCatalogLoadsSharedPlatformPolicies() throws {
        XCTAssertNil(RuntimeCatalog.loadIssue)
        XCTAssertEqual(RuntimeCatalog.defaultPHPCycle, "8.4")
        XCTAssertEqual(RuntimeCatalog.defaultNodeMajor, "22")
        XCTAssertEqual(
            RuntimeCatalog.installablePHPCycles,
            ["8.5", "8.4", "8.3", "8.2", "8.1", "8.0"]
        )
        XCTAssertEqual(RuntimeCatalog.macOSNodeMajors, ["26", "24", "22", "20", "18"])
        XCTAssertEqual(RuntimeCatalog.windowsNodeMajors, ["26", "24", "22", "20"])
        XCTAssertEqual(
            Set(RuntimeCatalog.services.map(\.id)),
            Set([
                "mariadb", "mysql", "postgresql", "mongodb", "redis",
                "valkey", "meilisearch", "typesense", "minio", "rustfs"
            ])
        )

        let rustFS = try XCTUnwrap(RuntimeCatalog.services.first { $0.id == "rustfs" })
        XCTAssertEqual(rustFS.macOS.architectures, ["arm64"])
        let valkey = try XCTUnwrap(RuntimeCatalog.services.first { $0.id == "valkey" })
        XCTAssertFalse(valkey.windows.installable)
        XCTAssertFalse(valkey.windows.unavailableReason?.isEmpty ?? true)
        let typesense = try XCTUnwrap(RuntimeCatalog.services.first { $0.id == "typesense" })
        XCTAssertFalse(typesense.windows.installable)
        XCTAssertFalse(typesense.windows.unavailableReason?.isEmpty ?? true)
    }

    func testRuntimeCatalogRejectsDuplicateRuntimeCycles() {
        let invalid = Data(
            #"""
            {
              "schemaVersion": 1,
              "defaults": { "phpCycle": "8.4", "nodeMajor": "22" },
              "php": { "installableCycles": ["8.4", "8.4"] },
              "node": { "macOSMajors": ["22"], "windowsMajors": ["22"] },
              "services": []
            }
            """#.utf8)

        XCTAssertThrowsError(try RuntimeCatalog.decodeAndValidate(invalid)) { error in
            XCTAssertTrue(error.localizedDescription.contains("PHP cycles must be unique"))
        }
    }

    func testManagedDownloadsRetryOnlyTransientFailures() async throws {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [ManagedDownloadURLProtocol.self]
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        let session = URLSession(configuration: configuration)
        defer { session.invalidateAndCancel() }
        let url = try XCTUnwrap(URL(string: "https://downloads.example.test/runtime"))
        let state = ManagedDownloadURLProtocolState.shared

        state.reset([
            .response(status: 503, body: "unavailable"),
            .response(status: 429, body: "limited"),
            .response(status: 200, body: "recovered")
        ])
        let (recoveredData, recoveredResponse) = try await ManagedDownloadClient.data(
            from: url,
            session: session,
            baseRetryDelayNanoseconds: 0
        )
        XCTAssertEqual((recoveredResponse as? HTTPURLResponse)?.statusCode, 200)
        XCTAssertEqual(String(decoding: recoveredData, as: UTF8.self), "recovered")
        XCTAssertEqual(state.count(), 3)

        state.reset([
            .failure(.networkConnectionLost),
            .response(status: 200, body: "connected")
        ])
        let (connectedData, _) = try await ManagedDownloadClient.data(
            from: url,
            session: session,
            baseRetryDelayNanoseconds: 0
        )
        XCTAssertEqual(String(decoding: connectedData, as: UTF8.self), "connected")
        XCTAssertEqual(state.count(), 2)

        state.reset([
            .response(status: 404, body: "missing"),
            .response(status: 200, body: "must not be requested")
        ])
        let (_, permanentResponse) = try await ManagedDownloadClient.data(
            from: url,
            session: session,
            baseRetryDelayNanoseconds: 0
        )
        XCTAssertEqual((permanentResponse as? HTTPURLResponse)?.statusCode, 404)
        XCTAssertEqual(state.count(), 1)

        state.reset([
            .failure(.cancelled),
            .response(status: 200, body: "must not be requested")
        ])
        do {
            _ = try await ManagedDownloadClient.data(
                from: url,
                session: session,
                baseRetryDelayNanoseconds: 0
            )
            XCTFail("A cancelled download must not succeed.")
        } catch let error as URLError {
            XCTAssertEqual(error.code, .cancelled)
        }
        XCTAssertEqual(state.count(), 1)

        state.reset([
            .response(status: 503, body: "unavailable"),
            .response(status: 200, body: "archive")
        ])
        let (downloadURL, downloadResponse) = try await ManagedDownloadClient.download(
            from: url,
            session: session,
            baseRetryDelayNanoseconds: 0
        )
        defer { try? FileManager.default.removeItem(at: downloadURL) }
        XCTAssertEqual((downloadResponse as? HTTPURLResponse)?.statusCode, 200)
        XCTAssertEqual(try String(contentsOf: downloadURL, encoding: .utf8), "archive")
        XCTAssertEqual(state.count(), 2)
    }

    func testOnboardingIsRequiredOnlyForNewInstallations() throws {
        XCTAssertFalse(AppConfiguration.default.onboardingCompleted)

        let legacy = try JSONDecoder().decode(
            AppConfiguration.self,
            from: Data("{}".utf8)
        )
        XCTAssertTrue(legacy.onboardingCompleted)

        let explicitIncomplete = try JSONDecoder().decode(
            AppConfiguration.self,
            from: Data(#"{"onboardingCompleted":false}"#.utf8)
        )
        XCTAssertFalse(explicitIncomplete.onboardingCompleted)

        var completed = AppConfiguration.default
        completed.onboardingCompleted = true
        let restored = try JSONDecoder().decode(
            AppConfiguration.self,
            from: JSONEncoder().encode(completed)
        )
        XCTAssertTrue(restored.onboardingCompleted)
    }

    func testOnboardingStagesKeepDependencyOrder() {
        XCTAssertEqual(
            OnboardingStage.installationStages,
            [.localDomains, .certificate, .php, .composer, .node, .finishing]
        )
    }

    func testUIExecutionContextUsesOnlyTheRequestedIsolatedSupportRoot() {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
            .standardizedFileURL
        let environment = [
            "HERDME_UI_TESTING": "1",
            "HERDME_UI_TEST_SUPPORT_ROOT": root.path
        ]

        XCTAssertFalse(AppExecutionContext.isTesting(environment: [:]))
        XCTAssertTrue(
            AppExecutionContext.isTesting(environment: [
                "XCTestConfigurationFilePath": "/tmp/HerdMeTests.xctestconfiguration"
            ]))

        #if DEBUG
            XCTAssertTrue(AppExecutionContext.isTesting(environment: environment))
            let store = AppExecutionContext.configurationStore(environment: environment)
            XCTAssertEqual(store.rootURL, root)
            XCTAssertEqual(
                store.projectsURL,
                root.appendingPathComponent("Projects", isDirectory: true)
            )
        #else
            XCTAssertFalse(AppExecutionContext.isTesting(environment: environment))
            XCTAssertNotEqual(
                AppExecutionContext.configurationStore(environment: environment).rootURL,
                root
            )
        #endif
    }

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

    @MainActor
    func testAppModelInitializationDoesNotLaunchManagedPHP() throws {
        let fixture = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let root = fixture.appendingPathComponent("support", isDirectory: true)
        let projects = fixture.appendingPathComponent("projects", isDirectory: true)
        let php = root.appendingPathComponent("Runtimes/php/8.4/bin/php")
        let invocationMarker = fixture.appendingPathComponent("php-was-launched")
        defer { try? FileManager.default.removeItem(at: fixture) }

        try FileManager.default.createDirectory(
            at: php.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try Data(
            "#!/bin/sh\ntouch \"\(invocationMarker.path)\"\nprintf '8.4.0'\n".utf8
        ).write(to: php)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: php.path)

        let store = ConfigurationStore(rootURL: root, projectsURL: projects)
        _ = AppModel(configurationStore: store)

        XCTAssertFalse(FileManager.default.fileExists(atPath: invocationMarker.path))
    }

    @MainActor
    func testApplicationTaskRegistryCancelsRunningTasksAndRejectsNewWork() async {
        let registry = ApplicationTaskRegistry()
        let probe = TestTaskRegistryProbe()

        XCTAssertTrue(
            registry.start(name: "cancellable-test-operation") {
                probe.started = true
                do {
                    try await Task.sleep(for: .seconds(30))
                } catch {
                    probe.observedCancellation = Task.isCancelled
                }
            })
        for _ in 0..<100 {
            if probe.started { break }
            await Task.yield()
        }
        XCTAssertTrue(probe.started)
        XCTAssertEqual(registry.activeCount, 1)
        XCTAssertEqual(registry.activeTaskNames, ["cancellable-test-operation"])

        let drained = await registry.cancelAllAndWait(timeout: .seconds(1))
        XCTAssertTrue(drained)
        XCTAssertTrue(probe.observedCancellation)
        XCTAssertEqual(registry.activeCount, 0)
        XCTAssertFalse(registry.acceptsNewTasks)
        XCTAssertFalse(registry.start { probe.rejectedOperationRan = true })
        await Task.yield()
        XCTAssertFalse(probe.rejectedOperationRan)
    }

    @MainActor
    func testApplicationTaskRegistryShutdownWaitIsBounded() async {
        let registry = ApplicationTaskRegistry()
        let probe = TestTaskRegistryProbe()

        XCTAssertTrue(
            registry.start(name: "bounded-test-operation") {
                probe.started = true
                let clock = ContinuousClock()
                let deadline = clock.now.advanced(by: .milliseconds(300))
                while clock.now < deadline { await Task.yield() }
            })
        for _ in 0..<100 {
            if probe.started { break }
            await Task.yield()
        }
        XCTAssertTrue(probe.started)

        let pending = await registry.cancelAllAndWaitReporting(timeout: .milliseconds(20))
        XCTAssertEqual(pending, ["bounded-test-operation"])
        XCTAssertGreaterThan(registry.activeCount, 0)

        for _ in 0..<100 where registry.activeCount > 0 {
            try? await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertEqual(registry.activeCount, 0)
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

        XCTAssertThrowsError(
            try ProjectCreator.validate(
                NewProjectRequest(
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
        let composer = root.appendingPathComponent("Composer/composer.phar")
        let laravel = root.appendingPathComponent("Composer/vendor/bin/laravel")
        let invocationMarker = root.appendingPathComponent("php-was-launched")
        defer { try? FileManager.default.removeItem(at: root) }

        for directory in [
            php.deletingLastPathComponent(),
            composer.deletingLastPathComponent(),
            laravel.deletingLastPathComponent()
        ] {
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
        XCTAssertTrue(FileManager.default.isExecutableFile(atPath: root.appendingPathComponent("bin/composer").path))
        XCTAssertTrue(FileManager.default.isExecutableFile(atPath: root.appendingPathComponent("bin/laravel").path))
    }

    func testManagedToolLaunchersMigrateLegacyComposerAndIgnoreOldHerdPATH() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(
            "HerdMe Support 'With Spaces' \(UUID().uuidString)",
            isDirectory: true
        )
        let php = root.appendingPathComponent("bin/php")
        let legacyComposer = root.appendingPathComponent("bin/composer")
        let composerPHAR = root.appendingPathComponent("Composer/composer.phar")
        let laravel = root.appendingPathComponent("Composer/vendor/bin/laravel")
        let oldHerdBin = root.appendingPathComponent("Old Herd/bin", isDirectory: true)
        let oldPHP = oldHerdBin.appendingPathComponent("php")
        let oldPHPMarker = root.appendingPathComponent("old-herd-php-ran")
        defer { try? FileManager.default.removeItem(at: root) }

        for directory in [
            php.deletingLastPathComponent(),
            laravel.deletingLastPathComponent(),
            oldHerdBin
        ] {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        }
        try Data(
            """
            #!/bin/sh
            printf 'php=%s\n' "$0"
            printf 'home=%s\n' "$COMPOSER_HOME"
            printf 'cache=%s\n' "$COMPOSER_CACHE_DIR"
            printf 'path=%s\n' "$PATH"
            printf 'phprc=%s\n' "${PHPRC-unset}"
            printf 'scan=%s\n' "${PHP_INI_SCAN_DIR-unset}"
            for argument in "$@"; do printf 'arg=%s\n' "$argument"; done
            """.utf8
        ).write(to: php)
        try Data(
            """
            #!/bin/sh
            touch "$HERDME_OLD_PHP_MARKER"
            exit 91
            """.utf8
        ).write(to: oldPHP)
        let legacyPHAR = Data("#!/usr/bin/env php\n<?php // legacy composer fixture\n".utf8)
        try legacyPHAR.write(to: legacyComposer)
        try Data("#!/usr/bin/env php\n<?php // laravel fixture\n".utf8).write(to: laravel)
        for executable in [php, oldPHP] {
            try FileManager.default.setAttributes(
                [.posixPermissions: 0o755],
                ofItemAtPath: executable.path
            )
        }

        let installer = RuntimeInstaller(rootURL: root)
        try await installer.repairManagedToolLaunchers()

        var environment = ProcessInfo.processInfo.environment
        environment["PATH"] = oldHerdBin.path + ":/usr/bin:/bin:/usr/sbin:/sbin"
        environment["PHPRC"] = "/Applications/Herd.app/legacy-php.ini"
        environment["PHP_INI_SCAN_DIR"] = "/Applications/Herd.app/legacy-conf.d"
        environment["HERDME_OLD_PHP_MARKER"] = oldPHPMarker.path
        let composer = try ProcessRunner.run(
            legacyComposer,
            arguments: ["install", "argument with spaces"],
            environment: environment
        )
        let laravelResult = try ProcessRunner.run(
            root.appendingPathComponent("bin/laravel"),
            arguments: ["new", "demo site"],
            environment: environment
        )
        let canonicalRootPath = root.path.withCString { path -> String in
            guard let resolved = Darwin.realpath(path, nil) else { return root.path }
            defer { free(resolved) }
            return String(cString: resolved)
        }
        let canonicalRoot = URL(fileURLWithPath: canonicalRootPath, isDirectory: true)
        let canonicalPHP = canonicalRoot.appendingPathComponent("bin/php")
        let canonicalComposerPHAR = canonicalRoot.appendingPathComponent("Composer/composer.phar")
        let canonicalLaravel = canonicalRoot.appendingPathComponent("Composer/vendor/bin/laravel")

        XCTAssertEqual(composer.status, 0)
        XCTAssertEqual(laravelResult.status, 0)
        XCTAssertEqual(try Data(contentsOf: composerPHAR), legacyPHAR)
        XCTAssertFalse(FileManager.default.fileExists(atPath: oldPHPMarker.path))
        XCTAssertTrue(composer.output.contains("php=\(canonicalPHP.path)"), composer.output)
        XCTAssertTrue(
            composer.output.contains("home=\(canonicalRoot.appendingPathComponent("Composer").path)"),
            composer.output
        )
        XCTAssertTrue(
            composer.output.contains("cache=\(canonicalRoot.appendingPathComponent("Cache/composer").path)"),
            composer.output
        )
        XCTAssertTrue(
            composer.output.contains("path=\(canonicalRoot.appendingPathComponent("bin").path):"),
            composer.output
        )
        XCTAssertTrue(composer.output.contains("phprc=unset"), composer.output)
        XCTAssertTrue(composer.output.contains("scan=unset"), composer.output)
        XCTAssertTrue(composer.output.contains("arg=\(canonicalComposerPHAR.path)"), composer.output)
        XCTAssertTrue(composer.output.contains("arg=argument with spaces"), composer.output)
        XCTAssertTrue(laravelResult.output.contains("arg=\(canonicalLaravel.path)"), laravelResult.output)
        XCTAssertTrue(laravelResult.output.contains("arg=demo site"), laravelResult.output)
        XCTAssertTrue(FileManager.default.isExecutableFile(atPath: legacyComposer.path))
        XCTAssertTrue(FileManager.default.isExecutableFile(atPath: root.appendingPathComponent("bin/laravel").path))
        let launcher = try String(contentsOf: legacyComposer, encoding: .utf8)
        XCTAssertTrue(launcher.hasPrefix("#!/bin/sh"))
        XCTAssertFalse(launcher.contains("#!/usr/bin/env php"))
    }

    func testComposerSelfUpdatePreservesManagedLauncher() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let php = root.appendingPathComponent("Runtimes/php/8.4/bin/php")
        let composerPHAR = root.appendingPathComponent("Composer/composer.phar")
        let composerLauncher = root.appendingPathComponent("bin/composer")
        defer { try? FileManager.default.removeItem(at: root) }
        for directory in [php.deletingLastPathComponent(), composerPHAR.deletingLastPathComponent()] {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        }
        try Data(
            """
            #!/bin/sh
            tool=$1
            shift
            if [ "${1:-}" = "--version" ]; then
                echo "Composer version 2.10.3 2026-07-01 11:24:45"
                exit 0
            fi
            if [ "${1:-}" = "self-update" ]; then
                printf '#!/usr/bin/env php\n<?php // updated composer fixture\n' > "$tool"
                exit 0
            fi
            exit 1
            """.utf8
        ).write(to: php)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: php.path)
        try Data("#!/usr/bin/env php\n<?php // original composer fixture\n".utf8).write(to: composerPHAR)

        let installer = RuntimeInstaller(rootURL: root)
        try await installer.repairManagedToolLaunchers()
        let launcherBefore = try Data(contentsOf: composerLauncher)

        let version = try await installer.updateComposer(cycle: "8.4")

        XCTAssertEqual(version, "2.10.3")
        XCTAssertEqual(try Data(contentsOf: composerLauncher), launcherBefore)
        XCTAssertTrue(try String(contentsOf: composerPHAR, encoding: .utf8).contains("updated composer fixture"))
        XCTAssertTrue(FileManager.default.isExecutableFile(atPath: composerLauncher.path))
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
        XCTAssertTrue(
            FileManager.default.fileExists(
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

    @MainActor
    func testProjectCreatorCancellationRemovesOwnedStagingDirectory() async throws {
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
            mkdir -p "$PWD/$3/vendor"
            touch "$PWD/$3/artisan"
            sleep 5
            touch "$PWD/$3/vendor/autoload.php"
            """.utf8
        ).write(to: php)
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o755],
            ofItemAtPath: php.path
        )
        defer { try? FileManager.default.removeItem(at: fixture) }

        let operation = Task {
            try await ProjectCreator(rootURL: root).create(
                NewProjectRequest(
                    name: "cancelled-project",
                    parentDirectory: parent,
                    starterKit: .none,
                    testingFramework: "Pest",
                    installBoost: false,
                    initializeGit: false
                ))
        }
        try await Task.sleep(for: .milliseconds(250))
        operation.cancel()

        do {
            _ = try await operation.value
            XCTFail("A cancelled project creation must not succeed.")
        } catch let error as ProjectCreationError {
            guard case .cancelled = error else {
                return XCTFail("Expected cancellation, received \(error).")
            }
        }
        XCTAssertFalse(
            FileManager.default.fileExists(
                atPath: parent.appendingPathComponent("cancelled-project").path
            ))
        let leftovers = try FileManager.default.contentsOfDirectory(atPath: parent.path)
            .filter { $0.hasPrefix(".herdme-create-") }
        XCTAssertTrue(leftovers.isEmpty)
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

    func testSiteRemovalAcceptsOnlyDirectParkedProjects() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let project = root.appendingPathComponent("parked-project")
        try FileManager.default.createDirectory(at: project, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }

        let site = try XCTUnwrap(SiteScanner().scan(paths: [root.path]).first)

        XCTAssertFalse(site.isLinked)
        XCTAssertEqual(
            try SiteRemovalManager.removableURL(for: site, parkPaths: [root.path]),
            project.standardizedFileURL
        )
    }

    func testSiteRemovalRejectsLinkedAndOutsideProjects() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let parked = root.appendingPathComponent("parked")
        let source = root.appendingPathComponent("source-project")
        let registration = parked.appendingPathComponent("linked-project")
        try FileManager.default.createDirectory(at: parked, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: source, withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: registration, withDestinationURL: source)
        defer { try? FileManager.default.removeItem(at: root) }

        let linked = try XCTUnwrap(SiteScanner().scan(paths: [parked.path]).first)
        XCTAssertThrowsError(
            try SiteRemovalManager.removableURL(for: linked, parkPaths: [parked.path])
        ) { error in
            guard case SiteRemovalError.linkedProject = error else {
                return XCTFail("Expected linkedProject, received \(error).")
            }
        }

        let outside = SiteProject(
            path: source,
            name: source.lastPathComponent,
            framework: "Site",
            isLinked: false,
            registrationPath: source
        )
        XCTAssertThrowsError(
            try SiteRemovalManager.removableURL(for: outside, parkPaths: [parked.path])
        ) { error in
            guard case SiteRemovalError.outsideParkedFolder = error else {
                return XCTFail("Expected outsideParkedFolder, received \(error).")
            }
        }
    }

    func testSiteRemovalRejectsSymlinkEvenWhenMetadataClaimsItIsParked() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let parked = root.appendingPathComponent("parked")
        let source = root.appendingPathComponent("source-project")
        let registration = parked.appendingPathComponent("linked-project")
        try FileManager.default.createDirectory(at: parked, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: source, withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: registration, withDestinationURL: source)
        defer { try? FileManager.default.removeItem(at: root) }

        let spoofed = SiteProject(
            path: source,
            name: registration.lastPathComponent,
            framework: "Site",
            isLinked: false,
            registrationPath: registration
        )
        XCTAssertThrowsError(
            try SiteRemovalManager.removableURL(for: spoofed, parkPaths: [parked.path])
        ) { error in
            guard case SiteRemovalError.unavailable = error else {
                return XCTFail("Expected unavailable, received \(error).")
            }
        }
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

        XCTAssertTrue(
            IndependentPathPolicy.belongsToOtherHerd(
                URL(fileURLWithPath: "/Users/test/Herd"),
                homeDirectory: home
            ))
        XCTAssertTrue(
            IndependentPathPolicy.belongsToOtherHerd(
                URL(fileURLWithPath: "/Users/test/Herd/project"),
                homeDirectory: home
            ))
        XCTAssertTrue(
            IndependentPathPolicy.belongsToOtherHerd(
                URL(fileURLWithPath: "/users/TEST/herd/project"),
                homeDirectory: home
            ))
        XCTAssertTrue(
            IndependentPathPolicy.belongsToOtherHerd(
                URL(fileURLWithPath: "/Users/test/Library/Application Support/Herd/bin/php"),
                homeDirectory: home
            ))
        XCTAssertFalse(
            IndependentPathPolicy.belongsToOtherHerd(
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
        XCTAssertEqual(configuration.theme, .dark)
        XCTAssertFalse(configuration.startAutomatically)
        XCTAssertTrue(configuration.automaticUpdates)
        XCTAssertEqual(configuration.smtpPort, 2525)
        XCTAssertEqual(configuration.configSchemaVersion, 0)
        XCTAssertEqual(configuration.independenceMigrationVersion, 0)
    }

    func testConfigurationPersistsLanguageIndependentThemeValues() throws {
        var configuration = AppConfiguration.default
        configuration.theme = .dark

        let data = try JSONEncoder().encode(configuration)
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])

        XCTAssertEqual(object["theme"] as? String, "dark")
        XCTAssertEqual(try JSONDecoder().decode(AppTheme.self, from: Data("\"Light\"".utf8)), .light)
        XCTAssertEqual(try JSONDecoder().decode(AppTheme.self, from: Data("\"unknown\"".utf8)), .automatic)
    }

    @MainActor
    func testArabicLocaleUsesRightToLeftLayoutDirection() {
        XCTAssertEqual(AppLocalization.layoutDirection(for: Locale(identifier: "ar_BH")), .rightToLeft)
        XCTAssertEqual(AppLocalization.layoutDirection(for: Locale(identifier: "en_US")), .leftToRight)
    }

    func testConfigurationStoreMigratesAndPersistsLegacySchema() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let projects = root.appendingPathComponent("projects")
        let store = ConfigurationStore(rootURL: root, projectsURL: projects)
        let legacyData = Data(
            """
            {
              "parkPaths": ["/tmp/projects"],
              "tld": "local",
              "selectedPHP": "8.3",
              "onboardingCompleted": true
            }
            """.utf8
        )
        try legacyData.write(to: store.configURL)
        defer { try? FileManager.default.removeItem(at: root) }

        let configuration = store.load()

        XCTAssertEqual(configuration.configSchemaVersion, ConfigurationStore.currentConfigSchemaVersion)
        XCTAssertEqual(configuration.tld, "local")
        XCTAssertEqual(configuration.selectedPHP, "8.3")
        let persisted = try XCTUnwrap(
            JSONSerialization.jsonObject(with: Data(contentsOf: store.configURL))
                as? [String: Any]
        )
        XCTAssertEqual(
            persisted["configSchemaVersion"] as? Int,
            ConfigurationStore.currentConfigSchemaVersion
        )
    }

    @MainActor
    func testFutureConfigurationIsPreservedAndNotReplacedDuringStartup() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let projects = root.appendingPathComponent("projects")
        let store = ConfigurationStore(rootURL: root, projectsURL: projects)
        let futureVersion = ConfigurationStore.currentConfigSchemaVersion + 1
        let futureData = Data(
            """
            {
              "configSchemaVersion": \(futureVersion),
              "parkPaths": ["/future/projects"],
              "tld": "future",
              "futureOnlySetting": "must survive"
            }
            """.utf8
        )
        try futureData.write(to: store.configURL)
        defer { try? FileManager.default.removeItem(at: root) }

        let model = AppModel(configurationStore: store)
        let issue = try XCTUnwrap(store.loadIssue)
        let backupURL = try XCTUnwrap(issue.backupURL)

        XCTAssertEqual(model.configuration.configSchemaVersion, ConfigurationStore.currentConfigSchemaVersion)
        XCTAssertEqual(model.lastError, issue.message)
        XCTAssertTrue(issue.message.contains("newer release"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.configURL.path))
        XCTAssertEqual(try Data(contentsOf: backupURL), futureData)
    }

    func testConfigurationStoreRefusesToSaveFutureSchema() {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let store = ConfigurationStore(
            rootURL: root,
            projectsURL: root.appendingPathComponent("projects")
        )
        defer { try? FileManager.default.removeItem(at: root) }
        var configuration = AppConfiguration.default
        configuration.configSchemaVersion = ConfigurationStore.currentConfigSchemaVersion + 1

        XCTAssertThrowsError(try store.save(configuration)) { error in
            XCTAssertEqual(
                error as? ConfigurationStoreError,
                .unsupportedSchemaVersion(
                    found: ConfigurationStore.currentConfigSchemaVersion + 1,
                    supported: ConfigurationStore.currentConfigSchemaVersion
                )
            )
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.configURL.path))
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
        XCTAssertTrue(
            ConfigurationStore.migratingIndependentPaths(
                in: migrated,
                homeDirectory: home
            ).parkPaths.isEmpty)
    }

    func testSiteDetailsInspectorReportsProjectHealthWithoutCredentials() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let routes = root.appendingPathComponent("routes", isDirectory: true)
        let logs = root.appendingPathComponent("storage/logs", isDirectory: true)
        try FileManager.default.createDirectory(at: routes, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: logs, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }

        try Data("<?php\n".utf8).write(to: routes.appendingPathComponent("web.php"))
        try Data("<?php\n".utf8).write(to: routes.appendingPathComponent("api.php"))
        try Data("first\n".utf8).write(to: logs.appendingPathComponent("laravel-1.log"))
        try Data("second\n".utf8).write(to: logs.appendingPathComponent("laravel-2.log"))
        try Data(
            "DB_CONNECTION=mysql\nDB_HOST=127.0.0.1\nDB_PORT=3307\nDB_PASSWORD=never-display\n".utf8
        ).write(to: root.appendingPathComponent(".env"))
        let git = try ProcessRunner.run(
            URL(fileURLWithPath: "/usr/bin/git"),
            arguments: ["init", root.path],
            timeout: 10
        )
        XCTAssertEqual(git.status, 0)

        let service = ServiceInstance(
            id: UUID(),
            definitionID: "mysql",
            name: "MySQL Project",
            version: "9.7.1",
            port: 3_307,
            isRunning: true
        )
        let site = SiteProject(
            path: root,
            name: "demo",
            framework: "Laravel",
            isLinked: false
        )

        let details = SiteDetailsInspector.inspect(site: site, services: [service])

        XCTAssertTrue(details.environmentExists)
        XCTAssertFalse(details.environmentUnreadable)
        XCTAssertEqual(details.logFileCount, 2)
        XCTAssertNotNil(details.latestLogName)
        XCTAssertEqual(details.routeFileNames, ["api.php", "web.php"])
        XCTAssertTrue(details.isGitRepository)
        XCTAssertGreaterThan(details.gitChangeCount, 0)
        XCTAssertEqual(details.associatedServices, ["MySQL Project (3307)"])
        XCTAssertFalse(details.associatedServices.joined().contains("never-display"))
    }

    func testSiteGitStatusParserHandlesBranchesFreshRepositoriesAndDetachedHead() {
        let dirty = SiteDetailsInspector.parseGitStatus(
            "## feature/login...origin/feature/login\n M app.swift\n?? notes.txt\n"
        )
        XCTAssertTrue(dirty.isRepository)
        XCTAssertEqual(dirty.branch, "feature/login")
        XCTAssertEqual(dirty.changeCount, 2)

        let fresh = SiteDetailsInspector.parseGitStatus("## No commits yet on main\n")
        XCTAssertEqual(fresh.branch, "main")
        XCTAssertEqual(fresh.changeCount, 0)

        let initial = SiteDetailsInspector.parseGitStatus("## Initial commit on trunk\n")
        XCTAssertEqual(initial.branch, "trunk")

        let detached = SiteDetailsInspector.parseGitStatus("## HEAD (no branch)\n")
        XCTAssertNil(detached.branch)
        XCTAssertEqual(detached.changeCount, 0)
    }

    func testSiteGitInspectionLoadsStatusForTheSiteList() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let cleanRoot = root.appendingPathComponent("clean", isDirectory: true)
        let dirtyRoot = root.appendingPathComponent("dirty", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }

        for repository in [cleanRoot, dirtyRoot] {
            let initialized = try ProcessRunner.run(
                URL(fileURLWithPath: "/usr/bin/git"),
                arguments: ["init", "-b", "main", repository.path],
                timeout: 10
            )
            XCTAssertEqual(initialized.status, 0)
        }
        try Data("uncommitted\n".utf8).write(to: dirtyRoot.appendingPathComponent("notes.txt"))

        let cleanSite = SiteProject(
            path: cleanRoot,
            name: "clean",
            framework: "Laravel",
            isLinked: false
        )
        let dirtySite = SiteProject(
            path: dirtyRoot,
            name: "dirty",
            framework: "Laravel",
            isLinked: false
        )
        let snapshots = await SiteDetailsInspector.inspectGit(for: [cleanSite, dirtySite])

        XCTAssertEqual(snapshots[cleanSite.id]?.branch, "main")
        XCTAssertEqual(snapshots[cleanSite.id]?.changeCount, 0)
        XCTAssertEqual(snapshots[dirtySite.id]?.branch, "main")
        XCTAssertEqual(snapshots[dirtySite.id]?.changeCount, 1)
    }

    @MainActor
    func testCorruptConfigurationIsPreservedAndNeverOverwrittenDuringStartup() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let projects = root.appendingPathComponent("projects")
        let store = ConfigurationStore(rootURL: root, projectsURL: projects)
        let corruptData = Data("{ this is not valid JSON".utf8)
        try corruptData.write(to: store.configURL)
        defer { try? FileManager.default.removeItem(at: root) }

        let model = AppModel(configurationStore: store)
        let issue = try XCTUnwrap(store.loadIssue)
        let backupURL = try XCTUnwrap(issue.backupURL)
        XCTAssertEqual(model.configuration.tld, AppConfiguration.default.tld)
        XCTAssertEqual(model.lastError, issue.message)
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.configURL.path))
        XCTAssertEqual(try Data(contentsOf: backupURL), corruptData)
    }

}
