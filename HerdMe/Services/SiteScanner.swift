import Foundation

struct SiteScanner {
    private let fileManager: FileManager
    private let homeDirectory: URL

    init(
        fileManager: FileManager = .default,
        homeDirectory: URL? = nil
    ) {
        self.fileManager = fileManager
        self.homeDirectory = homeDirectory ?? fileManager.homeDirectoryForCurrentUser
    }

    func scan(paths: [String]) -> [SiteProject] {
        var sites: [SiteProject] = []
        var seen = Set<String>()

        for path in paths {
            let root = URL(fileURLWithPath: NSString(string: path).expandingTildeInPath)
            guard let children = try? fileManager.contentsOfDirectory(
                at: root,
                includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey],
                options: [.skipsHiddenFiles]
            ) else { continue }

            for child in children.sorted(by: { $0.lastPathComponent.localizedCaseInsensitiveCompare($1.lastPathComponent) == .orderedAscending }) {
                let values = try? child.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
                guard values?.isDirectory == true || values?.isSymbolicLink == true else { continue }
                let resolved = child.resolvingSymlinksInPath()
                guard !IndependentPathPolicy.belongsToOtherHerd(
                    resolved,
                    homeDirectory: homeDirectory
                ) else { continue }
                guard seen.insert(resolved.path).inserted else { continue }
                sites.append(
                    SiteProject(
                        path: resolved,
                        name: child.lastPathComponent,
                        framework: detectFramework(at: resolved),
                        isLinked: values?.isSymbolicLink == true,
                        phpVersion: isolatedVersion(at: resolved, file: ".herdme-php"),
                        nodeVersion: isolatedVersion(at: resolved, file: ".herdme-node")
                            ?? isolatedVersion(at: resolved, file: ".nvmrc"),
                        registrationPath: child
                    )
                )
            }
        }

        return sites
    }

    private func detectFramework(at root: URL) -> String {
        if fileManager.fileExists(atPath: root.appendingPathComponent("artisan").path) { return "Laravel" }
        if fileManager.fileExists(atPath: root.appendingPathComponent("wp-config.php").path) { return "WordPress" }
        if fileManager.fileExists(atPath: root.appendingPathComponent("public/index.php").path) { return "PHP" }
        if fileManager.fileExists(atPath: root.appendingPathComponent("package.json").path) { return "Node.js" }
        return "Site"
    }

    private func isolatedVersion(at root: URL, file: String) -> String? {
        let url = root.appendingPathComponent(file)
        guard let value = try? String(contentsOf: url, encoding: .utf8) else { return nil }
        let version = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return version.isEmpty ? nil : version
    }
}

enum SiteLinkError: LocalizedError {
    case notLinked

    var errorDescription: String? {
        "HerdMe can only unlink projects that were registered through a symbolic link."
    }
}

struct SiteLinkManager {
    static func unlink(_ site: SiteProject, fileManager: FileManager = .default) throws {
        guard site.isLinked, let registrationPath = site.registrationPath,
              (try? registrationPath.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true else {
            throw SiteLinkError.notLinked
        }
        try fileManager.removeItem(at: registrationPath)
    }
}
