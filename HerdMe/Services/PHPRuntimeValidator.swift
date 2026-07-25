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
        case let .inspectionFailed(output):
            output.isEmpty ? "HerdMe could not inspect the selected PHP runtime." : output
        case let .missingExtensions(extensions):
            "The selected PHP runtime is missing Laravel extensions: "
                + extensions.joined(separator: ", ") + "."
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

    func report(executable: URL) throws -> PHPExtensionReport {
        let result = try ProcessRunner.run(executable, arguments: ["-m"], timeout: 30)
        let text = result.output
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard result.status == 0 else {
            throw PHPRuntimeValidationError.inspectionFailed(text)
        }

        return Self.report(moduleOutput: text)
    }

    func validate(executable: URL) throws {
        let report = try report(executable: executable)
        guard report.isLaravelCompatible else {
            throw PHPRuntimeValidationError.missingExtensions(report.missing)
        }
    }

    nonisolated static func report(moduleOutput: String) -> PHPExtensionReport {
        let loaded = Set(
            moduleOutput.components(separatedBy: .newlines)
                .map { $0.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() }
                .filter { !$0.isEmpty && !$0.hasPrefix("[") }
        )
        let missing = laravelRequiredExtensions.filter { !loaded.contains($0) }
        return PHPExtensionReport(loaded: loaded, missing: missing)
    }
}
