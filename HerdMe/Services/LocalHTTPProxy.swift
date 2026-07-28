import Foundation
import Network
import Security

final class LocalHTTPProxy: @unchecked Sendable {
    private let queue = DispatchQueue(label: "app.herdme.http-proxy", qos: .userInitiated)
    private let sessionsLock = NSLock()
    private let routesLock = NSLock()
    private var listener: NWListener?
    private var sessions: [UUID: HTTPProxySession] = [:]
    private var routes: [String: Int] = [:]
    private(set) var port: Int?

    var isRunning: Bool { listener != nil }

    var isHealthy: Bool {
        guard listener != nil, let port else { return false }
        return LocalEnvironmentEngine.canConnect(port: port)
    }

    func start(
        routes: [String: Int],
        identity: SecIdentity? = nil,
        preferredPort: Int = 80,
        fallbackPort: Int = 8_080
    ) throws -> Int {
        if let port, listener != nil {
            replaceRoutes(with: routes)
            return port
        }

        let selectedPort: Int?
        if LocalEnvironmentEngine.canBind(port: preferredPort) {
            selectedPort = preferredPort
        } else {
            selectedPort = LocalEnvironmentEngine.availablePort(startingAt: fallbackPort)
        }
        guard let selectedPort,
            let rawPort = UInt16(exactly: selectedPort),
            let networkPort = NWEndpoint.Port(rawValue: rawPort)
        else {
            throw LocalEnvironmentError.noAvailablePort
        }

        replaceRoutes(with: routes)
        let isSecure = identity != nil
        let parameters: NWParameters
        if let identity {
            let tlsOptions = NWProtocolTLS.Options()
            guard let securityIdentity = sec_identity_create(identity) else {
                throw LocalCertificateError.identityMissing
            }
            sec_protocol_options_set_local_identity(tlsOptions.securityProtocolOptions, securityIdentity)
            sec_protocol_options_set_min_tls_protocol_version(tlsOptions.securityProtocolOptions, .TLSv12)
            parameters = NWParameters(tls: tlsOptions, tcp: NWProtocolTCP.Options())
        } else {
            parameters = .tcp
        }
        parameters.allowLocalEndpointReuse = true
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: networkPort)
        let listener = try NWListener(using: parameters)
        listener.newConnectionHandler = { [weak self] connection in
            guard let self else {
                connection.cancel()
                return
            }
            let id = UUID()
            let session = HTTPProxySession(
                incoming: connection,
                secure: isSecure,
                route: { [weak self] host in self?.route(for: host) },
                onStop: { [weak self] in self?.removeSession(id) }
            )
            self.sessionsLock.lock()
            self.sessions[id] = session
            self.sessionsLock.unlock()
            session.start()
        }
        listener.stateUpdateHandler = { state in
            if case .failed(let error) = state {
                NSLog("HerdMe HTTP proxy failed: %@", error.localizedDescription)
            }
        }
        listener.start(queue: queue)
        self.listener = listener
        port = selectedPort
        return selectedPort
    }

    func update(routes: [String: Int]) {
        queue.async { [weak self] in self?.replaceRoutes(with: routes) }
    }

    func stop() {
        listener?.cancel()
        listener = nil
        sessionsLock.lock()
        let activeSessions = Array(sessions.values)
        sessions.removeAll()
        sessionsLock.unlock()
        for session in activeSessions { session.stop() }
        routesLock.lock()
        routes.removeAll()
        routesLock.unlock()
        port = nil
    }

    private func removeSession(_ id: UUID) {
        sessionsLock.lock()
        sessions.removeValue(forKey: id)
        sessionsLock.unlock()
    }

    private func route(for host: String) -> Int? {
        routesLock.lock()
        defer { routesLock.unlock() }
        return routes[host]
    }

    private func replaceRoutes(with routes: [String: Int]) {
        let normalized = Self.normalized(routes)
        routesLock.lock()
        self.routes = normalized
        routesLock.unlock()
    }

    nonisolated static func host(in request: Data) -> String? {
        guard let headers = String(data: request, encoding: .utf8) else { return nil }
        for line in headers.components(separatedBy: "\r\n") {
            guard let separator = line.firstIndex(of: ":") else { continue }
            let name = line[..<separator].trimmingCharacters(in: .whitespacesAndNewlines)
            guard name.caseInsensitiveCompare("Host") == .orderedSame else { continue }
            let value = line[line.index(after: separator)...]
                .trimmingCharacters(in: .whitespacesAndNewlines)
                .lowercased()
            guard !value.isEmpty else { return nil }
            return value.split(separator: ":", maxSplits: 1).first.map(String.init)
        }
        return nil
    }

    nonisolated static func addingForwardedHeaders(to request: Data, secure: Bool) -> Data {
        let delimiter = Data("\r\n\r\n".utf8)
        guard let range = request.range(of: delimiter),
            let headerText = String(data: request[..<range.lowerBound], encoding: .utf8)
        else {
            return request
        }
        var lines = headerText.components(separatedBy: "\r\n")
        lines.removeAll { line in
            guard let separator = line.firstIndex(of: ":") else { return false }
            let name = line[..<separator].trimmingCharacters(in: .whitespacesAndNewlines)
            return name.caseInsensitiveCompare("X-Forwarded-Proto") == .orderedSame
                || name.caseInsensitiveCompare("X-Forwarded-For") == .orderedSame
        }
        lines.append("X-Forwarded-Proto: " + (secure ? "https" : "http"))
        lines.append("X-Forwarded-For: 127.0.0.1")
        var forwarded = Data((lines.joined(separator: "\r\n") + "\r\n\r\n").utf8)
        forwarded.append(request[range.upperBound...])
        return forwarded
    }

    nonisolated private static func normalized(_ routes: [String: Int]) -> [String: Int] {
        routes.reduce(into: [:]) { result, route in
            result[route.key.lowercased()] = route.value
        }
    }
}

private final class HTTPProxySession: @unchecked Sendable {
    private let incoming: NWConnection
    private let queue = DispatchQueue(label: "app.herdme.http-proxy.session")
    private let secure: Bool
    private let route: @Sendable (String) -> Int?
    private let onStop: @Sendable () -> Void
    private var upstream: NWConnection?
    private var requestBuffer = Data()
    private var stopped = false

    init(
        incoming: NWConnection,
        secure: Bool,
        route: @escaping @Sendable (String) -> Int?,
        onStop: @escaping @Sendable () -> Void
    ) {
        self.incoming = incoming
        self.secure = secure
        self.route = route
        self.onStop = onStop
    }

    func start() {
        incoming.stateUpdateHandler = { [weak self] state in
            switch state {
            case .ready:
                self?.readRequest()
            case .failed, .cancelled:
                self?.stop()
            default:
                break
            }
        }
        incoming.start(queue: queue)
    }

    func stop() {
        guard !stopped else { return }
        stopped = true
        incoming.cancel()
        upstream?.cancel()
        upstream = nil
        onStop()
    }

    private func readRequest() {
        incoming.receive(minimumIncompleteLength: 1, maximumLength: 65_536) { [weak self] data, _, complete, error in
            guard let self, !self.stopped else { return }
            if let data { self.requestBuffer.append(data) }
            if self.requestBuffer.count > 1_048_576 {
                self.sendError(status: "431 Request Header Fields Too Large")
                return
            }
            if self.requestBuffer.range(of: Data("\r\n\r\n".utf8)) != nil {
                self.connectUpstream()
            } else if complete || error != nil {
                self.stop()
            } else {
                self.readRequest()
            }
        }
    }

    private func connectUpstream() {
        guard let host = LocalHTTPProxy.host(in: requestBuffer), let backendPort = route(host),
            let rawPort = UInt16(exactly: backendPort),
            let port = NWEndpoint.Port(rawValue: rawPort)
        else {
            sendError(status: "404 Not Found")
            return
        }

        let upstream = NWConnection(host: "127.0.0.1", port: port, using: .tcp)
        self.upstream = upstream
        upstream.stateUpdateHandler = { [weak self] state in
            guard let self, !self.stopped else { return }
            switch state {
            case .ready:
                let request = LocalHTTPProxy.addingForwardedHeaders(
                    to: self.requestBuffer,
                    secure: self.secure
                )
                self.requestBuffer.removeAll(keepingCapacity: false)
                upstream.send(
                    content: request,
                    completion: .contentProcessed { [weak self] error in
                        guard let self else { return }
                        if error != nil {
                            self.sendError(status: "502 Bad Gateway")
                        } else {
                            self.forward(from: self.incoming, to: upstream)
                            self.forward(from: upstream, to: self.incoming)
                        }
                    })
            case .failed:
                self.sendError(status: "502 Bad Gateway")
            case .cancelled:
                self.stop()
            default:
                break
            }
        }
        upstream.start(queue: queue)
    }

    private func forward(from source: NWConnection, to destination: NWConnection) {
        source.receive(minimumIncompleteLength: 1, maximumLength: 65_536) { [weak self] data, _, complete, error in
            guard let self, !self.stopped else { return }
            if let data, !data.isEmpty {
                destination.send(
                    content: data, isComplete: complete,
                    completion: .contentProcessed { [weak self] sendError in
                        guard let self else { return }
                        if complete || error != nil || sendError != nil {
                            self.stop()
                        } else {
                            self.forward(from: source, to: destination)
                        }
                    })
            } else if complete || error != nil {
                self.stop()
            } else {
                self.forward(from: source, to: destination)
            }
        }
    }

    private func sendError(status: String) {
        let body = "HerdMe could not route this local site.\n"
        let response =
            "HTTP/1.1 \(status)\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: \(body.utf8.count)\r\nConnection: close\r\n\r\n\(body)"
        incoming.send(
            content: Data(response.utf8), isComplete: true,
            completion: .contentProcessed { [weak self] _ in
                self?.stop()
            })
    }
}
