import Foundation

struct AppUpdateRelease: Codable, Equatable, Sendable {
    let version: String
    let build: Int
    let channel: String
    let notes: String
    let downloadURL: URL?
}

struct AppUpdateManifest: Codable, Equatable, Sendable {
    let releases: [AppUpdateRelease]
}

enum AppUpdateResult: Equatable, Sendable {
    case upToDate(version: String)
    case available(AppUpdateRelease)
}

enum AppUpdateError: LocalizedError, Sendable {
    case invalidResponse
    case noRelease(channel: String)

    var errorDescription: String? {
        switch self {
        case .invalidResponse:
            "The update service returned an invalid response."
        case let .noRelease(channel):
            "No \(channel.lowercased()) release is available in the update feed."
        }
    }
}

struct AppUpdateManager: Sendable {
    let feedURL: URL
    let currentVersion: String
    let currentBuild: Int

    static func configured(bundle: Bundle = .main) -> AppUpdateManager? {
        let environmentURL = ProcessInfo.processInfo.environment["HERDME_UPDATE_FEED_URL"]
            .flatMap(URL.init(string:))
        let configuredURL = (bundle.object(forInfoDictionaryKey: "HerdMeUpdateFeedURL") as? String)
            .flatMap(URL.init(string:))
        guard let feedURL = environmentURL
                ?? configuredURL
                ?? bundle.url(forResource: "release-manifest", withExtension: "json") else {
            return nil
        }
        let version = bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
            ?? "0.0.0"
        let build = Int(bundle.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "0") ?? 0
        return AppUpdateManager(feedURL: feedURL, currentVersion: version, currentBuild: build)
    }

    func check(channel: String) async throws -> AppUpdateResult {
        let data: Data
        if feedURL.isFileURL {
            data = try Data(contentsOf: feedURL)
        } else {
            let (remoteData, response) = try await URLSession.shared.data(from: feedURL)
            guard let httpResponse = response as? HTTPURLResponse,
                  200..<300 ~= httpResponse.statusCode else {
                throw AppUpdateError.invalidResponse
            }
            data = remoteData
        }
        let manifest = try JSONDecoder().decode(AppUpdateManifest.self, from: data)
        let selectedChannel = channel.lowercased()
        let acceptedChannels = selectedChannel == "beta"
            ? Set(["stable", "beta"])
            : Set(["stable"])
        let eligibleReleases = manifest.releases
            .filter { acceptedChannels.contains($0.channel.lowercased()) }
            .sorted(by: Self.isOrderedAfter)
        guard let latest = eligibleReleases.first else {
            throw AppUpdateError.noRelease(channel: channel)
        }
        return Self.isNewer(latest, than: currentVersion, build: currentBuild)
            ? .available(latest)
            : .upToDate(version: currentVersion)
    }

    private static func isOrderedAfter(_ lhs: AppUpdateRelease, _ rhs: AppUpdateRelease) -> Bool {
        let order = lhs.version.compare(rhs.version, options: .numeric)
        return order == .orderedSame ? lhs.build > rhs.build : order == .orderedDescending
    }

    private static func isNewer(
        _ release: AppUpdateRelease,
        than version: String,
        build: Int
    ) -> Bool {
        let order = release.version.compare(version, options: .numeric)
        return order == .orderedDescending || order == .orderedSame && release.build > build
    }
}
