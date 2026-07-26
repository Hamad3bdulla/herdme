import Foundation

enum LogRotation {
    static let defaultMaximumBytes: UInt64 = 10 * 1_024 * 1_024
    static let defaultArchiveCount = 5

    static func rotateIfNeeded(
        _ logURL: URL,
        maximumBytes: UInt64 = defaultMaximumBytes,
        archiveCount: Int = defaultArchiveCount,
        fileManager: FileManager = .default
    ) throws {
        guard maximumBytes > 0, archiveCount >= 0,
              let attributes = try? fileManager.attributesOfItem(atPath: logURL.path),
              let size = attributes[.size] as? NSNumber,
              size.uint64Value >= maximumBytes else {
            return
        }

        if archiveCount == 0 {
            try fileManager.removeItem(at: logURL)
            return
        }

        let oldest = archiveURL(for: logURL, index: archiveCount)
        if fileManager.fileExists(atPath: oldest.path) {
            try fileManager.removeItem(at: oldest)
        }
        if archiveCount > 1 {
            for index in stride(from: archiveCount - 1, through: 1, by: -1) {
                let source = archiveURL(for: logURL, index: index)
                guard fileManager.fileExists(atPath: source.path) else { continue }
                try fileManager.moveItem(
                    at: source,
                    to: archiveURL(for: logURL, index: index + 1)
                )
            }
        }
        try fileManager.moveItem(at: logURL, to: archiveURL(for: logURL, index: 1))
    }

    private static func archiveURL(for logURL: URL, index: Int) -> URL {
        URL(fileURLWithPath: logURL.path + ".\(index)")
    }
}
