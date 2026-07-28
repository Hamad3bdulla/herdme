import Combine
import Foundation

@MainActor
final class SitesCoordinator: ObservableObject {
    @Published private(set) var sites: [SiteProject] = []
    @Published private(set) var runtimePorts: [String: Int] = [:]

    private let rootURL: URL
    private let runtimeStore: SiteRuntimeStore
    private let fileManager: FileManager

    init(
        rootURL: URL,
        runtimeStore: SiteRuntimeStore = SiteRuntimeStore(),
        fileManager: FileManager = .default
    ) {
        self.rootURL = rootURL
        self.runtimeStore = runtimeStore
        self.fileManager = fileManager
    }

    func replaceSites(_ sites: [SiteProject]) {
        self.sites = sites
    }

    func selectedSite(identifier: SiteProject.ID?) -> SiteProject? {
        sites.first(where: { $0.id == identifier }) ?? sites.first
    }

    func validSelection(_ identifier: SiteProject.ID?) -> SiteProject.ID? {
        guard let identifier, sites.contains(where: { $0.id == identifier }) else {
            return sites.first?.id
        }
        return identifier
    }

    func replaceRuntimePorts(_ ports: [String: Int]) {
        runtimePorts = ports
    }

    func clearRuntimePorts() {
        runtimePorts.removeAll()
    }

    func setRuntime(
        _ cycle: String?,
        kind: SiteRuntimeKind,
        for site: SiteProject
    ) throws {
        try runtimeStore.set(cycle, kind: kind, for: site)
    }

    func linkExistingSite(at sourceURL: URL) throws -> SiteProject.ID {
        guard !IndependentPathPolicy.belongsToOtherHerd(sourceURL) else {
            throw IndependentPathError.otherHerdPath
        }
        let linksDirectory = rootURL.appendingPathComponent("Sites", isDirectory: true)
        let destination = linksDirectory.appendingPathComponent(sourceURL.lastPathComponent)
        if fileManager.fileExists(atPath: destination.path) {
            throw CocoaError(.fileWriteFileExists)
        }
        try fileManager.createDirectory(at: linksDirectory, withIntermediateDirectories: true)
        try fileManager.createSymbolicLink(at: destination, withDestinationURL: sourceURL)
        return sourceURL.resolvingSymlinksInPath().path
    }

    func unlink(_ site: SiteProject) throws {
        try SiteLinkManager.unlink(site, fileManager: fileManager)
    }
}
