import Darwin
import Foundation

struct ProcessResult: Sendable {
    let status: Int32
    let output: String
}

enum ProcessRunnerError: LocalizedError {
    case timedOut(seconds: TimeInterval, output: String)

    var errorDescription: String? {
        switch self {
        case let .timedOut(seconds, output):
            let summary = output.trimmingCharacters(in: .whitespacesAndNewlines)
            let message = "The command timed out after \(Int(seconds)) seconds."
            return summary.isEmpty ? message : message + "\n" + summary
        }
    }
}

enum ProcessRunner {
    static func run(
        _ executable: URL,
        arguments: [String] = [],
        currentDirectory: URL? = nil,
        environment: [String: String]? = nil,
        timeout: TimeInterval = 15 * 60
    ) throws -> ProcessResult {
        let process = Process()
        let pipe = Pipe()
        let capture = ProcessOutputCapture()
        let readerGroup = DispatchGroup()
        let termination = DispatchSemaphore(value: 0)

        process.executableURL = executable
        process.arguments = arguments
        process.currentDirectoryURL = currentDirectory
        if let environment { process.environment = environment }
        process.standardOutput = pipe
        process.standardError = pipe
        process.terminationHandler = { _ in termination.signal() }

        try process.run()
        readerGroup.enter()
        DispatchQueue.global(qos: .utility).async {
            while let chunk = try? pipe.fileHandleForReading.read(upToCount: 65_536),
                  !chunk.isEmpty {
                capture.append(chunk)
            }
            readerGroup.leave()
        }

        guard termination.wait(timeout: .now() + timeout) == .success else {
            process.terminate()
            if termination.wait(timeout: .now() + 2) == .timedOut, process.isRunning {
                kill(process.processIdentifier, SIGKILL)
                _ = termination.wait(timeout: .now() + 2)
            }
            _ = readerGroup.wait(timeout: .now() + 2)
            throw ProcessRunnerError.timedOut(seconds: timeout, output: capture.string)
        }

        readerGroup.wait()
        return ProcessResult(status: process.terminationStatus, output: capture.string)
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
