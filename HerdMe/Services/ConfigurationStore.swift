import Foundation

enum IndependentPathPolicy {
    private static func normalizedPaths(for url: URL) -> Set<String> {
        [
            url.standardizedFileURL.path.lowercased(),
            url.resolvingSymlinksInPath().standardizedFileURL.path.lowercased()
        ]
    }

    static func belongsToOtherHerd(
        _ url: URL,
        homeDirectory: URL = FileManager.default.homeDirectoryForCurrentUser
    ) -> Bool {
        let roots = [
            homeDirectory.appendingPathComponent("Herd", isDirectory: true),
            homeDirectory
                .appendingPathComponent("Library/Application Support", isDirectory: true)
                .appendingPathComponent("Herd", isDirectory: true)
        ]
        let candidates = normalizedPaths(for: url)
        return roots.flatMap { normalizedPaths(for: $0) }.contains { root in
            candidates.contains { candidate in
                candidate == root || candidate.hasPrefix(root + "/")
            }
        }
    }

    static func removingOtherHerdPaths(
        from paths: [String],
        homeDirectory: URL = FileManager.default.homeDirectoryForCurrentUser
    ) -> [String] {
        paths.filter {
            !belongsToOtherHerd(
                URL(fileURLWithPath: NSString(string: $0).expandingTildeInPath),
                homeDirectory: homeDirectory
            )
        }
    }
}

enum IndependentPathError: LocalizedError {
    case otherHerdPath

    var errorDescription: String? {
        String(
            localized:
                "HerdMe does not read or modify another application's project, runtime, or data folders. Choose a HerdMe-owned folder instead."
        )
    }
}

enum ConfigurationStoreError: LocalizedError, Equatable {
    case invalidSchemaVersion(Int)
    case unsupportedSchemaVersion(found: Int, supported: Int)

    var errorDescription: String? {
        switch self {
        case .invalidSchemaVersion(let version):
            String.localizedStringWithFormat(
                String(localized: "The settings schema version %lld is invalid."),
                Int64(version)
            )
        case .unsupportedSchemaVersion(let found, let supported):
            String.localizedStringWithFormat(
                String(
                    localized:
                        "The settings were created by a newer HerdMe release (schema %1$lld); this release supports up to schema %2$lld."),
                Int64(found),
                Int64(supported)
            )
        }
    }
}

final class ConfigurationStore {
    struct LoadIssue: Equatable {
        let message: String
        let backupURL: URL?
    }

    static let currentConfigSchemaVersion = 1
    static let currentIndependenceMigrationVersion = 1

    private struct SchemaHeader: Decodable {
        let configSchemaVersion: Int?
    }

    let rootURL: URL
    let configURL: URL
    let projectsURL: URL
    private let fileManager: FileManager
    private(set) var loadIssue: LoadIssue?

    init(fileManager: FileManager = .default) {
        self.fileManager = fileManager
        let applicationSupport =
            fileManager.urls(
                for: .applicationSupportDirectory,
                in: .userDomainMask
            ).first
            ?? fileManager.homeDirectoryForCurrentUser.appendingPathComponent(
                "Library/Application Support",
                isDirectory: true
            )
        rootURL = applicationSupport.appendingPathComponent("HerdMe", isDirectory: true)
        configURL = rootURL.appendingPathComponent("config.json")
        projectsURL = fileManager.homeDirectoryForCurrentUser.appendingPathComponent(
            "HerdMe",
            isDirectory: true
        )
        try? fileManager.createDirectory(at: rootURL, withIntermediateDirectories: true)
        try? fileManager.createDirectory(at: rootURL.appendingPathComponent("Sites"), withIntermediateDirectories: true)
        try? fileManager.createDirectory(at: projectsURL, withIntermediateDirectories: true)
    }

    init(rootURL: URL, projectsURL: URL, fileManager: FileManager = .default) {
        self.fileManager = fileManager
        self.rootURL = rootURL
        configURL = rootURL.appendingPathComponent("config.json")
        self.projectsURL = projectsURL
        try? fileManager.createDirectory(at: rootURL, withIntermediateDirectories: true)
        try? fileManager.createDirectory(
            at: rootURL.appendingPathComponent("Sites"),
            withIntermediateDirectories: true
        )
        try? fileManager.createDirectory(at: projectsURL, withIntermediateDirectories: true)
    }

    func load() -> AppConfiguration {
        guard fileManager.fileExists(atPath: configURL.path) else {
            return .default
        }
        loadIssue = nil
        let data: Data
        let sourceSchemaVersion: Int
        var value: AppConfiguration
        do {
            data = try Data(contentsOf: configURL)
            sourceSchemaVersion =
                try JSONDecoder().decode(
                    SchemaHeader.self,
                    from: data
                ).configSchemaVersion ?? 0
            guard sourceSchemaVersion >= 0 else {
                throw ConfigurationStoreError.invalidSchemaVersion(sourceSchemaVersion)
            }
            if sourceSchemaVersion > Self.currentConfigSchemaVersion {
                let backupURL = preserveConfiguration(
                    prefix: "config.unsupported-v\(sourceSchemaVersion)"
                )
                let location = backupURL?.path ?? configURL.path
                loadIssue = LoadIssue(
                    message: String.localizedStringWithFormat(
                        String(
                            localized:
                                "HerdMe did not replace settings created by a newer release (schema %1$lld). The original file was preserved at %2$@. Update HerdMe before restoring it."
                        ),
                        Int64(sourceSchemaVersion),
                        location
                    ),
                    backupURL: backupURL
                )
                return .default
            }
            value = try JSONDecoder().decode(AppConfiguration.self, from: data)
            value = try Self.migratingSchema(in: value)
        } catch {
            let backupURL = preserveConfiguration(prefix: "config.corrupt")
            let location = backupURL?.path ?? configURL.path
            loadIssue = LoadIssue(
                message: String.localizedStringWithFormat(
                    String(
                        localized:
                            "HerdMe could not read its settings. The original file was preserved at %@. No replacement settings were saved."
                    ),
                    location
                ),
                backupURL: backupURL
            )
            return .default
        }
        let migrated = Self.migratingIndependentPaths(
            in: value,
            homeDirectory: fileManager.homeDirectoryForCurrentUser
        )
        if migrated.parkPaths != value.parkPaths
            || migrated.independenceMigrationVersion != value.independenceMigrationVersion
            || migrated.configSchemaVersion != sourceSchemaVersion
        {
            value = migrated
            try? save(migrated)
        }
        return value
    }

    private func preserveConfiguration(prefix: String) -> URL? {
        let backupURL = rootURL.appendingPathComponent(
            "\(prefix)-\(UUID().uuidString.lowercased()).json"
        )
        do {
            try fileManager.moveItem(at: configURL, to: backupURL)
            return backupURL
        } catch {
            return nil
        }
    }

    static func migratingSchema(in configuration: AppConfiguration) throws -> AppConfiguration {
        guard configuration.configSchemaVersion >= 0 else {
            throw ConfigurationStoreError.invalidSchemaVersion(configuration.configSchemaVersion)
        }
        guard configuration.configSchemaVersion <= currentConfigSchemaVersion else {
            throw ConfigurationStoreError.unsupportedSchemaVersion(
                found: configuration.configSchemaVersion,
                supported: currentConfigSchemaVersion
            )
        }

        var migrated = configuration
        while migrated.configSchemaVersion < currentConfigSchemaVersion {
            switch migrated.configSchemaVersion {
            case 0:
                migrated.configSchemaVersion = 1
            default:
                throw ConfigurationStoreError.invalidSchemaVersion(
                    migrated.configSchemaVersion
                )
            }
        }
        return migrated
    }

    static func migratingIndependentPaths(
        in configuration: AppConfiguration,
        homeDirectory: URL
    ) -> AppConfiguration {
        var migrated = configuration
        migrated.parkPaths = IndependentPathPolicy.removingOtherHerdPaths(
            from: configuration.parkPaths,
            homeDirectory: homeDirectory
        )
        guard migrated.independenceMigrationVersion < currentIndependenceMigrationVersion else {
            return migrated
        }

        let independentProjects = homeDirectory.appendingPathComponent(
            "HerdMe",
            isDirectory: true
        ).path
        if !migrated.parkPaths.contains(independentProjects) {
            migrated.parkPaths.append(independentProjects)
        }
        migrated.independenceMigrationVersion = currentIndependenceMigrationVersion
        return migrated
    }

    func save(_ configuration: AppConfiguration) throws {
        let configuration = try Self.migratingSchema(in: configuration)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        let data = try encoder.encode(configuration)
        try data.write(to: configURL, options: .atomic)
    }
}
