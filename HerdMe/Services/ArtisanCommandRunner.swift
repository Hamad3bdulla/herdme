import Foundation

struct ArtisanCommandPreset: Identifiable, Hashable, Sendable {
    let id: String
    let title: String
    let arguments: [String]
    let timeout: TimeInterval

    static let all: [ArtisanCommandPreset] = [
        .init(id: "route-list", title: "Route List", arguments: ["route:list", "--no-interaction"], timeout: 5 * 60),
        .init(id: "migrate-status", title: "Migration Status", arguments: ["migrate:status", "--no-interaction"], timeout: 5 * 60),
        .init(id: "migrate", title: "Migrate", arguments: ["migrate", "--no-interaction"], timeout: 15 * 60),
        .init(id: "queue-work", title: "Queue Worker", arguments: ["queue:work", "--no-interaction"], timeout: 24 * 60 * 60),
        .init(id: "custom", title: "Custom", arguments: [], timeout: 15 * 60)
    ]
}

enum ArtisanCommandError: LocalizedError, Equatable {
    case empty
    case malformedQuotes
    case invalidCharacter
    case tooLong
    case tooManyArguments
    case invalidCommand

    var errorDescription: String? {
        switch self {
        case .empty:
            String(localized: "Enter an Artisan command.")
        case .malformedQuotes:
            String(localized: "The Artisan command contains an unfinished quote.")
        case .invalidCharacter:
            String(localized: "The Artisan command contains an unsupported control character.")
        case .tooLong:
            String(localized: "Artisan commands are limited to 4 KB and 512 bytes per argument.")
        case .tooManyArguments:
            String(localized: "Artisan commands are limited to 32 arguments.")
        case .invalidCommand:
            String(localized: "Enter an Artisan command such as route:list, without php or shell syntax.")
        }
    }
}

enum ArtisanCommandParser {
    private static let maximumCommandBytes = 4 * 1_024
    private static let maximumArgumentBytes = 512
    private static let maximumArguments = 32

    static func arguments(presetID: String, customCommand: String) throws -> (arguments: [String], timeout: TimeInterval) {
        guard let preset = ArtisanCommandPreset.all.first(where: { $0.id == presetID }) else {
            throw ArtisanCommandError.invalidCommand
        }
        if preset.id == "custom" {
            return (try parse(customCommand), preset.timeout)
        }
        return (preset.arguments, preset.timeout)
    }

    static func parse(_ command: String) throws -> [String] {
        let trimmed = command.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { throw ArtisanCommandError.empty }
        guard trimmed.utf8.count <= maximumCommandBytes else { throw ArtisanCommandError.tooLong }
        guard
            !trimmed.unicodeScalars.contains(where: { scalar in
                scalar.value == 0 || scalar.value < 32 || scalar.value == 127
            })
        else {
            throw ArtisanCommandError.invalidCharacter
        }

        enum Quote: Equatable {
            case single
            case double
        }

        var quote: Quote?
        var isEscaping = false
        var token = ""
        var tokenStarted = false
        var arguments: [String] = []

        func appendToken() throws {
            guard token.utf8.count <= maximumArgumentBytes else { throw ArtisanCommandError.tooLong }
            arguments.append(token)
            guard arguments.count <= maximumArguments else { throw ArtisanCommandError.tooManyArguments }
            token = ""
            tokenStarted = false
        }

        for character in trimmed {
            if isEscaping {
                token.append(character)
                tokenStarted = true
                isEscaping = false
                continue
            }

            if character == "\\", quote != .single {
                isEscaping = true
                tokenStarted = true
                continue
            }

            if character == "'", quote != .double {
                quote = quote == .single ? nil : .single
                tokenStarted = true
                continue
            }

            if character == "\"", quote != .single {
                quote = quote == .double ? nil : .double
                tokenStarted = true
                continue
            }

            if character.isWhitespace, quote == nil {
                if tokenStarted { try appendToken() }
                continue
            }

            token.append(character)
            tokenStarted = true
        }

        guard !isEscaping, quote == nil else { throw ArtisanCommandError.malformedQuotes }
        if tokenStarted { try appendToken() }
        if arguments.first == "artisan" { arguments.removeFirst() }
        guard let commandName = arguments.first,
            !commandName.isEmpty,
            !commandName.hasPrefix("-"),
            commandName != "php"
        else {
            throw ArtisanCommandError.invalidCommand
        }
        return arguments
    }
}

struct ArtisanInvocation: Sendable {
    let phpExecutable: URL
    let projectDirectory: URL
    let arguments: [String]
    let environment: [String: String]
    let timeout: TimeInterval
}

struct ArtisanCommandResult: Sendable {
    let status: Int32
    let output: String
}

final class ArtisanCancellation: @unchecked Sendable {
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

enum ArtisanCommandRunner {
    static func run(
        _ invocation: ArtisanInvocation,
        cancellation: ArtisanCancellation,
        outputReceived: @escaping @Sendable (Data) -> Void
    ) async throws -> ArtisanCommandResult {
        let task = Task.detached(priority: .userInitiated) {
            let result = try ProcessRunner.run(
                invocation.phpExecutable,
                arguments: ["artisan"] + invocation.arguments,
                currentDirectory: invocation.projectDirectory,
                environment: invocation.environment,
                timeout: invocation.timeout,
                cancellationRequested: { cancellation.isCancelled },
                outputReceived: outputReceived
            )
            return ArtisanCommandResult(status: result.status, output: result.output)
        }
        return try await withTaskCancellationHandler {
            try await task.value
        } onCancel: {
            cancellation.cancel()
            task.cancel()
        }
    }
}
