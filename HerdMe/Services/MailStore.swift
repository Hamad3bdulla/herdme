import Foundation

enum MailStoreError: LocalizedError {
    case messageNotFound
    case invalidMessage

    var errorDescription: String? {
        switch self {
        case .messageNotFound:
            String(localized: "This mail message no longer exists.")
        case .invalidMessage:
            String(localized: "HerdMe could not read this mail message because its saved data is invalid.")
        }
    }
}

actor MailStore {
    private static let maximumIndexBytes = 4 * 1_024 * 1_024

    private struct IndexEntry: Codable, Equatable, Sendable {
        let id: UUID
        let sender: String
        let recipients: [String]
        let subject: String
        let receivedAt: Date
        let fileSize: Int64
        let modifiedAtMilliseconds: Int64

        var summary: CapturedMailSummary {
            CapturedMailSummary(
                id: id,
                sender: sender,
                recipients: recipients,
                subject: subject,
                receivedAt: receivedAt
            )
        }
    }

    private struct IndexFile: Codable, Equatable, Sendable {
        let schemaVersion: Int
        let entries: [IndexEntry]
    }

    private struct MessageFile: Sendable {
        let id: UUID
        let url: URL
        let size: Int64
        let modifiedAtMilliseconds: Int64
    }

    nonisolated let directoryURL: URL
    nonisolated let indexURL: URL
    private let fileManager = FileManager.default
    private let retentionPolicy: CaptureRetentionPolicy
    private var cachedIndex: IndexFile?

    init(
        rootURL: URL,
        retentionLimit: Int = CaptureRetentionPolicy.defaultItemLimit,
        retentionAge: TimeInterval = CaptureRetentionPolicy.defaultMaximumAge
    ) {
        directoryURL = rootURL.appendingPathComponent("Mail", isDirectory: true)
        indexURL = directoryURL.appendingPathComponent("mail-v1.index")
        retentionPolicy = CaptureRetentionPolicy(
            itemLimit: retentionLimit,
            maximumAge: retentionAge
        )
        try? fileManager.createDirectory(at: directoryURL, withIntermediateDirectories: true)
    }

    func loadSummaries() -> [CapturedMailSummary] {
        try? retentionPolicy.prune(directoryURL: directoryURL, fileManager: fileManager)
        return synchronizedIndex().entries.map(\.summary)
    }

    func message(id: UUID) throws -> CapturedMail {
        let url = messageURL(id: id)
        guard fileManager.fileExists(atPath: url.path) else {
            throw MailStoreError.messageNotFound
        }
        guard let message = decodeMessage(at: url), message.id == id else {
            throw MailStoreError.invalidMessage
        }
        return message
    }

    func save(_ message: CapturedMail) throws {
        try encode(message).write(to: messageURL(id: message.id), options: .atomic)
        try retentionPolicy.prune(directoryURL: directoryURL, fileManager: fileManager)
        cachedIndex = nil
        _ = synchronizedIndex(knownMessages: [message.id: message])
    }

    func delete(id: UUID) throws {
        let url = messageURL(id: id)
        if fileManager.fileExists(atPath: url.path) {
            try fileManager.removeItem(at: url)
        }
        cachedIndex = nil
        _ = synchronizedIndex()
    }

    func clear() throws {
        for url in (try? fileManager.contentsOfDirectory(at: directoryURL, includingPropertiesForKeys: nil)) ?? []
        where url.pathExtension == "json" || url == indexURL {
            try fileManager.removeItem(at: url)
        }
        cachedIndex = IndexFile(schemaVersion: 1, entries: [])
    }

    private func synchronizedIndex(
        knownMessages: [UUID: CapturedMail] = [:]
    ) -> IndexFile {
        let files = messageFiles()
        let existing = cachedIndex ?? loadIndex()
        var existingByID: [UUID: IndexEntry] = [:]
        for entry in existing?.entries ?? [] {
            existingByID[entry.id] = entry
        }
        let entries = files.compactMap { file -> IndexEntry? in
            if let entry = existingByID[file.id],
                entry.fileSize == file.size,
                entry.modifiedAtMilliseconds == file.modifiedAtMilliseconds
            {
                return entry
            }
            guard let message = knownMessages[file.id] ?? decodeMessage(at: file.url),
                message.id == file.id
            else {
                return nil
            }
            return IndexEntry(
                id: message.id,
                sender: message.sender,
                recipients: message.recipients,
                subject: message.subject,
                receivedAt: message.receivedAt,
                fileSize: file.size,
                modifiedAtMilliseconds: file.modifiedAtMilliseconds
            )
        }
        .sorted {
            if $0.receivedAt != $1.receivedAt { return $0.receivedAt > $1.receivedAt }
            return $0.id.uuidString < $1.id.uuidString
        }
        let index = IndexFile(schemaVersion: 1, entries: entries)
        if index != existing {
            try? encode(index).write(to: indexURL, options: .atomic)
        }
        cachedIndex = index
        return index
    }

    private func loadIndex() -> IndexFile? {
        guard let values = try? indexURL.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey]),
            values.isRegularFile == true,
            let fileSize = values.fileSize,
            fileSize <= Self.maximumIndexBytes,
            let data = try? Data(contentsOf: indexURL),
            let index = try? decoder().decode(IndexFile.self, from: data),
            index.schemaVersion == 1
        else {
            return nil
        }
        return index
    }

    private func messageFiles() -> [MessageFile] {
        let keys: Set<URLResourceKey> = [.isRegularFileKey, .fileSizeKey, .contentModificationDateKey]
        return
            ((try? fileManager.contentsOfDirectory(
                at: directoryURL,
                includingPropertiesForKeys: Array(keys),
                options: [.skipsHiddenFiles]
            )) ?? []).compactMap { url in
                guard url.pathExtension == "json",
                    let id = UUID(uuidString: url.deletingPathExtension().lastPathComponent),
                    let values = try? url.resourceValues(forKeys: keys),
                    values.isRegularFile == true
                else {
                    return nil
                }
                return MessageFile(
                    id: id,
                    url: url,
                    size: Int64(values.fileSize ?? 0),
                    modifiedAtMilliseconds: Self.milliseconds(values.contentModificationDate ?? .distantPast)
                )
            }
    }

    private func messageURL(id: UUID) -> URL {
        directoryURL.appendingPathComponent(id.uuidString + ".json")
    }

    private func decodeMessage(at url: URL) -> CapturedMail? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        return try? decoder().decode(CapturedMail.self, from: data)
    }

    private func decoder() -> JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return decoder
    }

    private func encode<T: Encodable>(_ value: T) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        encoder.dateEncodingStrategy = .iso8601
        return try encoder.encode(value)
    }

    private static func milliseconds(_ date: Date) -> Int64 {
        Int64((date.timeIntervalSince1970 * 1_000).rounded())
    }
}
