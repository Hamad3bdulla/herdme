import Combine
import Foundation

@MainActor
final class ServicesCoordinator: ObservableObject {
    @Published private(set) var states: [UUID: ServiceRuntimeState] = [:]
    @Published private(set) var operation: UUID?
    @Published private(set) var outdatedDefinitionIDs: Set<String> = []

    private nonisolated let processManager: ServiceProcessManager
    private let credentialStore: ServiceCredentialStore
    private var didShutdown = false

    init(rootURL: URL, credentialStore: ServiceCredentialStore) {
        self.credentialStore = credentialStore
        processManager = ServiceProcessManager(
            rootURL: rootURL,
            credentialStore: credentialStore
        )
    }

    func beginOperation(for identifier: UUID) -> Bool {
        guard !didShutdown, operation == nil else { return false }
        operation = identifier
        return true
    }

    func endOperation(for identifier: UUID) {
        if operation == identifier { operation = nil }
    }

    func state(for instance: ServiceInstance) -> ServiceRuntimeState {
        states[instance.id] ?? processManager.state(for: instance)
    }

    func refreshStates(for instances: [ServiceInstance]) {
        states = Dictionary(
            uniqueKeysWithValues: instances.map { instance in
                (instance.id, processManager.state(for: instance))
            })
    }

    func removeState(for identifier: UUID) {
        states[identifier] = nil
    }

    func refreshUpdates() async {
        let identifiers = (try? await processManager.outdatedDefinitionIDs()) ?? []
        guard !Task.isCancelled, !didShutdown else { return }
        outdatedDefinitionIDs = identifiers
    }

    func isUpdateAvailable(for instance: ServiceInstance) -> Bool {
        state(for: instance) != .notInstalled
            && outdatedDefinitionIDs.contains(instance.definitionID)
    }

    func install(definitionID: String) async throws -> String {
        let version = try await processManager.install(definitionID: definitionID)
        guard !Task.isCancelled, !didShutdown else { return version }
        outdatedDefinitionIDs.remove(definitionID)
        return version
    }

    func start(
        _ instance: ServiceInstance,
        allowCredentialInteraction: Bool = true
    ) async throws {
        try await processManager.start(
            instance,
            allowCredentialInteraction: allowCredentialInteraction
        )
    }

    func stop(_ instance: ServiceInstance) async {
        await processManager.stop(instance)
    }

    func stopAll() async {
        await processManager.stopAll()
        operation = nil
    }

    func shutdown() async {
        beginShutdown()
        await stopAll()
    }

    func beginShutdown() {
        didShutdown = true
        operation = nil
        processManager.stopAllImmediately()
    }

    nonisolated func stopAllImmediately() {
        processManager.stopAllImmediately()
    }

    func dataDirectory(for instance: ServiceInstance) -> URL {
        processManager.dataDirectory(for: instance)
    }

    func consoleURL(for instance: ServiceInstance) -> URL? {
        processManager.consoleURL(for: instance)
    }

    func credentials(for identifier: UUID, allowInteraction: Bool = true) throws
        -> ServiceCredentials
    {
        try credentialStore.credentials(
            for: identifier,
            allowInteraction: allowInteraction
        )
    }

    func deleteCredentials(for identifier: UUID) throws {
        try credentialStore.delete(for: identifier)
    }
}
