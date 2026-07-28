import CryptoKit
import Foundation

enum ProjectEnvironmentFileError: LocalizedError, Equatable {
    case projectMissing
    case symbolicLink
    case invalidFile
    case fileTooLarge
    case changedExternally

    var errorDescription: String? {
        switch self {
        case .projectMissing:
            String(localized: "The selected project directory is no longer available.")
        case .symbolicLink:
            String(localized: "HerdMe will not modify a symbolic .env file. Replace it with a project-owned file first.")
        case .invalidFile:
            String(localized: "The project's .env file must be a regular UTF-8 text file.")
        case .fileTooLarge:
            String(localized: "The project's .env file is larger than the supported 4 MB limit.")
        case .changedExternally:
            String(localized: "The .env file changed outside HerdMe. Reload it before saving so those changes are not overwritten.")
        }
    }
}

struct ProjectEnvironmentRevision: Equatable, Sendable {
    let exists: Bool
    let digest: String?

    static let missing = ProjectEnvironmentRevision(exists: false, digest: nil)
}

struct ProjectEnvironmentDocument: Equatable, Sendable {
    let contents: String
    let exists: Bool
    let loadedFromExample: Bool
    let revision: ProjectEnvironmentRevision
}

enum ProjectEnvironmentFile {
    static let maximumFileBytes = 4 * 1_024 * 1_024

    static func load(
        projectURL: URL,
        fileManager: FileManager = .default
    ) throws -> ProjectEnvironmentDocument {
        try requireProjectDirectory(projectURL, fileManager: fileManager)
        let environmentURL = projectURL.appendingPathComponent(".env", isDirectory: false)
        let exampleURL = projectURL.appendingPathComponent(".env.example", isDirectory: false)

        if fileManager.fileExists(atPath: environmentURL.path) {
            let data = try readData(environmentURL)
            return ProjectEnvironmentDocument(
                contents: try decode(data),
                exists: true,
                loadedFromExample: false,
                revision: revision(for: data)
            )
        }

        if fileManager.fileExists(atPath: exampleURL.path) {
            let data = try readData(exampleURL)
            return ProjectEnvironmentDocument(
                contents: try decode(data),
                exists: false,
                loadedFromExample: true,
                revision: .missing
            )
        }

        return ProjectEnvironmentDocument(
            contents: "",
            exists: false,
            loadedFromExample: false,
            revision: .missing
        )
    }

    static func save(
        _ contents: String,
        projectURL: URL,
        expectedRevision: ProjectEnvironmentRevision,
        fileManager: FileManager = .default
    ) throws -> ProjectEnvironmentDocument {
        try requireProjectDirectory(projectURL, fileManager: fileManager)
        let environmentURL = projectURL.appendingPathComponent(".env", isDirectory: false)
        let data = Data(contents.utf8)
        guard data.count <= maximumFileBytes else {
            throw ProjectEnvironmentFileError.fileTooLarge
        }

        let currentRevision: ProjectEnvironmentRevision
        if fileManager.fileExists(atPath: environmentURL.path) {
            let currentData = try readData(environmentURL)
            currentRevision = revision(for: currentData)
        } else {
            currentRevision = .missing
        }
        guard currentRevision == expectedRevision else {
            throw ProjectEnvironmentFileError.changedExternally
        }

        try rejectSymbolicLink(environmentURL)
        let temporaryURL = projectURL.appendingPathComponent(
            ".env.herdme-\(UUID().uuidString.lowercased()).tmp",
            isDirectory: false
        )
        defer { try? fileManager.removeItem(at: temporaryURL) }
        try data.write(to: temporaryURL, options: [.atomic])
        try fileManager.setAttributes(
            [.posixPermissions: NSNumber(value: Int16(0o600))],
            ofItemAtPath: temporaryURL.path
        )
        if fileManager.fileExists(atPath: environmentURL.path) {
            _ = try fileManager.replaceItemAt(
                environmentURL,
                withItemAt: temporaryURL,
                backupItemName: nil,
                options: []
            )
        } else {
            try fileManager.moveItem(at: temporaryURL, to: environmentURL)
        }

        return ProjectEnvironmentDocument(
            contents: contents,
            exists: true,
            loadedFromExample: false,
            revision: revision(for: data)
        )
    }

    private static func requireProjectDirectory(
        _ projectURL: URL,
        fileManager: FileManager
    ) throws {
        var isDirectory: ObjCBool = false
        guard fileManager.fileExists(atPath: projectURL.path, isDirectory: &isDirectory),
            isDirectory.boolValue
        else {
            throw ProjectEnvironmentFileError.projectMissing
        }
    }

    private static func readData(_ url: URL) throws -> Data {
        try rejectSymbolicLink(url)
        guard let values = try? url.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey]),
            values.isRegularFile == true
        else {
            throw ProjectEnvironmentFileError.invalidFile
        }
        guard (values.fileSize ?? 0) <= maximumFileBytes else {
            throw ProjectEnvironmentFileError.fileTooLarge
        }
        do {
            return try Data(contentsOf: url, options: [.mappedIfSafe])
        } catch {
            throw ProjectEnvironmentFileError.invalidFile
        }
    }

    private static func decode(_ data: Data) throws -> String {
        guard data.count <= maximumFileBytes,
            let contents = String(data: data, encoding: .utf8)
        else {
            throw data.count > maximumFileBytes
                ? ProjectEnvironmentFileError.fileTooLarge
                : ProjectEnvironmentFileError.invalidFile
        }
        return contents.hasPrefix("\u{FEFF}") ? String(contents.dropFirst()) : contents
    }

    private static func rejectSymbolicLink(_ url: URL) throws {
        if (try? url.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true {
            throw ProjectEnvironmentFileError.symbolicLink
        }
    }

    private static func revision(for data: Data) -> ProjectEnvironmentRevision {
        ProjectEnvironmentRevision(
            exists: true,
            digest: SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
        )
    }
}
