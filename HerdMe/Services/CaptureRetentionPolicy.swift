import Foundation

struct CaptureRetentionPolicy: Sendable {
    static let defaultItemLimit = 1_000
    static let defaultMaximumAge: TimeInterval = 30 * 24 * 60 * 60

    let itemLimit: Int
    let maximumAge: TimeInterval

    init(
        itemLimit: Int = defaultItemLimit,
        maximumAge: TimeInterval = defaultMaximumAge
    ) {
        self.itemLimit = max(1, itemLimit)
        self.maximumAge = max(1, maximumAge)
    }

    func prune(
        directoryURL: URL,
        now: Date = Date(),
        fileManager: FileManager = .default
    ) throws {
        let keys: Set<URLResourceKey> = [.isRegularFileKey, .contentModificationDateKey]
        let candidates = try fileManager.contentsOfDirectory(
            at: directoryURL,
            includingPropertiesForKeys: Array(keys),
            options: [.skipsHiddenFiles]
        ).filter { $0.pathExtension == "json" }
        var retained: [(url: URL, modifiedAt: Date)] = []
        let cutoff = now.addingTimeInterval(-maximumAge)
        for url in candidates {
            let values = try url.resourceValues(forKeys: keys)
            guard values.isRegularFile == true else { continue }
            let modifiedAt = values.contentModificationDate ?? .distantPast
            if modifiedAt < cutoff {
                try fileManager.removeItem(at: url)
            } else {
                retained.append((url, modifiedAt))
            }
        }
        retained.sort {
            if $0.modifiedAt == $1.modifiedAt { return $0.url.path < $1.url.path }
            return $0.modifiedAt > $1.modifiedAt
        }
        for entry in retained.dropFirst(itemLimit) {
            try fileManager.removeItem(at: entry.url)
        }
    }
}
