import Foundation

enum PortableCoreClientError: Error, Equatable {
    case unavailable
    case processFailed(String)
    case invalidResponse

    var diagnostic: String {
        switch self {
        case .unavailable, .invalidResponse:
            ""
        case .processFailed(let output):
            output
        }
    }
}

struct PortableCoreClient: Sendable {
    let executableURL: URL

    init(executableURL: URL? = nil) {
        self.executableURL = executableURL ?? Self.bundledExecutableURL()
    }

    nonisolated static func bundledExecutableURL(bundle: Bundle = .main) -> URL {
        bundle.bundleURL
            .appendingPathComponent("Contents/Helpers", isDirectory: true)
            .appendingPathComponent("herdme-core")
    }

    func phpExtensionReport(
        moduleOutput: String,
        requiredExtensions: [String]
    ) throws -> PHPExtensionReport {
        guard FileManager.default.isExecutableFile(atPath: executableURL.path) else {
            throw PortableCoreClientError.unavailable
        }
        let result = try ProcessRunner.run(
            executableURL,
            arguments: ["php-extensions"],
            currentDirectory: executableURL.deletingLastPathComponent(),
            standardInput: Data(moduleOutput.utf8),
            timeout: 30
        )
        let output = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
        guard result.status == 0 else {
            throw PortableCoreClientError.processFailed(output)
        }
        guard let data = output.data(using: .utf8),
            let response = try? JSONDecoder().decode(CorePHPExtensionReport.self, from: data)
        else {
            throw PortableCoreClientError.invalidResponse
        }

        let loaded = Set(response.loaded)
        let expectedMissing = requiredExtensions.filter { !loaded.contains($0) }
        guard response.required == requiredExtensions,
            response.missing == expectedMissing,
            response.compatible == expectedMissing.isEmpty
        else {
            throw PortableCoreClientError.invalidResponse
        }
        return PHPExtensionReport(loaded: loaded, missing: expectedMissing)
    }
}

private struct CorePHPExtensionReport: Decodable {
    let required: [String]
    let loaded: [String]
    let missing: [String]
    let compatible: Bool
}
