import Foundation

struct LocalLogFile: Identifiable, Hashable, Sendable {
    let url: URL
    let relativePath: String
    let size: Int64
    let modifiedAt: Date

    var id: String { url.path }
}

struct LogStore {
    private static let appendLock = NSLock()
    let rootURL: URL

    init(rootURL: URL) {
        self.rootURL = rootURL.standardizedFileURL.resolvingSymlinksInPath()
    }

    func files() -> [LocalLogFile] {
        let keys: [URLResourceKey] = [.isRegularFileKey, .fileSizeKey, .contentModificationDateKey]
        guard let enumerator = FileManager.default.enumerator(
            at: rootURL,
            includingPropertiesForKeys: keys,
            options: [.skipsHiddenFiles]
        ) else {
            return []
        }

        var files: [LocalLogFile] = []
        for case let url as URL in enumerator {
            guard let values = try? url.resourceValues(forKeys: Set(keys)),
                  values.isRegularFile == true else { continue }
            let resolvedURL = url.standardizedFileURL.resolvingSymlinksInPath()
            files.append(LocalLogFile(
                url: resolvedURL,
                relativePath: String(resolvedURL.path.dropFirst(rootURL.path.count + 1)),
                size: Int64(values.fileSize ?? 0),
                modifiedAt: values.contentModificationDate ?? .distantPast
            ))
        }
        return files.sorted {
            $0.relativePath.localizedStandardCompare($1.relativePath) == .orderedAscending
        }
    }

    func contents(of file: LocalLogFile, maximumBytes: UInt64 = 4 * 1_024 * 1_024) throws -> String {
        let handle = try FileHandle(forReadingFrom: file.url)
        defer { try? handle.close() }
        let length = try handle.seekToEnd()
        if length > maximumBytes {
            try handle.seek(toOffset: length - maximumBytes)
        } else {
            try handle.seek(toOffset: 0)
        }
        return String(decoding: try handle.readToEnd() ?? Data(), as: UTF8.self)
    }

    func append(_ message: String, at date: Date = Date()) throws {
        let trimmed = message.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }

        Self.appendLock.lock()
        defer { Self.appendLock.unlock() }

        try FileManager.default.createDirectory(at: rootURL, withIntermediateDirectories: true)
        let logURL = rootURL.appendingPathComponent("app.log")
        if !FileManager.default.fileExists(atPath: logURL.path) {
            try Data().write(to: logURL, options: .atomic)
        }

        let handle = try FileHandle(forWritingTo: logURL)
        defer { try? handle.close() }
        try handle.seekToEnd()
        let timestamp = ISO8601DateFormatter().string(from: date)
        try handle.write(contentsOf: Data("[\(timestamp)] \(trimmed)\n".utf8))
    }
}
