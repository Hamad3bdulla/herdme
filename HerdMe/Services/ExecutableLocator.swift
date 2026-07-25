import Foundation

struct ExecutableLocator {
    let managedRoot: URL

    func find(_ name: String) -> URL? {
        let fileManager = FileManager.default
        var directories = [managedRoot.appendingPathComponent("bin", isDirectory: true).path]
        directories.append(contentsOf: (ProcessInfo.processInfo.environment["PATH"] ?? "")
            .split(separator: ":")
            .map(String.init))

        for directory in directories {
            let candidate = URL(fileURLWithPath: directory, isDirectory: true).appendingPathComponent(name)
            guard fileManager.isExecutableFile(atPath: candidate.path) else { continue }
            let resolved = candidate.resolvingSymlinksInPath()
            guard !Self.belongsToOtherHerd(resolved) else { continue }
            return resolved
        }
        return nil
    }

    static func belongsToOtherHerd(_ url: URL) -> Bool {
        let path = url.standardizedFileURL.path.lowercased()
        return path.contains("/library/application support/herd/")
            || path.contains("/appdata/local/herd/")
            || path.contains("/appdata/roaming/herd/")
    }
}
