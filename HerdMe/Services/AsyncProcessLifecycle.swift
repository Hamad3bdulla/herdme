import Darwin
import Foundation

actor AsyncOperationGate {
    private var isLocked = false
    private var waiters: [CheckedContinuation<Void, Never>] = []

    func acquire() async {
        if !isLocked {
            isLocked = true
            return
        }
        await withCheckedContinuation { continuation in
            waiters.append(continuation)
        }
    }

    func release() {
        guard !waiters.isEmpty else {
            isLocked = false
            return
        }
        waiters.removeFirst().resume()
    }
}

final class ApplicationTaskRegistry: @unchecked Sendable {
    private struct Entry {
        let name: String
        let task: Task<Void, Never>
    }

    private let lock = NSLock()
    private var tasks: [UUID: Entry] = [:]
    private var isAcceptingNewTasks = true

    var acceptsNewTasks: Bool {
        lock.withLock { isAcceptingNewTasks }
    }

    var activeCount: Int {
        lock.withLock { tasks.count }
    }

    var activeTaskNames: [String] {
        lock.withLock { tasks.values.map(\.name).sorted() }
    }

    @discardableResult
    func start(
        name: String = #function,
        priority: TaskPriority? = nil,
        operation: @escaping @MainActor @Sendable () async -> Void
    ) -> Bool {
        lock.lock()
        guard isAcceptingNewTasks else {
            lock.unlock()
            return false
        }
        let identifier = UUID()
        let task = Task(priority: priority) { @MainActor [weak self] in
            guard !Task.isCancelled else {
                self?.finish(identifier)
                return
            }
            await operation()
            self?.finish(identifier)
        }
        tasks[identifier] = Entry(name: name, task: task)
        lock.unlock()
        return true
    }

    func cancelAllImmediately() {
        let activeTasks = lock.withLock {
            isAcceptingNewTasks = false
            return tasks.values.map(\.task)
        }
        for task in activeTasks { task.cancel() }
    }

    func cancelAllAndWait(timeout: Duration = .seconds(5)) async -> Bool {
        await cancelAllAndWaitReporting(timeout: timeout).isEmpty
    }

    func cancelAllAndWaitReporting(timeout: Duration = .seconds(5)) async -> [String] {
        cancelAllImmediately()
        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: max(timeout, .zero))
        while activeCount > 0, clock.now < deadline {
            if Task.isCancelled { break }
            try? await Task.sleep(for: .milliseconds(20))
        }
        return activeTaskNames
    }

    private func finish(_ identifier: UUID) {
        lock.withLock { tasks[identifier] = nil }
    }
}

enum AsyncProcessLifecycle {
    static func runDetached<T: Sendable>(
        priority: TaskPriority? = nil,
        operation: @escaping @Sendable () throws -> T
    ) async throws -> T {
        let task = Task.detached(priority: priority, operation: operation)
        return try await withTaskCancellationHandler {
            try await task.value
        } onCancel: {
            task.cancel()
        }
    }

    static func waitUntil(
        timeout: TimeInterval,
        interval: Duration,
        condition: @escaping @Sendable () -> Bool
    ) async throws -> Bool {
        let deadline = ProcessInfo.processInfo.systemUptime + max(0, timeout)
        while true {
            try Task.checkCancellation()
            if condition() { return true }
            if ProcessInfo.processInfo.systemUptime >= deadline { return false }
            try await Task.sleep(for: interval)
        }
    }

    static func terminate(_ process: Process, gracePeriod: TimeInterval = 1) async {
        guard process.isRunning else { return }
        process.terminate()
        let exited =
            (try? await waitUntil(
                timeout: gracePeriod,
                interval: .milliseconds(25),
                condition: { !process.isRunning }
            )) ?? false
        if !exited, process.isRunning {
            Darwin.kill(process.processIdentifier, SIGKILL)
        }
    }

    static func terminate(pid: pid_t, gracePeriod: TimeInterval = 1) async {
        guard isAlive(pid: pid) else { return }
        Darwin.kill(pid, SIGTERM)
        let exited =
            (try? await waitUntil(
                timeout: gracePeriod,
                interval: .milliseconds(25),
                condition: { !isAlive(pid: pid) }
            )) ?? false
        if !exited, isAlive(pid: pid) {
            Darwin.kill(pid, SIGKILL)
        }
    }

    static func terminateImmediately(_ process: Process) {
        guard process.isRunning else { return }
        process.terminate()
    }

    static func terminateImmediately(pid: pid_t) {
        guard isAlive(pid: pid) else { return }
        Darwin.kill(pid, SIGTERM)
    }

    private static func isAlive(pid: pid_t) -> Bool {
        Darwin.kill(pid, 0) == 0
    }
}
