import Foundation
import Security

protocol ProcessRunning: Sendable {
    var isRunning: Bool { get }
    var isHealthy: Bool { get }

    func start(
        executable: URL,
        identifier: String,
        preferredPort: Int,
        phpOptions: [String: String]
    ) async throws -> Int
    func stopAll() async
    func stopAllImmediately()
}

protocol HTTPListening: Sendable {
    var isHealthy: Bool { get }

    func start(
        routes: [String: Int],
        identity: SecIdentity?,
        preferredPort: Int,
        fallbackPort: Int
    ) throws -> Int
    func update(routes: [String: Int])
    func stop()
}

extension HTTPListening {
    func update(routes: [String: Int]) {}
}

protocol FastCGIListening: Sendable {
    var isHealthy: Bool { get }

    func start(preferredPort: Int) throws -> Int
    func stop()
}

typealias FastCGIListenerFactory = @Sendable (URL, Int) -> any FastCGIListening

extension PHPFPMManager: ProcessRunning {}
extension LocalHTTPProxy: HTTPListening {}
extension LocalFastCGIGateway: FastCGIListening {}
