import Foundation

actor DumpStore {
    let directoryURL: URL
    private let fileManager = FileManager.default
    private let retentionPolicy: CaptureRetentionPolicy

    init(
        rootURL: URL,
        retentionLimit: Int = CaptureRetentionPolicy.defaultItemLimit,
        retentionAge: TimeInterval = CaptureRetentionPolicy.defaultMaximumAge
    ) {
        directoryURL = rootURL.appendingPathComponent("Dumps", isDirectory: true)
        retentionPolicy = CaptureRetentionPolicy(
            itemLimit: retentionLimit,
            maximumAge: retentionAge
        )
        try? fileManager.createDirectory(at: directoryURL, withIntermediateDirectories: true)
    }

    func load() -> [CapturedDump] {
        try? retentionPolicy.prune(directoryURL: directoryURL, fileManager: fileManager)
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
        try retentionPolicy.prune(directoryURL: directoryURL, fileManager: fileManager)
    }

    func clear() throws {
        for url in (try? fileManager.contentsOfDirectory(at: directoryURL, includingPropertiesForKeys: nil)) ?? [] where url.pathExtension == "json" {
            try fileManager.removeItem(at: url)
        }
    }
}
