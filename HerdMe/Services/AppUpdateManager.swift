import CryptoKit
import Foundation

struct AppUpdateDownloadURLs: Codable, Equatable, Sendable {
    let macOS: URL?
    let windowsX64: URL?
}

struct AppUpdateRelease: Codable, Equatable, Sendable {
    let version: String
    let build: Int
    let channel: String
    let notes: String
    let downloadURL: URL?
    let downloadURLs: AppUpdateDownloadURLs?

    init(
        version: String,
        build: Int,
        channel: String,
        notes: String,
        downloadURL: URL?,
        downloadURLs: AppUpdateDownloadURLs? = nil
    ) {
        self.version = version
        self.build = build
        self.channel = channel
        self.notes = notes
        self.downloadURL = downloadURL
        self.downloadURLs = downloadURLs
    }

    var platformDownloadURL: URL? {
        downloadURLs?.macOS ?? downloadURL
    }
}

struct AppUpdateManifest: Codable, Equatable, Sendable {
    let releases: [AppUpdateRelease]
}

struct AppUpdateSignedEnvelope: Codable, Equatable, Sendable {
    static let algorithm = "ECDSA_P256_SHA256"

    let algorithm: String
    let payload: String
    let signature: String
}

enum AppUpdateResult: Equatable, Sendable {
    case upToDate(version: String)
    case available(AppUpdateRelease)
}

enum AppUpdateError: LocalizedError, Equatable, Sendable {
    case invalidResponse
    case unsignedRemoteManifest
    case verificationKeyMissing
    case invalidSignature
    case incompletePlatformDownloads
    case noRelease(channel: String)

    var errorDescription: String? {
        switch self {
        case .invalidResponse:
            "The update service returned an invalid response."
        case .unsignedRemoteManifest:
            "The remote update feed is not signed and was rejected."
        case .verificationKeyMissing:
            "This build does not contain the public key required to verify updates."
        case .invalidSignature:
            "The update feed signature is invalid. The update was rejected."
        case .incompletePlatformDownloads:
            "The signed update feed must contain HTTPS downloads for macOS and Windows x64."
        case let .noRelease(channel):
            "No \(channel.lowercased()) release is available in the update feed."
        }
    }
}

struct AppUpdateManager: Sendable {
    private static let maximumFeedSize = 4 * 1_024 * 1_024

    let feedURL: URL
    let currentVersion: String
    let currentBuild: Int
    let verificationKey: Data?

    init(
        feedURL: URL,
        currentVersion: String,
        currentBuild: Int,
        verificationKey: Data? = nil
    ) {
        self.feedURL = feedURL
        self.currentVersion = currentVersion
        self.currentBuild = currentBuild
        self.verificationKey = verificationKey
    }

    static func configured(bundle: Bundle = .main) -> AppUpdateManager? {
        #if DEBUG
        let environmentURL = ProcessInfo.processInfo.environment["HERDME_UPDATE_FEED_URL"]
            .flatMap(URL.init(string:))
        let environmentKey = ProcessInfo.processInfo.environment["HERDME_UPDATE_PUBLIC_KEY"]
            .flatMap(Self.decodePublicKey)
        #else
        let environmentURL: URL? = nil
        let environmentKey: Data? = nil
        #endif
        let configuredURL = (bundle.object(forInfoDictionaryKey: "HerdMeUpdateFeedURL") as? String)
            .flatMap(Self.httpsURL)
        let bundledFeedURL = bundle.url(forResource: "release-feed-url", withExtension: "txt")
            .flatMap { try? String(contentsOf: $0, encoding: .utf8) }
            .flatMap(Self.httpsURL)
        guard let feedURL = environmentURL
                ?? configuredURL
                ?? bundledFeedURL
                ?? bundle.url(forResource: "release-manifest", withExtension: "json") else {
            return nil
        }
        let version = bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
            ?? "0.0.0"
        let build = Int(bundle.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "0") ?? 0
        let configuredKey = (bundle.object(forInfoDictionaryKey: "HerdMeUpdatePublicKey") as? String)
            .flatMap(Self.decodePublicKey)
        let bundledKey = bundle.url(forResource: "release-public-key", withExtension: "txt")
            .flatMap { try? String(contentsOf: $0, encoding: .utf8) }
            .flatMap(Self.decodePublicKey)
        return AppUpdateManager(
            feedURL: feedURL,
            currentVersion: version,
            currentBuild: build,
            verificationKey: environmentKey ?? configuredKey ?? bundledKey
        )
    }

    func check(channel: String) async throws -> AppUpdateResult {
        let data: Data
        if feedURL.isFileURL {
            data = try Data(contentsOf: feedURL)
        } else {
            let (remoteData, response) = try await ManagedDownloadClient.data(
                from: feedURL,
                session: Self.remoteSession
            )
            guard let httpResponse = response as? HTTPURLResponse,
                  200..<300 ~= httpResponse.statusCode else {
                throw AppUpdateError.invalidResponse
            }
            data = remoteData
        }
        guard data.count <= Self.maximumFeedSize else { throw AppUpdateError.invalidResponse }
        let manifest = try Self.decodeManifest(
            data,
            requiresSignature: !feedURL.isFileURL,
            publicKey: verificationKey
        )
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
        let order = VersionComparison.compare(lhs.version, rhs.version)
        return order == .orderedSame ? lhs.build > rhs.build : order == .orderedDescending
    }

    private static func isNewer(
        _ release: AppUpdateRelease,
        than version: String,
        build: Int
    ) -> Bool {
        let order = VersionComparison.compare(release.version, version)
        return order == .orderedDescending || order == .orderedSame && release.build > build
    }

    static func decodeManifest(
        _ data: Data,
        requiresSignature: Bool,
        publicKey: Data?
    ) throws -> AppUpdateManifest {
        if let envelope = try? JSONDecoder().decode(AppUpdateSignedEnvelope.self, from: data) {
            return try verify(envelope: envelope, publicKey: publicKey)
        }
        guard !requiresSignature else {
            throw AppUpdateError.unsignedRemoteManifest
        }
        return try validate(
            JSONDecoder().decode(AppUpdateManifest.self, from: data),
            requiresPlatformDownloads: false
        )
    }

    static func verify(
        envelope: AppUpdateSignedEnvelope,
        publicKey: Data?
    ) throws -> AppUpdateManifest {
        guard envelope.algorithm == AppUpdateSignedEnvelope.algorithm,
              let payload = Data(base64Encoded: envelope.payload),
              let signatureData = Data(base64Encoded: envelope.signature) else {
            throw AppUpdateError.invalidSignature
        }
        guard let publicKey else { throw AppUpdateError.verificationKeyMissing }
        do {
            let key = try P256.Signing.PublicKey(x963Representation: publicKey)
            let signature = try P256.Signing.ECDSASignature(derRepresentation: signatureData)
            guard key.isValidSignature(signature, for: payload) else {
                throw AppUpdateError.invalidSignature
            }
        } catch let error as AppUpdateError {
            throw error
        } catch {
            throw AppUpdateError.invalidSignature
        }
        guard payload.count <= maximumFeedSize else { throw AppUpdateError.invalidResponse }
        return try validate(
            JSONDecoder().decode(AppUpdateManifest.self, from: payload),
            requiresPlatformDownloads: true
        )
    }

    private static func validate(
        _ manifest: AppUpdateManifest,
        requiresPlatformDownloads: Bool
    ) throws -> AppUpdateManifest {
        let baseIsValid = manifest.releases.allSatisfy { release in
            release.build >= 0
                && ["stable", "beta"].contains(release.channel.lowercased())
                && isValidVersion(release.version)
                && isValidHTTPSURL(release.downloadURL, required: false)
                && (release.downloadURLs == nil || hasCompletePlatformDownloads(release))
        }
        guard baseIsValid else { throw AppUpdateError.invalidResponse }
        if requiresPlatformDownloads,
           !manifest.releases.allSatisfy(hasCompletePlatformDownloads) {
            throw AppUpdateError.incompletePlatformDownloads
        }
        return manifest
    }

    private static func hasCompletePlatformDownloads(_ release: AppUpdateRelease) -> Bool {
        guard let downloads = release.downloadURLs,
              downloads.macOS != downloads.windowsX64 else {
            return false
        }
        return isValidHTTPSURL(downloads.macOS, required: true)
            && isValidHTTPSURL(downloads.windowsX64, required: true)
    }

    private static func isValidHTTPSURL(_ url: URL?, required: Bool) -> Bool {
        guard let url else { return !required }
        return url.scheme == "https" && url.host != nil
    }

    private static func isValidVersion(_ value: String) -> Bool {
        VersionComparison.isSemanticVersion(value)
    }

    private static func decodePublicKey(_ value: String) -> Data? {
        let normalized = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let key = Data(base64Encoded: normalized), key.count == 65, key.first == 4 else {
            return nil
        }
        return key
    }

    private static func httpsURL(_ value: String) -> URL? {
        let normalized = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let url = URL(string: normalized), url.scheme == "https", url.host != nil else {
            return nil
        }
        return url
    }

    private static let remoteSession: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 20
        configuration.timeoutIntervalForResource = 60
        configuration.waitsForConnectivity = false
        return URLSession(configuration: configuration)
    }()
}
