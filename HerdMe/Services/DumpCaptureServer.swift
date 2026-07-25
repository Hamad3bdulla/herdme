import Foundation
import Network

final class DumpCaptureServer: @unchecked Sendable {
    private let queue = DispatchQueue(label: "app.herdme.dumps", qos: .userInitiated)
    private var listener: NWListener?
    private var sessions: [DumpSession] = []

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
            let session = DumpSession(connection: connection, onDump: onDump)
            self?.sessions.append(session)
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
        sessions.forEach { $0.stop() }
        sessions.removeAll()
    }
}

private final class DumpSession: @unchecked Sendable {
    private let connection: NWConnection
    private let queue = DispatchQueue(label: "app.herdme.dumps.session")
    private let onDump: @Sendable (CapturedDump) -> Void
    private var buffer = Data()

    init(connection: NWConnection, onDump: @escaping @Sendable (CapturedDump) -> Void) {
        self.connection = connection
        self.onDump = onDump
    }

    func start() {
        connection.stateUpdateHandler = { [weak self] state in
            if case .ready = state { self?.receive() }
        }
        connection.start(queue: queue)
    }

    func stop() {
        connection.cancel()
    }

    private func receive() {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 1_048_576) { [weak self] data, _, complete, error in
            guard let self else { return }
            if let data { self.buffer.append(data) }
            self.processBuffer()
            if complete || error != nil {
                self.connection.cancel()
            } else {
                self.receive()
            }
        }
    }

    private func processBuffer() {
        let delimiter = Data("\n".utf8)
        while let range = buffer.range(of: delimiter) {
            let lineData = buffer.subdata(in: buffer.startIndex..<range.lowerBound)
            buffer.removeSubrange(buffer.startIndex..<range.upperBound)
            let payload = (String(data: lineData, encoding: .utf8) ?? "")
                .trimmingCharacters(in: .whitespacesAndNewlines)
            if !payload.isEmpty { onDump(CapturedDump.decode(payload: payload)) }
        }
    }
}
