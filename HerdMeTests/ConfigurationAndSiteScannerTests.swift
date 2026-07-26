import Darwin
import CryptoKit
import Security
import XCTest
@testable import HerdMe

private final class TestServiceCredentialBackend: ServiceCredentialBacking {
    private var values: [String: Data] = [:]
    private(set) var readAllowsInteraction: [Bool] = []

    func read(account: String, allowInteraction: Bool) throws -> Data? {
        readAllowsInteraction.append(allowInteraction)
        return values[account]
    }

    func write(_ data: Data, account: String) throws { values[account] = data }

    func delete(account: String) throws { values[account] = nil }
}

private final class TestCertificateSecretBackend: CertificateSecretBacking {
    private var values: [String: Data] = [:]
    private(set) var readCount = 0

    func read(account: String, allowInteraction: Bool) throws -> Data? {
        readCount += 1
        return values[account]
    }

    func write(_ data: Data, account: String) throws { values[account] = data }
}

private final class InteractionRequiredCertificateSecretBackend: CertificateSecretBacking {
    private(set) var readCount = 0

    func read(account: String, allowInteraction: Bool) throws -> Data? {
        readCount += 1
        if !allowInteraction {
            throw CertificateSecretError.interactionRequired
        }
        return nil
    }

    func write(_ data: Data, account: String) throws {}
}

private final class TestCertificateKeychainAccess: CertificateKeychainAccess {
    enum Operation: Equatable {
        case read(dataProtection: Bool)
        case write(dataProtection: Bool)
        case delete(dataProtection: Bool)
    }

    var values: [Bool: Data] = [:]
    var readStatuses: [Bool: OSStatus] = [:]
    var writeStatuses: [Bool: OSStatus] = [:]
    var deleteStatuses: [Bool: OSStatus] = [:]
    private(set) var operations: [Operation] = []
    private(set) var readAllowsInteraction: [Bool] = []

    func read(
        service: String,
        account: String,
        dataProtection: Bool,
        allowInteraction: Bool
    ) -> (OSStatus, Data?) {
        operations.append(.read(dataProtection: dataProtection))
        readAllowsInteraction.append(allowInteraction)
        let status = readStatuses[dataProtection]
            ?? (values[dataProtection] == nil ? errSecItemNotFound : errSecSuccess)
        return (status, status == errSecSuccess ? values[dataProtection] : nil)
    }

    func write(
        _ data: Data,
        service: String,
        account: String,
        dataProtection: Bool
    ) -> OSStatus {
        operations.append(.write(dataProtection: dataProtection))
        let status = writeStatuses[dataProtection] ?? errSecSuccess
        if status == errSecSuccess { values[dataProtection] = data }
        return status
    }

    func delete(service: String, account: String, dataProtection: Bool) -> OSStatus {
        operations.append(.delete(dataProtection: dataProtection))
        let status = deleteStatuses[dataProtection]
            ?? (values[dataProtection] == nil ? errSecItemNotFound : errSecSuccess)
        if status == errSecSuccess { values[dataProtection] = nil }
        return status
    }
}

private enum ManagedDownloadFixture: Sendable {
    case response(status: Int, body: String)
    case failure(URLError.Code)
}

private final class ManagedDownloadURLProtocolState: @unchecked Sendable {
    static let shared = ManagedDownloadURLProtocolState()

    private let lock = NSLock()
    private var fixtures: [ManagedDownloadFixture] = []
    private(set) var requestCount = 0

    func reset(_ fixtures: [ManagedDownloadFixture]) {
        lock.lock()
        self.fixtures = fixtures
        requestCount = 0
        lock.unlock()
    }

    func next() -> ManagedDownloadFixture {
        lock.lock()
        defer { lock.unlock() }
        requestCount += 1
        guard !fixtures.isEmpty else { return .failure(.unknown) }
        return fixtures.removeFirst()
    }

    func count() -> Int {
        lock.lock()
        defer { lock.unlock() }
        return requestCount
    }
}

private final class ManagedDownloadURLProtocol: URLProtocol, @unchecked Sendable {
    override class func canInit(with request: URLRequest) -> Bool { true }

    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        switch ManagedDownloadURLProtocolState.shared.next() {
        case let .response(status, body):
            guard let url = request.url,
                  let response = HTTPURLResponse(
                      url: url,
                      statusCode: status,
                      httpVersion: "HTTP/1.1",
                      headerFields: ["Content-Type": "application/octet-stream"]
                  ) else {
                client?.urlProtocol(self, didFailWithError: URLError(.badServerResponse))
                return
            }
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: Data(body.utf8))
            client?.urlProtocolDidFinishLoading(self)
        case let .failure(code):
            client?.urlProtocol(self, didFailWithError: URLError(code))
        }
    }

    override func stopLoading() {}
}

final class ConfigurationAndSiteScannerTests: XCTestCase {
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
            try await ProjectCreator(rootURL: root).create(NewProjectRequest(
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
        XCTAssertFalse(FileManager.default.fileExists(
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
        XCTAssertEqual(configuration.configSchemaVersion, 0)
        XCTAssertEqual(configuration.independenceMigrationVersion, 0)
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
        XCTAssertTrue(ConfigurationStore.migratingIndependentPaths(
            in: migrated,
            homeDirectory: home
        ).parkPaths.isEmpty)
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

        let mysqlValues = Dictionary(uniqueKeysWithValues:
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
        let postgreSQLValues = Dictionary(uniqueKeysWithValues:
            ServiceEnvironmentConfiguration.variables(
                for: postgreSQL,
                credentials: credentials
            ).map { ($0.key, $0.value) }
        )
        XCTAssertEqual(postgreSQLValues["DB_USERNAME"], credentials.username)
        XCTAssertEqual(postgreSQLValues["DB_PASSWORD"], credentials.secret)

        let typesenseValues = Dictionary(uniqueKeysWithValues:
            ServiceEnvironmentConfiguration.variables(
                for: typesense,
                credentials: credentials
            ).map { ($0.key, $0.value) }
        )
        XCTAssertEqual(typesenseValues["TYPESENSE_API_KEY"], credentials.secret)
        XCTAssertEqual(typesenseValues["TYPESENSE_PROTOCOL"], "http")

        let storageValues = Dictionary(uniqueKeysWithValues:
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

        _ = try store.credentials(for: UUID(), allowInteraction: false)

        XCTAssertEqual(backend.readAllowsInteraction, [false])
    }

    func testTerminalCommandsShellQuoteProjectPaths() {
        XCTAssertEqual(TerminalCommandLauncher.shellQuote("/tmp/O'Brien App"), "'/tmp/O'\\''Brien App'")
    }

    func testSidebarExposesTheImplementedLogViewer() {
        XCTAssertTrue(SidebarPage.visibleCases.contains(.logs))
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
            guard case let .readinessTimedOut(name, port) = error else {
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
        XCTAssertTrue(log.contains("did not become ready on port \(instance.port)"))
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
        let processID = try XCTUnwrap(Int32(
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
        XCTAssertFalse(FileManager.default.fileExists(
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
            peeringPort: 8_107,
            credentials: ServiceCredentials(
                username: "herdme_typesense",
                secret: "typesense_secret_0123456789_ABCDEFGHIJKL"
            )
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
        let suggestion = try XCTUnwrap(LocalEnvironmentEngine.availablePort(
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
        let mysql = try XCTUnwrap(TablePlusConnection.url(
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

        let mariaDB = try XCTUnwrap(TablePlusConnection.url(
            for: instance("mariadb", port: 3_307),
            credentials: credentials
        ))
        XCTAssertEqual(mariaDB.scheme, "mariadb")
        XCTAssertEqual(mariaDB.user, credentials.username)
        XCTAssertEqual(mariaDB.path, "/mysql")

        let postgreSQL = try XCTUnwrap(TablePlusConnection.url(
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
            XCTAssertTrue(FileManager.default.fileExists(
                atPath: manager.dataDirectory(for: instance).appendingPathComponent(".herdme-auth-v1").path
            ))

            let client = candidate.1.deletingLastPathComponent().appendingPathComponent(candidate.2)
            let arguments = [
                "--no-defaults", "--protocol=TCP", "--host=127.0.0.1", "--port=\(instance.port)",
                "--user=\(credentials.username)", "--connect-timeout=5", "--batch", "--execute=SELECT 1"
            ]
            var authenticatedEnvironment = ProcessInfo.processInfo.environment
            authenticatedEnvironment["MYSQL_PWD"] = credentials.secret
            XCTAssertEqual(try ProcessRunner.run(
                client,
                arguments: arguments,
                environment: authenticatedEnvironment,
                timeout: 10
            ).status, 0)

            var passwordlessEnvironment = ProcessInfo.processInfo.environment
            passwordlessEnvironment["MYSQL_PWD"] = nil
            XCTAssertNotEqual(try ProcessRunner.run(
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
            guard case let .unsupportedPHPCycle(cycle) = error else {
                return XCTFail("Unexpected error: \(error)")
            }
            XCTAssertEqual(cycle, "7.4")
        }

        do {
            _ = try await installer.latestPHPVersions(cycles: ["8.4", "7.4"])
            XCTFail("Update checks must reject unsupported PHP cycles.")
        } catch let error as RuntimeInstallationError {
            guard case let .unsupportedPHPCycle(cycle) = error else {
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
        XCTAssertNil(RuntimeInstaller.nodeChecksum(
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

    func testProcessRunnerDrainsOutputLargerThanAPipeBuffer() throws {
        let result = try ProcessRunner.run(
            URL(fileURLWithPath: "/bin/sh"),
            arguments: ["-c", "dd if=/dev/zero bs=1024 count=128 2>/dev/null"],
            timeout: 5
        )

        XCTAssertEqual(result.status, 0)
        XCTAssertEqual(result.output.utf8.count, 128 * 1_024)
    }

    func testProcessRunnerWritesStandardInputAndClosesThePipe() throws {
        let input = Data("certificate-private-key-fixture".utf8)
        let result = try ProcessRunner.run(
            URL(fileURLWithPath: "/bin/sh"),
            arguments: ["-c", "dd if=/dev/zero bs=1024 count=128 2>/dev/null; cat"],
            standardInput: input,
            timeout: 5
        )

        XCTAssertEqual(result.status, 0)
        XCTAssertEqual(result.output.utf8.count, 128 * 1_024 + input.count)
        XCTAssertTrue(result.output.hasSuffix(String(decoding: input, as: UTF8.self)))
    }

    func testProcessRunnerTerminatesCommandsAfterTimeout() {
        XCTAssertThrowsError(try ProcessRunner.run(
            URL(fileURLWithPath: "/bin/sleep"),
            arguments: ["2"],
            timeout: 0.05
        )) { error in
            guard case ProcessRunnerError.timedOut = error else {
                return XCTFail("Expected a timeout, received \(error)")
            }
        }
    }

    func testProcessRunnerTerminatesDescendantsWhenCancelled() async throws {
        let fixture = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let marker = fixture.appendingPathComponent("descendant-finished")
        try FileManager.default.createDirectory(at: fixture, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: fixture) }
        var environment = ProcessInfo.processInfo.environment
        environment["HERDME_CANCELLATION_MARKER"] = marker.path

        let operation = Task.detached {
            try ProcessRunner.run(
                URL(fileURLWithPath: "/bin/sh"),
                arguments: [
                    "-c",
                    "(sleep 1; touch \"$HERDME_CANCELLATION_MARKER\") & wait"
                ],
                environment: environment,
                timeout: 5,
                cancellationRequested: { Task<Never, Never>.isCancelled }
            )
        }
        try await Task.sleep(for: .milliseconds(150))
        operation.cancel()

        do {
            _ = try await operation.value
            XCTFail("The cancelled process must not complete successfully.")
        } catch let error as ProcessRunnerError {
            guard case .cancelled = error else {
                return XCTFail("Expected cancellation, received \(error).")
            }
        }
        try await Task.sleep(for: .seconds(1.2))
        XCTAssertFalse(FileManager.default.fileExists(atPath: marker.path))
    }

    func testRuntimeUpdateComparisonOnlyAcceptsNewerStableVersions() {
        XCTAssertTrue(RuntimeInstaller.isNewerVersion("v22.24.0", than: "22.23.1"))
        XCTAssertFalse(RuntimeInstaller.isNewerVersion("22.23.1", than: "22.23.1"))
        XCTAssertFalse(RuntimeInstaller.isNewerVersion("5.30.0", than: "5.31.0"))
        XCTAssertTrue(RuntimeInstaller.isStableVersion("v5.31.0"))
        XCTAssertFalse(RuntimeInstaller.isStableVersion("v5.32.0-beta.1"))
    }

    func testComposerVersionParsingAndPHPCompatibilitySelection() throws {
        XCTAssertEqual(
            RuntimeInstaller.composerVersion(
                from: "Composer version 2.10.2 2026-07-01 11:24:45"
            ),
            "2.10.2"
        )
        XCTAssertNil(RuntimeInstaller.composerVersion(from: "PHP 8.5.8"))

        let index = Data("""
        {
          "stable": [
            { "version": "2.10.0", "min-php": 80400 },
            { "version": "2.9.9", "min-php": 70205 },
            { "version": "2.11.0-beta.1", "min-php": 70205 }
          ]
        }
        """.utf8)

        XCTAssertEqual(
            try RuntimeInstaller.compatibleComposerVersion(from: index, phpVersionID: 70433),
            "2.9.9"
        )
        XCTAssertEqual(
            try RuntimeInstaller.compatibleComposerVersion(from: index, phpVersionID: 80500),
            "2.10.0"
        )
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

    func testRuntimeInspectorOffersOnlySupportedPHPForFreshInstallations() {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }

        let cycles = RuntimeInspector(managedRoot: root).phpVersions(activeCycle: "8.4").map(\.cycle)

        XCTAssertEqual(cycles, PHPRuntimeSupport.installableCycles)
        XCTAssertFalse(cycles.contains("7.4"))
    }

    func testRuntimeInspectorReadsFullVersionsAndKeepsInstalledLegacyPHP() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        for (cycle, version) in [("8.4", "8.4.23"), ("8.3", "8.3.32"), ("7.4", "7.4.33")] {
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
        XCTAssertEqual(runtimes.first(where: { $0.cycle == "7.4" })?.installedVersion, "7.4.33")
        XCTAssertTrue(runtimes.first(where: { $0.cycle == "8.4" })?.isActive == true)
        XCTAssertTrue(runtimes.first(where: { $0.cycle == "8.3" })?.isActive == false)
        XCTAssertTrue(runtimes.first(where: { $0.cycle == "7.4" })?.isActive == false)
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

    func testEnvironmentUsesPublicDocumentRoot() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let publicDirectory = root.appendingPathComponent("public")
        try FileManager.default.createDirectory(at: publicDirectory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let site = SiteProject(path: root, name: "demo", framework: "Laravel", isLinked: false)

        XCTAssertEqual(
            LocalEnvironmentEngine.documentRoot(for: site).standardizedFileURL.path,
            publicDirectory.standardizedFileURL.path
        )
    }

    func testEnvironmentRemovesOnlyLegacyRouterArtifacts() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let runtime = root.appendingPathComponent("Runtime", isDirectory: true)
        let routers = runtime.appendingPathComponent("routers", isDirectory: true)
        let retained = runtime.appendingPathComponent("network-helper.conf")
        try FileManager.default.createDirectory(at: routers, withIntermediateDirectories: true)
        try Data("<?php // stale absolute project path".utf8)
            .write(to: routers.appendingPathComponent("removed-site.php"))
        try Data("http=8080\n".utf8).write(to: retained)
        defer { try? FileManager.default.removeItem(at: root) }

        XCTAssertTrue(LocalEnvironmentEngine.removeLegacyRouterArtifacts(rootURL: root))
        XCTAssertFalse(FileManager.default.fileExists(atPath: routers.path))
        XCTAssertEqual(try String(contentsOf: retained, encoding: .utf8), "http=8080\n")
        XCTAssertTrue(LocalEnvironmentEngine.removeLegacyRouterArtifacts(rootURL: root))
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

    func testSiteOpenPreparationNeverOpensBeforeRouteIsReady() {
        XCTAssertEqual(
            AppModel.siteOpenPreparation(environmentStatus: .running, hasRuntimePort: true),
            .open
        )
        XCTAssertEqual(
            AppModel.siteOpenPreparation(environmentStatus: .running, hasRuntimePort: false),
            .restart
        )
        XCTAssertEqual(
            AppModel.siteOpenPreparation(environmentStatus: .stopped, hasRuntimePort: false),
            .start
        )
        XCTAssertEqual(
            AppModel.siteOpenPreparation(environmentStatus: .conflict, hasRuntimePort: false),
            .start
        )
        XCTAssertEqual(
            AppModel.siteOpenPreparation(environmentStatus: .starting, hasRuntimePort: false),
            .wait
        )
        XCTAssertEqual(
            AppModel.siteOpenPreparation(environmentStatus: .stopping, hasRuntimePort: true),
            .wait
        )
    }

    func testBlockingPrivilegedOperationsLeaveTheMainThread() async throws {
        let ranOnMainThread = try await AppModel.performBlockingOperation {
            Thread.isMainThread
        }

        XCTAssertFalse(ranOnMainThread)
    }

    func testHTTPSStatusRequiresAnActiveListenerInsteadOfTrustAlone() {
        XCTAssertEqual(
            AppModel.httpsStatusTitle(
                certificateTrustState: .trusted,
                environmentStatus: .running,
                hasHTTPSPort: false,
                automaticHTTPSEnabled: false
            ),
            "HTTP only"
        )
        XCTAssertEqual(
            AppModel.httpsStatusTitle(
                certificateTrustState: .trusted,
                environmentStatus: .running,
                hasHTTPSPort: false,
                automaticHTTPSEnabled: true
            ),
            "Unavailable"
        )
        XCTAssertEqual(
            AppModel.httpsStatusTitle(
                certificateTrustState: .trusted,
                environmentStatus: .running,
                hasHTTPSPort: true,
                automaticHTTPSEnabled: true
            ),
            "Active"
        )
        XCTAssertEqual(
            AppModel.httpsStatusTitle(
                certificateTrustState: .untrusted,
                environmentStatus: .stopped,
                hasHTTPSPort: false,
                automaticHTTPSEnabled: false
            ),
            "Not trusted"
        )
    }

    func testAutomaticHTTPSAttemptsOnlyForTrustedCertificates() {
        XCTAssertTrue(AppModel.shouldAttemptAutomaticHTTPS(
            certificateTrustState: .trusted,
            automaticHTTPSEnabled: false
        ))
        XCTAssertTrue(AppModel.shouldAttemptAutomaticHTTPS(
            certificateTrustState: .trusted,
            automaticHTTPSEnabled: true
        ))
        XCTAssertFalse(AppModel.shouldAttemptAutomaticHTTPS(
            certificateTrustState: .untrusted,
            automaticHTTPSEnabled: true
        ))
        XCTAssertFalse(AppModel.shouldAttemptAutomaticHTTPS(
            certificateTrustState: .untrusted,
            automaticHTTPSEnabled: false
        ))
        XCTAssertFalse(AppModel.shouldAttemptAutomaticHTTPS(
            certificateTrustState: .missing,
            automaticHTTPSEnabled: true
        ))
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

    func testCapturedMailSearchMatchesVisibleMetadata() {
        let raw = "From: Sender <sender@example.com>\r\nTo: recipient@example.com\r\nSubject: Release report\r\n\r\nHidden body text."
        let message = CapturedMail.parse(
            sender: "envelope@example.com",
            recipients: ["recipient@example.com"],
            raw: raw
        )

        XCTAssertTrue(message.matchesSearch(""))
        XCTAssertTrue(message.matchesSearch(" SENDER "))
        XCTAssertTrue(message.matchesSearch("release REPORT"))
        XCTAssertTrue(message.matchesSearch("recipient@example.com"))
        XCTAssertFalse(message.matchesSearch("hidden body"))
        XCTAssertFalse(message.matchesSearch("does-not-exist"))
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
        let preview = MailMIMEParser.safeHTMLDocument(message.htmlBody ?? "")
        XCTAssertTrue(preview.contains("default-src 'none'"))
        XCTAssertTrue(preview.contains("form-action 'none'"))
        XCTAssertTrue(preview.contains("frame-src 'none'"))
        XCTAssertTrue(preview.contains("style-src 'sha256-48hOXKVM1rwpXip/9XRIr0XijcrNP/RHiD+a7aSGrzg='"))
        XCTAssertTrue(preview.contains("; sandbox"))
        XCTAssertFalse(preview.contains("unsafe-inline"))
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

    func testAppUpdateManagerUsesSemanticVersionPrecedence() async throws {
        XCTAssertEqual(
            VersionComparison.compare("1.0.0-beta.10", "1.0.0-beta.2"),
            .orderedDescending
        )
        XCTAssertEqual(
            VersionComparison.compare("1.0.0", "1.0.0-rc.99"),
            .orderedDescending
        )
        XCTAssertEqual(
            VersionComparison.compare("1.0.0+macos", "1.0.0+windows"),
            .orderedSame
        )

        let manifestURL = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
            .appendingPathExtension("json")
        let stable = AppUpdateRelease(
            version: "1.0.0",
            build: 1,
            channel: "stable",
            notes: "Stable",
            downloadURL: nil
        )
        let prerelease = AppUpdateRelease(
            version: "1.0.0-beta.10",
            build: 99,
            channel: "beta",
            notes: "Prerelease",
            downloadURL: nil
        )
        try JSONEncoder().encode(AppUpdateManifest(releases: [prerelease, stable]))
            .write(to: manifestURL, options: .atomic)
        defer { try? FileManager.default.removeItem(at: manifestURL) }

        let result = try await AppUpdateManager(
            feedURL: manifestURL,
            currentVersion: "1.0.0-beta.9",
            currentBuild: 100
        ).check(channel: "Beta")
        XCTAssertEqual(result, .available(stable))
    }

    func testAppUpdateManagerVerifiesSignedManifestAndRejectsTampering() throws {
        let manifest = AppUpdateManifest(releases: [
            AppUpdateRelease(
                version: "0.2.0",
                build: 2,
                channel: "stable",
                notes: "Signed release",
                downloadURL: nil,
                downloadURLs: AppUpdateDownloadURLs(
                    macOS: URL(string: "https://example.test/herdme-macos.zip"),
                    windowsX64: URL(string: "https://example.test/herdme-windows.zip")
                )
            )
        ])
        let payload = try JSONEncoder().encode(manifest)
        let privateKey = P256.Signing.PrivateKey()
        let signature = try privateKey.signature(for: payload)
        let envelope = AppUpdateSignedEnvelope(
            algorithm: AppUpdateSignedEnvelope.algorithm,
            payload: payload.base64EncodedString(),
            signature: signature.derRepresentation.base64EncodedString()
        )
        let encodedEnvelope = try JSONEncoder().encode(envelope)

        XCTAssertEqual(
            try AppUpdateManager.decodeManifest(
                encodedEnvelope,
                requiresSignature: true,
                publicKey: privateKey.publicKey.x963Representation
            ),
            manifest
        )
        XCTAssertEqual(
            manifest.releases.first?.platformDownloadURL,
            URL(string: "https://example.test/herdme-macos.zip")
        )

        var tamperedPayload = payload
        tamperedPayload.append(0)
        let tamperedEnvelope = AppUpdateSignedEnvelope(
            algorithm: envelope.algorithm,
            payload: tamperedPayload.base64EncodedString(),
            signature: envelope.signature
        )
        XCTAssertThrowsError(try AppUpdateManager.decodeManifest(
            JSONEncoder().encode(tamperedEnvelope),
            requiresSignature: true,
            publicKey: privateKey.publicKey.x963Representation
        )) { error in
            XCTAssertEqual(error as? AppUpdateError, .invalidSignature)
        }

        let legacyPayload = try JSONEncoder().encode(AppUpdateManifest(releases: [
            AppUpdateRelease(
                version: "0.2.0",
                build: 2,
                channel: "stable",
                notes: "Legacy release",
                downloadURL: URL(string: "https://example.test/herdme.zip")
            )
        ]))
        let legacySignature = try privateKey.signature(for: legacyPayload)
        let legacyEnvelope = AppUpdateSignedEnvelope(
            algorithm: AppUpdateSignedEnvelope.algorithm,
            payload: legacyPayload.base64EncodedString(),
            signature: legacySignature.derRepresentation.base64EncodedString()
        )
        XCTAssertThrowsError(try AppUpdateManager.decodeManifest(
            JSONEncoder().encode(legacyEnvelope),
            requiresSignature: true,
            publicKey: privateKey.publicKey.x963Representation
        )) { error in
            XCTAssertEqual(error as? AppUpdateError, .incompletePlatformDownloads)
        }

        let sharedArtifactPayload = try JSONEncoder().encode(AppUpdateManifest(releases: [
            AppUpdateRelease(
                version: "0.2.0",
                build: 2,
                channel: "stable",
                notes: "Invalid shared artifact",
                downloadURL: nil,
                downloadURLs: AppUpdateDownloadURLs(
                    macOS: URL(string: "https://example.test/herdme.zip"),
                    windowsX64: URL(string: "https://example.test/herdme.zip")
                )
            )
        ]))
        let sharedArtifactEnvelope = AppUpdateSignedEnvelope(
            algorithm: AppUpdateSignedEnvelope.algorithm,
            payload: sharedArtifactPayload.base64EncodedString(),
            signature: try privateKey.signature(for: sharedArtifactPayload)
                .derRepresentation.base64EncodedString()
        )
        XCTAssertThrowsError(try AppUpdateManager.decodeManifest(
            JSONEncoder().encode(sharedArtifactEnvelope),
            requiresSignature: true,
            publicKey: privateKey.publicKey.x963Representation
        )) { error in
            XCTAssertEqual(error as? AppUpdateError, .invalidResponse)
        }
    }

    func testAppUpdateManagerRejectsUnsignedRemoteManifest() throws {
        let payload = try JSONEncoder().encode(AppUpdateManifest(releases: []))

        XCTAssertThrowsError(try AppUpdateManager.decodeManifest(
            payload,
            requiresSignature: true,
            publicKey: nil
        )) { error in
            XCTAssertEqual(error as? AppUpdateError, .unsignedRemoteManifest)
        }
    }

    func testAppUpdateManagerRejectsInsecureDownloadURL() throws {
        let payload = try JSONEncoder().encode(AppUpdateManifest(releases: [
            AppUpdateRelease(
                version: "1.0.0",
                build: 1,
                channel: "stable",
                notes: "Insecure release",
                downloadURL: URL(string: "http://example.test/herdme.zip")
            )
        ]))

        XCTAssertThrowsError(try AppUpdateManager.decodeManifest(
            payload,
            requiresSignature: false,
            publicKey: nil
        )) { error in
            XCTAssertEqual(error as? AppUpdateError, .invalidResponse)
        }
    }

    func testPHPSerializationParserRendersArrays() throws {
        let serialized = Data("a:2:{s:5:\"value\";i:42;s:4:\"name\";s:6:\"HerdMe\";}".utf8)
        var parser = PHPSerializationParser(data: serialized)

        let rendered = try parser.parse().rendered()

        XCTAssertTrue(rendered.contains("value: 42"))
        XCTAssertTrue(rendered.contains("name: \"HerdMe\""))
    }

    func testPHPSerializationParserRejectsOversizedCollections() {
        var parser = PHPSerializationParser(data: Data("a:10001:{}".utf8))

        XCTAssertThrowsError(try parser.parse()) { error in
            guard case PHPSerializationError.resourceLimit = error else {
                return XCTFail("Expected the parser resource limit, received \(error)")
            }
        }
    }

    func testPHPSerializationParserRejectsExcessiveNesting() {
        var serialized = "N;"
        for _ in 0..<33 {
            serialized = "a:1:{i:0;\(serialized)}"
        }
        var parser = PHPSerializationParser(data: Data(serialized.utf8))

        XCTAssertThrowsError(try parser.parse()) { error in
            guard case PHPSerializationError.resourceLimit = error else {
                return XCTFail("Expected the parser resource limit, received \(error)")
            }
        }
    }

    func testSMTPMessageBufferRejectsMessagesAboveAdvertisedLimit() {
        var buffer = SMTPMessageBuffer(maximumBytes: 32)
        XCTAssertTrue(buffer.append("Subject: HerdMe"))
        XCTAssertFalse(buffer.append(String(repeating: "x", count: 32)))
        XCTAssertTrue(buffer.isTooLarge)

        buffer.reset()
        XCTAssertTrue(buffer.append("..dot-stuffed"))
        XCTAssertEqual(buffer.rawMessage, ".dot-stuffed")
    }

    func testDumpLineBufferEnforcesConnectionLimitAndReturnsCompleteLines() throws {
        var buffer = DumpLineBuffer()
        XCTAssertTrue(buffer.append(Data("first\npartial".utf8)))
        XCTAssertEqual(String(decoding: try XCTUnwrap(buffer.nextLine()), as: UTF8.self), "first")
        XCTAssertNil(buffer.nextLine())
        XCTAssertFalse(buffer.append(Data(repeating: 0x61, count: DumpLineBuffer.maximumBytes)))
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

    func testPHPFPMInitializationDefersStaleProcessCleanup() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let pidURL = root.appendingPathComponent("Runtime/fpm/stale.pid")
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(
            at: pidURL.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try Data("invalid-pid\n".utf8).write(to: pidURL)

        _ = PHPFPMManager(rootURL: root)

        XCTAssertTrue(FileManager.default.fileExists(atPath: pidURL.path))
    }

    func testPHPRequestSettingsProduceBoundedFPMValues() {
        let settings = PHPRequestSettings(maxUploadMegabytes: -4, memoryLimitMegabytes: 200_000)

        XCTAssertEqual(settings.phpOptions["upload_max_filesize"], "1M")
        XCTAssertEqual(settings.phpOptions["post_max_size"], "1M")
        XCTAssertEqual(settings.phpOptions["memory_limit"], "100000M")
    }

    func testPHPFPMPoolCapacityScalesWithProcessorsAndStaysBounded() {
        XCTAssertEqual(PHPFPMManager.maximumChildren(logicalProcessorCount: -1), 4)
        XCTAssertEqual(PHPFPMManager.maximumChildren(logicalProcessorCount: 1), 4)
        XCTAssertEqual(PHPFPMManager.maximumChildren(logicalProcessorCount: 8), 16)
        XCTAssertEqual(PHPFPMManager.maximumChildren(logicalProcessorCount: 64), 32)

        let configuration = PHPFPMManager.configuration(
            port: 9_000,
            pidURL: URL(fileURLWithPath: "/tmp/herdme test/php.pid"),
            errorLogURL: URL(fileURLWithPath: "/tmp/herdme test/php.log"),
            logicalProcessorCount: 8
        )
        XCTAssertTrue(configuration.contains("pm.max_children = 16"))
        XCTAssertTrue(configuration.contains("listen = 127.0.0.1:9000"))
        XCTAssertTrue(configuration.contains("pid = \"/tmp/herdme test/php.pid\""))
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

    func testXdebugSourceReleaseUsesOfficialHTTPSArchiveAndChecksum() throws {
        let checksum = String(repeating: "A", count: 64)
        let html = """
        <html><body>
          <a href="/files/php_xdebug-3.5.3.dll" title="SHA256: deadbeef">Windows</a>
          <a class="download" href='/files/xdebug-3.5.3.tgz'
             title='SHA256:&nbsp;\(checksum)'>source</a>
        </body></html>
        """

        let release = try XCTUnwrap(XdebugManager.sourceRelease(from: html))

        XCTAssertEqual(release.version, "3.5.3")
        XCTAssertEqual(release.archiveURL.absoluteString, "https://xdebug.org/files/xdebug-3.5.3.tgz")
        XCTAssertEqual(release.sha256, checksum.lowercased())
        XCTAssertNil(XdebugManager.sourceRelease(
            from: html.replacingOccurrences(of: checksum, with: "not-a-checksum")
        ))
    }

    func testXdebugSHA256MatchesKnownFixture() {
        XCTAssertEqual(
            XdebugManager.sha256(of: Data("abc".utf8)),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        )
    }

    func testXdebugArchiveListingRejectsUnsafePaths() {
        XCTAssertTrue(XdebugManager.archiveEntriesAreSafe(
            "xdebug-3.5.3/config.m4\nxdebug-3.5.3/src/base/base.c\n"
        ))
        XCTAssertFalse(XdebugManager.archiveEntriesAreSafe("../outside\n"))
        XCTAssertFalse(XdebugManager.archiveEntriesAreSafe("/tmp/outside\n"))
        XCTAssertFalse(XdebugManager.archiveEntriesAreSafe("folder\\outside\n"))
        XCTAssertFalse(XdebugManager.archiveEntriesAreSafe(""))
    }

    func testTarArchivePolicyRejectsUnsafeTypesPathsAndLimits() throws {
        try TarArchivePolicy.validate(
            nameListing: "node/bin/node\nnode/lib/module.js\n",
            verboseListing: "-rwxr-xr-x  0 user group 4 Jul 26 10:00 node/bin/node\n"
                + "-rw-r--r--  0 user group 8 Jul 26 10:00 node/lib/module.js\n"
        )

        XCTAssertThrowsError(try TarArchivePolicy.validate(
            nameListing: "../outside\n",
            verboseListing: "-rw-r--r--  0 user group 1 Jul 26 10:00 ../outside\n"
        ))
        try TarArchivePolicy.validate(
            nameListing: "node/link\n",
            verboseListing: "lrwxr-xr-x  0 user group 0 Jul 26 10:00 node/link -> lib/module.js\n"
        )
        XCTAssertThrowsError(try TarArchivePolicy.validate(
            nameListing: "node/link\n",
            verboseListing: "lrwxr-xr-x  0 user group 0 Jul 26 10:00 node/link -> ../../outside\n"
        ))
        XCTAssertThrowsError(try TarArchivePolicy.validate(
            nameListing: "node/a\nnode/b\n",
            verboseListing: "-rw-r--r--  0 user group 1 Jul 26 10:00 node/a\n"
                + "-rw-r--r--  0 user group 1 Jul 26 10:00 node/b\n",
            entryLimit: 1
        ))
        XCTAssertThrowsError(try TarArchivePolicy.validate(
            nameListing: "node/a\n",
            verboseListing: "-rw-r--r--  0 user group 5 Jul 26 10:00 node/a\n",
            expandedByteLimit: 4
        ))
    }

    func testLaravelExtensionReportIdentifiesMissingModules() {
        let modules = PHPRuntimeValidator.laravelRequiredExtensions
            .filter { $0 != "curl" }
            .joined(separator: "\n")

        let report = PHPRuntimeValidator.report(moduleOutput: "[PHP Modules]\n" + modules)

        XCTAssertEqual(report.missing, ["curl"])
        XCTAssertFalse(report.isLaravelCompatible)
    }

    func testLaravelExtensionReportMatchesSharedCoreFixtures() throws {
        let fixtureBundle = Bundle(for: Self.self)
        let fixtureNames = ["complete", "missing-mbstring", "missing-curl-and-xml"]

        for name in fixtureNames {
            let moduleURL = try XCTUnwrap(
                fixtureBundle.url(forResource: name, withExtension: "modules")
            )
            let expectedURL = try XCTUnwrap(
                fixtureBundle.url(forResource: name, withExtension: "missing")
            )
            let expected = try String(contentsOf: expectedURL, encoding: .utf8)
                .components(separatedBy: .newlines)
                .filter { !$0.isEmpty }
            let output = try String(contentsOf: moduleURL, encoding: .utf8)
            let report = PHPRuntimeValidator.report(moduleOutput: output)
            XCTAssertEqual(report.missing, expected, moduleURL.lastPathComponent)
        }
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

    func testCertificateKeychainFallsBackWhenDataProtectionEntitlementIsUnavailable() throws {
        let keychain = TestCertificateKeychainAccess()
        keychain.readStatuses[true] = errSecMissingEntitlement
        keychain.writeStatuses[true] = errSecMissingEntitlement
        let store = CertificateSecretStore(
            rootURL: URL(fileURLWithPath: "/tmp/herdme-keychain-fallback"),
            backend: KeychainCertificateSecretBackend(keychain: keychain)
        )
        let secret = Data("fallback-secret".utf8)

        XCTAssertNil(try store.data(for: .authorityPrivateKey))
        try store.store(secret, for: .authorityPrivateKey)

        XCTAssertEqual(keychain.values[false], secret)
        XCTAssertEqual(
            keychain.operations,
            [
                .read(dataProtection: true),
                .read(dataProtection: false),
                .write(dataProtection: true),
                .write(dataProtection: false)
            ]
        )
    }

    func testAutomaticCertificateReadUsesApprovedLegacyKeychainWithoutUI() throws {
        let keychain = TestCertificateKeychainAccess()
        keychain.readStatuses[true] = errSecMissingEntitlement
        let legacy = Data("legacy-secret".utf8)
        keychain.values[false] = legacy
        let store = CertificateSecretStore(
            rootURL: URL(fileURLWithPath: "/tmp/herdme-keychain-no-ui"),
            backend: KeychainCertificateSecretBackend(keychain: keychain)
        )

        XCTAssertEqual(
            try store.data(for: .authorityPrivateKey, allowInteraction: false),
            legacy
        )
        XCTAssertEqual(
            keychain.operations,
            [.read(dataProtection: true), .read(dataProtection: false)]
        )
        XCTAssertEqual(keychain.readAllowsInteraction, [false, false])
    }

    func testAutomaticCertificateReadReportsLegacyInteractionRequirement() {
        let keychain = TestCertificateKeychainAccess()
        keychain.readStatuses[true] = errSecMissingEntitlement
        keychain.readStatuses[false] = errSecInteractionNotAllowed
        let store = CertificateSecretStore(
            rootURL: URL(fileURLWithPath: "/tmp/herdme-keychain-interaction-required"),
            backend: KeychainCertificateSecretBackend(keychain: keychain)
        )

        XCTAssertThrowsError(try store.data(
            for: .authorityPrivateKey,
            allowInteraction: false
        )) { error in
            guard case CertificateSecretError.interactionRequired = error else {
                return XCTFail("Unexpected error: \(error)")
            }
        }
        XCTAssertEqual(
            keychain.operations,
            [.read(dataProtection: true), .read(dataProtection: false)]
        )
        XCTAssertEqual(keychain.readAllowsInteraction, [false, false])
    }

    func testNonInteractiveKeychainPolicySuppressesLegacyAndProtectedPrompts() {
        var query: [CFString: Any] = [:]

        KeychainQueryInteraction.apply(allowInteraction: false, to: &query)

        XCTAssertEqual(query[kSecUseAuthenticationUI] as? String, "u_AuthUIF")
        XCTAssertNotNil(query[kSecUseAuthenticationContext])

        var interactiveQuery: [CFString: Any] = [:]
        KeychainQueryInteraction.apply(allowInteraction: true, to: &interactiveQuery)
        XCTAssertNil(interactiveQuery[kSecUseAuthenticationUI])
        XCTAssertNil(interactiveQuery[kSecUseAuthenticationContext])
    }

    func testAutomaticCertificateReadDoesNotMutateKeychain() throws {
        let keychain = TestCertificateKeychainAccess()
        let protected = Data("protected-secret".utf8)
        keychain.values[true] = protected
        keychain.values[false] = Data("legacy-secret".utf8)
        let store = CertificateSecretStore(
            rootURL: URL(fileURLWithPath: "/tmp/herdme-keychain-read-only"),
            backend: KeychainCertificateSecretBackend(keychain: keychain)
        )

        XCTAssertEqual(
            try store.data(for: .authorityPrivateKey, allowInteraction: false),
            protected
        )
        XCTAssertEqual(keychain.values[false], Data("legacy-secret".utf8))
        XCTAssertEqual(keychain.operations, [.read(dataProtection: true)])
    }

    func testCertificateKeychainDoesNotFallbackForUnrelatedErrors() {
        let keychain = TestCertificateKeychainAccess()
        keychain.values[false] = Data("legacy-secret".utf8)
        keychain.readStatuses[true] = errSecAuthFailed
        let store = CertificateSecretStore(
            rootURL: URL(fileURLWithPath: "/tmp/herdme-keychain-error"),
            backend: KeychainCertificateSecretBackend(keychain: keychain)
        )

        XCTAssertThrowsError(try store.data(for: .authorityPrivateKey)) { error in
            guard case let CertificateSecretError.keychainReadFailed(status) = error else {
                return XCTFail("Unexpected error: \(error)")
            }
            XCTAssertEqual(status, errSecAuthFailed)
        }
        XCTAssertEqual(keychain.operations, [.read(dataProtection: true)])
    }

    func testCertificateKeychainPrefersProtectedValue() throws {
        let keychain = TestCertificateKeychainAccess()
        let protected = Data("protected-secret".utf8)
        keychain.values[true] = protected
        keychain.values[false] = Data("stale-legacy-secret".utf8)
        let store = CertificateSecretStore(
            rootURL: URL(fileURLWithPath: "/tmp/herdme-keychain-preferred"),
            backend: KeychainCertificateSecretBackend(keychain: keychain)
        )

        XCTAssertEqual(try store.data(for: .authorityPrivateKey), protected)
        XCTAssertNil(keychain.values[false])
        XCTAssertEqual(
            keychain.operations,
            [.read(dataProtection: true), .delete(dataProtection: false)]
        )
    }

    func testCertificateKeychainMigratesLegacyValueAfterProtectedWriteSucceeds() throws {
        let keychain = TestCertificateKeychainAccess()
        let legacy = Data("legacy-secret".utf8)
        keychain.values[false] = legacy
        let store = CertificateSecretStore(
            rootURL: URL(fileURLWithPath: "/tmp/herdme-keychain-migration"),
            backend: KeychainCertificateSecretBackend(keychain: keychain)
        )

        XCTAssertEqual(try store.data(for: .authorityPrivateKey), legacy)
        XCTAssertEqual(keychain.values[true], legacy)
        XCTAssertNil(keychain.values[false])
        XCTAssertEqual(
            keychain.operations,
            [
                .read(dataProtection: true),
                .read(dataProtection: false),
                .write(dataProtection: true),
                .delete(dataProtection: false)
            ]
        )
    }

    func testCertificateKeychainUsesLegacyValueWhenMigrationNeedsMissingEntitlement() throws {
        let keychain = TestCertificateKeychainAccess()
        let legacy = Data("legacy-secret".utf8)
        keychain.values[false] = legacy
        keychain.writeStatuses[true] = errSecMissingEntitlement
        let store = CertificateSecretStore(
            rootURL: URL(fileURLWithPath: "/tmp/herdme-keychain-migration-fallback"),
            backend: KeychainCertificateSecretBackend(keychain: keychain)
        )

        XCTAssertEqual(try store.data(for: .authorityPrivateKey), legacy)
        XCTAssertEqual(keychain.values[false], legacy)
        XCTAssertNil(keychain.values[true])
        XCTAssertEqual(
            keychain.operations,
            [
                .read(dataProtection: true),
                .read(dataProtection: false),
                .write(dataProtection: true)
            ]
        )
    }

    func testCertificateKeychainKeepsLegacyValueWhenMigrationFails() {
        let keychain = TestCertificateKeychainAccess()
        let legacy = Data("legacy-secret".utf8)
        keychain.values[false] = legacy
        keychain.writeStatuses[true] = errSecAuthFailed
        let store = CertificateSecretStore(
            rootURL: URL(fileURLWithPath: "/tmp/herdme-keychain-migration-failure"),
            backend: KeychainCertificateSecretBackend(keychain: keychain)
        )

        XCTAssertThrowsError(try store.data(for: .authorityPrivateKey)) { error in
            guard case let CertificateSecretError.keychainWriteFailed(status) = error else {
                return XCTFail("Unexpected error: \(error)")
            }
            XCTAssertEqual(status, errSecAuthFailed)
        }
        XCTAssertEqual(keychain.values[false], legacy)
        XCTAssertNil(keychain.values[true])
        XCTAssertEqual(
            keychain.operations,
            [
                .read(dataProtection: true),
                .read(dataProtection: false),
                .write(dataProtection: true)
            ]
        )
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
        XCTAssertTrue(proxy.isHealthy)
        proxy.stop()
        XCTAssertFalse(proxy.isHealthy)
    }

    func testHTTPSProxyUsesGeneratedCertificateForLocalDomain() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let secretStore = CertificateSecretStore(
            rootURL: root,
            backend: TestCertificateSecretBackend()
        )
        let certificateManager = LocalCertificateManager(rootURL: root, secretStore: secretStore)
        let backend = try TestHTTPBackend(responseBody: "secured-by-herdme")
        let proxy = LocalHTTPProxy()
        defer {
            proxy.stop()
            backend.stop()
        }
        let (importedSubject, proxyPort) = try await AppModel.performBlockingOperation {
            let identity = try certificateManager.prepareIdentity(
                tld: "test",
                domains: ["demo.test"]
            )
            var importedCertificate: SecCertificate?
            guard SecIdentityCopyCertificate(identity, &importedCertificate) == errSecSuccess else {
                throw LocalCertificateError.identityMissing
            }
            let subject = importedCertificate.flatMap { SecCertificateCopySubjectSummary($0) as String? }
            let preferredPort = try XCTUnwrap(LocalEnvironmentEngine.availablePort(startingAt: 9_443))
            let port = try proxy.start(
                routes: ["demo.test": backend.port],
                identity: identity,
                preferredPort: preferredPort,
                fallbackPort: 9_543
            )
            return (subject, port)
        }
        XCTAssertEqual(importedSubject, "*.test")
        let certificateURL = root.appendingPathComponent("Certificates/herdme-ca.pem")
        let leafCertificateURL = root.appendingPathComponent("Certificates/local-sites.pem")
        let privateKeyURL = root.appendingPathComponent("Certificates/herdme-ca.key")
        let leafKeyURL = root.appendingPathComponent("Certificates/local-sites.key")
        XCTAssertTrue(FileManager.default.fileExists(atPath: certificateURL.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: privateKeyURL.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: leafKeyURL.path))
        XCTAssertNotNil(try secretStore.data(for: .authorityPrivateKey))
        let identityPassphrase = try secretStore.identityPassphrase()
        XCTAssertGreaterThanOrEqual(identityPassphrase.count, 32)
        XCTAssertNotEqual(identityPassphrase, "HerdMe-Local-Identity")
        var identityEnvironment = ProcessInfo.processInfo.environment
        identityEnvironment["HERDME_TEST_IDENTITY_PASSPHRASE"] = identityPassphrase
        let identityInspection = try ProcessRunner.run(
            URL(fileURLWithPath: "/usr/bin/openssl"),
            arguments: [
                "pkcs12", "-in", root.appendingPathComponent("Certificates/local-sites.p12").path,
                "-noout", "-passin", "env:HERDME_TEST_IDENTITY_PASSPHRASE"
            ],
            environment: identityEnvironment,
            timeout: 10
        )
        XCTAssertEqual(identityInspection.status, 0, identityInspection.output)
        let inspection = try Self.runProcess(
            executable: "/usr/bin/openssl",
            arguments: ["x509", "-in", leafCertificateURL.path, "-noout", "-text"]
        )
        XCTAssertEqual(inspection.status, 0, inspection.output)
        XCTAssertTrue(inspection.output.contains("DNS:*.test"), inspection.output)
        XCTAssertTrue(inspection.output.contains("DNS:demo.test"), inspection.output)

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

    func testUserCertificateTrustBackendParsesPEMCertificate() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let keyURL = root.appendingPathComponent("certificate.key")
        let certificateURL = root.appendingPathComponent("certificate.pem")
        let generated = try Self.runProcess(
            executable: "/usr/bin/openssl",
            arguments: [
                "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
                "-subj", "/CN=HerdMe Test Certificate", "-keyout", keyURL.path,
                "-out", certificateURL.path
            ]
        )
        XCTAssertEqual(generated.status, 0, generated.output)

        let certificateData = try Data(contentsOf: certificateURL)
        XCTAssertNotNil(UserCertificateTrustBackend.certificate(from: certificateData))
    }

    func testCertificateManagerMigratesLegacyAuthorityKeyWithoutReplacingAuthority() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let certificates = root.appendingPathComponent("Certificates", isDirectory: true)
        try FileManager.default.createDirectory(at: certificates, withIntermediateDirectories: true)
        let keyURL = certificates.appendingPathComponent("herdme-ca.key")
        let certificateURL = certificates.appendingPathComponent("herdme-ca.pem")
        let generated = try Self.runProcess(
            executable: "/usr/bin/openssl",
            arguments: [
                "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "3650",
                "-subj", "/CN=HerdMe Legacy CA/O=HerdMe", "-addext", "basicConstraints=critical,CA:TRUE",
                "-addext", "keyUsage=critical,keyCertSign,cRLSign", "-keyout", keyURL.path,
                "-out", certificateURL.path
            ]
        )
        XCTAssertEqual(generated.status, 0, generated.output)
        let fingerprintBefore = try Self.runProcess(
            executable: "/usr/bin/openssl",
            arguments: ["x509", "-in", certificateURL.path, "-noout", "-fingerprint", "-sha256"]
        )
        XCTAssertEqual(fingerprintBefore.status, 0, fingerprintBefore.output)

        let secretStore = CertificateSecretStore(
            rootURL: root,
            backend: TestCertificateSecretBackend()
        )
        let manager = LocalCertificateManager(rootURL: root, secretStore: secretStore)
        _ = try await AppModel.performBlockingOperation {
            _ = try manager.prepareIdentity(tld: "test", domains: ["legacy.test"])
            return ()
        }

        let fingerprintAfter = try Self.runProcess(
            executable: "/usr/bin/openssl",
            arguments: ["x509", "-in", certificateURL.path, "-noout", "-fingerprint", "-sha256"]
        )
        XCTAssertEqual(fingerprintAfter.status, 0, fingerprintAfter.output)
        XCTAssertEqual(fingerprintAfter.output, fingerprintBefore.output)
        XCTAssertFalse(FileManager.default.fileExists(atPath: keyURL.path))
        XCTAssertNotNil(try secretStore.data(for: .authorityPrivateKey))
    }

    func testCertificateManagerReusesApprovedIdentityWithoutASecondKeychainRead() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let backend = TestCertificateSecretBackend()
        let manager = LocalCertificateManager(
            rootURL: root,
            secretStore: CertificateSecretStore(rootURL: root, backend: backend)
        )

        _ = try await AppModel.performBlockingOperation {
            _ = try manager.prepareIdentity(
                tld: "test",
                domains: ["first.test"],
                allowKeychainInteraction: true
            )
            return ()
        }
        let approvedReadCount = backend.readCount

        _ = try await AppModel.performBlockingOperation {
            _ = try manager.prepareIdentity(
                tld: "test",
                domains: ["first.test"],
                allowKeychainInteraction: false
            )
            return ()
        }

        XCTAssertGreaterThan(approvedReadCount, 0)
        XCTAssertEqual(backend.readCount, approvedReadCount)
    }

    func testCertificateManagerRegeneratesIdentityWhenDomainsChange() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let backend = TestCertificateSecretBackend()
        let manager = LocalCertificateManager(
            rootURL: root,
            secretStore: CertificateSecretStore(rootURL: root, backend: backend)
        )

        _ = try await AppModel.performBlockingOperation {
            _ = try manager.prepareIdentity(tld: "test", domains: ["first.test"])
            _ = try manager.prepareIdentity(tld: "test", domains: ["second.test"])
            return ()
        }

        let marker = try String(
            contentsOf: root.appendingPathComponent("Certificates/local-sites.tld"),
            encoding: .utf8
        )
        XCTAssertEqual(marker, "test\nsecond.test\n")
        let inspection = try Self.runProcess(
            executable: "/usr/bin/openssl",
            arguments: [
                "x509", "-in", root.appendingPathComponent("Certificates/local-sites.pem").path,
                "-noout", "-text"
            ]
        )
        XCTAssertEqual(inspection.status, 0, inspection.output)
        XCTAssertTrue(inspection.output.contains("DNS:second.test"), inspection.output)
        XCTAssertFalse(inspection.output.contains("DNS:first.test"), inspection.output)
    }

    func testCertificateAuthorityUsesUserTrustBackendOnlyOnce() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let trust = TestCertificateTrustBackend(state: .untrusted)
        let manager = LocalCertificateManager(
            rootURL: root,
            secretStore: CertificateSecretStore(
                rootURL: root,
                backend: TestCertificateSecretBackend()
            ),
            trustBackend: trust
        )

        let installed = try await AppModel.performBlockingOperation {
            try manager.installAuthority(tld: "test")
        }
        XCTAssertTrue(installed)
        XCTAssertEqual(trust.installCount, 1)
        XCTAssertEqual(manager.trustState(), .trusted)

        let repeated = try await AppModel.performBlockingOperation {
            try manager.installAuthority(tld: "test")
        }
        XCTAssertFalse(repeated)
        XCTAssertEqual(trust.installCount, 1)
    }

    func testStaticGatewayStreamsLargeFilesAndSupportsByteRanges() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        var asset = Data(count: 2 * 1_024 * 1_024)
        asset.withUnsafeMutableBytes { rawBuffer in
            let bytes = rawBuffer.bindMemory(to: UInt8.self)
            for index in bytes.indices { bytes[index] = UInt8(65 + index % 26) }
        }
        try asset.write(to: root.appendingPathComponent("large.bin"))

        let gateway = LocalFastCGIGateway(documentRoot: root, fpmPort: 1)
        let port = try gateway.start(preferredPort: 42_000)
        defer { gateway.stop() }

        func split(_ response: Data) throws -> (headers: String, body: Data) {
            let delimiter = Data("\r\n\r\n".utf8)
            let range = try XCTUnwrap(response.range(of: delimiter))
            return (
                String(decoding: response[..<range.lowerBound], as: UTF8.self),
                Data(response[range.upperBound...])
            )
        }

        let full = try split(Self.sendHTTPRequestData(
            port: port,
            request: "GET /large.bin HTTP/1.1\r\nHost: static.test\r\nConnection: close\r\n\r\n"
        ))
        XCTAssertTrue(full.headers.hasPrefix("HTTP/1.1 200 OK\r\n"))
        XCTAssertTrue(full.headers.contains("Accept-Ranges: bytes"))
        XCTAssertTrue(full.headers.contains("Content-Length: \(asset.count)"))
        XCTAssertEqual(full.body, asset)

        let start = 1_048_570
        let end = 1_048_633
        let partial = try split(Self.sendHTTPRequestData(
            port: port,
            request: "GET /large.bin HTTP/1.1\r\nHost: static.test\r\nRange: bytes=\(start)-\(end)\r\nConnection: close\r\n\r\n"
        ))
        XCTAssertTrue(partial.headers.hasPrefix("HTTP/1.1 206 Partial Content\r\n"))
        XCTAssertTrue(partial.headers.contains("Content-Range: bytes \(start)-\(end)/\(asset.count)"))
        XCTAssertEqual(partial.body, asset.subdata(in: start..<(end + 1)))

        let openStart = asset.count - 37
        let openEnded = try split(Self.sendHTTPRequestData(
            port: port,
            request: "GET /large.bin HTTP/1.1\r\nHost: static.test\r\nRange: bytes=\(openStart)-\r\nConnection: close\r\n\r\n"
        ))
        XCTAssertEqual(openEnded.body, asset.suffix(37))

        let suffix = try split(Self.sendHTTPRequestData(
            port: port,
            request: "GET /large.bin HTTP/1.1\r\nHost: static.test\r\nRange: bytes=-64\r\nConnection: close\r\n\r\n"
        ))
        XCTAssertEqual(suffix.body, asset.suffix(64))

        let head = try split(Self.sendHTTPRequestData(
            port: port,
            request: "HEAD /large.bin HTTP/1.1\r\nHost: static.test\r\nRange: bytes=10-19\r\nConnection: close\r\n\r\n"
        ))
        XCTAssertTrue(head.headers.hasPrefix("HTTP/1.1 206 Partial Content\r\n"))
        XCTAssertTrue(head.headers.contains("Content-Length: 10"))
        XCTAssertTrue(head.body.isEmpty)

        let unsatisfiable = try split(Self.sendHTTPRequestData(
            port: port,
            request: "GET /large.bin HTTP/1.1\r\nHost: static.test\r\nRange: bytes=\(asset.count)-\r\nConnection: close\r\n\r\n"
        ))
        XCTAssertTrue(unsatisfiable.headers.hasPrefix("HTTP/1.1 416 Range Not Satisfiable\r\n"))
        XCTAssertTrue(unsatisfiable.headers.contains("Content-Range: bytes */\(asset.count)"))
        XCTAssertTrue(unsatisfiable.body.isEmpty)
    }

    func testFastCGIGatewayStreamsBeforeEndRequest() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try Data("<?php echo 'unused';".utf8).write(to: root.appendingPathComponent("index.php"))

        let backend = try TestFastCGIStreamingBackend()
        let gateway = LocalFastCGIGateway(documentRoot: root, fpmPort: backend.port)
        let port = try gateway.start(preferredPort: 42_100)
        defer {
            backend.allowCompletion()
            gateway.stop()
            backend.stop()
        }

        var descriptor: Int32 = -1
        for _ in 0..<100 {
            let candidate = socket(AF_INET, SOCK_STREAM, 0)
            guard candidate >= 0 else { throw POSIXError(.EIO) }
            var address = sockaddr_in()
            address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
            address.sin_family = sa_family_t(AF_INET)
            address.sin_port = in_port_t(UInt16(port).bigEndian)
            address.sin_addr = in_addr(s_addr: inet_addr("127.0.0.1"))
            let status = withUnsafePointer(to: &address) { pointer in
                pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    Darwin.connect(candidate, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
            if status == 0 {
                descriptor = candidate
                break
            }
            Darwin.close(candidate)
            usleep(10_000)
        }
        guard descriptor >= 0 else { throw POSIXError(.ECONNREFUSED) }
        defer { Darwin.close(descriptor) }
        var timeout = timeval(tv_sec: 5, tv_usec: 0)
        setsockopt(descriptor, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
        let request = Data("GET / HTTP/1.1\r\nHost: stream.test\r\nConnection: close\r\n\r\n".utf8)
        try Self.writeAll(request, to: descriptor)

        var response = Data()
        var buffer = [UInt8](repeating: 0, count: 4_096)
        while response.range(of: Data("first-".utf8)) == nil {
            let count = Darwin.read(descriptor, &buffer, buffer.count)
            guard count > 0 else { throw POSIXError(.ETIMEDOUT) }
            response.append(contentsOf: buffer.prefix(count))
        }
        XCTAssertEqual(backend.endWasSent.wait(timeout: .now()), .timedOut)
        backend.allowCompletion()
        while true {
            let count = Darwin.read(descriptor, &buffer, buffer.count)
            if count <= 0 { break }
            response.append(contentsOf: buffer.prefix(count))
        }

        let delimiter = try XCTUnwrap(response.range(of: Data("\r\n\r\n".utf8)))
        let headers = String(decoding: response[..<delimiter.lowerBound], as: UTF8.self)
        XCTAssertTrue(headers.hasPrefix("HTTP/1.1 200 OK\r\n"))
        XCTAssertTrue(headers.contains("X-HerdMe-Stream: yes"))
        XCTAssertTrue(headers.contains("Connection: close"))
        XCTAssertFalse(headers.lowercased().contains("transfer-encoding:"))
        XCTAssertFalse(headers.lowercased().contains("content-length:"))
        XCTAssertEqual(String(decoding: response[delimiter.upperBound...], as: UTF8.self), "first-second")
        XCTAssertEqual(backend.endWasSent.wait(timeout: .now() + .seconds(1)), .success)
    }

    func testEnvironmentServesPHPThroughFPMOverHTTPAndHTTPS() async throws {
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

        let certificateManager = LocalCertificateManager(
            rootURL: engineRoot,
            secretStore: CertificateSecretStore(
                rootURL: engineRoot,
                backend: TestCertificateSecretBackend()
            )
        )
        let engine = LocalEnvironmentEngine(rootURL: engineRoot, certificateManager: certificateManager)
        defer { engine.stopAllImmediately() }
        let site = SiteProject(path: app, name: "fpm-demo", framework: "Laravel", isLinked: false)
        let routes = try await engine.start(sites: [site], defaultPHP: php, tld: "test")
        XCTAssertNotNil(routes[site.id])
        XCTAssertTrue(engine.isRunning)
        XCTAssertTrue(engine.hasManagedState)

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

        await engine.stopAll()
        XCTAssertFalse(engine.isRunning)
        XCTAssertFalse(engine.hasManagedState)
    }

    func testEnvironmentKeepsHTTPRunningWhenHTTPSNeedsKeychainInteraction() async throws {
        let managedRoot = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/HerdMe")
        let php = managedRoot.appendingPathComponent("Runtimes/php/8.4/bin/php")
        let fpm = managedRoot.appendingPathComponent("Runtimes/php/8.4/sbin/php-fpm")
        guard FileManager.default.isExecutableFile(atPath: php.path),
              FileManager.default.isExecutableFile(atPath: fpm.path) else {
            throw XCTSkip("HerdMe-managed PHP-FPM 8.4 is not installed on this machine.")
        }

        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let app = root.appendingPathComponent("http-fallback")
        let publicDirectory = app.appendingPathComponent("public")
        let engineRoot = root.appendingPathComponent("HerdMeData")
        try FileManager.default.createDirectory(at: publicDirectory, withIntermediateDirectories: true)
        try Data("<?php echo 'http-fallback-ok';".utf8).write(
            to: publicDirectory.appendingPathComponent("index.php")
        )
        defer { try? FileManager.default.removeItem(at: root) }

        let secretBackend = InteractionRequiredCertificateSecretBackend()
        let certificateManager = LocalCertificateManager(
            rootURL: engineRoot,
            secretStore: CertificateSecretStore(
                rootURL: engineRoot,
                backend: secretBackend
            )
        )
        let engine = LocalEnvironmentEngine(rootURL: engineRoot, certificateManager: certificateManager)
        defer { engine.stopAllImmediately() }
        let site = SiteProject(
            path: app,
            name: "http-fallback",
            framework: "PHP",
            isLinked: false
        )

        _ = try await engine.start(
            sites: [site],
            defaultPHP: php,
            tld: "test",
            enableHTTPS: false
        )

        XCTAssertTrue(engine.isRunning)
        XCTAssertNil(engine.httpsProxyPort)
        XCTAssertNotNil(engine.httpsStartupError)
        XCTAssertEqual(secretBackend.readCount, 0)

        await engine.stopAll()
        _ = try await engine.start(sites: [site], defaultPHP: php, tld: "test")

        XCTAssertTrue(engine.isRunning)
        XCTAssertNil(engine.httpsProxyPort)
        XCTAssertNotNil(engine.httpsStartupError)
        XCTAssertGreaterThan(secretBackend.readCount, 0)
        let response = try Self.sendHTTPRequest(
            port: try XCTUnwrap(engine.proxyPort),
            request: "GET / HTTP/1.1\r\nHost: http-fallback.test\r\nConnection: close\r\n\r\n"
        )
        XCTAssertTrue(response.contains("200 OK"), response)
        XCTAssertTrue(response.contains("http-fallback-ok"), response)
    }

    func testEnvironmentLoadsInstalledXdebugAndPHPSettings() async throws {
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

        let certificateManager = LocalCertificateManager(
            rootURL: engineRoot,
            secretStore: CertificateSecretStore(
                rootURL: engineRoot,
                backend: TestCertificateSecretBackend()
            )
        )
        let engine = LocalEnvironmentEngine(rootURL: engineRoot, certificateManager: certificateManager)
        defer { engine.stopAllImmediately() }
        let site = SiteProject(path: app, name: "xdebug-demo", framework: "PHP", isLinked: false)
        _ = try await engine.start(
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

        let certificateManager = LocalCertificateManager(
            rootURL: engineRoot,
            secretStore: CertificateSecretStore(
                rootURL: engineRoot,
                backend: TestCertificateSecretBackend()
            )
        )
        let engine = LocalEnvironmentEngine(rootURL: engineRoot, certificateManager: certificateManager)
        defer { engine.stopAllImmediately() }
        let site = SiteProject(path: app, name: "real-laravel", framework: "Laravel", isLinked: false)
        _ = try await engine.start(sites: [site], defaultPHP: php, tld: "test")

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

    func testModernNetworkServiceManifestUsesFixedBundleProgramAndMode() throws {
        let manifest = Bundle.main.bundleURL
            .appendingPathComponent("Contents/Library/LaunchDaemons")
            .appendingPathComponent(DomainResolverManager.modernPlistName)

        XCTAssertTrue(FileManager.default.isReadableFile(atPath: manifest.path))
        XCTAssertTrue(DomainResolverManager.modernServiceManifestIsValid(at: manifest))

        let invalid = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: invalid) }
        var contents = try String(contentsOf: manifest, encoding: .utf8)
        contents = contents.replacingOccurrences(
            of: "Contents/Helpers/herdme-network-helper",
            with: "/tmp/untrusted-helper"
        )
        try contents.write(to: invalid, atomically: true, encoding: .utf8)
        XCTAssertFalse(DomainResolverManager.modernServiceManifestIsValid(at: invalid))
    }

    func testModernNetworkServiceRegistersAndRestartsThroughFixedController() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let controller = TestNetworkServiceController(status: .notRegistered)
        let manager = DomainResolverManager(rootURL: root, networkService: controller)

        try manager.registerModernService(restart: false)
        XCTAssertEqual(controller.operations, [.register])
        XCTAssertEqual(controller.status(), .enabled)

        try manager.registerModernService(restart: true)
        XCTAssertEqual(controller.operations, [.register, .unregister, .register])
        XCTAssertEqual(controller.status(), .enabled)
    }

    func testModernNetworkServiceReportsApprovalWithoutRegisteringAgain() {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let controller = TestNetworkServiceController(status: .requiresApproval)
        let manager = DomainResolverManager(rootURL: root, networkService: controller)

        XCTAssertThrowsError(try manager.registerModernService(restart: false)) { error in
            guard case DomainResolverError.serviceRequiresApproval = error else {
                return XCTFail("Unexpected error: \(error)")
            }
        }
        XCTAssertEqual(controller.operations, [])
    }

    func testModernNetworkServiceReportsApprovalRaisedDuringRegistration() {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let controller = TestNetworkServiceController(
            status: .notRegistered,
            statusAfterRegister: .requiresApproval,
            failRegistration: true
        )
        let manager = DomainResolverManager(rootURL: root, networkService: controller)

        XCTAssertThrowsError(try manager.registerModernService(restart: false)) { error in
            guard case DomainResolverError.serviceRequiresApproval = error else {
                return XCTFail("Unexpected error: \(error)")
            }
        }
        XCTAssertEqual(controller.operations, [.register])
        XCTAssertEqual(controller.status(), .requiresApproval)
    }

    func testModernNetworkServiceRejectsRegistrationThatNeverBecomesEnabled() {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let controller = TestNetworkServiceController(
            status: .notRegistered,
            statusAfterRegister: .notRegistered
        )
        let manager = DomainResolverManager(rootURL: root, networkService: controller)

        XCTAssertThrowsError(try manager.registerModernService(restart: false)) { error in
            guard case DomainResolverError.installationVerificationFailed = error else {
                return XCTFail("Unexpected error: \(error)")
            }
        }
        XCTAssertEqual(controller.operations, [.register])
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
        try manager.updateNetworkRouting(httpPort: 8_082, httpsPort: nil, tld: "local-test")
        let httpOnlyConfiguration = try String(
            contentsOf: root.appendingPathComponent("Runtime/network-helper.conf"),
            encoding: .utf8
        )
        XCTAssertEqual(httpOnlyConfiguration, "http=8082\nhttps=0\ntld=local-test\n")
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
        String(decoding: try sendHTTPRequestData(port: port, request: request), as: UTF8.self)
    }

    private static func sendHTTPRequestData(port: Int, request: String) throws -> Data {
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
                return response
            }
            lastError = POSIXError(.ECONNREFUSED)
            Darwin.close(descriptor)
            usleep(10_000)
        }
        throw lastError
    }

    private static func writeAll(_ data: Data, to descriptor: Int32) throws {
        try data.withUnsafeBytes { rawBuffer in
            guard let baseAddress = rawBuffer.baseAddress else { return }
            var offset = 0
            while offset < rawBuffer.count {
                let written = Darwin.write(descriptor, baseAddress.advanced(by: offset), rawBuffer.count - offset)
                guard written > 0 else { throw POSIXError(.EIO) }
                offset += written
            }
        }
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

private final class TestNetworkServiceController: NetworkServiceControlling, @unchecked Sendable {
    enum Operation: Equatable {
        case register
        case unregister
        case openApprovalSettings
    }

    private let lock = NSLock()
    private var currentStatus: NetworkServiceRegistrationStatus
    private let statusAfterRegister: NetworkServiceRegistrationStatus
    private let failRegistration: Bool
    private(set) var operations: [Operation] = []

    init(
        status: NetworkServiceRegistrationStatus,
        statusAfterRegister: NetworkServiceRegistrationStatus = .enabled,
        failRegistration: Bool = false
    ) {
        currentStatus = status
        self.statusAfterRegister = statusAfterRegister
        self.failRegistration = failRegistration
    }

    func status() -> NetworkServiceRegistrationStatus {
        lock.withLock { currentStatus }
    }

    func register() throws {
        lock.withLock {
            operations.append(.register)
            currentStatus = statusAfterRegister
        }
        if failRegistration {
            throw NSError(
                domain: "TestNetworkServiceController",
                code: 1,
                userInfo: [NSLocalizedDescriptionKey: "Registration requires approval."]
            )
        }
    }

    func unregister() throws {
        lock.withLock {
            operations.append(.unregister)
            currentStatus = .notRegistered
        }
    }

    func openApprovalSettings() {
        lock.withLock {
            operations.append(.openApprovalSettings)
        }
    }
}

private final class TestCertificateTrustBackend: CertificateTrustBacking, @unchecked Sendable {
    private let lock = NSLock()
    private var currentState: CertificateTrustState
    private(set) var installCount = 0

    init(state: CertificateTrustState) {
        currentState = state
    }

    func state(for _: Data) -> CertificateTrustState {
        lock.withLock { currentState }
    }

    func install(_: Data) throws {
        lock.withLock {
            installCount += 1
            currentState = .trusted
        }
    }
}

private final class TestFastCGIStreamingBackend: @unchecked Sendable {
    let port: Int
    let endWasSent = DispatchSemaphore(value: 0)
    private let listener: Int32
    private let releaseTail = DispatchSemaphore(value: 0)
    private let completed = DispatchSemaphore(value: 0)

    init() throws {
        let descriptor = socket(AF_INET, SOCK_STREAM, 0)
        guard descriptor >= 0 else { throw POSIXError(.EIO) }
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = 0
        address.sin_addr = in_addr(s_addr: inet_addr("127.0.0.1"))
        let bound = withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.bind(descriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard bound == 0, listen(descriptor, 1) == 0 else {
            Darwin.close(descriptor)
            throw POSIXError(.EADDRINUSE)
        }
        var resolvedAddress = sockaddr_in()
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let resolved = withUnsafeMutablePointer(to: &resolvedAddress) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                getsockname(descriptor, $0, &length)
            }
        }
        guard resolved == 0 else {
            Darwin.close(descriptor)
            throw POSIXError(.EIO)
        }
        listener = descriptor
        port = Int(UInt16(bigEndian: resolvedAddress.sin_port))
        DispatchQueue.global(qos: .userInitiated).async { [self] in run() }
    }

    func allowCompletion() {
        releaseTail.signal()
    }

    func stop() {
        releaseTail.signal()
        Darwin.shutdown(listener, SHUT_RDWR)
        Darwin.close(listener)
        _ = completed.wait(timeout: .now() + .seconds(1))
    }

    private func run() {
        defer { completed.signal() }
        let client = accept(listener, nil, nil)
        guard client >= 0 else { return }
        defer { Darwin.close(client) }
        do {
            try readRequest(from: client)
            try Self.writeRecord(
                type: 6,
                content: Data("Status: 200 OK\r\nContent-Type: text/plain\r\nTransfer-Encoding: chunked\r\nX-Herd".utf8),
                to: client
            )
            try Self.writeRecord(
                type: 6,
                content: Data("Me-Stream: yes\r\n\r\nfirst-".utf8),
                to: client
            )
            guard releaseTail.wait(timeout: .now() + .seconds(5)) == .success else { return }
            try Self.writeRecord(type: 6, content: Data("second".utf8), to: client)
            try Self.writeRecord(type: 6, content: Data(), to: client)
            try Self.writeRecord(type: 3, content: Data(repeating: 0, count: 8), to: client)
            endWasSent.signal()
        } catch {
        }
    }

    private func readRequest(from descriptor: Int32) throws {
        while true {
            let header = try Self.readExactly(8, from: descriptor)
            let bytes = [UInt8](header)
            let length = Int(UInt16(bytes[4]) << 8 | UInt16(bytes[5]))
            let padding = Int(bytes[6])
            if length > 0 { _ = try Self.readExactly(length, from: descriptor) }
            if padding > 0 { _ = try Self.readExactly(padding, from: descriptor) }
            if bytes[1] == 5, length == 0 { return }
        }
    }

    private static func writeRecord(type: UInt8, content: Data, to descriptor: Int32) throws {
        let padding = (8 - content.count % 8) % 8
        var record = Data([
            1, type, 0, 1,
            UInt8((content.count >> 8) & 0xff),
            UInt8(content.count & 0xff),
            UInt8(padding), 0
        ])
        record.append(content)
        if padding > 0 { record.append(Data(repeating: 0, count: padding)) }
        try writeAll(record, to: descriptor)
    }

    private static func writeAll(_ data: Data, to descriptor: Int32) throws {
        try data.withUnsafeBytes { rawBuffer in
            guard let baseAddress = rawBuffer.baseAddress else { return }
            var offset = 0
            while offset < rawBuffer.count {
                let written = Darwin.write(descriptor, baseAddress.advanced(by: offset), rawBuffer.count - offset)
                guard written > 0 else { throw POSIXError(.EIO) }
                offset += written
            }
        }
    }

    private static func readExactly(_ count: Int, from descriptor: Int32) throws -> Data {
        var data = Data(count: count)
        try data.withUnsafeMutableBytes { rawBuffer in
            guard let baseAddress = rawBuffer.baseAddress else { return }
            var offset = 0
            while offset < count {
                let received = Darwin.read(descriptor, baseAddress.advanced(by: offset), count - offset)
                guard received > 0 else { throw POSIXError(.EIO) }
                offset += received
            }
        }
        return data
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
