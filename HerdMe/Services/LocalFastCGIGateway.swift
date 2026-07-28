import Darwin
import Foundation
import Network

final class LocalFastCGIGateway: @unchecked Sendable {
    private let documentRoot: URL
    private let fpmPort: Int
    private let queue = DispatchQueue(label: "app.herdme.fastcgi-gateway", qos: .userInitiated)
    private let sessionsLock = NSLock()
    private var listener: NWListener?
    private var sessions: [UUID: FastCGIHTTPSession] = [:]
    private(set) var port: Int?

    init(documentRoot: URL, fpmPort: Int) {
        self.documentRoot = documentRoot.standardizedFileURL.resolvingSymlinksInPath()
        self.fpmPort = fpmPort
    }

    var isRunning: Bool { listener != nil }

    var isHealthy: Bool {
        guard listener != nil, let port else { return false }
        return LocalEnvironmentEngine.canConnect(port: port)
    }

    func start(preferredPort: Int) throws -> Int {
        if let port, listener != nil { return port }
        guard let selectedPort = LocalEnvironmentEngine.availablePort(startingAt: preferredPort),
            let rawPort = UInt16(exactly: selectedPort),
            let networkPort = NWEndpoint.Port(rawValue: rawPort)
        else {
            throw LocalEnvironmentError.noAvailablePort
        }

        let parameters = NWParameters.tcp
        parameters.allowLocalEndpointReuse = true
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: networkPort)
        let listener = try NWListener(using: parameters)
        listener.newConnectionHandler = { [weak self] connection in
            guard let self else {
                connection.cancel()
                return
            }
            let identifier = UUID()
            let session = FastCGIHTTPSession(
                incoming: connection,
                handler: FastCGIHTTPHandler(documentRoot: self.documentRoot, fpmPort: self.fpmPort),
                onStop: { [weak self] in self?.removeSession(identifier) }
            )
            self.sessionsLock.lock()
            self.sessions[identifier] = session
            self.sessionsLock.unlock()
            session.start()
        }
        listener.stateUpdateHandler = { state in
            if case .failed(let error) = state {
                NSLog("HerdMe FastCGI gateway failed: %@", error.localizedDescription)
            }
        }
        listener.start(queue: queue)
        self.listener = listener
        port = selectedPort
        return selectedPort
    }

    func stop() {
        listener?.cancel()
        listener = nil
        sessionsLock.lock()
        let activeSessions = Array(sessions.values)
        sessions.removeAll()
        sessionsLock.unlock()
        for session in activeSessions { session.stop() }
        port = nil
    }

    private func removeSession(_ identifier: UUID) {
        sessionsLock.lock()
        sessions.removeValue(forKey: identifier)
        sessionsLock.unlock()
    }
}

private final class FastCGIHTTPSession: @unchecked Sendable {
    private static let fileChunkSize = 64 * 1_024
    private static let maximumPersistentRequests = 100
    private static let persistentIdleTimeout: DispatchTimeInterval = .seconds(5)
    private static let processingQueue = DispatchQueue(
        label: "app.herdme.fastcgi-gateway.processing",
        qos: .userInitiated,
        attributes: .concurrent
    )
    private let incoming: NWConnection
    private let handler: FastCGIHTTPHandler
    private let onStop: @Sendable () -> Void
    private let queue = DispatchQueue(label: "app.herdme.fastcgi-gateway.session", qos: .userInitiated)
    private var buffer = Data()
    private var stopped = false
    private var peerCompleted = false
    private var receiveGeneration = 0
    private var requestCount = 0
    private var responseFileDescriptor: Int32?

    init(
        incoming: NWConnection,
        handler: FastCGIHTTPHandler,
        onStop: @escaping @Sendable () -> Void
    ) {
        self.incoming = incoming
        self.handler = handler
        self.onStop = onStop
    }

    func start() {
        incoming.stateUpdateHandler = { [weak self] state in
            switch state {
            case .ready:
                self?.receiveRequest()
            case .failed, .cancelled:
                self?.stop()
            default:
                break
            }
        }
        incoming.start(queue: queue)
    }

    func stop() {
        queue.async { [weak self] in self?.stopOnQueue() }
    }

    private func stopOnQueue() {
        guard !stopped else { return }
        stopped = true
        if let responseFileDescriptor {
            Darwin.close(responseFileDescriptor)
            self.responseFileDescriptor = nil
        }
        incoming.cancel()
        onStop()
    }

    private func receiveRequest() {
        guard !stopped else { return }
        if buffer.count > HTTPWireRequest.maximumSize {
            send(HTTPWireResponse.error(status: "413 Payload Too Large"))
            return
        }
        do {
            if let length = try HTTPWireRequest.completeLength(in: buffer), buffer.count >= length {
                let requestData = Data(buffer.prefix(length))
                buffer.removeFirst(length)
                let request = try HTTPWireRequest(data: requestData)
                requestCount += 1
                let keepAlive =
                    !peerCompleted
                    && request.allowsPersistentConnection
                    && requestCount < Self.maximumPersistentRequests
                receiveGeneration &+= 1
                process(request, keepAlive: keepAlive)
                return
            }
        } catch {
            NSLog("HerdMe FastCGI request failed: %@", error.localizedDescription)
            send(HTTPWireResponse.error(status: Self.status(for: error)))
            return
        }
        if peerCompleted {
            send(HTTPWireResponse.error(status: "400 Bad Request"))
            return
        }

        receiveGeneration &+= 1
        let generation = receiveGeneration
        queue.asyncAfter(deadline: .now() + Self.persistentIdleTimeout) { [weak self] in
            guard let self, !self.stopped, self.receiveGeneration == generation else { return }
            self.stopOnQueue()
        }
        incoming.receive(minimumIncompleteLength: 1, maximumLength: 65_536) { [weak self] data, _, complete, error in
            guard let self, !self.stopped else { return }
            if let data { self.buffer.append(data) }
            if complete || error != nil { self.peerCompleted = true }
            self.receiveRequest()
        }
    }

    private func process(_ request: HTTPWireRequest, keepAlive: Bool) {
        Self.processingQueue.async { [weak self] in
            guard let self else { return }
            do {
                switch try self.handler.responsePlan(to: request, keepAlive: keepAlive) {
                case .complete(let response):
                    self.queue.async { [weak self] in
                        guard let self, !self.stopped else { return }
                        self.send(response)
                    }
                case .fastCGI(let fastCGIRequest):
                    try self.streamFastCGI(fastCGIRequest, headOnly: request.method == "HEAD")
                }
            } catch {
                NSLog("HerdMe FastCGI request failed: %@", error.localizedDescription)
                self.queue.async { [weak self] in
                    guard let self, !self.stopped else { return }
                    self.send(HTTPWireResponse.error(status: Self.status(for: error)))
                }
            }
        }
    }

    private func streamFastCGI(_ request: FastCGIRequest, headOnly: Bool) throws {
        var parser = FastCGIHTTPStreamParser(
            headOnly: headOnly,
            allowKeepAlive: request.keepAlive
        )
        do {
            let errors = try FastCGIClient(port: handler.fpmPort).performStreaming(
                parameters: request.parameters,
                body: request.body
            ) { chunk in
                try parser.consume(chunk) { [weak self] content in
                    guard let self else {
                        throw LocalFastCGIError.connectionFailed("The HTTP session ended.")
                    }
                    try self.sendStreamingChunk(content)
                }
            }
            try parser.finish()
            if !errors.isEmpty {
                NSLog("HerdMe PHP-FPM: %@", String(decoding: errors, as: UTF8.self))
            }
            let keepAlive = parser.keepsConnectionAlive
            queue.async { [weak self] in self?.finishStreamingResponse(keepAlive: keepAlive) }
        } catch {
            guard parser.didStartResponse else { throw error }
            queue.async { [weak self] in self?.stopOnQueue() }
        }
    }

    private func sendStreamingChunk(_ content: Data) throws {
        guard !content.isEmpty else { return }
        let completion = NetworkSendCompletion()
        incoming.send(
            content: content,
            isComplete: false,
            completion: .contentProcessed { error in completion.resolve(error) }
        )
        try completion.wait()
    }

    private func send(_ response: HTTPWireResponse) {
        let keepAlive = response.keepAlive && !peerCompleted
        switch response.body {
        case .data(let body):
            var content = response.header
            content.append(body)
            incoming.send(
                content: content, isComplete: !keepAlive,
                completion: .contentProcessed { [weak self] error in
                    guard let self else { return }
                    if error == nil {
                        self.finishResponse(keepAlive: keepAlive)
                    } else {
                        self.stopOnQueue()
                    }
                })
        case .file(let url, let offset, let length):
            guard length > 0 else {
                incoming.send(
                    content: response.header,
                    isComplete: !keepAlive,
                    completion: .contentProcessed { [weak self] error in
                        guard let self else { return }
                        if error == nil {
                            self.finishResponse(keepAlive: keepAlive)
                        } else {
                            self.stopOnQueue()
                        }
                    }
                )
                return
            }
            let descriptor = Darwin.open(url.path, O_RDONLY | O_CLOEXEC)
            var metadata = stat()
            guard descriptor >= 0,
                fstat(descriptor, &metadata) == 0,
                metadata.st_mode & S_IFMT == S_IFREG,
                metadata.st_size >= off_t(offset) + off_t(length)
            else {
                if descriptor >= 0 { Darwin.close(descriptor) }
                send(HTTPWireResponse.error(status: "404 Not Found"))
                return
            }
            responseFileDescriptor = descriptor
            incoming.send(
                content: response.header,
                isComplete: false,
                completion: .contentProcessed { [weak self] error in
                    guard let self else { return }
                    guard error == nil else {
                        self.stopOnQueue()
                        return
                    }
                    self.sendFileChunk(
                        descriptor: descriptor,
                        offset: offset,
                        remaining: length,
                        keepAlive: keepAlive
                    )
                }
            )
        }
    }

    private func sendFileChunk(
        descriptor: Int32,
        offset: Int64,
        remaining: Int,
        keepAlive: Bool
    ) {
        guard !stopped, responseFileDescriptor == descriptor, remaining > 0 else {
            stopOnQueue()
            return
        }
        let requested = min(Self.fileChunkSize, remaining)
        var chunk = Data(count: requested)
        let count = chunk.withUnsafeMutableBytes { bytes in
            pread(descriptor, bytes.baseAddress, bytes.count, off_t(offset))
        }
        guard count > 0 else {
            stopOnQueue()
            return
        }
        if count < chunk.count { chunk.removeSubrange(count..<chunk.count) }
        let nextRemaining = remaining - count
        incoming.send(
            content: chunk,
            isComplete: nextRemaining == 0 && !keepAlive,
            completion: .contentProcessed { [weak self] error in
                guard let self else { return }
                guard error == nil else {
                    self.stopOnQueue()
                    return
                }
                if nextRemaining == 0 {
                    Darwin.close(descriptor)
                    self.responseFileDescriptor = nil
                    self.finishResponse(keepAlive: keepAlive)
                } else {
                    self.sendFileChunk(
                        descriptor: descriptor,
                        offset: offset + Int64(count),
                        remaining: nextRemaining,
                        keepAlive: keepAlive
                    )
                }
            }
        )
    }

    private func finishStreamingResponse(keepAlive: Bool) {
        guard !stopped else { return }
        let keepAlive = keepAlive && !peerCompleted
        if keepAlive {
            finishResponse(keepAlive: true)
        } else {
            incoming.send(
                content: nil,
                isComplete: true,
                completion: .contentProcessed { [weak self] _ in self?.stopOnQueue() }
            )
        }
    }

    private func finishResponse(keepAlive: Bool) {
        guard !stopped else { return }
        if keepAlive {
            receiveRequest()
        } else {
            stopOnQueue()
        }
    }

    private static func status(for error: Error) -> String {
        switch error {
        case LocalFastCGIError.headerTooLarge: "431 Request Header Fields Too Large"
        case LocalFastCGIError.requestTooLarge: "413 Payload Too Large"
        case LocalFastCGIError.unsupportedHTTPVersion: "505 HTTP Version Not Supported"
        case LocalFastCGIError.unsupportedTransferCoding: "501 Not Implemented"
        case LocalFastCGIError.invalidPath: "403 Forbidden"
        case LocalFastCGIError.scriptMissing: "404 Not Found"
        case LocalFastCGIError.malformedRequest: "400 Bad Request"
        default: "502 Bad Gateway"
        }
    }
}

private final class NetworkSendCompletion: @unchecked Sendable {
    private let lock = NSLock()
    private let semaphore = DispatchSemaphore(value: 0)
    private var error: NWError?

    func resolve(_ error: NWError?) {
        lock.lock()
        self.error = error
        lock.unlock()
        semaphore.signal()
    }

    func wait() throws {
        guard semaphore.wait(timeout: .now() + .seconds(60)) == .success else {
            throw LocalFastCGIError.connectionFailed("The HTTP client stopped accepting response data.")
        }
        lock.lock()
        let resolvedError = error
        lock.unlock()
        if let resolvedError {
            throw LocalFastCGIError.connectionFailed(resolvedError.localizedDescription)
        }
    }
}

private struct HTTPWireRequest: Sendable {
    static let maximumSize = 32 * 1_024 * 1_024
    private static let maximumHeaderSize = 1 * 1_024 * 1_024
    private static let headerDelimiter = Data("\r\n\r\n".utf8)
    private static let lineDelimiter = Data("\r\n".utf8)

    private enum BodyFraming {
        case contentLength(Int)
        case chunked
    }

    private struct ParsedHead {
        let method: String
        let target: String
        let protocolVersion: String
        let headers: [(name: String, value: String)]
        let framing: BodyFraming
    }

    let method: String
    let target: String
    let protocolVersion: String
    let headers: [(name: String, value: String)]
    let body: Data

    init(data: Data) throws {
        guard data.count <= Self.maximumSize,
            let headerRange = data.range(of: Self.headerDelimiter)
        else {
            throw LocalFastCGIError.malformedRequest
        }
        let headerLength = data.distance(from: data.startIndex, to: headerRange.lowerBound)
        guard headerLength <= Self.maximumHeaderSize else {
            throw LocalFastCGIError.headerTooLarge
        }
        let parsed = try Self.parseHead(Data(data[..<headerRange.lowerBound]))
        method = parsed.method.uppercased()
        target = parsed.target
        protocolVersion = parsed.protocolVersion
        headers = parsed.headers

        let encodedBody = Data(data[headerRange.upperBound...])
        switch parsed.framing {
        case .contentLength(let expected):
            guard encodedBody.count == expected else {
                throw LocalFastCGIError.malformedRequest
            }
            body = encodedBody
        case .chunked:
            guard let decoded = try Self.decodeChunked(encodedBody),
                decoded.consumed == encodedBody.count
            else {
                throw LocalFastCGIError.malformedRequest
            }
            body = decoded.body
        }
    }

    func header(named name: String) -> String? {
        headers.first(where: { $0.name.caseInsensitiveCompare(name) == .orderedSame })?.value
    }

    var allowsPersistentConnection: Bool {
        let connectionTokens =
            headers
            .filter { $0.name.caseInsensitiveCompare("Connection") == .orderedSame }
            .flatMap { $0.value.split(separator: ",") }
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() }
        if connectionTokens.contains("close") { return false }
        if protocolVersion.caseInsensitiveCompare("HTTP/1.1") == .orderedSame { return true }
        return protocolVersion.caseInsensitiveCompare("HTTP/1.0") == .orderedSame
            && connectionTokens.contains("keep-alive")
    }

    static func completeLength(in data: Data) throws -> Int? {
        guard let headerRange = data.range(of: headerDelimiter) else {
            if data.count > maximumHeaderSize { throw LocalFastCGIError.headerTooLarge }
            return nil
        }
        let headerLength = data.distance(from: data.startIndex, to: headerRange.lowerBound)
        guard headerLength <= maximumHeaderSize else { throw LocalFastCGIError.headerTooLarge }
        let parsed = try parseHead(Data(data[..<headerRange.lowerBound]))
        let bodyOffset = data.distance(from: data.startIndex, to: headerRange.upperBound)
        switch parsed.framing {
        case .contentLength(let contentLength):
            guard contentLength <= maximumSize - bodyOffset else {
                throw LocalFastCGIError.requestTooLarge
            }
            let expected = bodyOffset + contentLength
            return data.count >= expected ? expected : nil
        case .chunked:
            let encodedBody = Data(data[headerRange.upperBound...])
            guard let decoded = try decodeChunked(encodedBody) else { return nil }
            let expected = bodyOffset + decoded.consumed
            guard expected <= maximumSize else { throw LocalFastCGIError.requestTooLarge }
            return expected
        }
    }

    private static func parseHead(_ data: Data) throws -> ParsedHead {
        guard let headerText = String(data: data, encoding: .utf8) else {
            throw LocalFastCGIError.malformedRequest
        }
        let lines = headerText.components(separatedBy: "\r\n")
        guard let requestLine = lines.first else { throw LocalFastCGIError.malformedRequest }
        let components = requestLine.split(separator: " ", maxSplits: 2).map(String.init)
        guard components.count == 3,
            isValidToken(components[0]),
            components[1].utf8.allSatisfy({ $0 > 0x20 && $0 != 0x7f })
        else {
            throw LocalFastCGIError.malformedRequest
        }
        guard components[2].hasPrefix("HTTP/") else {
            throw LocalFastCGIError.malformedRequest
        }
        guard components[2] == "HTTP/1.0" || components[2] == "HTTP/1.1" else {
            throw LocalFastCGIError.unsupportedHTTPVersion
        }

        let headers = try lines.dropFirst().map(parseHeaderLine)
        let hostHeaders = headers.filter { $0.name.caseInsensitiveCompare("Host") == .orderedSame }
        guard hostHeaders.count <= 1,
            components[2] != "HTTP/1.1" || hostHeaders.count == 1,
            !hostHeaders.contains(where: { $0.value.isEmpty })
        else {
            throw LocalFastCGIError.malformedRequest
        }

        let transferCodings =
            headers
            .filter { $0.name.caseInsensitiveCompare("Transfer-Encoding") == .orderedSame }
            .flatMap {
                $0.value.split(separator: ",", omittingEmptySubsequences: false)
                    .map { $0.trimmingCharacters(in: .whitespaces).lowercased() }
            }
        guard !transferCodings.contains(where: { $0.isEmpty }) else {
            throw LocalFastCGIError.malformedRequest
        }
        if transferCodings.contains(where: { $0 != "chunked" }) {
            throw LocalFastCGIError.unsupportedTransferCoding
        }
        guard transferCodings.count <= 1,
            components[2] != "HTTP/1.0" || transferCodings.isEmpty
        else {
            throw LocalFastCGIError.malformedRequest
        }

        let contentLengthHeaders = headers.filter {
            $0.name.caseInsensitiveCompare("Content-Length") == .orderedSame
        }
        guard contentLengthHeaders.count <= 1,
            transferCodings.isEmpty || contentLengthHeaders.isEmpty
        else {
            throw LocalFastCGIError.malformedRequest
        }
        let framing: BodyFraming
        if !transferCodings.isEmpty {
            framing = .chunked
        } else if let contentLength = contentLengthHeaders.first?.value {
            guard !contentLength.isEmpty,
                contentLength.utf8.allSatisfy({ (0x30...0x39).contains($0) }),
                let parsed = Int(contentLength)
            else {
                throw LocalFastCGIError.malformedRequest
            }
            guard parsed <= maximumSize else { throw LocalFastCGIError.requestTooLarge }
            framing = .contentLength(parsed)
        } else {
            framing = .contentLength(0)
        }
        return ParsedHead(
            method: components[0],
            target: components[1],
            protocolVersion: components[2],
            headers: headers,
            framing: framing
        )
    }

    private static func parseHeaderLine(_ line: String) throws -> (name: String, value: String) {
        guard let separator = line.firstIndex(of: ":") else {
            throw LocalFastCGIError.malformedRequest
        }
        let name = String(line[..<separator])
        let value = String(line[line.index(after: separator)...]).trimmingCharacters(in: .whitespaces)
        guard isValidToken(name), isValidHeaderValue(value) else {
            throw LocalFastCGIError.malformedRequest
        }
        return (name, value)
    }

    private static func isValidToken(_ value: String) -> Bool {
        let punctuation = Array("!#$%&'*+-.^_`|~".utf8)
        return !value.isEmpty
            && value.utf8.allSatisfy { byte in
                (0x30...0x39).contains(byte)
                    || (0x41...0x5a).contains(byte)
                    || (0x61...0x7a).contains(byte)
                    || punctuation.contains(byte)
            }
    }

    private static func isValidHeaderValue(_ value: String) -> Bool {
        value.utf8.allSatisfy { $0 == 0x09 || (0x20...0x7e).contains($0) }
    }

    private static func decodeChunked(_ data: Data) throws -> (body: Data, consumed: Int)? {
        var cursor = data.startIndex
        var body = Data()
        while true {
            guard let lineRange = data.range(of: lineDelimiter, in: cursor..<data.endIndex) else {
                if data.distance(from: cursor, to: data.endIndex) > 8_192 {
                    throw LocalFastCGIError.malformedRequest
                }
                return nil
            }
            guard let sizeLine = String(data: data[cursor..<lineRange.lowerBound], encoding: .utf8),
                isValidHeaderValue(sizeLine)
            else {
                throw LocalFastCGIError.malformedRequest
            }
            let sizeText = sizeLine.split(separator: ";", maxSplits: 1)[0]
            guard !sizeText.isEmpty,
                sizeText.utf8.allSatisfy({
                    (0x30...0x39).contains($0)
                        || (0x41...0x46).contains($0)
                        || (0x61...0x66).contains($0)
                }),
                let size = Int(sizeText, radix: 16)
            else {
                throw LocalFastCGIError.malformedRequest
            }
            cursor = lineRange.upperBound
            if size == 0 {
                while true {
                    guard let trailerRange = data.range(of: lineDelimiter, in: cursor..<data.endIndex) else {
                        if data.distance(from: cursor, to: data.endIndex) > maximumHeaderSize {
                            throw LocalFastCGIError.headerTooLarge
                        }
                        return nil
                    }
                    guard let trailer = String(data: data[cursor..<trailerRange.lowerBound], encoding: .utf8) else {
                        throw LocalFastCGIError.malformedRequest
                    }
                    cursor = trailerRange.upperBound
                    if trailer.isEmpty {
                        return (body, data.distance(from: data.startIndex, to: cursor))
                    }
                    _ = try parseHeaderLine(trailer)
                }
            }
            guard size <= maximumSize - body.count else {
                throw LocalFastCGIError.requestTooLarge
            }
            guard data.distance(from: cursor, to: data.endIndex) >= size + lineDelimiter.count else {
                return nil
            }
            let end = data.index(cursor, offsetBy: size)
            body.append(data[cursor..<end])
            cursor = end
            let terminatorEnd = data.index(cursor, offsetBy: lineDelimiter.count)
            guard data[cursor..<terminatorEnd] == lineDelimiter else {
                throw LocalFastCGIError.malformedRequest
            }
            cursor = terminatorEnd
        }
    }
}

private struct FastCGIRequest: Sendable {
    let parameters: [String: String]
    let body: Data
    let keepAlive: Bool
}

private struct FastCGIHTTPHandler: Sendable {
    let documentRoot: URL
    let fpmPort: Int

    enum ResponsePlan: Sendable {
        case complete(HTTPWireResponse)
        case fastCGI(FastCGIRequest)
    }

    func responsePlan(to request: HTTPWireRequest, keepAlive: Bool) throws -> ResponsePlan {
        let target = try RequestTarget(request.target)
        let resource = try resolve(path: target.path)
        switch resource {
        case .staticFile(let url):
            guard request.method == "GET" || request.method == "HEAD" else {
                return .complete(HTTPWireResponse.error(status: "405 Method Not Allowed"))
            }
            return .complete(
                try HTTPWireResponse.staticFile(
                    url,
                    headOnly: request.method == "HEAD",
                    rangeHeader: request.header(named: "Range"),
                    keepAlive: keepAlive
                ))
        case .script(let scriptURL, let scriptName, let pathInfo):
            return .fastCGI(
                FastCGIRequest(
                    parameters: parameters(
                        for: request,
                        target: target,
                        scriptURL: scriptURL,
                        scriptName: scriptName,
                        pathInfo: pathInfo
                    ),
                    body: request.body,
                    keepAlive: keepAlive
                ))
        }
    }

    private func parameters(
        for request: HTTPWireRequest,
        target: RequestTarget,
        scriptURL: URL,
        scriptName: String,
        pathInfo: String?
    ) -> [String: String] {
        let hostHeader = request.header(named: "Host") ?? "localhost"
        let host = hostHeader.split(separator: ":", maxSplits: 1).first.map(String.init) ?? hostHeader
        let secure = request.header(named: "X-Forwarded-Proto")?.lowercased() == "https"
        var values = [
            "GATEWAY_INTERFACE": "CGI/1.1",
            "SERVER_SOFTWARE": "HerdMe",
            "SERVER_PROTOCOL": request.protocolVersion,
            "REQUEST_METHOD": request.method,
            "REQUEST_URI": request.target,
            "QUERY_STRING": target.query,
            "DOCUMENT_ROOT": documentRoot.path,
            "DOCUMENT_URI": target.path,
            "SCRIPT_FILENAME": scriptURL.path,
            "SCRIPT_NAME": scriptName,
            "SERVER_NAME": host,
            "SERVER_PORT": secure ? "443" : "80",
            "REQUEST_SCHEME": secure ? "https" : "http",
            "REMOTE_ADDR": "127.0.0.1",
            "REMOTE_PORT": "0",
            "SERVER_ADDR": "127.0.0.1",
            "CONTENT_LENGTH": String(request.body.count)
        ]
        if secure { values["HTTPS"] = "on" }
        if let pathInfo, !pathInfo.isEmpty { values["PATH_INFO"] = pathInfo }
        if let contentType = request.header(named: "Content-Type") {
            values["CONTENT_TYPE"] = contentType
        }
        for header in request.headers {
            let normalized = header.name
                .uppercased()
                .map { $0.isLetter || $0.isNumber ? $0 : "_" }
            let key = "HTTP_" + String(normalized)
            values[key] = values[key].map { $0 + ", " + header.value } ?? header.value
        }
        return values
    }

    private enum Resource {
        case staticFile(URL)
        case script(URL, scriptName: String, pathInfo: String?)
    }

    private func resolve(path: String) throws -> Resource {
        let relativePath = path.drop(while: { $0 == "/" })
        let candidate =
            documentRoot
            .appendingPathComponent(String(relativePath))
            .standardizedFileURL
            .resolvingSymlinksInPath()
        guard Self.isInside(candidate, root: documentRoot) else {
            throw LocalFastCGIError.invalidPath
        }

        var isDirectory: ObjCBool = false
        if FileManager.default.fileExists(atPath: candidate.path, isDirectory: &isDirectory) {
            if isDirectory.boolValue {
                for index in ["index.html", "index.htm"] {
                    let file = candidate.appendingPathComponent(index)
                    if FileManager.default.fileExists(atPath: file.path) { return .staticFile(file) }
                }
                let index = candidate.appendingPathComponent("index.php")
                if FileManager.default.fileExists(atPath: index.path) {
                    let name = path.hasSuffix("/") ? path + "index.php" : path + "/index.php"
                    return .script(index, scriptName: name, pathInfo: nil)
                }
            } else if candidate.pathExtension.lowercased() == "php" {
                return .script(candidate, scriptName: path, pathInfo: nil)
            } else {
                return .staticFile(candidate)
            }
        }

        let frontController = documentRoot.appendingPathComponent("index.php")
        guard FileManager.default.fileExists(atPath: frontController.path) else {
            throw LocalFastCGIError.scriptMissing
        }
        return .script(
            frontController,
            scriptName: "/index.php",
            pathInfo: path == "/" ? nil : path
        )
    }

    private static func isInside(_ candidate: URL, root: URL) -> Bool {
        candidate.path == root.path || candidate.path.hasPrefix(root.path + "/")
    }
}

private struct RequestTarget: Sendable {
    let path: String
    let query: String

    init(_ target: String) throws {
        let raw: String
        if target.hasPrefix("http://") || target.hasPrefix("https://") {
            guard let components = URLComponents(string: target) else {
                throw LocalFastCGIError.malformedRequest
            }
            raw = components.percentEncodedPath + (components.percentEncodedQuery.map { "?" + $0 } ?? "")
        } else {
            raw = target
        }
        let components = raw.split(separator: "?", maxSplits: 1, omittingEmptySubsequences: false)
        let encodedPath = components.first.map(String.init) ?? "/"
        guard let decodedPath = encodedPath.removingPercentEncoding, decodedPath.hasPrefix("/") else {
            throw LocalFastCGIError.invalidPath
        }
        path = decodedPath.isEmpty ? "/" : decodedPath
        query = components.count > 1 ? String(components[1]) : ""
    }
}

private struct HTTPWireResponse: Sendable {
    enum Body: Sendable {
        case data(Data)
        case file(URL, offset: Int64, length: Int)
    }

    let header: Data
    let body: Body
    let keepAlive: Bool

    static func staticFile(
        _ url: URL,
        headOnly: Bool,
        rangeHeader: String?,
        keepAlive: Bool
    ) throws -> HTTPWireResponse {
        let values = try url.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey])
        guard values.isRegularFile == true, let fileSize = values.fileSize, fileSize >= 0 else {
            throw LocalFastCGIError.invalidPath
        }
        guard let selectedRange = HTTPByteRange.select(rangeHeader, fileSize: fileSize) else {
            return make(
                status: "416 Range Not Satisfiable",
                headers: [
                    ("Content-Range", "bytes */\(fileSize)"),
                    ("Accept-Ranges", "bytes"),
                    ("Content-Length", "0"),
                    ("Cache-Control", "no-cache"),
                    ("Connection", keepAlive ? "keep-alive" : "close")
                ],
                body: Data(),
                keepAlive: keepAlive
            )
        }
        var headers = [
            ("Content-Type", mimeType(for: url.pathExtension)),
            ("Content-Length", String(selectedRange.length)),
            ("Accept-Ranges", "bytes"),
            ("Cache-Control", "no-cache"),
            ("Connection", keepAlive ? "keep-alive" : "close")
        ]
        if selectedRange.isPartial {
            headers.insert(
                (
                    "Content-Range",
                    "bytes \(selectedRange.offset)-\(selectedRange.offset + Int64(selectedRange.length) - 1)/\(fileSize)"
                ),
                at: 2
            )
        }
        let responseHeader = HTTPWireHeader.make(
            status: selectedRange.isPartial ? "206 Partial Content" : "200 OK",
            headers: headers
        )
        if headOnly || selectedRange.length == 0 {
            return HTTPWireResponse(
                header: responseHeader,
                body: .data(Data()),
                keepAlive: keepAlive
            )
        }
        return make(
            header: responseHeader,
            body: .file(url, offset: selectedRange.offset, length: selectedRange.length),
            keepAlive: keepAlive
        )
    }

    static func error(status: String) -> HTTPWireResponse {
        let body = Data("HerdMe could not serve this local site.\n".utf8)
        return make(
            status: status,
            headers: [
                ("Content-Type", "text/plain; charset=utf-8"),
                ("Content-Length", String(body.count)),
                ("Connection", "close")
            ],
            body: body,
            keepAlive: false
        )
    }

    private static func make(
        status: String,
        headers: [(String, String)],
        body: Data,
        keepAlive: Bool
    ) -> HTTPWireResponse {
        HTTPWireResponse(
            header: HTTPWireHeader.make(status: status, headers: headers),
            body: .data(body),
            keepAlive: keepAlive
        )
    }

    private static func make(header: Data, body: Body, keepAlive: Bool) -> HTTPWireResponse {
        HTTPWireResponse(header: header, body: body, keepAlive: keepAlive)
    }

    private static func mimeType(for pathExtension: String) -> String {
        switch pathExtension.lowercased() {
        case "html", "htm": "text/html; charset=utf-8"
        case "css": "text/css; charset=utf-8"
        case "js", "mjs": "text/javascript; charset=utf-8"
        case "json", "map": "application/json; charset=utf-8"
        case "xml": "application/xml; charset=utf-8"
        case "txt", "log": "text/plain; charset=utf-8"
        case "svg": "image/svg+xml"
        case "png": "image/png"
        case "jpg", "jpeg": "image/jpeg"
        case "gif": "image/gif"
        case "webp": "image/webp"
        case "ico": "image/x-icon"
        case "woff": "font/woff"
        case "woff2": "font/woff2"
        case "ttf": "font/ttf"
        case "pdf": "application/pdf"
        case "zip": "application/zip"
        default: "application/octet-stream"
        }
    }

}

private struct HTTPByteRange: Sendable {
    let offset: Int64
    let length: Int
    let isPartial: Bool

    static func select(_ value: String?, fileSize: Int) -> HTTPByteRange? {
        guard let value else {
            return HTTPByteRange(offset: 0, length: fileSize, isPartial: false)
        }
        guard fileSize > 0 else { return nil }
        let components = value.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
        guard components.count == 2,
            components[0].trimmingCharacters(in: .whitespaces).lowercased() == "bytes",
            !components[1].contains(",")
        else {
            return nil
        }
        let bounds = components[1].split(separator: "-", maxSplits: 1, omittingEmptySubsequences: false)
        guard bounds.count == 2 else { return nil }
        let startText = bounds[0].trimmingCharacters(in: .whitespaces)
        let endText = bounds[1].trimmingCharacters(in: .whitespaces)

        if startText.isEmpty {
            guard let suffixLength = Int(endText), suffixLength > 0 else { return nil }
            let length = min(suffixLength, fileSize)
            return HTTPByteRange(
                offset: Int64(fileSize - length),
                length: length,
                isPartial: true
            )
        }

        guard let start = Int(startText), start >= 0, start < fileSize else { return nil }
        let end: Int
        if endText.isEmpty {
            end = fileSize - 1
        } else {
            guard let requestedEnd = Int(endText), requestedEnd >= start else { return nil }
            end = min(requestedEnd, fileSize - 1)
        }
        return HTTPByteRange(
            offset: Int64(start),
            length: end - start + 1,
            isPartial: true
        )
    }
}

private struct FastCGIClient: Sendable {
    private static let version: UInt8 = 1
    private static let beginRequest: UInt8 = 1
    private static let endRequest: UInt8 = 3
    private static let parameters: UInt8 = 4
    private static let standardInput: UInt8 = 5
    private static let standardOutput: UInt8 = 6
    private static let standardError: UInt8 = 7
    private static let requestIdentifier: UInt16 = 1
    private static let maximumContentLength = 65_535
    private static let maximumStandardErrorSize = 1 * 1_024 * 1_024

    let port: Int

    func performStreaming(
        parameters: [String: String],
        body: Data,
        onStandardOutput: (Data) throws -> Void
    ) throws -> Data {
        let descriptor = socket(AF_INET, SOCK_STREAM, 0)
        guard descriptor >= 0 else { throw LocalFastCGIError.connectionFailed(Self.systemError()) }
        defer { Darwin.close(descriptor) }
        var timeout = timeval(tv_sec: 15, tv_usec: 0)
        setsockopt(descriptor, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
        setsockopt(descriptor, SOL_SOCKET, SO_SNDTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))

        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = in_port_t(UInt16(port).bigEndian)
        address.sin_addr = in_addr(s_addr: inet_addr("127.0.0.1"))
        let connected = withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.connect(descriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard connected == 0 else { throw LocalFastCGIError.connectionFailed(Self.systemError()) }

        try Self.write(Self.record(type: Self.beginRequest, content: Data([0, 1, 0, 0, 0, 0, 0, 0])), to: descriptor)
        let parameterData = Self.encoded(parameters: parameters)
        try Self.writeRecords(type: Self.parameters, content: parameterData, to: descriptor)
        try Self.write(Self.record(type: Self.parameters, content: Data()), to: descriptor)
        try Self.writeRecords(type: Self.standardInput, content: body, to: descriptor)
        try Self.write(Self.record(type: Self.standardInput, content: Data()), to: descriptor)

        var errors = Data()
        var errorsWereTruncated = false
        while true {
            let header = try Self.readExactly(8, from: descriptor)
            let bytes = [UInt8](header)
            guard bytes[0] == Self.version else { throw LocalFastCGIError.invalidResponse }
            let requestID = UInt16(bytes[2]) << 8 | UInt16(bytes[3])
            let length = Int(UInt16(bytes[4]) << 8 | UInt16(bytes[5]))
            let padding = Int(bytes[6])
            let content = try Self.readExactly(length, from: descriptor)
            if padding > 0 { _ = try Self.readExactly(padding, from: descriptor) }
            guard requestID == Self.requestIdentifier else { continue }
            switch bytes[1] {
            case Self.standardOutput:
                if !content.isEmpty { try onStandardOutput(content) }
            case Self.standardError:
                let available = max(0, Self.maximumStandardErrorSize - errors.count)
                if available > 0 { errors.append(content.prefix(available)) }
                if content.count > available { errorsWereTruncated = true }
            case Self.endRequest:
                guard content.count >= 8, content[4] == 0 else {
                    throw LocalFastCGIError.invalidResponse
                }
                if errorsWereTruncated {
                    errors.append(Data("\n[HerdMe truncated PHP-FPM stderr at 1MB]\n".utf8))
                }
                return errors
            default:
                break
            }
        }
    }

    private static func encoded(parameters: [String: String]) -> Data {
        var data = Data()
        for key in parameters.keys.sorted() {
            let name = Data(key.utf8)
            let value = Data((parameters[key] ?? "").utf8)
            append(length: name.count, to: &data)
            append(length: value.count, to: &data)
            data.append(name)
            data.append(value)
        }
        return data
    }

    private static func append(length: Int, to data: inout Data) {
        if length < 128 {
            data.append(UInt8(length))
        } else {
            let value = UInt32(length) | 0x8000_0000
            data.append(UInt8((value >> 24) & 0xff))
            data.append(UInt8((value >> 16) & 0xff))
            data.append(UInt8((value >> 8) & 0xff))
            data.append(UInt8(value & 0xff))
        }
    }

    private static func writeRecords(type: UInt8, content: Data, to descriptor: Int32) throws {
        var offset = 0
        while offset < content.count {
            let length = min(maximumContentLength, content.count - offset)
            let end = offset + length
            try write(record(type: type, content: content[offset..<end]), to: descriptor)
            offset = end
        }
    }

    private static func record(type: UInt8, content: Data) -> Data {
        let length = content.count
        let padding = (8 - length % 8) % 8
        var data = Data([
            version,
            type,
            UInt8((requestIdentifier >> 8) & 0xff),
            UInt8(requestIdentifier & 0xff),
            UInt8((length >> 8) & 0xff),
            UInt8(length & 0xff),
            UInt8(padding),
            0
        ])
        data.append(content)
        if padding > 0 { data.append(Data(repeating: 0, count: padding)) }
        return data
    }

    private static func write(_ data: Data, to descriptor: Int32) throws {
        try data.withUnsafeBytes { rawBuffer in
            guard let baseAddress = rawBuffer.baseAddress else { return }
            var offset = 0
            while offset < rawBuffer.count {
                let count = Darwin.write(descriptor, baseAddress.advanced(by: offset), rawBuffer.count - offset)
                guard count > 0 else { throw LocalFastCGIError.connectionFailed(systemError()) }
                offset += count
            }
        }
    }

    private static func readExactly(_ count: Int, from descriptor: Int32) throws -> Data {
        if count == 0 { return Data() }
        var data = Data(count: count)
        try data.withUnsafeMutableBytes { rawBuffer in
            guard let baseAddress = rawBuffer.baseAddress else { return }
            var offset = 0
            while offset < count {
                let received = Darwin.read(descriptor, baseAddress.advanced(by: offset), count - offset)
                guard received > 0 else { throw LocalFastCGIError.connectionFailed(systemError()) }
                offset += received
            }
        }
        return data
    }

    private static func systemError() -> String {
        String(cString: strerror(errno))
    }
}
