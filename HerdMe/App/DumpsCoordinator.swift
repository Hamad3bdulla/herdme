import Combine
import Foundation

@MainActor
final class DumpsCoordinator: ObservableObject {
    @Published private(set) var dumps: [CapturedDump] = []
    @Published private(set) var isServerRunning = false

    private let store: DumpStore
    private nonisolated let server: DumpCaptureServer
    private var didShutdown = false

    init(rootURL: URL, server: DumpCaptureServer = DumpCaptureServer()) {
        store = DumpStore(rootURL: rootURL)
        self.server = server
    }

    func load() async {
        let loadedDumps = await store.load()
        guard !Task.isCancelled, !didShutdown else { return }
        dumps = loadedDumps
    }

    func start(
        port: Int,
        reportErrors: Bool = true,
        onFailure: @escaping @MainActor @Sendable (_ message: String, _ report: Bool) -> Void
    ) {
        guard !didShutdown else { return }
        guard !server.isRunning else {
            isServerRunning = true
            return
        }
        do {
            try server.start(
                port: port,
                onStateChange: { [weak self] running, errorMessage in
                    Task { @MainActor in
                        guard let self, !self.didShutdown else { return }
                        self.isServerRunning = running
                        if let errorMessage {
                            onFailure("Dump server failed: " + errorMessage, reportErrors)
                        }
                    }
                },
                onDump: { [weak self] dump in
                    Task { @MainActor in
                        guard let self, !self.didShutdown else { return }
                        await self.capture(dump, onFailure: onFailure)
                    }
                }
            )
        } catch {
            isServerRunning = false
            onFailure("Dump server failed: " + error.localizedDescription, reportErrors)
        }
    }

    func stop() {
        server.stop()
        isServerRunning = false
    }

    nonisolated func stopImmediately() {
        server.stop()
    }

    func shutdown() {
        didShutdown = true
        stop()
    }

    func clear() async throws {
        try await store.clear()
        guard !Task.isCancelled, !didShutdown else { return }
        dumps.removeAll()
    }

    private func capture(
        _ dump: CapturedDump,
        onFailure: @escaping @MainActor @Sendable (_ message: String, _ report: Bool) -> Void
    ) async {
        do {
            try await store.save(dump)
            guard !Task.isCancelled, !didShutdown else { return }
            dumps.insert(dump, at: 0)
        } catch {
            onFailure("Dump storage failed: " + error.localizedDescription, true)
        }
    }
}
