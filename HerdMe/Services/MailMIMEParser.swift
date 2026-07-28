import Foundation

#if canImport(AppKit)
    import AppKit
#endif

struct MailMIMEContent: Equatable {
    var plainText: String?
    var html: String?
}

enum MailMIMEParser {
    static let maximumNestingDepth = 32
    static let maximumPartCount = 10_000

    private static let maximumBoundaryBytes = 70
    private static let maximumPreviewCharacters = 4 * 1_024 * 1_024
    private static let previewStyle =
        "body{font:14px system-ui;margin:18px;line-height:1.45;overflow-wrap:anywhere}img{max-width:100%;height:auto}pre{white-space:pre-wrap}"
    private static let previewStyleHash = "48hOXKVM1rwpXip/9XRIr0XijcrNP/RHiD+a7aSGrzg="

    static func parse(_ raw: String) -> MailMIMEContent {
        var remainingParts = maximumPartCount
        return parsePart(
            raw.replacingOccurrences(of: "\r\n", with: "\n"),
            depth: 0,
            remainingParts: &remainingParts
        )
    }

    static func decodedHeader(_ value: String) -> String {
        let pattern = #"=\?([^?]+)\?([bBqQ])\?([^?]*)\?="#
        guard let expression = try? NSRegularExpression(pattern: pattern) else { return value }
        let source = value as NSString
        let matches = expression.matches(
            in: value,
            range: NSRange(location: 0, length: source.length)
        )
        guard !matches.isEmpty else { return value }
        var output = value
        for match in matches.reversed() {
            guard let range = Range(match.range, in: output),
                let charsetRange = Range(match.range(at: 1), in: value),
                let encodingRange = Range(match.range(at: 2), in: value),
                let payloadRange = Range(match.range(at: 3), in: value)
            else { continue }
            let charset = String(value[charsetRange])
            let encoding = String(value[encodingRange]).lowercased()
            let payload = String(value[payloadRange])
            let data =
                encoding == "b"
                ? Data(base64Encoded: payload, options: .ignoreUnknownCharacters)
                : decodeQuotedPrintable(payload.replacingOccurrences(of: "_", with: " "))
            guard let data, let decoded = decode(data, charset: charset) else { continue }
            output.replaceSubrange(range, with: decoded)
        }
        return output.replacingOccurrences(of: "?= =?", with: "?==?")
    }

    static func plainText(fromHTML html: String) -> String {
        #if canImport(AppKit)
            guard let data = html.data(using: .utf8),
                let attributed = try? NSAttributedString(
                    data: data,
                    options: [
                        .documentType: NSAttributedString.DocumentType.html,
                        .characterEncoding: String.Encoding.utf8.rawValue
                    ],
                    documentAttributes: nil
                )
            else {
                return html.replacingOccurrences(of: "<[^>]+>", with: " ", options: .regularExpression)
            }
            return attributed.string
                .replacingOccurrences(of: "[ \t]+", with: " ", options: .regularExpression)
                .replacingOccurrences(of: "\n{3,}", with: "\n\n", options: .regularExpression)
                .trimmingCharacters(in: .whitespacesAndNewlines)
        #else
            return html.replacingOccurrences(of: "<[^>]+>", with: " ", options: .regularExpression)
                .replacingOccurrences(of: "[ \t]+", with: " ", options: .regularExpression)
                .replacingOccurrences(of: "\n{3,}", with: "\n\n", options: .regularExpression)
                .trimmingCharacters(in: .whitespacesAndNewlines)
        #endif
    }

    static func safeHTMLDocument(_ html: String) -> String {
        let policy =
            "default-src 'none'; base-uri 'none'; form-action 'none'; frame-src 'none'; object-src 'none'; img-src data: cid:; font-src 'none'; style-src 'sha256-\(previewStyleHash)'; sandbox"
        let preview =
            html.count <= maximumPreviewCharacters
            ? html
            : String(html.prefix(maximumPreviewCharacters)) + "<p>[Preview truncated]</p>"
        return """
            <!doctype html><html><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="\(policy)">
            <meta name="color-scheme" content="light dark">
            <style>\(previewStyle)</style>
            </head><body>\(preview)</body></html>
            """
    }

    static func escapedHTML(_ text: String) -> String {
        text.replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
            .replacingOccurrences(of: "\"", with: "&quot;")
            .replacingOccurrences(of: "'", with: "&#39;")
    }

    private static func parsePart(
        _ raw: String,
        depth: Int,
        remainingParts: inout Int
    ) -> MailMIMEContent {
        guard depth <= maximumNestingDepth, remainingParts > 0 else {
            return MailMIMEContent()
        }
        remainingParts -= 1

        let (headers, body) = split(raw)
        let contentType = headers["content-type"] ?? "text/plain; charset=utf-8"
        let mediaType =
            contentType.split(separator: ";", maxSplits: 1).first?
            .trimmingCharacters(in: .whitespacesAndNewlines).lowercased() ?? "text/plain"
        if mediaType.hasPrefix("multipart/"),
            let boundary = parameter("boundary", in: contentType),
            isValidBoundary(boundary)
        {
            var content = MailMIMEContent()
            visitMultipartParts(
                body,
                boundary: boundary,
                maximumParts: remainingParts
            ) { part in
                let parsed = parsePart(
                    String(part),
                    depth: depth + 1,
                    remainingParts: &remainingParts
                )
                if content.plainText == nil, let plain = parsed.plainText { content.plainText = plain }
                if content.html == nil, let html = parsed.html { content.html = html }
                return remainingParts > 0 && (content.plainText == nil || content.html == nil)
            }
            return content
        }
        if (headers["content-disposition"] ?? "").lowercased().hasPrefix("attachment") {
            return MailMIMEContent()
        }
        guard mediaType == "text/plain" || mediaType == "text/html" else {
            return MailMIMEContent()
        }
        let transferEncoding = (headers["content-transfer-encoding"] ?? "").lowercased()
        let data: Data
        if transferEncoding == "base64" {
            data = Data(base64Encoded: body, options: .ignoreUnknownCharacters) ?? Data()
        } else if transferEncoding == "quoted-printable" {
            data = decodeQuotedPrintable(body)
        } else {
            data = Data(body.utf8)
        }
        let decoded =
            decode(data, charset: parameter("charset", in: contentType) ?? "utf-8")
            ?? String(decoding: data, as: UTF8.self)
        return mediaType == "text/html"
            ? MailMIMEContent(html: decoded)
            : MailMIMEContent(plainText: decoded)
    }

    private static func split(_ raw: String) -> ([String: String], String) {
        let separator = raw.range(of: "\n\n")
        let headerText = separator.map { String(raw[..<$0.lowerBound]) } ?? raw
        let body = separator.map { String(raw[$0.upperBound...]) } ?? ""
        var headers: [String: String] = [:]
        var currentKey: String?
        for line in headerText.components(separatedBy: "\n") {
            if line.first?.isWhitespace == true, let currentKey {
                headers[currentKey, default: ""] += " " + line.trimmingCharacters(in: .whitespaces)
                continue
            }
            guard let colon = line.firstIndex(of: ":") else { continue }
            let key = line[..<colon].trimmingCharacters(in: .whitespaces).lowercased()
            headers[key] = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            currentKey = key
        }
        return (headers, body)
    }

    private static func parameter(_ name: String, in header: String) -> String? {
        let escaped = NSRegularExpression.escapedPattern(for: name)
        let pattern = "(?:^|;)\\s*\(escaped)\\s*=\\s*(?:\"([^\"]*)\"|([^;\\s]*))"
        guard let expression = try? NSRegularExpression(pattern: pattern, options: .caseInsensitive),
            let match = expression.firstMatch(
                in: header,
                range: NSRange(header.startIndex..., in: header)
            )
        else { return nil }
        for index in 1...2 where match.range(at: index).location != NSNotFound {
            if let range = Range(match.range(at: index), in: header) { return String(header[range]) }
        }
        return nil
    }

    private static func isValidBoundary(_ boundary: String) -> Bool {
        let bytes = Array(boundary.utf8)
        guard !bytes.isEmpty,
            bytes.count <= maximumBoundaryBytes,
            bytes.last != 0x20
        else {
            return false
        }
        return bytes.allSatisfy { byte in
            switch byte {
            case 0x30...0x39, 0x41...0x5A, 0x61...0x7A,
                0x27, 0x28, 0x29, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F,
                0x3A, 0x3D, 0x3F, 0x5F, 0x20:
                true
            default:
                false
            }
        }
    }

    private static func visitMultipartParts(
        _ body: String,
        boundary: String,
        maximumParts: Int,
        visit: (Substring) -> Bool
    ) {
        guard maximumParts > 0 else { return }
        let marker = "--" + boundary
        var searchStart = body.startIndex
        var partStart: String.Index?
        var visitedParts = 0

        while visitedParts < maximumParts,
            let boundaryLine = nextBoundaryLine(
                in: body,
                marker: marker,
                startingAt: searchStart
            )
        {
            if let partStart {
                let part = trimNewlines(body[partStart..<boundaryLine.start])
                if !part.isEmpty {
                    visitedParts += 1
                    if !visit(part) { return }
                }
            }
            if boundaryLine.isClosing { return }
            partStart = boundaryLine.contentStart
            searchStart = boundaryLine.contentStart
        }

        if visitedParts < maximumParts, let partStart {
            let part = trimNewlines(body[partStart...])
            if !part.isEmpty { _ = visit(part) }
        }
    }

    private static func nextBoundaryLine(
        in body: String,
        marker: String,
        startingAt start: String.Index
    ) -> (start: String.Index, contentStart: String.Index, isClosing: Bool)? {
        var searchStart = start
        while searchStart < body.endIndex,
            let markerRange = body.range(
                of: marker,
                range: searchStart..<body.endIndex
            )
        {
            let startsLine =
                markerRange.lowerBound == body.startIndex
                || body[body.index(before: markerRange.lowerBound)] == "\n"
            let lineEnd =
                body[markerRange.upperBound...].firstIndex(of: "\n")
                ?? body.endIndex
            var suffix = body[markerRange.upperBound..<lineEnd]
            let isClosing = suffix.hasPrefix("--")
            if isClosing { suffix = suffix.dropFirst(2) }
            let hasValidSuffix = suffix.allSatisfy {
                $0 == " " || $0 == "\t" || $0 == "\r"
            }

            if startsLine && hasValidSuffix {
                let contentStart =
                    lineEnd < body.endIndex
                    ? body.index(after: lineEnd)
                    : body.endIndex
                return (markerRange.lowerBound, contentStart, isClosing)
            }
            searchStart = markerRange.upperBound
        }
        return nil
    }

    private static func trimNewlines(_ value: Substring) -> Substring {
        var start = value.startIndex
        var end = value.endIndex
        while start < end, value[start] == "\n" || value[start] == "\r" {
            start = value.index(after: start)
        }
        while start < end {
            let previous = value.index(before: end)
            guard value[previous] == "\n" || value[previous] == "\r" else { break }
            end = previous
        }
        return value[start..<end]
    }

    private static func decodeQuotedPrintable(_ value: String) -> Data {
        let bytes = Array(value.utf8)
        var output = Data()
        var index = 0
        while index < bytes.count {
            if bytes[index] == 61 {
                if index + 1 < bytes.count, bytes[index + 1] == 10 {
                    index += 2
                    continue
                }
                if index + 2 < bytes.count,
                    let high = hex(bytes[index + 1]), let low = hex(bytes[index + 2])
                {
                    output.append(high << 4 | low)
                    index += 3
                    continue
                }
            }
            output.append(bytes[index])
            index += 1
        }
        return output
    }

    private static func decode(_ data: Data, charset: String) -> String? {
        switch charset.lowercased().replacingOccurrences(of: "_", with: "-") {
        case "utf-8", "utf8", "us-ascii": String(data: data, encoding: .utf8)
        case "iso-8859-1", "latin1", "latin-1": String(data: data, encoding: .isoLatin1)
        case "windows-1252", "cp1252": String(data: data, encoding: .windowsCP1252)
        default: String(data: data, encoding: .utf8) ?? String(data: data, encoding: .isoLatin1)
        }
    }

    private static func hex(_ byte: UInt8) -> UInt8? {
        switch byte {
        case 48...57: byte - 48
        case 65...70: byte - 55
        case 97...102: byte - 87
        default: nil
        }
    }
}
