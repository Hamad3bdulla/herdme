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
        "HerdMe does not read or modify another application's project, runtime, or data folders. Choose a HerdMe-owned folder instead."
    }
}

final class ConfigurationStore {
    static let currentIndependenceMigrationVersion = 1

    let rootURL: URL
    let configURL: URL
    let projectsURL: URL
    private let fileManager: FileManager

    init(fileManager: FileManager = .default) {
        self.fileManager = fileManager
        let applicationSupport = fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
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

    func load() -> AppConfiguration {
        guard let data = try? Data(contentsOf: configURL),
              var value = try? JSONDecoder().decode(AppConfiguration.self, from: data) else {
            return .default
        }
        let migrated = Self.migratingIndependentPaths(
            in: value,
            homeDirectory: fileManager.homeDirectoryForCurrentUser
        )
        if migrated.parkPaths != value.parkPaths
            || migrated.independenceMigrationVersion != value.independenceMigrationVersion {
            value = migrated
            try? save(migrated)
        }
        return value
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
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        let data = try encoder.encode(configuration)
        try data.write(to: configURL, options: .atomic)
    }
}
