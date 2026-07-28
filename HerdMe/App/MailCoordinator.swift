import Combine
import Foundation

@MainActor
final class MailCoordinator: ObservableObject {
    @Published private(set) var messages: [CapturedMailSummary] = []
    @Published private(set) var isServerRunning = false

    private let store: MailStore
    private nonisolated let server: SMTPServer
    private var didShutdown = false

    init(rootURL: URL, server: SMTPServer = SMTPServer()) {
        store = MailStore(rootURL: rootURL)
        self.server = server
    }

    func load() async {
        let loadedMessages = await store.loadSummaries()
        guard !Task.isCancelled, !didShutdown else { return }
        messages = loadedMessages
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
                            onFailure("SMTP server failed: " + errorMessage, reportErrors)
                        }
                    }
                },
                onMessage: { [weak self] message in
                    Task { @MainActor in
                        guard let self, !self.didShutdown else { return }
                        await self.capture(message, onFailure: onFailure)
                    }
                }
            )
        } catch {
            isServerRunning = false
            onFailure("SMTP server failed: " + error.localizedDescription, reportErrors)
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

    func message(id: CapturedMail.ID) async throws -> CapturedMail {
        try await store.message(id: id)
    }

    func delete(id: CapturedMail.ID) async throws {
        try await store.delete(id: id)
        guard !Task.isCancelled, !didShutdown else { return }
        messages.removeAll { $0.id == id }
    }

    func clear() async throws {
        try await store.clear()
        guard !Task.isCancelled, !didShutdown else { return }
        messages.removeAll()
    }

    private func capture(
        _ message: CapturedMail,
        onFailure: @escaping @MainActor @Sendable (_ message: String, _ report: Bool) -> Void
    ) async {
        do {
            try await store.save(message)
            guard !Task.isCancelled, !didShutdown else { return }
            messages.insert(message.summary, at: 0)
        } catch {
            onFailure("Mail storage failed: " + error.localizedDescription, true)
        }
    }
}
