import Foundation

struct PHPExtensionReport: Sendable, Equatable {
    let loaded: Set<String>
    let missing: [String]

    var isLaravelCompatible: Bool { missing.isEmpty }
}

enum PHPRuntimeValidationError: LocalizedError {
    case inspectionFailed(String)
    case missingExtensions([String])

    var errorDescription: String? {
        switch self {
        case .inspectionFailed(let output):
            output.isEmpty ? String(localized: "HerdMe could not inspect the selected PHP runtime.") : output
        case .missingExtensions(let extensions):
            String.localizedStringWithFormat(
                String(localized: "The selected PHP runtime is missing Laravel extensions: %@."),
                extensions.joined(separator: ", ")
            )
        }
    }
}

struct PHPRuntimeValidator: Sendable {
    static let laravelRequiredExtensions = [
        "ctype",
        "curl",
        "dom",
        "fileinfo",
        "filter",
        "hash",
        "mbstring",
        "openssl",
        "pcre",
        "pdo",
        "session",
        "tokenizer",
        "xml"
    ]

    private let coreClient: PortableCoreClient

    init(coreClient: PortableCoreClient = PortableCoreClient()) {
        self.coreClient = coreClient
    }

    func report(executable: URL) throws -> PHPExtensionReport {
        let result = try ProcessRunner.run(executable, arguments: ["-m"], timeout: 30)
        let text = result.output
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard result.status == 0 else {
            throw PHPRuntimeValidationError.inspectionFailed(text)
        }
        do {
            return try coreClient.phpExtensionReport(
                moduleOutput: text,
                requiredExtensions: Self.laravelRequiredExtensions
            )
        } catch let error as PortableCoreClientError {
            throw PHPRuntimeValidationError.inspectionFailed(error.diagnostic)
        } catch {
            throw PHPRuntimeValidationError.inspectionFailed(error.localizedDescription)
        }
    }

    func validate(executable: URL) throws {
        let report = try report(executable: executable)
        guard report.isLaravelCompatible else {
            throw PHPRuntimeValidationError.missingExtensions(report.missing)
        }
    }

    nonisolated static func report(moduleOutput: String) -> PHPExtensionReport {
        var readingPHPModules = false
        var loaded = Set<String>()
        for line in moduleOutput.components(separatedBy: .newlines) {
            let normalized = line.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
            if normalized == "[php modules]" {
                readingPHPModules = true
                continue
            }
            if normalized.hasPrefix("["), normalized.hasSuffix("]") {
                readingPHPModules = false
                continue
            }
            if readingPHPModules, !normalized.isEmpty { loaded.insert(normalized) }
        }
        let missing = laravelRequiredExtensions.filter { !loaded.contains($0) }
        return PHPExtensionReport(loaded: loaded, missing: missing)
    }
}
