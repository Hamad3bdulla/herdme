import Foundation

actor DumpStore {
    let directoryURL: URL
    private let fileManager = FileManager.default

    init(rootURL: URL) {
        directoryURL = rootURL.appendingPathComponent("Dumps", isDirectory: true)
        try? fileManager.createDirectory(at: directoryURL, withIntermediateDirectories: true)
    }

    func load() -> [CapturedDump] {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return ((try? fileManager.contentsOfDirectory(at: directoryURL, includingPropertiesForKeys: nil)) ?? [])
            .filter { $0.pathExtension == "json" }
            .compactMap { url in
                guard let data = try? Data(contentsOf: url) else { return nil }
                return try? decoder.decode(CapturedDump.self, from: data)
            }
            .sorted { $0.receivedAt > $1.receivedAt }
    }

    func save(_ dump: CapturedDump) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        encoder.dateEncodingStrategy = .iso8601
        try encoder.encode(dump).write(
            to: directoryURL.appendingPathComponent(dump.id.uuidString + ".json"),
            options: .atomic
        )
    }

    func clear() throws {
        for url in (try? fileManager.contentsOfDirectory(at: directoryURL, includingPropertiesForKeys: nil)) ?? [] where url.pathExtension == "json" {
            try fileManager.removeItem(at: url)
        }
    }
}
