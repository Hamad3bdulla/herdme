import Foundation

struct SiteCommandFavorite: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let tool: String
    let command: String

    init(id: UUID = UUID(), tool: String, command: String) {
        self.id = id
        self.tool = tool
        self.command = command
    }
}

/// Keeps command history outside project directories, matching the Windows
/// implementation's per-site favorites contract.
final class SiteCommandFavoritesStore: @unchecked Sendable {
    private struct Document: Codable {
        var sites: [String: [SiteCommandFavorite]] = [:]
    }

    private let fileURL: URL
    private let lock = NSLock()
    private let fileManager: FileManager
    private let maximumFavorites = 60

    init(rootURL: URL, fileManager: FileManager = .default) {
        self.fileURL = rootURL.appendingPathComponent("Config/command-favorites.json")
        self.fileManager = fileManager
    }

    func load(site: URL, tool: String) throws -> [SiteCommandFavorite] {
        try validateTool(tool)
        lock.lock()
        defer { lock.unlock() }
        return try read().sites[key(for: site)]?.filter { $0.tool == tool } ?? []
    }

    func add(site: URL, tool: String, command: String) throws {
        try validateTool(tool)
        let normalized = try normalize(command)
        lock.lock()
        defer { lock.unlock() }
        var document = try read()
        var favorites = document.sites[key(for: site)] ?? []
        favorites.removeAll { $0.tool == tool && $0.command.caseInsensitiveCompare(normalized) == .orderedSame }
        favorites.insert(SiteCommandFavorite(tool: tool, command: normalized), at: 0)
        document.sites[key(for: site)] = Array(favorites.prefix(maximumFavorites))
        try write(document)
    }

    func remove(site: URL, tool: String, command: String) throws {
        try validateTool(tool)
        lock.lock()
        defer { lock.unlock() }
        var document = try read()
        guard var favorites = document.sites[key(for: site)] else { return }
        favorites.removeAll { $0.tool == tool && $0.command == command }
        if favorites.isEmpty {
            document.sites.removeValue(forKey: key(for: site))
        } else {
            document.sites[key(for: site)] = favorites
        }
        try write(document)
    }

    private func key(for site: URL) -> String {
        site.standardizedFileURL.path
    }

    private func validateTool(_ tool: String) throws {
        guard ["artisan", "composer", "npm"].contains(tool) else {
            throw SiteCommandFavoritesError.invalidTool
        }
    }

    private func normalize(_ command: String) throws -> String {
        let value = command.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !value.isEmpty, value.count <= 512, !value.contains("\0") else {
            throw SiteCommandFavoritesError.invalidCommand
        }
        return value
    }

    private func read() throws -> Document {
        guard fileManager.fileExists(atPath: fileURL.path) else { return Document() }
        let data = try Data(contentsOf: fileURL)
        guard !data.isEmpty else { throw SiteCommandFavoritesError.invalidFile }
        do {
            return try JSONDecoder().decode(Document.self, from: data)
        } catch {
            let backup = fileURL.deletingLastPathComponent().appendingPathComponent(
                "command-favorites.corrupt-\(UUID().uuidString).json"
            )
            try? fileManager.moveItem(at: fileURL, to: backup)
            throw SiteCommandFavoritesError.invalidFile
        }
    }

    private func write(_ document: Document) throws {
        try fileManager.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(document).write(to: fileURL, options: .atomic)
    }
}

enum SiteCommandFavoritesError: LocalizedError {
    case invalidTool
    case invalidCommand
    case invalidFile

    var errorDescription: String? {
        switch self {
        case .invalidTool: String(localized: "This command type is not supported for favorites.")
        case .invalidCommand: String(localized: "Enter a command with 512 characters or fewer.")
        case .invalidFile: String(localized: "The saved command favorites file is invalid. A backup was created.")
        }
    }
}
