import Foundation

actor MailStore {
    let directoryURL: URL
    private let fileManager = FileManager.default

    init(rootURL: URL) {
        directoryURL = rootURL.appendingPathComponent("Mail", isDirectory: true)
        try? fileManager.createDirectory(at: directoryURL, withIntermediateDirectories: true)
    }

    func load() -> [CapturedMail] {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return ((try? fileManager.contentsOfDirectory(at: directoryURL, includingPropertiesForKeys: nil)) ?? [])
            .filter { $0.pathExtension == "json" }
            .compactMap { url in
                guard let data = try? Data(contentsOf: url) else { return nil }
                return try? decoder.decode(CapturedMail.self, from: data)
            }
            .sorted { $0.receivedAt > $1.receivedAt }
    }

    func save(_ message: CapturedMail) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        encoder.dateEncodingStrategy = .iso8601
        let data = try encoder.encode(message)
        try data.write(to: directoryURL.appendingPathComponent(message.id.uuidString + ".json"), options: .atomic)
    }

    func delete(_ message: CapturedMail) throws {
        let url = directoryURL.appendingPathComponent(message.id.uuidString + ".json")
        if fileManager.fileExists(atPath: url.path) {
            try fileManager.removeItem(at: url)
        }
    }

    func clear() throws {
        for url in (try? fileManager.contentsOfDirectory(at: directoryURL, includingPropertiesForKeys: nil)) ?? [] where url.pathExtension == "json" {
            try fileManager.removeItem(at: url)
        }
    }
}
