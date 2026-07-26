import Foundation

enum ArchiveSafetyError: LocalizedError {
    case empty
    case tooManyEntries(Int)
    case expandedSizeLimit(Int64)
    case unsafePath
    case unsupportedEntryType
    case invalidListing
    case extractedTreeChanged

    var errorDescription: String? {
        switch self {
        case .empty:
            "The downloaded archive is empty."
        case let .tooManyEntries(limit):
            "The downloaded archive contains more than \(limit) entries."
        case let .expandedSizeLimit(limit):
            "The downloaded archive expands beyond the supported \(limit)-byte limit."
        case .unsafePath:
            "The downloaded archive contains an unsafe path."
        case .unsupportedEntryType:
            "The downloaded archive contains a link or special file."
        case .invalidListing:
            "The downloaded archive has an invalid entry listing."
        case .extractedTreeChanged:
            "The extracted archive did not match its validated contents."
        }
    }
}

enum TarArchivePolicy {
    static let maximumEntries = 20_000
    static let maximumExpandedBytes: Int64 = 2 * 1_024 * 1_024 * 1_024

    static func validate(
        nameListing: String,
        verboseListing: String,
        entryLimit: Int = maximumEntries,
        expandedByteLimit: Int64 = maximumExpandedBytes
    ) throws {
        guard entryLimit > 0, expandedByteLimit > 0 else {
            throw ArchiveSafetyError.invalidListing
        }
        let names = nameListing.split(whereSeparator: \.isNewline).map(String.init)
        let details = verboseListing.split(whereSeparator: \.isNewline).map(String.init)
        guard !names.isEmpty else { throw ArchiveSafetyError.empty }
        guard names.count <= entryLimit else { throw ArchiveSafetyError.tooManyEntries(entryLimit) }
        guard details.count == names.count else { throw ArchiveSafetyError.invalidListing }

        var normalizedNames = Set<String>()
        for name in names {
            let normalized = try normalizedPath(name)
            guard normalizedNames.insert(normalized.lowercased()).inserted else {
                throw ArchiveSafetyError.unsafePath
            }
        }

        var expandedBytes: Int64 = 0
        for (index, detail) in details.enumerated() {
            guard let type = detail.first else { throw ArchiveSafetyError.invalidListing }
            if type == "l" {
                let parts = detail.components(separatedBy: " -> ")
                guard parts.count == 2 else { throw ArchiveSafetyError.invalidListing }
                try validateSymbolicLink(entryPath: names[index], target: parts[1])
                continue
            }
            guard type == "-" || type == "d" else {
                throw ArchiveSafetyError.unsupportedEntryType
            }
            let fields = detail.split(whereSeparator: \.isWhitespace)
            guard fields.count >= 8, let size = Int64(fields[4]), size >= 0 else {
                throw ArchiveSafetyError.invalidListing
            }
            guard expandedBytes <= expandedByteLimit - size else {
                throw ArchiveSafetyError.expandedSizeLimit(expandedByteLimit)
            }
            expandedBytes += size
        }
    }

    static func validateExtractedTree(
        at rootURL: URL,
        fileManager: FileManager = .default,
        entryLimit: Int = maximumEntries,
        expandedByteLimit: Int64 = maximumExpandedBytes
    ) throws {
        let root = rootURL.resolvingSymlinksInPath().standardizedFileURL
        let prefix = root.path.hasSuffix("/") ? root.path : root.path + "/"
        var enumerationFailed = false
        guard let enumerator = fileManager.enumerator(
            at: root,
            includingPropertiesForKeys: [.isDirectoryKey, .isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey],
            options: [],
            errorHandler: { _, _ in
                enumerationFailed = true
                return false
            }
        ) else {
            throw ArchiveSafetyError.extractedTreeChanged
        }

        var count = 0
        var expandedBytes: Int64 = 0
        for case let url as URL in enumerator {
            count += 1
            guard count <= entryLimit else { throw ArchiveSafetyError.tooManyEntries(entryLimit) }
            let standardized = url.standardizedFileURL
            guard standardized.path.hasPrefix(prefix) else { throw ArchiveSafetyError.unsafePath }
            let values = try url.resourceValues(forKeys: [
                .isDirectoryKey, .isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey
            ])
            if values.isSymbolicLink == true {
                let relativePath = String(standardized.path.dropFirst(prefix.count))
                let target = try fileManager.destinationOfSymbolicLink(atPath: url.path)
                try validateSymbolicLink(entryPath: relativePath, target: target)
                let resolved = url.resolvingSymlinksInPath().standardizedFileURL
                guard resolved.path.hasPrefix(prefix) else { throw ArchiveSafetyError.unsafePath }
                continue
            }
            guard values.isDirectory == true || values.isRegularFile == true else {
                throw ArchiveSafetyError.unsupportedEntryType
            }
            if values.isRegularFile == true {
                let size = Int64(values.fileSize ?? 0)
                guard size >= 0, expandedBytes <= expandedByteLimit - size else {
                    throw ArchiveSafetyError.expandedSizeLimit(expandedByteLimit)
                }
                expandedBytes += size
            }
        }
        guard !enumerationFailed else { throw ArchiveSafetyError.extractedTreeChanged }
        guard count > 0 else { throw ArchiveSafetyError.empty }
    }

    private static func normalizedPath(_ value: String) throws -> String {
        guard !value.isEmpty, value.utf8.count <= 1_024,
              !value.hasPrefix("/"), !value.contains("//"),
              !value.contains("\\"), !value.contains("\0") else {
            throw ArchiveSafetyError.unsafePath
        }
        let components = value.split(separator: "/", omittingEmptySubsequences: false)
        guard !components.contains(where: { $0 == "." || $0 == ".." }) else {
            throw ArchiveSafetyError.unsafePath
        }
        return components.filter { !$0.isEmpty }.joined(separator: "/")
    }

    private static func validateSymbolicLink(entryPath: String, target: String) throws {
        guard !target.isEmpty, target.utf8.count <= 1_024,
              !target.hasPrefix("/"), !target.contains("\\"), !target.contains("\0") else {
            throw ArchiveSafetyError.unsafePath
        }
        var resolved = try normalizedPath(entryPath).split(separator: "/").map(String.init)
        guard !resolved.isEmpty else { throw ArchiveSafetyError.unsafePath }
        resolved.removeLast()
        for component in target.split(separator: "/", omittingEmptySubsequences: false) {
            if component.isEmpty || component == "." { continue }
            if component == ".." {
                guard !resolved.isEmpty else { throw ArchiveSafetyError.unsafePath }
                resolved.removeLast()
            } else {
                resolved.append(String(component))
            }
        }
        guard !resolved.isEmpty else { throw ArchiveSafetyError.unsafePath }
    }
}
