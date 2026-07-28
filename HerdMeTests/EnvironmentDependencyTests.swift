import Foundation
import Security
import XCTest

@testable import HerdMe

private final class TestProcessRunner: ProcessRunning, @unchecked Sendable {
    private let lock = NSLock()
    private var running = false
    private var starts = 0
    private var gracefulStops = 0
    private var immediateStops = 0

    var isRunning: Bool { withLock { running } }
    var isHealthy: Bool { withLock { running } }
    var startCount: Int { withLock { starts } }
    var stopCount: Int { withLock { gracefulStops } }
    var immediateStopCount: Int { withLock { immediateStops } }

    func start(
        executable: URL,
        identifier: String,
        preferredPort: Int,
        phpOptions: [String: String]
    ) async throws -> Int {
        withLock {
            starts += 1
            running = true
        }
        return preferredPort
    }

    func stopAll() async {
        withLock {
            gracefulStops += 1
            running = false
        }
    }

    func stopAllImmediately() {
        withLock {
            immediateStops += 1
            running = false
        }
    }

    private func withLock<T>(_ body: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }
}

private final class TestHTTPListener: HTTPListening, @unchecked Sendable {
    private let lock = NSLock()
    private let failure: Error?
    private var currentPort: Int?
    private var starts = 0
    private var stops = 0

    init(failure: Error? = nil) {
        self.failure = failure
    }

    var isHealthy: Bool { withLock { currentPort != nil } }
    var startCount: Int { withLock { starts } }
    var stopCount: Int { withLock { stops } }

    func start(
        routes: [String: Int],
        identity: SecIdentity?,
        preferredPort: Int,
        fallbackPort: Int
    ) throws -> Int {
        try withLock {
            starts += 1
            if let failure { throw failure }
            currentPort = preferredPort
            return preferredPort
        }
    }

    func stop() {
        withLock {
            stops += 1
            currentPort = nil
        }
    }

    private func withLock<T>(_ body: () throws -> T) rethrows -> T {
        lock.lock()
        defer { lock.unlock() }
        return try body()
    }
}

private final class TestFastCGIListener: FastCGIListening, @unchecked Sendable {
    let documentRoot: URL
    let fpmPort: Int
    private let lock = NSLock()
    private var currentPort: Int?
    private var stops = 0

    init(documentRoot: URL, fpmPort: Int) {
        self.documentRoot = documentRoot
        self.fpmPort = fpmPort
    }

    var isHealthy: Bool { withLock { currentPort != nil } }
    var stopCount: Int { withLock { stops } }

    func start(preferredPort: Int) throws -> Int {
        withLock { currentPort = preferredPort }
        return preferredPort
    }

    func stop() {
        withLock {
            stops += 1
            currentPort = nil
        }
    }

    private func withLock<T>(_ body: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }
}

private final class TestFastCGIListenerFactory: @unchecked Sendable {
    private let lock = NSLock()
    private var storage: [TestFastCGIListener] = []

    var listeners: [TestFastCGIListener] {
        lock.lock()
        defer { lock.unlock() }
        return storage
    }

    func make(documentRoot: URL, fpmPort: Int) -> any FastCGIListening {
        let listener = TestFastCGIListener(documentRoot: documentRoot, fpmPort: fpmPort)
        lock.lock()
        storage.append(listener)
        lock.unlock()
        return listener
    }
}

final class EnvironmentDependencyTests: XCTestCase {
    func testInjectedEnvironmentDependenciesOwnCompleteLifecycle() async throws {
        let fixture = try makeFixture()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        let process = TestProcessRunner()
        let gatewayFactory = TestFastCGIListenerFactory()
        let http = TestHTTPListener()
        let https = TestHTTPListener()
        let engine = LocalEnvironmentEngine(
            rootURL: fixture.engineRoot,
            fpmManager: process,
            gatewayFactory: gatewayFactory.make,
            httpProxy: http,
            httpsProxy: https
        )

        let ports = try await engine.start(
            sites: [fixture.site],
            defaultPHP: fixture.php,
            defaultPHPCycle: "8.4",
            tld: "test",
            enableHTTPS: false
        )

        XCTAssertEqual(process.startCount, 1)
        XCTAssertEqual(http.startCount, 1)
        XCTAssertEqual(https.startCount, 0)
        XCTAssertEqual(ports[fixture.site.id], 8_790)
        XCTAssertEqual(engine.proxyPort, 80)
        XCTAssertTrue(engine.isRunning)
        let gateway = try XCTUnwrap(gatewayFactory.listeners.first)
        XCTAssertEqual(gateway.documentRoot, fixture.site.path.appendingPathComponent("public"))
        XCTAssertEqual(gateway.fpmPort, 9_070)

        await engine.stopAll()

        XCTAssertFalse(engine.hasManagedState)
        XCTAssertEqual(process.stopCount, 1)
        XCTAssertEqual(gateway.stopCount, 1)
        XCTAssertEqual(http.stopCount, 1)
        XCTAssertEqual(https.stopCount, 1)
    }

    func testInjectedEnvironmentDependenciesRollBackPartialStartup() async throws {
        let fixture = try makeFixture()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        let process = TestProcessRunner()
        let gatewayFactory = TestFastCGIListenerFactory()
        let http = TestHTTPListener(failure: LocalEnvironmentError.noAvailablePort)
        let https = TestHTTPListener()
        let engine = LocalEnvironmentEngine(
            rootURL: fixture.engineRoot,
            fpmManager: process,
            gatewayFactory: gatewayFactory.make,
            httpProxy: http,
            httpsProxy: https
        )

        await assertThrowsErrorAsync {
            _ = try await engine.start(
                sites: [fixture.site],
                defaultPHP: fixture.php,
                tld: "test",
                enableHTTPS: false
            )
        }

        XCTAssertFalse(engine.hasManagedState)
        XCTAssertEqual(process.stopCount, 1)
        XCTAssertEqual(gatewayFactory.listeners.first?.stopCount, 1)
        XCTAssertEqual(http.stopCount, 1)
        XCTAssertEqual(https.stopCount, 1)
    }

    private func makeFixture() throws -> (
        root: URL,
        engineRoot: URL,
        php: URL,
        site: SiteProject
    ) {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let runtime = root.appendingPathComponent("runtime")
        let php = runtime.appendingPathComponent("bin/php")
        let fpm = runtime.appendingPathComponent("sbin/php-fpm")
        let siteRoot = root.appendingPathComponent("site")
        try FileManager.default.createDirectory(
            at: php.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try FileManager.default.createDirectory(
            at: fpm.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try FileManager.default.createDirectory(
            at: siteRoot.appendingPathComponent("public"),
            withIntermediateDirectories: true
        )
        let modules = (["[PHP Modules]"] + PHPRuntimeValidator.laravelRequiredExtensions).joined(
            separator: "\n"
        )
        try Data("#!/bin/sh\nprintf '%s\\n' '\(modules)'\n".utf8).write(to: php)
        try Data("#!/bin/sh\nexit 0\n".utf8).write(to: fpm)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: php.path)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: fpm.path)
        return (
            root,
            root.appendingPathComponent("engine"),
            php,
            SiteProject(path: siteRoot, name: "injected", framework: "PHP", isLinked: false)
        )
    }
}

private func assertThrowsErrorAsync(
    _ expression: () async throws -> Void,
    file: StaticString = #filePath,
    line: UInt = #line
) async {
    do {
        try await expression()
        XCTFail("Expected an error to be thrown.", file: file, line: line)
    } catch {}
}
