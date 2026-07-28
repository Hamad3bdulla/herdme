import Combine
import Foundation

struct EnvironmentEngineSnapshot: Sendable {
    let isRunning: Bool
    let hasManagedState: Bool
    let sitePorts: [String: Int]
    let proxyPort: Int?
    let httpsPort: Int?
    let httpsStartupError: String?
    let httpsStartupNeedsApproval: Bool
}

@MainActor
final class EnvironmentCoordinator: ObservableObject {
    @Published var status: EnvironmentStatus = .stopped
    @Published var proxyPort: Int?
    @Published var httpsPort: Int?
    @Published var httpsStartupNeedsApproval = false

    private nonisolated let engine: any LocalEnvironmentRunning
    private let inspectionGate = AsyncOperationGate()

    init(
        rootURL: URL,
        certificateManager: LocalCertificateManager,
        engine: (any LocalEnvironmentRunning)? = nil
    ) {
        self.engine =
            engine
            ?? LocalEnvironmentEngine(
                rootURL: rootURL,
                certificateManager: certificateManager
            )
    }

    var isHTTPSActive: Bool {
        status == .running && httpsPort != nil
    }

    func startEngine(
        sites: [SiteProject],
        defaultPHP: URL?,
        defaultPHPCycle: String,
        tld: String,
        debuggerSettings: DebuggerSettings,
        phpRequestSettings: PHPRequestSettings,
        enableHTTPS: Bool
    ) async throws -> EnvironmentEngineSnapshot {
        let engine = engine
        let startTask = Task.detached(priority: .userInitiated) {
            let ports = try await engine.start(
                sites: sites,
                defaultPHP: defaultPHP,
                defaultPHPCycle: defaultPHPCycle,
                tld: tld,
                debuggerSettings: debuggerSettings,
                phpRequestSettings: phpRequestSettings,
                enableHTTPS: enableHTTPS
            )
            return EnvironmentEngineSnapshot(
                isRunning: engine.isRunning,
                hasManagedState: engine.hasManagedState,
                sitePorts: ports,
                proxyPort: engine.proxyPort,
                httpsPort: engine.httpsProxyPort,
                httpsStartupError: engine.httpsStartupError,
                httpsStartupNeedsApproval: engine.httpsStartupNeedsApproval
            )
        }
        return try await withTaskCancellationHandler {
            try await startTask.value
        } onCancel: {
            startTask.cancel()
        }
    }

    func engineSnapshot() -> EnvironmentEngineSnapshot {
        EnvironmentEngineSnapshot(
            isRunning: engine.isRunning,
            hasManagedState: engine.hasManagedState,
            sitePorts: engine.ports,
            proxyPort: engine.proxyPort,
            httpsPort: engine.httpsProxyPort,
            httpsStartupError: engine.httpsStartupError,
            httpsStartupNeedsApproval: engine.httpsStartupNeedsApproval
        )
    }

    func stopEngine() async {
        await engine.stopAll()
    }

    nonisolated func stopImmediately() {
        engine.stopAllImmediately()
    }

    func acquireInspection() async {
        await inspectionGate.acquire()
    }

    func releaseInspection() async {
        await inspectionGate.release()
    }

    func apply(
        proxyPort: Int?,
        httpsPort: Int?,
        httpsStartupNeedsApproval: Bool = false
    ) {
        self.proxyPort = proxyPort
        self.httpsPort = httpsPort
        self.httpsStartupNeedsApproval = httpsStartupNeedsApproval
    }

    func clearEndpoints() {
        proxyPort = nil
        httpsPort = nil
        httpsStartupNeedsApproval = false
    }
}
