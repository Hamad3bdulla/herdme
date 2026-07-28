import Combine
import CryptoKit
import Darwin
import Security
import XCTest

@testable import HerdMe

final class TestLocalEnvironmentRunner: LocalEnvironmentRunning, @unchecked Sendable {
    private let queue = DispatchQueue(label: "app.herdme.tests.environment-runner")
    private var running = false
    private var managed = false
    private var sitePorts: [String: Int] = [:]
    private var httpPort: Int?
    private var tlsPort: Int?
    private var startCalls = 0
    private var stopCalls = 0
    private var immediateStopCalls = 0

    var isRunning: Bool { queue.sync { running } }
    var hasManagedState: Bool { queue.sync { managed } }
    var ports: [String: Int] { queue.sync { sitePorts } }
    var proxyPort: Int? { queue.sync { httpPort } }
    var httpsProxyPort: Int? { queue.sync { tlsPort } }
    var httpsStartupError: String? { nil }
    var httpsStartupNeedsApproval: Bool { false }
    var startCount: Int { queue.sync { startCalls } }
    var stopCount: Int { queue.sync { stopCalls } }
    var immediateStopCount: Int { queue.sync { immediateStopCalls } }

    func start(
        sites: [SiteProject],
        defaultPHP: URL?,
        defaultPHPCycle: String?,
        tld: String,
        debuggerSettings: DebuggerSettings,
        phpRequestSettings: PHPRequestSettings,
        enableHTTPS: Bool
    ) async throws -> [String: Int] {
        queue.sync {
            startCalls += 1
            running = true
            managed = true
            sitePorts = Dictionary(uniqueKeysWithValues: sites.map { ($0.id, 8_790) })
            httpPort = 8_080
            tlsPort = enableHTTPS ? 8_443 : nil
        }
        return ports
    }

    func stopAll() async {
        queue.sync {
            stopCalls += 1
            clearRuntimeState()
        }
    }

    func stopAllImmediately() {
        queue.sync {
            immediateStopCalls += 1
            clearRuntimeState()
        }
    }

    private func clearRuntimeState() {
        running = false
        managed = false
        sitePorts.removeAll()
        httpPort = nil
        tlsPort = nil
    }
}

actor TestRuntimeInstaller: RuntimeInstalling {
    private(set) var calls: [String] = []

    func activatePHP(cycle: String) async throws {
        calls.append("activate-php:" + cycle)
    }

    func installPHP(cycle: String) async throws -> String {
        calls.append("install-php:" + cycle)
        return cycle + ".test"
    }

    func installNode(cycle: String) async throws -> String {
        calls.append("install-node:" + cycle)
        return cycle + ".test"
    }

    func activateNode(cycle: String) async throws {
        calls.append("activate-node:" + cycle)
    }

    func removeNode(cycle: String) async throws {
        calls.append("remove-node:" + cycle)
    }

    func composerVersion(cycle: String) async -> String? {
        calls.append("composer-version:" + cycle)
        return "2.9.0"
    }

    func latestComposerVersion(cycle: String) async throws -> String {
        calls.append("latest-composer:" + cycle)
        return "2.9.1"
    }

    func updateComposer(cycle: String) async throws -> String {
        calls.append("update-composer:" + cycle)
        return "2.9.1"
    }

    func laravelInstallerVersion(cycle: String) async -> String? {
        calls.append("laravel-version:" + cycle)
        return "5.20.0"
    }

    func latestLaravelInstallerVersion() async throws -> String {
        calls.append("latest-laravel")
        return "5.21.0"
    }

    func updateLaravelInstaller(cycle: String) async throws -> String {
        calls.append("update-laravel:" + cycle)
        return "5.21.0"
    }

    func prepareLaravelInstallerForProjectCreation(cycle: String) async throws {
        calls.append("prepare-laravel:" + cycle)
    }

    func latestPHPVersions(cycles: [String]) async throws -> [String: String] {
        calls.append("latest-php:" + cycles.joined(separator: ","))
        return Dictionary(uniqueKeysWithValues: cycles.map { ($0, $0 + ".test") })
    }

    func latestNodeVersions(cycles: [String]) async throws -> [String: String] {
        calls.append("latest-node:" + cycles.joined(separator: ","))
        return Dictionary(uniqueKeysWithValues: cycles.map { ($0, $0 + ".test") })
    }
}

final class TestServiceCredentialBackend: ServiceCredentialBacking {
    private var values: [String: Data] = [:]
    private(set) var readAllowsInteraction: [Bool] = []

    func read(account: String, allowInteraction: Bool) throws -> Data? {
        readAllowsInteraction.append(allowInteraction)
        return values[account]
    }

    func write(_ data: Data, account: String) throws { values[account] = data }

    func delete(account: String) throws { values[account] = nil }
}

final class ThreadRecordingServiceCredentialBackend: ServiceCredentialBacking, @unchecked Sendable {
    private let lock = NSLock()
    private var readResult: (isMainThread: Bool, allowsInteraction: Bool)?
    private let storedCredentials = try! JSONEncoder().encode(
        ServiceCredentials(
            username: "herdme_thread_test",
            secret: "thread_test_secret_0123456789_ABCDEFGHIJKL"
        ))

    func read(account: String, allowInteraction: Bool) throws -> Data? {
        lock.withLock {
            readResult = (Thread.isMainThread, allowInteraction)
        }
        return storedCredentials
    }

    func write(_ data: Data, account: String) throws {}

    func delete(account: String) throws {}

    func result() -> (isMainThread: Bool, allowsInteraction: Bool)? {
        lock.withLock { readResult }
    }
}

final class TestCertificateSecretBackend: CertificateSecretBacking {
    private var values: [String: Data] = [:]
    private(set) var readCount = 0

    func read(account: String, allowInteraction: Bool) throws -> Data? {
        readCount += 1
        return values[account]
    }

    func write(_ data: Data, account: String) throws { values[account] = data }
}

@MainActor
final class TestTaskRegistryProbe {
    var started = false
    var observedCancellation = false
    var rejectedOperationRan = false
}

final class TestDetachedCancellationProbe: @unchecked Sendable {
    private let lock = NSLock()
    private var didStart = false
    private var didObserveCancellation = false

    var started: Bool { lock.withLock { didStart } }
    var observedCancellation: Bool { lock.withLock { didObserveCancellation } }

    func markStarted() {
        lock.withLock { didStart = true }
    }

    func markCancellationObserved() {
        lock.withLock { didObserveCancellation = true }
    }
}

enum TestAppUpdateCheckError: LocalizedError, Sendable {
    case failed

    var errorDescription: String? { "Test update failure" }
}

actor TestAppUpdateChecker: AppUpdateChecking {
    let result: Result<AppUpdateResult, TestAppUpdateCheckError>
    private(set) var channels: [String] = []

    init(result: Result<AppUpdateResult, TestAppUpdateCheckError>) {
        self.result = result
    }

    func check(channel: String) async throws -> AppUpdateResult {
        channels.append(channel)
        await Task.yield()
        return try result.get()
    }
}

@MainActor
final class TestLaunchAtLoginManager: LaunchAtLoginManaging {
    var currentStatus: LaunchAtLoginStatus
    private(set) var requestedStates: [Bool] = []
    private(set) var openSettingsCount = 0

    init(status: LaunchAtLoginStatus) {
        currentStatus = status
    }

    func status() -> LaunchAtLoginStatus { currentStatus }

    func setEnabled(_ enabled: Bool) throws {
        requestedStates.append(enabled)
        currentStatus = LaunchAtLoginStatus(isEnabled: enabled, requiresApproval: false)
    }

    func openSystemSettings() {
        openSettingsCount += 1
    }
}

@MainActor
final class TestTerminalCommandLauncher: TerminalCommandLaunching {
    struct Invocation: Equatable {
        let command: String
        let title: String
    }

    private(set) var invocations: [Invocation] = []

    @discardableResult
    func open(command: String, title: String) throws -> URL {
        invocations.append(Invocation(command: command, title: title))
        return URL(fileURLWithPath: "/tmp/herdme-test-terminal.command")
    }
}

struct TestNodeRuntimeInspector: NodeRuntimeInspecting {
    let versions: [RuntimeVersion]

    func nodeVersions() -> [RuntimeVersion] { versions }
}

final class InteractionRequiredCertificateSecretBackend: CertificateSecretBacking {
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

final class TestCertificateKeychainAccess: CertificateKeychainAccess {
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
        let status =
            readStatuses[dataProtection]
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
        let status =
            deleteStatuses[dataProtection]
            ?? (values[dataProtection] == nil ? errSecItemNotFound : errSecSuccess)
        if status == errSecSuccess { values[dataProtection] = nil }
        return status
    }
}

enum ManagedDownloadFixture: Sendable {
    case response(status: Int, body: String)
    case failure(URLError.Code)
}

final class ManagedDownloadURLProtocolState: @unchecked Sendable {
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

final class ManagedDownloadURLProtocol: URLProtocol, @unchecked Sendable {
    override class func canInit(with request: URLRequest) -> Bool { true }

    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        switch ManagedDownloadURLProtocolState.shared.next() {
        case .response(let status, let body):
            guard let url = request.url,
                let response = HTTPURLResponse(
                    url: url,
                    statusCode: status,
                    httpVersion: "HTTP/1.1",
                    headerFields: ["Content-Type": "application/octet-stream"]
                )
            else {
                client?.urlProtocol(self, didFailWithError: URLError(.badServerResponse))
                return
            }
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: Data(body.utf8))
            client?.urlProtocolDidFinishLoading(self)
        case .failure(let code):
            client?.urlProtocol(self, didFailWithError: URLError(code))
        }
    }

    override func stopLoading() {}
}

final class ConfigurationAndSiteScannerTests: XCTestCase {
    static func sendHTTPRequest(port: Int, request: String) throws -> String {
        String(decoding: try sendHTTPRequestData(port: port, request: request), as: UTF8.self)
    }

    static func sendHTTPRequestData(port: Int, request: String) throws -> Data {
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

    static func writeAll(_ data: Data, to descriptor: Int32) throws {
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

    static func exchangeRawHTTP(
        port: Int,
        request: Data
    ) throws -> (response: Data, reachedEOF: Bool) {
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
                var timeout = timeval(tv_sec: 5, tv_usec: 0)
                setsockopt(
                    descriptor,
                    SOL_SOCKET,
                    SO_RCVTIMEO,
                    &timeout,
                    socklen_t(MemoryLayout<timeval>.size)
                )
                try writeAll(request, to: descriptor)
                var response = Data()
                var buffer = [UInt8](repeating: 0, count: 4_096)
                while true {
                    let count = Darwin.read(descriptor, &buffer, buffer.count)
                    if count == 0 { return (response, true) }
                    if count < 0 {
                        if errno == EAGAIN || errno == EWOULDBLOCK { return (response, false) }
                        throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
                    }
                    response.append(contentsOf: buffer.prefix(count))
                }
            }
            lastError = POSIXError(.ECONNREFUSED)
            Darwin.close(descriptor)
            usleep(10_000)
        }
        throw lastError
    }

    static func runProcess(
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

final class TestNetworkServiceController: NetworkServiceControlling, @unchecked Sendable {
    enum Operation: Equatable {
        case register
        case unregister
        case openApprovalSettings
    }

    private let lock = NSLock()
    private var currentStatus: NetworkServiceRegistrationStatus
    private let statusAfterRegister: NetworkServiceRegistrationStatus
    private var registrationFailuresRemaining: Int
    private(set) var operations: [Operation] = []

    init(
        status: NetworkServiceRegistrationStatus,
        statusAfterRegister: NetworkServiceRegistrationStatus = .enabled,
        failRegistration: Bool = false,
        registrationFailures: Int = 0
    ) {
        currentStatus = status
        self.statusAfterRegister = statusAfterRegister
        registrationFailuresRemaining = registrationFailures + (failRegistration ? 1 : 0)
    }

    func status() -> NetworkServiceRegistrationStatus {
        lock.withLock { currentStatus }
    }

    func register() throws {
        let shouldFail = lock.withLock {
            operations.append(.register)
            guard registrationFailuresRemaining > 0 else {
                currentStatus = statusAfterRegister
                return false
            }
            registrationFailuresRemaining -= 1
            currentStatus =
                statusAfterRegister == .requiresApproval
                ? .requiresApproval
                : .notRegistered
            return true
        }
        if shouldFail {
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

final class TestCertificateTrustBackend: CertificateTrustBacking, @unchecked Sendable {
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

final class TestFastCGIRecordingBackend: @unchecked Sendable {
    let port: Int
    private let listener: Int32
    private let lock = NSLock()
    private let requestReceived = DispatchSemaphore(value: 0)
    private let completed = DispatchSemaphore(value: 0)
    private var body = Data()

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

    func waitForBody() -> Data? {
        guard requestReceived.wait(timeout: .now() + .seconds(5)) == .success else { return nil }
        return lock.withLock { body }
    }

    func stop() {
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
            let received = try Self.readRequest(from: client)
            lock.withLock { body = received }
            requestReceived.signal()
            let responseBody = Data("fastcgi-response".utf8)
            try Self.writeRecord(
                type: 6,
                content: Data(
                    ("Status: 200 OK\r\nContent-Type: text/plain\r\n"
                        + "Content-Length: \(responseBody.count)\r\n\r\n").utf8
                ) + responseBody,
                to: client
            )
            try Self.writeRecord(type: 6, content: Data(), to: client)
            try Self.writeRecord(type: 3, content: Data(repeating: 0, count: 8), to: client)
        } catch {
            requestReceived.signal()
        }
    }

    private static func readRequest(from descriptor: Int32) throws -> Data {
        var body = Data()
        while true {
            let header = try readExactly(8, from: descriptor)
            let bytes = [UInt8](header)
            let length = Int(UInt16(bytes[4]) << 8 | UInt16(bytes[5]))
            let padding = Int(bytes[6])
            let content = length > 0 ? try readExactly(length, from: descriptor) : Data()
            if padding > 0 { _ = try readExactly(padding, from: descriptor) }
            if bytes[1] == 5 {
                if content.isEmpty { return body }
                body.append(content)
            }
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

    static func writeAll(_ data: Data, to descriptor: Int32) throws {
        try data.withUnsafeBytes { rawBuffer in
            guard let baseAddress = rawBuffer.baseAddress else { return }
            var offset = 0
            while offset < rawBuffer.count {
                let written = Darwin.write(
                    descriptor,
                    baseAddress.advanced(by: offset),
                    rawBuffer.count - offset
                )
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

final class TestFastCGIStreamingBackend: @unchecked Sendable {
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

    static func writeAll(_ data: Data, to descriptor: Int32) throws {
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

final class TestProcessOutputCapture: @unchecked Sendable {
    private let lock = NSLock()
    private var data = Data()

    var string: String {
        lock.lock()
        defer { lock.unlock() }
        return String(decoding: data, as: UTF8.self)
    }

    func append(_ value: Data) {
        lock.lock()
        data.append(value)
        lock.unlock()
    }
}

final class TestHTTPBackend: @unchecked Sendable {
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

        let response =
            "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: \(responseBody.utf8.count)\r\nConnection: close\r\n\r\n\(responseBody)"
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
