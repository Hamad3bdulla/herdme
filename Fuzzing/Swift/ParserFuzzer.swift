import Foundation

private let maximumFuzzInputBytes = 1024 * 1024

private func exercise(_ bytes: UnsafeRawBufferPointer) {
    #if HERDME_FUZZ_MIME
    let raw = String(decoding: bytes, as: UTF8.self)
    let content = MailMIMEParser.parse(raw)
    _ = content.plainText?.utf8.count
    _ = content.html?.utf8.count
    _ = MailMIMEParser.decodedHeader(raw)
    _ = MailMIMEParser.plainText(fromHTML: raw)
    _ = MailMIMEParser.safeHTMLDocument(raw)
    _ = MailMIMEParser.escapedHTML(raw)
    #elseif HERDME_FUZZ_PHP_SERIALIZATION
    var parser = PHPSerializationParser(data: Data(bytes))
    if let value = try? parser.parse() {
        _ = value.rendered()
        _ = value.firstString(forKeysContaining: ["message", "exception", "file"])
    }
    #elseif HERDME_FUZZ_FASTCGI
    let input = Data(bytes)
    var parser = FastCGIHTTPStreamParser(
        headOnly: input.first.map { $0 & 1 == 1 } ?? false,
        allowKeepAlive: input.first.map { $0 & 2 == 2 } ?? false
    )
    let chunkSize = max(1, input.first.map { Int($0) } ?? 1)
    var offset = input.startIndex
    do {
        while offset < input.endIndex {
            let remaining = input.distance(from: offset, to: input.endIndex)
            let end = input.index(offset, offsetBy: min(chunkSize, remaining))
            try parser.consume(Data(input[offset..<end])) { output in
                _ = output.count
            }
            offset = end
        }
        try parser.finish()
    } catch {
        _ = error.localizedDescription
    }
    #else
    #error("Define exactly one HerdMe Swift fuzzer")
    #endif
}

@_cdecl("LLVMFuzzerTestOneInput")
public func herdMeFuzzerTestOneInput(
    _ data: UnsafePointer<UInt8>?,
    _ size: Int
) -> Int32 {
    guard size >= 0, size <= maximumFuzzInputBytes else { return 0 }
    guard size == 0 || data != nil else { return 0 }
    exercise(UnsafeRawBufferPointer(start: data, count: size))
    return 0
}

#if HERDME_FUZZ_STANDALONE
@main
private enum CorpusReplay {
    static func main() throws {
        let arguments = Array(CommandLine.arguments.dropFirst())
        guard !arguments.isEmpty else {
            FileHandle.standardError.write(Data("Usage: fuzzer <corpus-file-or-directory> [...]\n".utf8))
            throw CorpusReplayError.missingCorpus
        }
        for argument in arguments {
            try replay(URL(fileURLWithPath: argument))
        }
    }

    private static func replay(_ url: URL) throws {
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory) else {
            throw CorpusReplayError.missingPath(url.path)
        }
        if !isDirectory.boolValue {
            try replayFile(url)
            return
        }
        guard let enumerator = FileManager.default.enumerator(
            at: url,
            includingPropertiesForKeys: [.isRegularFileKey],
            options: [.skipsHiddenFiles]
        ) else {
            throw CorpusReplayError.unreadablePath(url.path)
        }
        for case let fileURL as URL in enumerator {
            let values = try fileURL.resourceValues(forKeys: [.isRegularFileKey])
            if values.isRegularFile == true { try replayFile(fileURL) }
        }
    }

    private static func replayFile(_ url: URL) throws {
        let data = try Data(contentsOf: url, options: [.mappedIfSafe])
        guard data.count <= maximumFuzzInputBytes else {
            throw CorpusReplayError.oversizedFile(url.path)
        }
        data.withUnsafeBytes { bytes in
            _ = herdMeFuzzerTestOneInput(
                bytes.bindMemory(to: UInt8.self).baseAddress,
                bytes.count
            )
        }
    }
}

private enum CorpusReplayError: LocalizedError {
    case missingCorpus
    case missingPath(String)
    case unreadablePath(String)
    case oversizedFile(String)

    var errorDescription: String? {
        switch self {
        case .missingCorpus:
            "At least one corpus path is required."
        case let .missingPath(path):
            "Corpus path does not exist: \(path)"
        case let .unreadablePath(path):
            "Corpus path cannot be enumerated: \(path)"
        case let .oversizedFile(path):
            "Corpus file exceeds the 1 MiB safety limit: \(path)"
        }
    }
}
#endif
