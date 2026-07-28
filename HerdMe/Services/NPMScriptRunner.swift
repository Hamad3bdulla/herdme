import Foundation

struct NPMScript: Identifiable, Hashable, Sendable {
    let name: String

    var id: String { name }
}

enum NPMScriptCatalogError: LocalizedError, Equatable {
    case packageJSONMissing
    case packageJSONUnreadable
    case packageJSONTooLarge
    case packageJSONInvalid
    case tooManyScripts
    case noScripts
    case invalidScriptName
    case scriptUnavailable

    var errorDescription: String? {
        switch self {
        case .packageJSONMissing:
            String(localized: "This project does not contain a package.json file.")
        case .packageJSONUnreadable:
            String(localized: "HerdMe could not read this project's package.json file.")
        case .packageJSONTooLarge:
            String(localized: "package.json must be no larger than 1 MB.")
        case .packageJSONInvalid:
            String(localized: "package.json does not contain valid JSON.")
        case .tooManyScripts:
            String(localized: "package.json contains more than 256 npm scripts.")
        case .noScripts:
            String(localized: "This project does not define any runnable npm scripts.")
        case .invalidScriptName:
            String(localized: "The npm script name is invalid.")
        case .scriptUnavailable:
            String(localized: "The selected npm script is no longer available. Reload the script list.")
        }
    }
}

enum NPMScriptCatalog {
    private static let maximumPackageBytes = 1 * 1_024 * 1_024
    private static let maximumScriptNameBytes = 256
    private static let maximumScripts = 256
    private static let preferredOrder = ["dev", "build", "test"]

    static func scripts(in projectDirectory: URL) throws -> [NPMScript] {
        let packageURL = projectDirectory.appendingPathComponent("package.json")
        guard FileManager.default.fileExists(atPath: packageURL.path) else {
            throw NPMScriptCatalogError.packageJSONMissing
        }

        let data: Data
        do {
            let handle = try FileHandle(forReadingFrom: packageURL)
            defer { try? handle.close() }
            data = try handle.read(upToCount: maximumPackageBytes + 1) ?? Data()
        } catch {
            throw NPMScriptCatalogError.packageJSONUnreadable
        }
        guard data.count <= maximumPackageBytes else {
            throw NPMScriptCatalogError.packageJSONTooLarge
        }

        let root: [String: Any]
        do {
            guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                throw NPMScriptCatalogError.packageJSONInvalid
            }
            root = object
        } catch let error as NPMScriptCatalogError {
            throw error
        } catch {
            throw NPMScriptCatalogError.packageJSONInvalid
        }

        guard let rawScripts = root["scripts"] as? [String: Any] else {
            throw NPMScriptCatalogError.noScripts
        }
        guard rawScripts.count <= maximumScripts else {
            throw NPMScriptCatalogError.tooManyScripts
        }

        let scripts = rawScripts.compactMap { name, value -> NPMScript? in
            guard value is String, isValid(name: name) else { return nil }
            return NPMScript(name: name)
        }.sorted(by: orderedBefore)
        guard !scripts.isEmpty else { throw NPMScriptCatalogError.noScripts }
        return scripts
    }

    static func validate(name: String) throws {
        guard isValid(name: name) else { throw NPMScriptCatalogError.invalidScriptName }
    }

    static func timeout(for name: String) -> TimeInterval {
        ["dev", "start", "serve", "watch"].contains(name.lowercased())
            ? 24 * 60 * 60
            : 30 * 60
    }

    private static func isValid(name: String) -> Bool {
        guard !name.isEmpty,
            name == name.trimmingCharacters(in: .whitespacesAndNewlines),
            !name.hasPrefix("-"),
            name.utf8.count <= maximumScriptNameBytes
        else {
            return false
        }
        return !name.unicodeScalars.contains { scalar in
            scalar.value == 0 || scalar.value < 32 || scalar.value == 127
        }
    }

    private static func orderedBefore(_ lhs: NPMScript, _ rhs: NPMScript) -> Bool {
        let leftPriority = preferredOrder.firstIndex(of: lhs.name.lowercased())
        let rightPriority = preferredOrder.firstIndex(of: rhs.name.lowercased())
        switch (leftPriority, rightPriority) {
        case (.some(let left), .some(let right)):
            return left < right
        case (.some, .none):
            return true
        case (.none, .some):
            return false
        case (.none, .none):
            return lhs.name.localizedCaseInsensitiveCompare(rhs.name) == .orderedAscending
        }
    }
}

struct NPMScriptInvocation: Sendable {
    let nodeExecutable: URL
    let npmCLI: URL
    let projectDirectory: URL
    let scriptName: String
    let environment: [String: String]
    let timeout: TimeInterval
}

struct NPMScriptResult: Sendable {
    let status: Int32
    let output: String
}

final class NPMScriptCancellation: @unchecked Sendable {
    private let lock = NSLock()
    private var cancelled = false

    var isCancelled: Bool {
        lock.lock()
        defer { lock.unlock() }
        return cancelled
    }

    func cancel() {
        lock.lock()
        cancelled = true
        lock.unlock()
    }
}

enum NPMScriptRunner {
    static func run(
        _ invocation: NPMScriptInvocation,
        cancellation: NPMScriptCancellation,
        outputReceived: @escaping @Sendable (Data) -> Void
    ) async throws -> NPMScriptResult {
        try NPMScriptCatalog.validate(name: invocation.scriptName)
        guard FileManager.default.isExecutableFile(atPath: invocation.nodeExecutable.path) else {
            throw RuntimeInstallationError.commandFailed(
                String(localized: "The selected managed Node.js executable is unavailable.")
            )
        }
        guard FileManager.default.isReadableFile(atPath: invocation.npmCLI.path) else {
            throw RuntimeInstallationError.commandFailed(
                String(localized: "The selected managed Node.js runtime does not contain npm.")
            )
        }
        guard invocation.timeout > 0 else {
            throw RuntimeInstallationError.commandFailed(
                String(localized: "The npm script invocation is incomplete.")
            )
        }

        let task = Task.detached(priority: .userInitiated) {
            let result = try ProcessRunner.run(
                invocation.nodeExecutable,
                arguments: [
                    invocation.npmCLI.path,
                    "--no-update-notifier",
                    "run",
                    invocation.scriptName
                ],
                currentDirectory: invocation.projectDirectory,
                environment: invocation.environment,
                timeout: invocation.timeout,
                cancellationRequested: { cancellation.isCancelled },
                outputReceived: outputReceived
            )
            return NPMScriptResult(status: result.status, output: result.output)
        }
        return try await withTaskCancellationHandler {
            try await task.value
        } onCancel: {
            cancellation.cancel()
            task.cancel()
        }
    }
}
