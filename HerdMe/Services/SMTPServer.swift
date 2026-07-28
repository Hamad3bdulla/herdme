import Foundation
import Network

final class SMTPServer: @unchecked Sendable {
    private let queue = DispatchQueue(label: "app.herdme.smtp", qos: .userInitiated)
    private let sessionsLock = NSLock()
    private var listener: NWListener?
    private var sessions: [UUID: SMTPSession] = [:]

    var isRunning: Bool { listener != nil }

    func start(
        port: Int,
        onStateChange: @escaping @Sendable (Bool, String?) -> Void,
        onMessage: @escaping @Sendable (CapturedMail) -> Void
    ) throws {
        guard listener == nil else { return }
        guard let rawPort = UInt16(exactly: port), rawPort > 0,
            let networkPort = NWEndpoint.Port(rawValue: rawPort)
        else {
            throw LocalListenerError.invalidPort(service: "SMTP")
        }
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: networkPort)
        let listener = try NWListener(using: parameters)
        listener.newConnectionHandler = { [weak self] connection in
            guard let self else { return }
            let identifier = UUID()
            let session = SMTPSession(
                connection: connection,
                onMessage: onMessage,
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
            case .failed(let error):
                NSLog("HerdMe SMTP listener failed: %@", error.localizedDescription)
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
        for session in activeSessions { session.stop() }
    }

    private func removeSession(_ identifier: UUID) {
        sessionsLock.lock()
        sessions.removeValue(forKey: identifier)
        sessionsLock.unlock()
    }
}

struct SMTPMessageBuffer {
    static let maximumBytes = 50 * 1_024 * 1_024

    private let limit: Int
    private(set) var byteCount = 0
    private(set) var isTooLarge = false
    private var lines: [String] = []

    init(maximumBytes: Int = Self.maximumBytes) {
        limit = max(maximumBytes, 0)
    }

    mutating func append(_ line: String) -> Bool {
        let lineBytes = line.utf8.count + 2
        guard !isTooLarge,
            lineBytes <= limit,
            byteCount <= limit - lineBytes
        else {
            isTooLarge = true
            return false
        }
        lines.append(line)
        byteCount += lineBytes
        return true
    }

    mutating func reset() {
        byteCount = 0
        isTooLarge = false
        lines.removeAll(keepingCapacity: true)
    }

    var rawMessage: String {
        lines.map { $0.hasPrefix("..") ? String($0.dropFirst()) : $0 }
            .joined(separator: "\r\n")
    }
}

private final class SMTPSession: @unchecked Sendable {
    private static let maximumLineBytes = 1 * 1_024 * 1_024

    private let connection: NWConnection
    private let queue = DispatchQueue(label: "app.herdme.smtp.session")
    private let onMessage: @Sendable (CapturedMail) -> Void
    private let onStop: @Sendable () -> Void
    private var buffer = Data()
    private var sender = "Unknown sender"
    private var recipients: [String] = []
    private var messageBuffer = SMTPMessageBuffer()
    private var readingData = false

    init(
        connection: NWConnection,
        onMessage: @escaping @Sendable (CapturedMail) -> Void,
        onStop: @escaping @Sendable () -> Void
    ) {
        self.connection = connection
        self.onMessage = onMessage
        self.onStop = onStop
    }

    func start() {
        connection.stateUpdateHandler = { [weak self] state in
            switch state {
            case .ready:
                self?.send("220 HerdMe SMTP ready\r\n")
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
        connection.receive(minimumIncompleteLength: 1, maximumLength: 65_536) { [weak self] data, _, complete, error in
            guard let self else { return }
            if let data { self.buffer.append(data) }
            self.processBuffer()
            if self.buffer.count > Self.maximumLineBytes {
                self.connection.cancel()
                self.onStop()
                return
            }
            if complete || error != nil {
                self.connection.cancel()
                self.onStop()
            } else {
                self.receive()
            }
        }
    }

    private func processBuffer() {
        let delimiter = Data("\r\n".utf8)
        while let range = buffer.range(of: delimiter) {
            let lineData = buffer.subdata(in: buffer.startIndex..<range.lowerBound)
            buffer.removeSubrange(buffer.startIndex..<range.upperBound)
            handle(String(data: lineData, encoding: .utf8) ?? "")
        }
    }

    private func handle(_ line: String) {
        if readingData {
            if line == "." {
                readingData = false
                if messageBuffer.isTooLarge {
                    send("552 5.3.4 Message size exceeds fixed maximum message size\r\n")
                } else {
                    let message = CapturedMail.parse(
                        sender: sender,
                        recipients: recipients,
                        raw: messageBuffer.rawMessage
                    )
                    onMessage(message)
                    send("250 2.0.0 Message accepted\r\n")
                }
                messageBuffer.reset()
            } else {
                _ = messageBuffer.append(line)
            }
            return
        }

        let uppercased = line.uppercased()
        if uppercased.hasPrefix("EHLO") || uppercased.hasPrefix("HELO") {
            send("250-HerdMe\r\n250-8BITMIME\r\n250 SIZE 52428800\r\n")
        } else if uppercased.hasPrefix("MAIL FROM:") {
            sender = Self.address(from: line)
            recipients.removeAll()
            send("250 2.1.0 Sender accepted\r\n")
        } else if uppercased.hasPrefix("RCPT TO:") {
            recipients.append(Self.address(from: line))
            send("250 2.1.5 Recipient accepted\r\n")
        } else if uppercased == "DATA" {
            readingData = true
            messageBuffer.reset()
            send("354 End data with <CR><LF>.<CR><LF>\r\n")
        } else if uppercased == "RSET" {
            sender = "Unknown sender"
            recipients.removeAll()
            messageBuffer.reset()
            readingData = false
            send("250 2.0.0 Reset\r\n")
        } else if uppercased == "NOOP" {
            send("250 2.0.0 OK\r\n")
        } else if uppercased == "QUIT" {
            connection.send(
                content: Data("221 2.0.0 Bye\r\n".utf8),
                completion: .contentProcessed { [weak self] _ in
                    self?.connection.cancel()
                })
        } else {
            send("502 5.5.1 Command not implemented\r\n")
        }
    }

    private func send(_ text: String) {
        connection.send(content: Data(text.utf8), completion: .contentProcessed { _ in })
    }

    private static func address(from command: String) -> String {
        guard let colon = command.firstIndex(of: ":") else { return command }
        return command[command.index(after: colon)...]
            .trimmingCharacters(in: CharacterSet(charactersIn: " <>"))
    }
}
