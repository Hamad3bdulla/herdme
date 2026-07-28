import Foundation

struct HomebrewCLI: Sendable {
    static let defaultExecutablePaths = [
        "/opt/homebrew/bin/brew",
        "/usr/local/bin/brew"
    ]

    let executableURL: URL
    let environment: [String: String]

    init(
        executableURL: URL,
        environment: [String: String]
    ) {
        self.executableURL = executableURL
        self.environment = environment
    }

    init?(
        fileManager: FileManager = .default,
        executablePaths: [String] = defaultExecutablePaths,
        baseEnvironment: [String: String] = ProcessInfo.processInfo.environment,
        homeDirectory: URL? = nil,
        userName: String = NSUserName()
    ) {
        guard
            let executableURL = Self.executableURL(
                fileManager: fileManager,
                executablePaths: executablePaths
            )
        else {
            return nil
        }
        self.init(
            executableURL: executableURL,
            environment: Self.environment(
                base: baseEnvironment,
                homeDirectory: homeDirectory ?? fileManager.homeDirectoryForCurrentUser,
                userName: userName
            )
        )
    }

    func run(
        arguments: [String],
        timeout: TimeInterval = 15 * 60,
        cancellationRequested: @Sendable () -> Bool = { Task<Never, Never>.isCancelled }
    ) throws -> ProcessResult {
        let result = try ProcessRunner.run(
            executableURL,
            arguments: arguments,
            environment: environment,
            timeout: timeout,
            cancellationRequested: cancellationRequested
        )
        return ProcessResult(
            status: result.status,
            output: result.output.trimmingCharacters(in: .whitespacesAndNewlines)
        )
    }

    static func executableURL(
        fileManager: FileManager,
        executablePaths: [String] = defaultExecutablePaths
    ) -> URL? {
        executablePaths
            .map { URL(fileURLWithPath: $0) }
            .first { fileManager.isExecutableFile(atPath: $0.path) }
    }

    static func environment(
        base: [String: String],
        homeDirectory: URL,
        userName: String
    ) -> [String: String] {
        var environment = base
        environment["HOME"] = homeDirectory.path
        environment["USER"] = userName
        environment["LOGNAME"] = userName
        environment["PATH"] = [
            "/opt/homebrew/bin",
            "/usr/local/bin",
            "/usr/bin",
            "/bin",
            "/usr/sbin",
            "/sbin"
        ].joined(separator: ":")
        return environment
    }

    static func formulaTrustTarget(from output: String, expectedFormula: String) -> String? {
        guard output.localizedCaseInsensitiveContains("untrusted tap"),
            let match = output.range(
                of: #"brew trust --formula [`']?([A-Za-z0-9._+/@-]+)"#,
                options: .regularExpression
            )
        else {
            return nil
        }
        let command = String(output[match])
        guard
            let target = command.split(separator: " ").last.map(String.init)?
                .trimmingCharacters(in: CharacterSet(charactersIn: "`'")),
            target == expectedFormula || target.hasPrefix(expectedFormula + "@")
        else {
            return nil
        }
        return target
    }
}
