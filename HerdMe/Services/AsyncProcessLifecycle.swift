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

enum AsyncProcessLifecycle {
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
        let exited = (try? await waitUntil(
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
        let exited = (try? await waitUntil(
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
