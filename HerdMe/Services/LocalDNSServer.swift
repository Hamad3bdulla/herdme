import Foundation
import Network

final class LocalDNSServer: @unchecked Sendable {
    private let queue = DispatchQueue(label: "app.herdme.dns", qos: .userInitiated)
    private var listener: NWListener?
    private var sessions: [UUID: DNSSession] = [:]

    var isRunning: Bool { listener != nil }

    func start(
        port: Int,
        tld: String,
        onStateChange: @escaping @Sendable (Bool, String?) -> Void
    ) throws {
        guard listener == nil else { return }
        guard let rawPort = UInt16(exactly: port), rawPort > 0,
              let networkPort = NWEndpoint.Port(rawValue: rawPort) else {
            throw LocalListenerError.invalidPort(service: "DNS")
        }
        let parameters = NWParameters.udp
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: networkPort)
        let listener = try NWListener(using: parameters)
        listener.newConnectionHandler = { [weak self] connection in
            guard let self else {
                connection.cancel()
                return
            }
            let id = UUID()
            let session = DNSSession(connection: connection, tld: tld) { [weak self] in
                self?.removeSession(id)
            }
            self.sessions[id] = session
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
        sessions.values.forEach { $0.stop() }
        sessions.removeAll()
    }

    private func removeSession(_ id: UUID) {
        queue.async { [weak self] in self?.sessions[id] = nil }
    }
}

private final class DNSSession: @unchecked Sendable {
    private let connection: NWConnection
    private let queue = DispatchQueue(label: "app.herdme.dns.session")
    private let tld: String
    private let onStop: @Sendable () -> Void

    init(connection: NWConnection, tld: String, onStop: @escaping @Sendable () -> Void) {
        self.connection = connection
        self.tld = tld
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
    }

    private func receive() {
        connection.receiveMessage { [weak self] data, _, _, error in
            guard let self else { return }
            guard error == nil, let data, let response = DNSMessage.response(to: data, tld: self.tld) else {
                self.connection.cancel()
                return
            }
            self.connection.send(content: response, completion: .contentProcessed { [weak self] _ in
                self?.receive()
            })
        }
    }
}

enum DNSMessage {
    static func response(to query: Data, tld: String) -> Data? {
        let bytes = [UInt8](query)
        guard bytes.count >= 17, bytes[2] & 0x80 == 0 else { return nil }
        var index = 12
        var labels: [String] = []
        while index < bytes.count {
            let length = Int(bytes[index])
            index += 1
            if length == 0 { break }
            guard length <= 63, index + length <= bytes.count else { return nil }
            labels.append(String(decoding: bytes[index..<(index + length)], as: UTF8.self))
            index += length
        }
        guard index + 4 <= bytes.count, !labels.isEmpty else { return nil }
        let questionEnd = index + 4
        let queryType = UInt16(bytes[index]) << 8 | UInt16(bytes[index + 1])
        let queryClass = UInt16(bytes[index + 2]) << 8 | UInt16(bytes[index + 3])
        let name = labels.joined(separator: ".").lowercased()
        let normalizedTLD = tld.lowercased()
        let matchesTLD = name == normalizedTLD || name.hasSuffix("." + normalizedTLD)
        let supportsType = queryType == 1 || queryType == 28
        let hasAnswer = matchesTLD && supportsType && queryClass == 1

        var response = Data([bytes[0], bytes[1], 0x81, matchesTLD ? 0x80 : 0x83])
        response.append(contentsOf: [0x00, 0x01, 0x00, hasAnswer ? 0x01 : 0x00, 0x00, 0x00, 0x00, 0x00])
        response.append(contentsOf: bytes[12..<questionEnd])
        guard hasAnswer else { return response }

        response.append(contentsOf: [0xC0, 0x0C, bytes[index], bytes[index + 1], 0x00, 0x01])
        response.append(contentsOf: [0x00, 0x00, 0x00, 0x01])
        if queryType == 1 {
            response.append(contentsOf: [0x00, 0x04, 127, 0, 0, 1])
        } else {
            response.append(contentsOf: [0x00, 0x10] + Array(repeating: 0, count: 15) + [1])
        }
        return response
    }
}
