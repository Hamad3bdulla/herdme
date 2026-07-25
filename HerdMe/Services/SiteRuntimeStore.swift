import Foundation

enum SiteRuntimeKind {
    case php
    case node

    var fileName: String {
        switch self {
        case .php: ".herdme-php"
        case .node: ".herdme-node"
        }
    }
}

enum SiteRuntimeStoreError: LocalizedError {
    case invalidVersion

    var errorDescription: String? {
        "The selected runtime version is invalid."
    }
}

struct SiteRuntimeStore {
    private let fileManager: FileManager

    init(fileManager: FileManager = .default) {
        self.fileManager = fileManager
    }

    func set(_ version: String?, kind: SiteRuntimeKind, for site: SiteProject) throws {
        let fileURL = site.path.appendingPathComponent(kind.fileName)
        guard let version else {
            if fileManager.fileExists(atPath: fileURL.path) {
                try fileManager.removeItem(at: fileURL)
            }
            return
        }

        let normalized = version.trimmingCharacters(in: .whitespacesAndNewlines)
        guard Self.isValid(version: normalized, kind: kind) else {
            throw SiteRuntimeStoreError.invalidVersion
        }
        try (normalized + "\n").write(to: fileURL, atomically: true, encoding: .utf8)
    }

    static func isValid(version: String, kind: SiteRuntimeKind) -> Bool {
        guard !version.isEmpty, version.count <= 20 else { return false }
        switch kind {
        case .php:
            let components = version.split(separator: ".")
            return components.count == 2 && components.allSatisfy { !$0.isEmpty && $0.allSatisfy(\.isNumber) }
        case .node:
            return version.allSatisfy(\.isNumber)
        }
    }
}
