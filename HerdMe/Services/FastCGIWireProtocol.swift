import Foundation

enum LocalFastCGIError: LocalizedError {
    case malformedRequest
    case headerTooLarge
    case requestTooLarge
    case unsupportedHTTPVersion
    case unsupportedTransferCoding
    case invalidPath
    case scriptMissing
    case connectionFailed(String)
    case invalidResponse

    var errorDescription: String? {
        switch self {
        case .malformedRequest:
            String(localized: "The local HTTP request is malformed.")
        case .headerTooLarge:
            String(localized: "The local HTTP request headers are too large.")
        case .requestTooLarge:
            String(localized: "The local HTTP request is too large.")
        case .unsupportedHTTPVersion:
            String(localized: "The local HTTP version is not supported.")
        case .unsupportedTransferCoding:
            String(localized: "The local HTTP transfer coding is not supported.")
        case .invalidPath:
            String(localized: "The requested local path is invalid.")
        case .scriptMissing:
            String(localized: "The requested local site has no front controller.")
        case .connectionFailed(let message):
            String.localizedStringWithFormat(
                String(localized: "PHP-FPM connection failed: %@"),
                message
            )
        case .invalidResponse:
            String(localized: "PHP-FPM returned an invalid response.")
        }
    }
}

enum HTTPWireHeader {
    static func make(status: String, headers: [(String, String)]) -> Data {
        var response = Data("HTTP/1.1 \(status)\r\n".utf8)
        for header in headers {
            response.append(Data("\(header.0): \(header.1)\r\n".utf8))
        }
        response.append(Data("\r\n".utf8))
        return response
    }
}

private struct FastCGIHTTPHeader: Sendable {
    let data: Data
    let contentLength: Int?
    let bodyForbidden: Bool
    let keepAlive: Bool
}

struct FastCGIHTTPStreamParser {
    private static let maximumHeaderSize = 1 * 1_024 * 1_024
    private static let delimiter = Data("\r\n\r\n".utf8)
    private static let alternateDelimiter = Data("\n\n".utf8)

    let headOnly: Bool
    let allowKeepAlive: Bool
    private(set) var didStartResponse = false
    private(set) var keepsConnectionAlive = false
    private var headerBuffer = Data()
    private var declaredContentLength: Int?
    private var bodyBytes = 0
    private var bodyForbidden = false

    init(headOnly: Bool, allowKeepAlive: Bool = false) {
        self.headOnly = headOnly
        self.allowKeepAlive = allowKeepAlive
    }

    mutating func consume(_ chunk: Data, send: (Data) throws -> Void) throws {
        guard !chunk.isEmpty else { return }
        if didStartResponse {
            try consumeBody(chunk, send: send)
            return
        }

        headerBuffer.append(chunk)
        guard headerBuffer.count <= Self.maximumHeaderSize else {
            throw LocalFastCGIError.invalidResponse
        }
        guard let range = Self.headerRange(in: headerBuffer) else { return }
        let parsed = try Self.parseHeader(
            Data(headerBuffer[..<range.lowerBound]),
            allowKeepAlive: allowKeepAlive,
            headOnly: headOnly
        )
        declaredContentLength = parsed.contentLength
        bodyForbidden = parsed.bodyForbidden
        keepsConnectionAlive = parsed.keepAlive
        try send(parsed.data)
        didStartResponse = true
        let body = Data(headerBuffer[range.upperBound...])
        headerBuffer.removeAll(keepingCapacity: false)
        try consumeBody(body, send: send)
    }

    mutating func finish() throws {
        guard didStartResponse else { throw LocalFastCGIError.invalidResponse }
        if !headOnly, !bodyForbidden, let declaredContentLength,
            bodyBytes != declaredContentLength
        {
            throw LocalFastCGIError.invalidResponse
        }
    }

    private mutating func consumeBody(_ body: Data, send: (Data) throws -> Void) throws {
        guard !body.isEmpty else { return }
        let (updatedBodyBytes, overflow) = bodyBytes.addingReportingOverflow(body.count)
        guard !overflow else { throw LocalFastCGIError.invalidResponse }
        bodyBytes = updatedBodyBytes
        if let declaredContentLength, bodyBytes > declaredContentLength {
            throw LocalFastCGIError.invalidResponse
        }
        if !headOnly, !bodyForbidden { try send(body) }
    }

    private static func headerRange(in data: Data) -> Range<Data.Index>? {
        let standard = data.range(of: delimiter)
        let alternate = data.range(of: alternateDelimiter)
        return switch (standard, alternate) {
        case (let left?, let right?): left.lowerBound <= right.lowerBound ? left : right
        case (let left?, nil): left
        case (nil, let right?): right
        case (nil, nil): nil
        }
    }

    private static func parseHeader(
        _ data: Data,
        allowKeepAlive: Bool,
        headOnly: Bool
    ) throws -> FastCGIHTTPHeader {
        guard let headerText = String(data: data, encoding: .utf8) else {
            throw LocalFastCGIError.invalidResponse
        }
        var status = "200 OK"
        var headers: [(String, String)] = []
        var contentLength: Int?
        let hopByHopHeaders: Set<String> = [
            "connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
            "te", "trailer", "transfer-encoding", "upgrade"
        ]
        for line in headerText.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n") {
            guard !line.isEmpty else { continue }
            guard let separator = line.firstIndex(of: ":") else {
                throw LocalFastCGIError.invalidResponse
            }
            let name = String(line[..<separator]).trimmingCharacters(in: .whitespaces)
            let value = String(line[line.index(after: separator)...]).trimmingCharacters(in: .whitespaces)
            guard isValidHeaderName(name), isValidHeaderValue(value) else {
                throw LocalFastCGIError.invalidResponse
            }
            if name.caseInsensitiveCompare("Status") == .orderedSame {
                status = value
            } else if name.caseInsensitiveCompare("Content-Length") == .orderedSame {
                guard contentLength == nil, let parsed = Int(value), parsed >= 0 else {
                    throw LocalFastCGIError.invalidResponse
                }
                contentLength = parsed
                headers.append((name, value))
            } else if !hopByHopHeaders.contains(name.lowercased()) {
                headers.append((name, value))
            }
        }
        guard isValidStatus(status) else { throw LocalFastCGIError.invalidResponse }
        if status == "200 OK",
            headers.contains(where: { $0.0.caseInsensitiveCompare("Location") == .orderedSame })
        {
            status = "302 Found"
        }
        if !headers.contains(where: { $0.0.caseInsensitiveCompare("Content-Type") == .orderedSame }) {
            headers.append(("Content-Type", "text/html; charset=utf-8"))
        }
        let statusCode = Int(status.prefix(3)) ?? 0
        let bodyForbidden = (100..<200).contains(statusCode) || statusCode == 204 || statusCode == 304
        let keepAlive = allowKeepAlive && (contentLength != nil || bodyForbidden || headOnly)
        headers.append(("Connection", keepAlive ? "keep-alive" : "close"))
        return FastCGIHTTPHeader(
            data: HTTPWireHeader.make(status: status, headers: headers),
            contentLength: contentLength,
            bodyForbidden: bodyForbidden,
            keepAlive: keepAlive
        )
    }

    private static func isValidStatus(_ status: String) -> Bool {
        guard status.count >= 3,
            status.prefix(3).allSatisfy(\.isNumber),
            let code = Int(status.prefix(3)),
            (100...599).contains(code)
        else {
            return false
        }
        if status.count == 3 { return true }
        return status[status.index(status.startIndex, offsetBy: 3)].isWhitespace
            && isValidHeaderValue(status)
    }

    private static func isValidHeaderName(_ name: String) -> Bool {
        !name.isEmpty
            && name.utf8.allSatisfy { byte in
                (48...57).contains(byte)
                    || (65...90).contains(byte)
                    || (97...122).contains(byte)
                    || byte == 45
            }
    }

    private static func isValidHeaderValue(_ value: String) -> Bool {
        value.utf8.allSatisfy { byte in byte == 9 || (32...126).contains(byte) }
    }
}
