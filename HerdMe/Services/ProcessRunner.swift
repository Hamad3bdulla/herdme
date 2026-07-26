import Darwin
import Foundation

struct ProcessResult: Sendable {
    let status: Int32
    let output: String
}

enum ProcessRunnerError: LocalizedError {
    case timedOut(seconds: TimeInterval, output: String)
    case cancelled(output: String)

    var errorDescription: String? {
        switch self {
        case let .timedOut(seconds, output):
            let summary = output.trimmingCharacters(in: .whitespacesAndNewlines)
            let message = "The command timed out after \(Int(seconds)) seconds."
            return summary.isEmpty ? message : message + "\n" + summary
        case .cancelled:
            return "The command was cancelled."
        }
    }
}

enum ProcessRunner {
    static func run(
        _ executable: URL,
        arguments: [String] = [],
        currentDirectory: URL? = nil,
        environment: [String: String]? = nil,
        standardInput: Data? = nil,
        timeout: TimeInterval = 15 * 60,
        cancellationRequested: @Sendable () -> Bool = { false }
    ) throws -> ProcessResult {
        if cancellationRequested() {
            throw ProcessRunnerError.cancelled(output: "")
        }
        let process = Process()
        let pipe = Pipe()
        let capture = ProcessOutputCapture()
        let readerGroup = DispatchGroup()
        let writerGroup = DispatchGroup()
        let inputFailure = ProcessInputFailure()
        let termination = DispatchSemaphore(value: 0)

        process.executableURL = executable
        process.arguments = arguments
        process.currentDirectoryURL = currentDirectory
        if let environment { process.environment = environment }
        process.standardOutput = pipe
        process.standardError = pipe
        let inputPipe = standardInput.map { _ in Pipe() }
        process.standardInput = inputPipe
        process.terminationHandler = { _ in termination.signal() }

        try process.run()
        readerGroup.enter()
        DispatchQueue.global(qos: .userInitiated).async {
            while let chunk = try? pipe.fileHandleForReading.read(upToCount: 65_536),
                  !chunk.isEmpty {
                capture.append(chunk)
            }
            readerGroup.leave()
        }
        if let standardInput, let inputPipe {
            writerGroup.enter()
            DispatchQueue.global(qos: .userInitiated).async {
                do {
                    try inputPipe.fileHandleForWriting.write(contentsOf: standardInput)
                } catch {
                    inputFailure.store(error)
                }
                try? inputPipe.fileHandleForWriting.close()
                writerGroup.leave()
            }
        }

        let startedAt = Date.timeIntervalSinceReferenceDate
        while termination.wait(timeout: .now() + .milliseconds(100)) != .success {
            if cancellationRequested() {
                stop(process, termination: termination)
                _ = writerGroup.wait(timeout: .now() + 2)
                _ = readerGroup.wait(timeout: .now() + 2)
                throw ProcessRunnerError.cancelled(output: capture.string)
            }
            if Date.timeIntervalSinceReferenceDate - startedAt >= timeout {
                stop(process, termination: termination)
                _ = writerGroup.wait(timeout: .now() + 2)
                _ = readerGroup.wait(timeout: .now() + 2)
                throw ProcessRunnerError.timedOut(seconds: timeout, output: capture.string)
            }
        }

        writerGroup.wait()
        readerGroup.wait()
        if let error = inputFailure.error { throw error }
        return ProcessResult(status: process.terminationStatus, output: capture.string)
    }

    private static func stop(_ process: Process, termination: DispatchSemaphore) {
        let descendants = descendantProcessIdentifiers(of: process.processIdentifier)
        descendants.forEach { kill($0, SIGTERM) }
        if process.isRunning { process.terminate() }
        if termination.wait(timeout: .now() + 1) == .timedOut {
            descendants.forEach { kill($0, SIGKILL) }
            if process.isRunning { kill(process.processIdentifier, SIGKILL) }
            _ = termination.wait(timeout: .now() + 1)
        }
    }

    private static func descendantProcessIdentifiers(of root: pid_t) -> [pid_t] {
        var visited = Set<pid_t>()
        var ordered: [pid_t] = []

        func collect(_ parent: pid_t) {
            for child in childProcessIdentifiers(of: parent) where visited.insert(child).inserted {
                collect(child)
                ordered.append(child)
            }
        }

        collect(root)
        return ordered
    }

    private static func childProcessIdentifiers(of parent: pid_t) -> [pid_t] {
        let requiredBytes = proc_listchildpids(parent, nil, 0)
        guard requiredBytes > 0 else { return [] }
        var identifiers = [pid_t](
            repeating: 0,
            count: Int(requiredBytes) / MemoryLayout<pid_t>.stride
        )
        let writtenBytes = identifiers.withUnsafeMutableBytes { buffer in
            proc_listchildpids(parent, buffer.baseAddress, Int32(buffer.count))
        }
        guard writtenBytes > 0 else { return [] }
        return identifiers.prefix(Int(writtenBytes) / MemoryLayout<pid_t>.stride).filter { $0 > 0 }
    }
}

private final class ProcessInputFailure: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Error?

    var error: Error? {
        lock.lock()
        defer { lock.unlock() }
        return value
    }

    func store(_ error: Error) {
        lock.lock()
        value = error
        lock.unlock()
    }
}

private final class ProcessOutputCapture: @unchecked Sendable {
    private static let maximumBytes = 16 * 1_024 * 1_024
    private let lock = NSLock()
    private var data = Data()

    var string: String {
        lock.lock()
        defer { lock.unlock() }
        return String(decoding: data, as: UTF8.self)
    }

    func append(_ value: Data) {
        lock.lock()
        if value.count >= Self.maximumBytes {
            data = Data(value.suffix(Self.maximumBytes))
        } else {
            let overflow = data.count + value.count - Self.maximumBytes
            if overflow > 0 { data.removeFirst(overflow) }
            data.append(value)
        }
        lock.unlock()
    }
}
