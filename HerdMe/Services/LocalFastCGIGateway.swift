import Darwin
import Foundation
import Network

enum LocalFastCGIError: LocalizedError {
    case malformedRequest
    case requestTooLarge
    case invalidPath
    case scriptMissing
    case connectionFailed(String)
    case invalidResponse

    var errorDescription: String? {
        switch self {
        case .malformedRequest:
            "The local HTTP request is malformed."
        case .requestTooLarge:
            "The local HTTP request is too large."
        case .invalidPath:
            "The requested local path is invalid."
        case .scriptMissing:
            "The requested local site has no front controller."
        case let .connectionFailed(message):
            "PHP-FPM connection failed: \(message)"
        case .invalidResponse:
            "PHP-FPM returned an invalid response."
        }
    }
}

final class LocalFastCGIGateway: @unchecked Sendable {
    private let documentRoot: URL
    private let fpmPort: Int
    private let queue = DispatchQueue(label: "app.herdme.fastcgi-gateway", qos: .userInitiated)
    private var listener: NWListener?
    private var sessions: [UUID: FastCGIHTTPSession] = [:]
    private(set) var port: Int?

    init(documentRoot: URL, fpmPort: Int) {
        self.documentRoot = documentRoot.standardizedFileURL.resolvingSymlinksInPath()
        self.fpmPort = fpmPort
    }

    var isRunning: Bool { listener != nil }

    func start(preferredPort: Int) throws -> Int {
        if let port, listener != nil { return port }
        guard let selectedPort = LocalEnvironmentEngine.availablePort(startingAt: preferredPort),
              let rawPort = UInt16(exactly: selectedPort),
              let networkPort = NWEndpoint.Port(rawValue: rawPort) else {
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
            self.sessions[identifier] = session
            session.start()
        }
        listener.stateUpdateHandler = { state in
            if case let .failed(error) = state {
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
        sessions.values.forEach { $0.stop() }
        sessions.removeAll()
        port = nil
    }

    private func removeSession(_ identifier: UUID) {
        queue.async { [weak self] in self?.sessions[identifier] = nil }
    }
}

private final class FastCGIHTTPSession: @unchecked Sendable {
    private let incoming: NWConnection
    private let handler: FastCGIHTTPHandler
    private let onStop: @Sendable () -> Void
    private let queue = DispatchQueue(label: "app.herdme.fastcgi-gateway.session", qos: .userInitiated)
    private var buffer = Data()
    private var stopped = false

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
        guard !stopped else { return }
        stopped = true
        incoming.cancel()
        onStop()
    }

    private func receiveRequest() {
        incoming.receive(minimumIncompleteLength: 1, maximumLength: 65_536) { [weak self] data, _, complete, error in
            guard let self, !self.stopped else { return }
            if let data { self.buffer.append(data) }
            if self.buffer.count > HTTPWireRequest.maximumSize {
                self.send(HTTPWireResponse.error(status: "413 Payload Too Large"))
                return
            }
            if let length = try? HTTPWireRequest.completeLength(in: self.buffer),
               self.buffer.count >= length {
                do {
                    let request = try HTTPWireRequest(data: self.buffer.prefix(length))
                    self.send(try self.handler.response(to: request))
                } catch {
                    NSLog("HerdMe FastCGI request failed: %@", error.localizedDescription)
                    self.send(HTTPWireResponse.error(status: Self.status(for: error)))
                }
            } else if complete || error != nil {
                self.send(HTTPWireResponse.error(status: "400 Bad Request"))
            } else {
                self.receiveRequest()
            }
        }
    }

    private func send(_ response: Data) {
        incoming.send(content: response, isComplete: true, completion: .contentProcessed { [weak self] _ in
            self?.stop()
        })
    }

    private static func status(for error: Error) -> String {
        switch error {
        case LocalFastCGIError.requestTooLarge: "413 Payload Too Large"
        case LocalFastCGIError.invalidPath: "403 Forbidden"
        case LocalFastCGIError.scriptMissing: "404 Not Found"
        case LocalFastCGIError.malformedRequest: "400 Bad Request"
        default: "502 Bad Gateway"
        }
    }
}

private struct HTTPWireRequest: Sendable {
    static let maximumSize = 32 * 1_024 * 1_024
    private static let headerDelimiter = Data("\r\n\r\n".utf8)

    let method: String
    let target: String
    let protocolVersion: String
    let headers: [(name: String, value: String)]
    let body: Data

    init(data: Data) throws {
        guard data.count <= Self.maximumSize,
              let headerRange = data.range(of: Self.headerDelimiter),
              let headerText = String(data: data[..<headerRange.lowerBound], encoding: .utf8) else {
            throw LocalFastCGIError.malformedRequest
        }
        let lines = headerText.components(separatedBy: "\r\n")
        guard let requestLine = lines.first else { throw LocalFastCGIError.malformedRequest }
        let components = requestLine.split(separator: " ", maxSplits: 2).map(String.init)
        guard components.count == 3, components[2].hasPrefix("HTTP/") else {
            throw LocalFastCGIError.malformedRequest
        }

        method = components[0].uppercased()
        target = components[1]
        protocolVersion = components[2]
        let parsedHeaders: [(name: String, value: String)] = try lines.dropFirst().map { line in
            guard let separator = line.firstIndex(of: ":") else {
                throw LocalFastCGIError.malformedRequest
            }
            return (
                String(line[..<separator]).trimmingCharacters(in: .whitespaces),
                String(line[line.index(after: separator)...]).trimmingCharacters(in: .whitespaces)
            )
        }
        headers = parsedHeaders

        let encodedBody = Data(data[headerRange.upperBound...])
        let transferEncoding = parsedHeaders.first {
            $0.name.caseInsensitiveCompare("Transfer-Encoding") == .orderedSame
        }?.value
        if transferEncoding?.lowercased().contains("chunked") == true {
            body = try Self.decodeChunked(encodedBody).body
        } else {
            let contentLength = parsedHeaders.first {
                $0.name.caseInsensitiveCompare("Content-Length") == .orderedSame
            }?.value
            let expected = Int(contentLength ?? "0") ?? 0
            guard expected >= 0, encodedBody.count >= expected else {
                throw LocalFastCGIError.malformedRequest
            }
            body = encodedBody.prefix(expected)
        }
    }

    func header(named name: String) -> String? {
        headers.first(where: { $0.name.caseInsensitiveCompare(name) == .orderedSame })?.value
    }

    static func completeLength(in data: Data) throws -> Int? {
        guard let headerRange = data.range(of: headerDelimiter) else { return nil }
        guard let headerText = String(data: data[..<headerRange.lowerBound], encoding: .utf8) else {
            throw LocalFastCGIError.malformedRequest
        }
        if headerText.range(of: "(?im)^Transfer-Encoding:\\s*.*chunked", options: .regularExpression) != nil {
            let encodedBody = Data(data[headerRange.upperBound...])
            guard let decoded = try? decodeChunked(encodedBody) else { return nil }
            return headerRange.upperBound + decoded.consumed
        }
        let contentLength = headerText
            .components(separatedBy: "\r\n")
            .first { $0.lowercased().hasPrefix("content-length:") }
            .flatMap { Int($0.dropFirst("content-length:".count).trimmingCharacters(in: .whitespaces)) } ?? 0
        guard contentLength >= 0 else { throw LocalFastCGIError.malformedRequest }
        let expected = headerRange.upperBound + contentLength
        return data.count >= expected ? expected : nil
    }

    private static func decodeChunked(_ data: Data) throws -> (body: Data, consumed: Int) {
        let delimiter = Data("\r\n".utf8)
        var cursor = data.startIndex
        var body = Data()
        while true {
            guard let lineRange = data.range(of: delimiter, in: cursor..<data.endIndex),
                  let sizeLine = String(data: data[cursor..<lineRange.lowerBound], encoding: .utf8),
                  let size = Int(sizeLine.split(separator: ";", maxSplits: 1)[0], radix: 16),
                  size >= 0 else {
                throw LocalFastCGIError.malformedRequest
            }
            cursor = lineRange.upperBound
            guard data.distance(from: cursor, to: data.endIndex) >= size + 2 else {
                throw LocalFastCGIError.malformedRequest
            }
            if size > 0 {
                let end = data.index(cursor, offsetBy: size)
                body.append(data[cursor..<end])
                cursor = end
            }
            guard data[cursor..<data.index(cursor, offsetBy: 2)] == delimiter else {
                throw LocalFastCGIError.malformedRequest
            }
            cursor = data.index(cursor, offsetBy: 2)
            if size == 0 {
                return (body, data.distance(from: data.startIndex, to: cursor))
            }
            guard body.count <= maximumSize else { throw LocalFastCGIError.requestTooLarge }
        }
    }
}

private struct FastCGIHTTPHandler: Sendable {
    let documentRoot: URL
    let fpmPort: Int

    func response(to request: HTTPWireRequest) throws -> Data {
        let target = try RequestTarget(request.target)
        let resource = try resolve(path: target.path)
        switch resource {
        case let .staticFile(url):
            guard request.method == "GET" || request.method == "HEAD" else {
                return HTTPWireResponse.error(status: "405 Method Not Allowed")
            }
            return try HTTPWireResponse.staticFile(url, headOnly: request.method == "HEAD")
        case let .script(scriptURL, scriptName, pathInfo):
            let result = try FastCGIClient(port: fpmPort).perform(
                parameters: parameters(
                    for: request,
                    target: target,
                    scriptURL: scriptURL,
                    scriptName: scriptName,
                    pathInfo: pathInfo
                ),
                body: request.body
            )
            if !result.standardError.isEmpty {
                NSLog("HerdMe PHP-FPM: %@", String(decoding: result.standardError, as: UTF8.self))
            }
            return try HTTPWireResponse.fastCGI(result.standardOutput)
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
        let candidate = documentRoot
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

private enum HTTPWireResponse {
    static func fastCGI(_ response: Data) throws -> Data {
        let delimiter = Data("\r\n\r\n".utf8)
        let alternateDelimiter = Data("\n\n".utf8)
        let range = response.range(of: delimiter) ?? response.range(of: alternateDelimiter)
        guard let range,
              let headerText = String(data: response[..<range.lowerBound], encoding: .utf8) else {
            throw LocalFastCGIError.invalidResponse
        }
        let body = Data(response[range.upperBound...])
        var status = "200 OK"
        var headers: [(String, String)] = []
        for line in headerText.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n") {
            guard let separator = line.firstIndex(of: ":") else { continue }
            let name = String(line[..<separator]).trimmingCharacters(in: .whitespaces)
            let value = String(line[line.index(after: separator)...]).trimmingCharacters(in: .whitespaces)
            if name.caseInsensitiveCompare("Status") == .orderedSame {
                status = value
            } else if name.caseInsensitiveCompare("Connection") != .orderedSame {
                headers.append((name, value))
            }
        }
        if status == "200 OK", headers.contains(where: { $0.0.caseInsensitiveCompare("Location") == .orderedSame }) {
            status = "302 Found"
        }
        if !headers.contains(where: { $0.0.caseInsensitiveCompare("Content-Length") == .orderedSame }) {
            headers.append(("Content-Length", String(body.count)))
        }
        if !headers.contains(where: { $0.0.caseInsensitiveCompare("Content-Type") == .orderedSame }) {
            headers.append(("Content-Type", "text/html; charset=utf-8"))
        }
        headers.append(("Connection", "close"))
        return make(status: status, headers: headers, body: body)
    }

    static func staticFile(_ url: URL, headOnly: Bool) throws -> Data {
        let data = try Data(contentsOf: url, options: .mappedIfSafe)
        return make(
            status: "200 OK",
            headers: [
                ("Content-Type", mimeType(for: url.pathExtension)),
                ("Content-Length", String(data.count)),
                ("Cache-Control", "no-cache"),
                ("Connection", "close")
            ],
            body: headOnly ? Data() : data
        )
    }

    static func error(status: String) -> Data {
        let body = Data("HerdMe could not serve this local site.\n".utf8)
        return make(
            status: status,
            headers: [
                ("Content-Type", "text/plain; charset=utf-8"),
                ("Content-Length", String(body.count)),
                ("Connection", "close")
            ],
            body: body
        )
    }

    private static func make(status: String, headers: [(String, String)], body: Data) -> Data {
        var response = Data("HTTP/1.1 \(status)\r\n".utf8)
        for header in headers {
            response.append(Data("\(header.0): \(header.1)\r\n".utf8))
        }
        response.append(Data("\r\n".utf8))
        response.append(body)
        return response
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

private struct FastCGIResult: Sendable {
    let standardOutput: Data
    let standardError: Data
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

    let port: Int

    func perform(parameters: [String: String], body: Data) throws -> FastCGIResult {
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

        var output = Data()
        var errors = Data()
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
                output.append(content)
            case Self.standardError:
                errors.append(content)
            case Self.endRequest:
                return FastCGIResult(standardOutput: output, standardError: errors)
            default:
                break
            }
            guard output.count + errors.count <= 64 * 1_024 * 1_024 else {
                throw LocalFastCGIError.invalidResponse
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
