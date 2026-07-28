import Combine
import CryptoKit
import Darwin
import Security
import XCTest

@testable import HerdMe

extension ConfigurationAndSiteScannerTests {
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
        XCTAssertThrowsError(
            try ProcessRunner.run(
                URL(fileURLWithPath: "/bin/sleep"),
                arguments: ["2"],
                timeout: 0.05
            )
        ) { error in
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

    func testArtisanCommandParserPreservesQuotedArgumentsWithoutUsingAShell() throws {
        XCTAssertEqual(
            try ArtisanCommandParser.parse(
                "artisan route:list --path='api v1' --name=\"users.show\" --columns=method,uri"
            ),
            ["route:list", "--path=api v1", "--name=users.show", "--columns=method,uri"]
        )
        let queue = try ArtisanCommandParser.arguments(
            presetID: "queue-work",
            customCommand: ""
        )
        XCTAssertEqual(queue.arguments, ["queue:work", "--no-interaction"])
        XCTAssertEqual(queue.timeout, 24 * 60 * 60)
    }

    func testArtisanCommandParserRejectsIncompleteAndExecutableCommands() {
        for command in ["", "route:list '", "php artisan route:list", "--version"] {
            XCTAssertThrowsError(try ArtisanCommandParser.parse(command), "Expected rejection for \(command)")
        }
        XCTAssertThrowsError(try ArtisanCommandParser.parse("route:list\nconfig:clear")) { error in
            XCTAssertEqual(error as? ArtisanCommandError, .invalidCharacter)
        }
        XCTAssertThrowsError(
            try ArtisanCommandParser.parse(
                (["route:list"] + Array(repeating: "--flag", count: 32)).joined(separator: " ")
            )
        ) { error in
            XCTAssertEqual(error as? ArtisanCommandError, .tooManyArguments)
        }
    }

    @MainActor
    func testSiteToolsCoordinatorBuildsManagedTerminalArtisanAndNPMInvocations() throws {
        let fixture = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString + " Project's", isDirectory: true)
        let root = fixture.appendingPathComponent("Support Root", isDirectory: true)
        let project = fixture.appendingPathComponent("Laravel Project's", isDirectory: true)
        let php = root.appendingPathComponent("Runtimes/php/8.4/bin/php")
        let node = root.appendingPathComponent("Runtimes/node/22.23.1/bin/node")
        let npmCLI = root.appendingPathComponent(
            "Runtimes/node/22.23.1/lib/node_modules/npm/bin/npm-cli.js"
        )
        defer { try? FileManager.default.removeItem(at: fixture) }

        for directory in [
            project,
            php.deletingLastPathComponent(),
            node.deletingLastPathComponent(),
            npmCLI.deletingLastPathComponent()
        ] {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        }
        try Data("#!/bin/sh\n".utf8).write(to: php)
        try Data("#!/bin/sh\n".utf8).write(to: node)
        try Data("// npm fixture\n".utf8).write(to: npmCLI)
        try Data("#!/usr/bin/env php\n".utf8).write(to: project.appendingPathComponent("artisan"))
        try Data(#"{"scripts":{"dev":"vite","build":"vite build"}}"#.utf8)
            .write(to: project.appendingPathComponent("package.json"))
        for executable in [php, node] {
            try FileManager.default.setAttributes(
                [.posixPermissions: 0o700],
                ofItemAtPath: executable.path
            )
        }

        let terminal = TestTerminalCommandLauncher()
        let coordinator = SiteToolsCoordinator(
            rootURL: root,
            terminalCommandLauncher: terminal,
            runtimeInspector: TestNodeRuntimeInspector(
                versions: [
                    RuntimeVersion(cycle: "22", installedVersion: "22.23.1", isActive: true)
                ]
            )
        )
        let site = SiteProject(
            path: project,
            name: "Quoted Site",
            framework: "Laravel",
            isLinked: true,
            phpVersion: nil,
            nodeVersion: nil
        )

        try coordinator.openTerminal(for: site)
        try coordinator.openTinker(for: site, defaultPHP: "8.4")
        let quotedProjectPath = TerminalCommandLauncher.shellQuote(project.path)
        let quotedPHPPath = TerminalCommandLauncher.shellQuote(php.path)

        XCTAssertEqual(terminal.invocations.count, 2)
        XCTAssertEqual(terminal.invocations[0].title, "Quoted Site")
        XCTAssertTrue(
            terminal.invocations[0].command.contains(
                "cd " + quotedProjectPath
            )
        )
        XCTAssertEqual(terminal.invocations[1].title, "Quoted Site-Tinker")
        XCTAssertTrue(
            terminal.invocations[1].command.contains(
                quotedPHPPath + " artisan tinker"
            )
        )

        let artisan = try coordinator.artisanInvocation(
            for: site,
            defaultPHP: "8.4",
            presetID: "route-list",
            customCommand: ""
        )
        XCTAssertEqual(artisan.phpExecutable, php)
        XCTAssertEqual(artisan.projectDirectory, project)
        XCTAssertEqual(artisan.arguments, ["route:list", "--no-interaction"])
        XCTAssertEqual(artisan.environment["COMPOSER_HOME"], root.appendingPathComponent("Composer").path)
        XCTAssertEqual(
            artisan.environment["COMPOSER_CACHE_DIR"],
            root.appendingPathComponent("Cache/composer").path
        )
        XCTAssertNil(artisan.environment["PHPRC"])
        XCTAssertNil(artisan.environment["PHP_INI_SCAN_DIR"])
        XCTAssertFalse(artisan.environment.keys.contains { $0.hasPrefix("HERD_") })

        let npm = try coordinator.npmInvocation(for: site, scriptName: "dev")
        XCTAssertEqual(npm.nodeExecutable, node)
        XCTAssertEqual(npm.npmCLI, npmCLI)
        XCTAssertEqual(npm.projectDirectory, project)
        XCTAssertEqual(npm.scriptName, "dev")
        XCTAssertEqual(npm.timeout, 24 * 60 * 60)
        XCTAssertEqual(npm.environment["npm_config_cache"], root.appendingPathComponent("Cache/npm").path)
        XCTAssertTrue(npm.environment["PATH"]?.hasPrefix(node.deletingLastPathComponent().path + ":") == true)
        XCTAssertFalse(npm.environment.keys.contains { $0.hasPrefix("HERD_") })
    }

    @MainActor
    func testAppModelDelegatesSiteToolsToInjectedCoordinator() throws {
        let fixture = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        let root = fixture.appendingPathComponent("support", isDirectory: true)
        let projects = fixture.appendingPathComponent("projects", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: fixture) }

        let terminal = TestTerminalCommandLauncher()
        let coordinator = SiteToolsCoordinator(
            rootURL: root,
            terminalCommandLauncher: terminal,
            runtimeInspector: TestNodeRuntimeInspector(versions: [])
        )
        let store = ConfigurationStore(rootURL: root, projectsURL: projects)
        let model = AppModel(configurationStore: store, siteTools: coordinator)
        let site = SiteProject(
            path: projects.appendingPathComponent("delegated-site"),
            name: "Delegated Site",
            framework: "Other",
            isLinked: true
        )

        XCTAssertTrue(model.siteTools === coordinator)
        model.openTerminal(for: site)
        let quotedSitePath = TerminalCommandLauncher.shellQuote(site.path.path)
        XCTAssertEqual(
            terminal.invocations,
            [
                TestTerminalCommandLauncher.Invocation(
                    command: "cd \(quotedSitePath)\n"
                        + "exec \"${SHELL:-/bin/zsh}\" -l",
                    title: site.name
                )
            ]
        )
        XCTAssertNil(model.lastError)
    }

    @MainActor
    func testSiteToolsCoordinatorReportsMissingManagedTools() throws {
        let fixture = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        let root = fixture.appendingPathComponent("support", isDirectory: true)
        let project = fixture.appendingPathComponent("project", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: fixture) }
        try FileManager.default.createDirectory(at: project, withIntermediateDirectories: true)

        let coordinator = SiteToolsCoordinator(
            rootURL: root,
            terminalCommandLauncher: TestTerminalCommandLauncher(),
            runtimeInspector: TestNodeRuntimeInspector(versions: [])
        )
        let site = SiteProject(
            path: project,
            name: "Missing Tools",
            framework: "Other",
            isLinked: true
        )

        XCTAssertThrowsError(try coordinator.openTinker(for: site, defaultPHP: "8.4")) { error in
            guard case .commandFailed = error as? RuntimeInstallationError else {
                return XCTFail("Expected the missing-artisan error, received \(error).")
            }
        }

        try Data("#!/usr/bin/env php\n".utf8).write(to: project.appendingPathComponent("artisan"))
        XCTAssertThrowsError(
            try coordinator.artisanInvocation(
                for: site,
                defaultPHP: "8.4",
                presetID: "route-list",
                customCommand: ""
            )
        ) { error in
            guard case .runtimeNotInstalled(let name, let cycle) = error as? RuntimeInstallationError else {
                return XCTFail("Expected the missing-PHP error, received \(error).")
            }
            XCTAssertEqual(name, "PHP")
            XCTAssertEqual(cycle, "8.4")
        }

        try Data(#"{"scripts":{"build":"vite build"}}"#.utf8)
            .write(to: project.appendingPathComponent("package.json"))
        XCTAssertThrowsError(try coordinator.npmInvocation(for: site, scriptName: "build")) { error in
            guard case .runtimeNotInstalled(let name, let cycle) = error as? RuntimeInstallationError else {
                return XCTFail("Expected the missing-Node error, received \(error).")
            }
            XCTAssertEqual(name, "Node.js")
            XCTAssertEqual(cycle, RuntimeCatalog.defaultNodeMajor)
        }
    }

    func testArtisanRunnerStreamsOutputAndPassesArgumentsDirectly() async throws {
        let fixture = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let executable = fixture.appendingPathComponent("php-fixture")
        try FileManager.default.createDirectory(at: fixture, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: fixture) }
        try Data("#!/bin/sh\nprintf 'first:%s\\n' \"$1\"\nprintf 'second:%s\\n' \"$2\"\n".utf8)
            .write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: executable.path)

        let streamedOutput = TestProcessOutputCapture()
        let result = try await ArtisanCommandRunner.run(
            ArtisanInvocation(
                phpExecutable: executable,
                projectDirectory: fixture,
                arguments: ["route:list", "--path=api v1"],
                environment: ProcessInfo.processInfo.environment,
                timeout: 5
            ),
            cancellation: ArtisanCancellation(),
            outputReceived: { streamedOutput.append($0) }
        )

        XCTAssertEqual(result.status, 0)
        XCTAssertEqual(result.output, "first:artisan\nsecond:route:list\n")
        XCTAssertEqual(streamedOutput.string, result.output)
    }

    func testNPMScriptCatalogDiscoversOnlySafeStringScriptsInPreferredOrder() throws {
        let fixture = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: fixture, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: fixture) }
        let package: [String: Any] = [
            "name": "npm-runner-fixture",
            "scripts": [
                "zeta": "echo zeta",
                "test": "echo test",
                "build": "echo build",
                "dev": "echo dev",
                "alpha": "echo alpha",
                "-unsafe": "echo unsafe",
                "bad\nname": "echo unsafe",
                "non-string": 42
            ]
        ]
        try JSONSerialization.data(withJSONObject: package)
            .write(to: fixture.appendingPathComponent("package.json"))

        XCTAssertEqual(
            try NPMScriptCatalog.scripts(in: fixture).map(\.name),
            ["dev", "build", "test", "alpha", "zeta"]
        )
        XCTAssertEqual(NPMScriptCatalog.timeout(for: "dev"), 24 * 60 * 60)
        XCTAssertEqual(NPMScriptCatalog.timeout(for: "build"), 30 * 60)
        XCTAssertThrowsError(try NPMScriptCatalog.validate(name: "-unsafe")) { error in
            XCTAssertEqual(error as? NPMScriptCatalogError, .invalidScriptName)
        }
    }

    func testNPMScriptCatalogRejectsMissingMalformedOversizedAndEmptyPackages() throws {
        let fixture = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: fixture, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: fixture) }
        let packageURL = fixture.appendingPathComponent("package.json")

        XCTAssertThrowsError(try NPMScriptCatalog.scripts(in: fixture)) { error in
            XCTAssertEqual(error as? NPMScriptCatalogError, .packageJSONMissing)
        }
        try Data("{".utf8).write(to: packageURL)
        XCTAssertThrowsError(try NPMScriptCatalog.scripts(in: fixture)) { error in
            XCTAssertEqual(error as? NPMScriptCatalogError, .packageJSONInvalid)
        }
        try Data("{\"scripts\":{\"unsafe\":42}}".utf8).write(to: packageURL)
        XCTAssertThrowsError(try NPMScriptCatalog.scripts(in: fixture)) { error in
            XCTAssertEqual(error as? NPMScriptCatalogError, .noScripts)
        }
        try Data(repeating: 0x20, count: 1 * 1_024 * 1_024 + 1).write(to: packageURL)
        XCTAssertThrowsError(try NPMScriptCatalog.scripts(in: fixture)) { error in
            XCTAssertEqual(error as? NPMScriptCatalogError, .packageJSONTooLarge)
        }
    }

    func testNPMScriptRunnerStreamsOutputAndKeepsScriptNameAsOneArgument() async throws {
        let fixture = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let node = fixture.appendingPathComponent("node-fixture")
        let npmCLI = fixture.appendingPathComponent("npm-cli.js")
        try FileManager.default.createDirectory(at: fixture, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: fixture) }
        try Data(
            "#!/bin/sh\nprintf 'cli:%s\\n' \"$1\"\nprintf 'option:%s\\n' \"$2\"\nprintf 'command:%s\\n' \"$3\"\nprintf 'script:%s\\n' \"$4\"\n"
                .utf8
        )
        .write(to: node)
        try Data("fixture".utf8).write(to: npmCLI)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: node.path)

        let streamedOutput = TestProcessOutputCapture()
        let result = try await NPMScriptRunner.run(
            NPMScriptInvocation(
                nodeExecutable: node,
                npmCLI: npmCLI,
                projectDirectory: fixture,
                scriptName: "build production",
                environment: ProcessInfo.processInfo.environment,
                timeout: 5
            ),
            cancellation: NPMScriptCancellation(),
            outputReceived: { streamedOutput.append($0) }
        )

        XCTAssertEqual(result.status, 0)
        XCTAssertTrue(result.output.contains("cli:\(npmCLI.path)\n"))
        XCTAssertTrue(result.output.contains("option:--no-update-notifier\n"))
        XCTAssertTrue(result.output.contains("command:run\n"))
        XCTAssertTrue(result.output.contains("script:build production\n"))
        XCTAssertEqual(streamedOutput.string, result.output)
    }

    func testNPMScriptRunnerCancellationStopsTheManagedProcess() async throws {
        let fixture = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let node = fixture.appendingPathComponent("node-fixture")
        let npmCLI = fixture.appendingPathComponent("npm-cli.js")
        try FileManager.default.createDirectory(at: fixture, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: fixture) }
        try Data("#!/bin/sh\nsleep 10\n".utf8).write(to: node)
        try Data("fixture".utf8).write(to: npmCLI)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: node.path)
        let cancellation = NPMScriptCancellation()
        let operation = Task {
            try await NPMScriptRunner.run(
                NPMScriptInvocation(
                    nodeExecutable: node,
                    npmCLI: npmCLI,
                    projectDirectory: fixture,
                    scriptName: "dev",
                    environment: ProcessInfo.processInfo.environment,
                    timeout: 30
                ),
                cancellation: cancellation,
                outputReceived: { _ in }
            )
        }
        try await Task.sleep(for: .milliseconds(150))
        cancellation.cancel()

        do {
            _ = try await operation.value
            XCTFail("The cancelled npm script must not complete successfully.")
        } catch let error as ProcessRunnerError {
            guard case .cancelled = error else {
                return XCTFail("Expected cancellation, received \(error).")
            }
        }
    }

    func testRuntimeInspectorSelectsNewestInstalledNodeVersionNumerically() throws {
        let fixture = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: fixture) }
        for version in ["22.9.0", "22.10.0"] {
            let node = fixture.appendingPathComponent("Runtimes/node/\(version)/bin/node")
            try FileManager.default.createDirectory(
                at: node.deletingLastPathComponent(),
                withIntermediateDirectories: true
            )
            try Data("#!/bin/sh\nprintf '%s' 'v\(version)'\n".utf8).write(to: node)
            try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: node.path)
        }

        let runtime = try XCTUnwrap(
            RuntimeInspector(managedRoot: fixture).nodeVersions().first { $0.cycle == "22" }
        )
        XCTAssertEqual(runtime.installedVersion, "22.10.0")
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

        let index = Data(
            """
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
        XCTAssertEqual(
            AppModel.siteDisplayAddress(domain: "memo.test", navigationURL: nil),
            "http://memo.test"
        )
    }

    func testLocalSiteURLRejectsInvalidSchemesAndPorts() throws {
        let portlessURL = try XCTUnwrap(
            AppModel.localSiteURL(scheme: "https", host: "memo.test")
        )
        XCTAssertEqual(portlessURL.absoluteString, "https://memo.test")
        XCTAssertNil(AppModel.localSiteURL(scheme: "file", host: "memo.test"))
        XCTAssertNil(AppModel.localSiteURL(scheme: "http", host: "memo.test", port: 0))
        XCTAssertNil(AppModel.localSiteURL(scheme: "https", host: "memo.test", port: 65_536))
    }

    func testOfficialRuntimeURLRejectsUnexpectedOriginsAndCredentials() throws {
        XCTAssertEqual(
            try RuntimeInstaller.officialURL(
                "https://nodejs.org/dist/index.json",
                expectedHost: "nodejs.org"
            ).absoluteString,
            "https://nodejs.org/dist/index.json"
        )
        XCTAssertThrowsError(
            try RuntimeInstaller.officialURL(
                "http://nodejs.org/dist/index.json",
                expectedHost: "nodejs.org"
            ))
        XCTAssertThrowsError(
            try RuntimeInstaller.officialURL(
                "https://example.invalid/dist/index.json",
                expectedHost: "nodejs.org"
            ))
        XCTAssertThrowsError(
            try RuntimeInstaller.officialURL(
                "https://user:password@nodejs.org/dist/index.json",
                expectedHost: "nodejs.org"
            ))
        XCTAssertThrowsError(
            try RuntimeInstaller.officialURL(
                "https://nodejs.org:8443/dist/index.json",
                expectedHost: "nodejs.org"
            ))
    }

    func testProductLinksAreDistinctSecureWebAddresses() throws {
        XCTAssertEqual(ProductLinks.all.count, 3)
        XCTAssertEqual(Set(ProductLinks.all.map(\.address)).count, ProductLinks.all.count)

        for link in ProductLinks.all {
            let url = try XCTUnwrap(link.url, link.title)
            XCTAssertEqual(url.scheme, "https", link.title)
            XCTAssertFalse(try XCTUnwrap(url.host, link.title).isEmpty, link.title)
        }
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

    func testBlockingOperationPropagatesParentCancellation() async {
        let probe = TestDetachedCancellationProbe()
        let operation = Task {
            try await AppModel.performBlockingOperation {
                probe.markStarted()
                let deadline = ProcessInfo.processInfo.systemUptime + 2
                while !Task.isCancelled,
                    ProcessInfo.processInfo.systemUptime < deadline
                {
                    Thread.sleep(forTimeInterval: 0.001)
                }
                if Task.isCancelled { probe.markCancellationObserved() }
                try Task.checkCancellation()
            }
        }

        for _ in 0..<500 where !probe.started {
            try? await Task.sleep(for: .milliseconds(1))
        }
        XCTAssertTrue(probe.started)

        operation.cancel()
        _ = try? await operation.value
        XCTAssertTrue(probe.observedCancellation)
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
                automaticHTTPSEnabled: true,
                needsUserApproval: true
            ),
            "Needs approval"
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
        XCTAssertFalse(
            AppModel.shouldAttemptAutomaticHTTPS(
                certificateTrustState: .trusted,
                automaticHTTPSEnabled: false
            ))
        XCTAssertTrue(
            AppModel.shouldAttemptAutomaticHTTPS(
                certificateTrustState: .trusted,
                automaticHTTPSEnabled: true
            ))
        XCTAssertFalse(
            AppModel.shouldAttemptAutomaticHTTPS(
                certificateTrustState: .untrusted,
                automaticHTTPSEnabled: true
            ))
        XCTAssertFalse(
            AppModel.shouldAttemptAutomaticHTTPS(
                certificateTrustState: .untrusted,
                automaticHTTPSEnabled: false
            ))
        XCTAssertFalse(
            AppModel.shouldAttemptAutomaticHTTPS(
                certificateTrustState: .missing,
                automaticHTTPSEnabled: true
            ))
    }

    func testEnvironmentRecoveryRequiresAutomaticExpectedEnvironment() {
        XCTAssertTrue(
            AppModel.shouldRecoverEnvironment(
                hadManagedState: true,
                previousStatus: .running,
                startAutomatically: true,
                hasSites: true
            ))
        XCTAssertTrue(
            AppModel.shouldRecoverEnvironment(
                hadManagedState: false,
                previousStatus: .running,
                startAutomatically: true,
                hasSites: true
            ))
        XCTAssertFalse(
            AppModel.shouldRecoverEnvironment(
                hadManagedState: true,
                previousStatus: .stopped,
                startAutomatically: false,
                hasSites: true
            ))
        XCTAssertFalse(
            AppModel.shouldRecoverEnvironment(
                hadManagedState: true,
                previousStatus: .stopped,
                startAutomatically: true,
                hasSites: false
            ))
        XCTAssertFalse(
            AppModel.shouldRecoverEnvironment(
                hadManagedState: false,
                previousStatus: .stopped,
                startAutomatically: true,
                hasSites: true
            ))
    }

    func testEnvironmentInspectionRejectsStaleCancelledAndShutdownResults() {
        XCTAssertTrue(
            AppModel.shouldCommitEnvironmentInspection(
                expectedStatus: .starting,
                currentStatus: .starting,
                didShutdown: false,
                isCancelled: false
            ))
        XCTAssertFalse(
            AppModel.shouldCommitEnvironmentInspection(
                expectedStatus: .starting,
                currentStatus: .running,
                didShutdown: false,
                isCancelled: false
            ))
        XCTAssertFalse(
            AppModel.shouldCommitEnvironmentInspection(
                expectedStatus: .stopping,
                currentStatus: .stopping,
                didShutdown: true,
                isCancelled: false
            ))
        XCTAssertFalse(
            AppModel.shouldCommitEnvironmentInspection(
                expectedStatus: .stopped,
                currentStatus: .stopped,
                didShutdown: false,
                isCancelled: true
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

    func testMailMIMEParserLimitsMultipartNesting() {
        func wrapping(_ message: String, level: Int) -> String {
            let boundary = "herdme-depth-\(level)"
            return """
                Content-Type: multipart/mixed; boundary="\(boundary)"

                --\(boundary)
                \(message)
                --\(boundary)--
                """
        }

        var atLimit = "Content-Type: text/plain; charset=utf-8\n\nvisible-at-limit"
        for level in 0..<MailMIMEParser.maximumNestingDepth {
            atLimit = wrapping(atLimit, level: level)
        }
        XCTAssertEqual(MailMIMEParser.parse(atLimit).plainText, "visible-at-limit")

        let beyondLimit = wrapping(atLimit, level: MailMIMEParser.maximumNestingDepth)
        XCTAssertNil(MailMIMEParser.parse(beyondLimit).plainText)
        XCTAssertNil(MailMIMEParser.parse(beyondLimit).html)
    }

    func testMailMIMEParserLimitsTotalPartCount() {
        let boundary = "herdme-part-budget"
        var sections: [String] = []
        sections.reserveCapacity(MailMIMEParser.maximumPartCount + 1)
        for index in 0..<(MailMIMEParser.maximumPartCount - 1) {
            sections.append(
                """
                --\(boundary)
                Content-Type: application/octet-stream

                ignored-\(index)
                """)
        }
        sections.append(
            """
            --\(boundary)
            Content-Type: text/plain; charset=utf-8

            must-not-be-reached
            """)
        sections.append("--\(boundary)--")
        let raw = """
            Content-Type: multipart/mixed; boundary="\(boundary)"

            \(sections.joined(separator: "\n"))
            """

        let content = MailMIMEParser.parse(raw)

        XCTAssertNil(content.plainText)
        XCTAssertNil(content.html)
    }

    func testMailMIMEParserRequiresValidBoundaryAndLineDelimiters() {
        let oversizedBoundary = String(repeating: "x", count: 71)
        let invalid = """
            Content-Type: multipart/mixed; boundary="\(oversizedBoundary)"

            --\(oversizedBoundary)
            Content-Type: text/plain

            must-not-be-parsed
            --\(oversizedBoundary)--
            """
        XCTAssertEqual(MailMIMEParser.parse(invalid), MailMIMEContent())

        let valid = """
            Content-Type: multipart/mixed; boundary="line-boundary"

            --line-boundary
            Content-Type: text/plain

            Prefix--line-boundary remains body text.
            --line-boundary--
            """
        XCTAssertEqual(
            MailMIMEParser.parse(valid).plainText,
            "Prefix--line-boundary remains body text."
        )
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
        XCTAssertThrowsError(
            try AppUpdateManager.decodeManifest(
                JSONEncoder().encode(tamperedEnvelope),
                requiresSignature: true,
                publicKey: privateKey.publicKey.x963Representation
            )
        ) { error in
            XCTAssertEqual(error as? AppUpdateError, .invalidSignature)
        }

        let legacyPayload = try JSONEncoder().encode(
            AppUpdateManifest(releases: [
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
        XCTAssertThrowsError(
            try AppUpdateManager.decodeManifest(
                JSONEncoder().encode(legacyEnvelope),
                requiresSignature: true,
                publicKey: privateKey.publicKey.x963Representation
            )
        ) { error in
            XCTAssertEqual(error as? AppUpdateError, .incompletePlatformDownloads)
        }

        let sharedArtifactPayload = try JSONEncoder().encode(
            AppUpdateManifest(releases: [
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
        XCTAssertThrowsError(
            try AppUpdateManager.decodeManifest(
                JSONEncoder().encode(sharedArtifactEnvelope),
                requiresSignature: true,
                publicKey: privateKey.publicKey.x963Representation
            )
        ) { error in
            XCTAssertEqual(error as? AppUpdateError, .invalidResponse)
        }
    }

    func testAppUpdateManagerRejectsUnsignedRemoteManifest() throws {
        let payload = try JSONEncoder().encode(AppUpdateManifest(releases: []))

        XCTAssertThrowsError(
            try AppUpdateManager.decodeManifest(
                payload,
                requiresSignature: true,
                publicKey: nil
            )
        ) { error in
            XCTAssertEqual(error as? AppUpdateError, .unsignedRemoteManifest)
        }
    }

    func testAppUpdateManagerRejectsInsecureDownloadURL() throws {
        let payload = try JSONEncoder().encode(
            AppUpdateManifest(releases: [
                AppUpdateRelease(
                    version: "1.0.0",
                    build: 1,
                    channel: "stable",
                    notes: "Insecure release",
                    downloadURL: URL(string: "http://example.test/herdme.zip")
                )
            ]))

        XCTAssertThrowsError(
            try AppUpdateManager.decodeManifest(
                payload,
                requiresSignature: false,
                publicKey: nil
            )
        ) { error in
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

    func testPHPSerializationParserRejectsInvalidBooleanAndTrailingBytes() {
        for serialized in ["b:2;", "b:-1;", "b:1;trailing", "N;\n"] {
            var parser = PHPSerializationParser(data: Data(serialized.utf8))
            XCTAssertThrowsError(try parser.parse(), serialized) { error in
                guard case PHPSerializationError.malformed = error else {
                    return XCTFail("Expected malformed data for \(serialized), received \(error)")
                }
            }
        }
    }

    func testPHPSerializationParserRejectsOversizedInputBeforeParsing() {
        var parser = PHPSerializationParser(data: Data(repeating: 0x4E, count: 4 * 1_024 * 1_024 + 1))

        XCTAssertThrowsError(try parser.parse()) { error in
            guard case PHPSerializationError.resourceLimit = error else {
                return XCTFail("Expected the parser resource limit, received \(error)")
            }
        }
    }

    func testUntrustedInputParsersHandleDeterministicMutationCorpus() {
        let seeds = [
            Array(
                """
                Content-Type: multipart/alternative; boundary="mutation-boundary"

                --mutation-boundary
                Content-Type: text/plain; charset=utf-8

                HerdMe mutation seed
                --mutation-boundary--
                """.utf8),
            Array("a:2:{s:4:\"name\";s:6:\"HerdMe\";s:5:\"value\";i:42;}".utf8),
            [
                0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x04, 0x64, 0x65, 0x6D, 0x6F,
                0x04, 0x74, 0x65, 0x73, 0x74,
                0x00, 0x00, 0x01, 0x00, 0x01
            ]
        ]
        var state: UInt64 = 0x4845_5244_4D45

        func nextValue(_ state: inout UInt64) -> UInt64 {
            state = state &* 6_364_136_223_846_793_005 &+ 1_442_695_040_888_963_407
            return state
        }

        for iteration in 0..<512 {
            var bytes = seeds[iteration % seeds.count]
            let operationCount = Int(nextValue(&state) % 12) + 1
            for _ in 0..<operationCount {
                switch nextValue(&state) % 4 {
                case 0 where !bytes.isEmpty:
                    let index = Int(nextValue(&state) % UInt64(bytes.count))
                    bytes[index] ^= UInt8(truncatingIfNeeded: nextValue(&state)) | 1
                case 1 where bytes.count < 4_096:
                    let index = Int(nextValue(&state) % UInt64(bytes.count + 1))
                    bytes.insert(UInt8(truncatingIfNeeded: nextValue(&state)), at: index)
                case 2 where !bytes.isEmpty:
                    let index = Int(nextValue(&state) % UInt64(bytes.count))
                    bytes.remove(at: index)
                default:
                    if !bytes.isEmpty, bytes.count < 4_096 {
                        let length = min(bytes.count, Int(nextValue(&state) % 32) + 1)
                        bytes.append(contentsOf: bytes.prefix(length))
                    }
                }
            }

            let raw = String(decoding: bytes, as: UTF8.self)
            let mail = MailMIMEParser.parse(raw)
            XCTAssertLessThanOrEqual(mail.plainText?.utf8.count ?? 0, raw.utf8.count * 2)
            XCTAssertLessThanOrEqual(mail.html?.utf8.count ?? 0, raw.utf8.count * 2)

            var phpParser = PHPSerializationParser(data: Data(bytes))
            _ = try? phpParser.parse()

            if let response = DNSMessage.response(to: Data(bytes), tld: "test") {
                XCTAssertLessThanOrEqual(response.count, bytes.count + 28)
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
        var settings = DebuggerSettings(
            enabled: true,
            detectBreakpoints: true,
            port: 90_003,
            ideKey: " VS CODE!\n"
        )

        XCTAssertTrue(settings.startOnlyOnTrigger)
        settings.startOnlyOnTrigger = false
        XCTAssertFalse(settings.detectBreakpoints)

        settings = settings.normalized

        XCTAssertEqual(settings.port, 65_535)
        XCTAssertEqual(settings.ideKey, "VSCODE")
    }

    func testCreateSiteWizardFlowIncludesReachableExistingProjectSelection() {
        XCTAssertEqual(
            CreateSiteWizardStep.visibleSteps(isNewProject: true),
            [.template, .starter, .configure]
        )
        XCTAssertEqual(
            CreateSiteWizardStep.visibleSteps(isNewProject: false),
            [.template, .starter]
        )
        XCTAssertEqual(CreateSiteWizardStep.template.next(isNewProject: false), .starter)
        XCTAssertNil(CreateSiteWizardStep.starter.next(isNewProject: false))
        XCTAssertEqual(CreateSiteWizardStep.starter.previous, .template)
        XCTAssertEqual(CreateSiteWizardStep.starter.title(isNewProject: false), "Project")
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
        XCTAssertNil(
            XdebugManager.sourceRelease(
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
        XCTAssertTrue(
            XdebugManager.archiveEntriesAreSafe(
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

        XCTAssertThrowsError(
            try TarArchivePolicy.validate(
                nameListing: "../outside\n",
                verboseListing: "-rw-r--r--  0 user group 1 Jul 26 10:00 ../outside\n"
            ))
        try TarArchivePolicy.validate(
            nameListing: "node/link\n",
            verboseListing: "lrwxr-xr-x  0 user group 0 Jul 26 10:00 node/link -> lib/module.js\n"
        )
        XCTAssertThrowsError(
            try TarArchivePolicy.validate(
                nameListing: "node/link\n",
                verboseListing: "lrwxr-xr-x  0 user group 0 Jul 26 10:00 node/link -> ../../outside\n"
            ))
        XCTAssertThrowsError(
            try TarArchivePolicy.validate(
                nameListing: "node/a\nnode/b\n",
                verboseListing: "-rw-r--r--  0 user group 1 Jul 26 10:00 node/a\n"
                    + "-rw-r--r--  0 user group 1 Jul 26 10:00 node/b\n",
                entryLimit: 1
            ))
        XCTAssertThrowsError(
            try TarArchivePolicy.validate(
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

    func testBundledPortableCoreMatchesApplicationAndPHPContracts() throws {
        let executable = PortableCoreClient.bundledExecutableURL()
        XCTAssertTrue(FileManager.default.isExecutableFile(atPath: executable.path))

        let versionResult = try ProcessRunner.run(
            executable,
            arguments: ["--version"],
            timeout: 5
        )
        XCTAssertEqual(versionResult.status, 0)
        XCTAssertEqual(
            versionResult.output.trimmingCharacters(in: .whitespacesAndNewlines),
            Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
        )

        let modules = PHPRuntimeValidator.laravelRequiredExtensions.joined(separator: "\n")
        let report = try PortableCoreClient(executableURL: executable).phpExtensionReport(
            moduleOutput: "[PHP Modules]\n" + modules,
            requiredExtensions: PHPRuntimeValidator.laravelRequiredExtensions
        )
        XCTAssertTrue(report.isLaravelCompatible)
        XCTAssertEqual(report.missing, [])
    }

    func testPortableCoreRejectsAnInconsistentExtensionContract() throws {
        let fixture = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: fixture) }
        let response = "{\"required\":[],\"loaded\":[],\"missing\":[],\"compatible\":true}"
        let script = "#!/bin/sh\nprintf '%s\\n' '\(response)'\n"
        try script.write(to: fixture, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: fixture.path)

        XCTAssertThrowsError(
            try PortableCoreClient(executableURL: fixture).phpExtensionReport(
                moduleOutput: "[PHP Modules]\n",
                requiredExtensions: PHPRuntimeValidator.laravelRequiredExtensions
            )
        ) { error in
            XCTAssertEqual(error as? PortableCoreClientError, .invalidResponse)
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

}
