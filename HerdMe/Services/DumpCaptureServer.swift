import Foundation
import Network

final class DumpCaptureServer: @unchecked Sendable {
    private let queue = DispatchQueue(label: "app.herdme.dumps", qos: .userInitiated)
    private let sessionsLock = NSLock()
    private var listener: NWListener?
    private var sessions: [UUID: DumpSession] = [:]

    var isRunning: Bool { listener != nil }

    func start(
        port: Int,
        onStateChange: @escaping @Sendable (Bool, String?) -> Void,
        onDump: @escaping @Sendable (CapturedDump) -> Void
    ) throws {
        guard listener == nil else { return }
        guard let rawPort = UInt16(exactly: port), rawPort > 0,
              let networkPort = NWEndpoint.Port(rawValue: rawPort) else {
            throw LocalListenerError.invalidPort(service: "dump listener")
        }
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: networkPort)
        let listener = try NWListener(using: parameters)
        listener.newConnectionHandler = { [weak self] connection in
            guard let self else { return }
            let identifier = UUID()
            let session = DumpSession(
                connection: connection,
                onDump: onDump,
                onStop: { [weak self] in self?.removeSession(identifier) }
            )
            self.sessionsLock.lock()
            self.sessions[identifier] = session
            self.sessionsLock.unlock()
            session.start()
        }
        listener.stateUpdateHandler = { [weak self] state in
            switch state {
            case .ready:
                onStateChange(true, nil)
            case let .failed(error):
                self?.listener = nil
                onStateChange(false, error.localizedDescription)
            case .cancelled:
                onStateChange(false, nil)
            default:
                break
            }
        }
        listener.start(queue: queue)
        self.listener = listener
    }

    func stop() {
        listener?.cancel()
        listener = nil
        sessionsLock.lock()
        let activeSessions = Array(sessions.values)
        sessions.removeAll()
        sessionsLock.unlock()
        activeSessions.forEach { $0.stop() }
    }

    private func removeSession(_ identifier: UUID) {
        sessionsLock.lock()
        sessions.removeValue(forKey: identifier)
        sessionsLock.unlock()
    }
}

struct DumpLineBuffer {
    static let maximumBytes = 4 * 1_024 * 1_024

    private var data = Data()

    mutating func append(_ chunk: Data) -> Bool {
        guard chunk.count <= Self.maximumBytes,
              data.count <= Self.maximumBytes - chunk.count else {
            return false
        }
        data.append(chunk)
        return true
    }

    mutating func nextLine() -> Data? {
        guard let range = data.range(of: Data("\n".utf8)) else { return nil }
        let line = data.subdata(in: data.startIndex..<range.lowerBound)
        data.removeSubrange(data.startIndex..<range.upperBound)
        return line
    }
}

private final class DumpSession: @unchecked Sendable {
    private let connection: NWConnection
    private let queue = DispatchQueue(label: "app.herdme.dumps.session")
    private let onDump: @Sendable (CapturedDump) -> Void
    private let onStop: @Sendable () -> Void
    private var buffer = DumpLineBuffer()

    init(
        connection: NWConnection,
        onDump: @escaping @Sendable (CapturedDump) -> Void,
        onStop: @escaping @Sendable () -> Void
    ) {
        self.connection = connection
        self.onDump = onDump
        self.onStop = onStop
    }

    func start() {
        connection.stateUpdateHandler = { [weak self] state in
            switch state {
            case .ready:
                self?.receive()
            case .failed, .cancelled:
                self?.onStop()
            default:
                break
            }
        }
        connection.start(queue: queue)
    }

    func stop() {
        connection.cancel()
        onStop()
    }

    private func receive() {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 1_048_576) { [weak self] data, _, complete, error in
            guard let self else { return }
            if let data, !self.buffer.append(data) {
                self.connection.cancel()
                self.onStop()
                return
            }
            self.processBuffer()
            if complete || error != nil {
                self.connection.cancel()
                self.onStop()
            } else {
                self.receive()
            }
        }
    }

    private func processBuffer() {
        while let lineData = buffer.nextLine() {
            let payload = (String(data: lineData, encoding: .utf8) ?? "")
                .trimmingCharacters(in: .whitespacesAndNewlines)
            if !payload.isEmpty { onDump(CapturedDump.decode(payload: payload)) }
        }
    }
}
