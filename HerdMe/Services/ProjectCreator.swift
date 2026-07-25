import Foundation

enum StarterKit: String, CaseIterable, Identifiable {
    case none = "No Starter Kit"
    case react = "React"
    case vue = "Vue"
    case svelte = "Svelte"
    case livewire = "Livewire"
    case custom = "Custom Starter Kit"

    var id: String { rawValue }

    var symbol: String {
        switch self {
        case .none: "l.square.fill"
        case .react: "atom"
        case .vue: "v.circle.fill"
        case .svelte: "s.circle.fill"
        case .livewire: "waveform.circle.fill"
        case .custom: "puzzlepiece.extension.fill"
        }
    }

    var commandFlag: String? {
        switch self {
        case .none, .custom: nil
        case .react: "--react"
        case .vue: "--vue"
        case .svelte: "--svelte"
        case .livewire: "--livewire"
        }
    }

    var requiresFrontendAssets: Bool {
        switch self {
        case .react, .vue, .svelte, .livewire: true
        case .none, .custom: false
        }
    }
}

struct NewProjectRequest: Sendable {
    let name: String
    let parentDirectory: URL
    let starterKit: StarterKit
    let customStarterKit: String?
    let testingFramework: String
    let installBoost: Bool
    let initializeGit: Bool

    init(
        name: String,
        parentDirectory: URL,
        starterKit: StarterKit,
        customStarterKit: String? = nil,
        testingFramework: String,
        installBoost: Bool,
        initializeGit: Bool
    ) {
        self.name = name
        self.parentDirectory = parentDirectory
        self.starterKit = starterKit
        self.customStarterKit = customStarterKit
        self.testingFramework = testingFramework
        self.installBoost = installBoost
        self.initializeGit = initializeGit
    }
}

enum ProjectCreationStage: String, CaseIterable, Sendable, Equatable, Identifiable {
    case validatingRequest
    case preparingLaravelInstaller
    case creatingLaravelProject
    case installingLaravelBoost
    case preparingNodeRuntime
    case installingFrontendDependencies
    case buildingFrontendAssets
    case initializingGitRepository
    case verifyingProject
    case registeringSite
    case completed

    var id: String { rawValue }

    var title: String {
        switch self {
        case .validatingRequest: "Checking project details"
        case .preparingLaravelInstaller: "Preparing Laravel Installer"
        case .creatingLaravelProject: "Creating Laravel project"
        case .installingLaravelBoost: "Installing Laravel Boost"
        case .preparingNodeRuntime: "Preparing Node.js"
        case .installingFrontendDependencies: "Installing frontend packages"
        case .buildingFrontendAssets: "Building frontend assets"
        case .initializingGitRepository: "Initializing Git repository"
        case .verifyingProject: "Verifying Laravel project"
        case .registeringSite: "Registering local site"
        case .completed: "Site created"
        }
    }

    var detail: String {
        switch self {
        case .validatingRequest: "Verifying the name, location, and project folder."
        case .preparingLaravelInstaller:
            "Using the managed Laravel Installer already on this Mac, and installing it only if needed."
        case .creatingLaravelProject:
            "Running the installed Laravel Installer to create and configure the application."
        case .installingLaravelBoost: "Adding Laravel Boost as a development dependency."
        case .preparingNodeRuntime: "Making sure a HerdMe-managed Node.js runtime is ready."
        case .installingFrontendDependencies: "Restoring the starter kit's npm packages."
        case .buildingFrontendAssets: "Compiling the production Vite assets used by the site."
        case .initializingGitRepository: "Creating the initial local Git repository."
        case .verifyingProject: "Checking that Laravel finished with its required files."
        case .registeringSite: "Adding the project to HerdMe's local sites."
        case .completed: "Your project is ready to open."
        }
    }

    static func stages(
        installBoost: Bool,
        buildFrontendAssets: Bool = false,
        initializeGit: Bool
    ) -> [Self] {
        var stages: [Self] = [
            .validatingRequest,
            .preparingLaravelInstaller,
            .creatingLaravelProject
        ]
        if installBoost { stages.append(.installingLaravelBoost) }
        if buildFrontendAssets {
            stages += [
                .preparingNodeRuntime,
                .installingFrontendDependencies,
                .buildingFrontendAssets
            ]
        }
        if initializeGit { stages.append(.initializingGitRepository) }
        stages += [.verifyingProject, .registeringSite, .completed]
        return stages
    }
}

enum ProjectCreationError: LocalizedError {
    case invalidName
    case invalidCustomStarterKit
    case destinationExists
    case externalApplicationPath
    case incompleteProject([String])
    case commandFailed(String)

    var errorDescription: String? {
        switch self {
        case .invalidName: "Enter a valid project name."
        case .invalidCustomStarterKit:
            "Enter the custom starter kit as a Composer package such as vendor/package."
        case .destinationExists: "A folder with this project name already exists."
        case .externalApplicationPath:
            "HerdMe does not create projects inside another application's folders. Choose a HerdMe-owned folder instead."
        case let .incompleteProject(missingFiles):
            "Laravel Installer finished without creating a complete Laravel project. Missing: \(missingFiles.joined(separator: ", "))."
        case let .commandFailed(output):
            ErrorPresentation(output, fallback: "Laravel Installer could not finish creating the site.").message
        }
    }

    var technicalDetails: String? {
        guard case let .commandFailed(output) = self else { return nil }
        return ErrorPresentation(
            output,
            fallback: "Laravel Installer could not finish creating the site."
        ).technicalDetails
    }
}

actor ProjectCreator {
    static var defaultProjectsDirectory: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("HerdMe", isDirectory: true)
    }

    private let rootURL: URL

    init(rootURL: URL) {
        self.rootURL = rootURL
    }

    nonisolated static func validate(_ request: NewProjectRequest) throws {
        let trimmedName = request.name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedName.isEmpty,
              trimmedName.range(of: "^[A-Za-z0-9._-]+$", options: .regularExpression) != nil else {
            throw ProjectCreationError.invalidName
        }
        guard !IndependentPathPolicy.belongsToOtherHerd(request.parentDirectory) else {
            throw ProjectCreationError.externalApplicationPath
        }
        if request.starterKit == .custom {
            let package = request.customStarterKit?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            guard package.range(
                of: "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?::\\S+)?$",
                options: .regularExpression
            ) != nil else {
                throw ProjectCreationError.invalidCustomStarterKit
            }
        }

        let destination = request.parentDirectory.appendingPathComponent(trimmedName, isDirectory: true)
        guard !FileManager.default.fileExists(atPath: destination.path) else {
            throw ProjectCreationError.destinationExists
        }
    }

    func create(
        _ request: NewProjectRequest,
        progress: @escaping @MainActor @Sendable (ProjectCreationStage) -> Void = { _ in }
    ) async throws -> URL {
        try Self.validate(request)

        let trimmedName = request.name.trimmingCharacters(in: .whitespacesAndNewlines)
        let destination = request.parentDirectory.appendingPathComponent(trimmedName, isDirectory: true)

        let php = rootURL.appendingPathComponent("bin/php")
        let laravel = rootURL.appendingPathComponent("Composer/vendor/bin/laravel")
        guard FileManager.default.isExecutableFile(atPath: php.path),
              FileManager.default.isReadableFile(atPath: laravel.path) else {
            throw ProjectCreationError.commandFailed("Install Laravel Installer from the PHP page first.")
        }

        let arguments = [laravel.path] + Self.laravelArguments(for: request)

        await progress(.creatingLaravelProject)
        let process = Process()
        let output = Pipe()
        process.executableURL = php
        process.currentDirectoryURL = request.parentDirectory
        process.arguments = arguments
        process.environment = managedEnvironment
        process.standardOutput = output
        process.standardError = output
        try process.run()
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        let text = String(data: data, encoding: .utf8) ?? ""
        guard process.terminationStatus == 0 else {
            throw ProjectCreationError.commandFailed(text)
        }

        if request.installBoost {
            await progress(.installingLaravelBoost)
            try installBoost(at: destination)
        }
        if request.starterKit.requiresFrontendAssets {
            await progress(.preparingNodeRuntime)
            let npm = try await ensureManagedNPM()
            try validateFrontendBuild(at: destination)

            await progress(.installingFrontendDependencies)
            try runManagedCommand(
                npm,
                arguments: ["install", "--no-audit", "--no-fund", "--no-progress"],
                at: destination,
                fallbackError: "Frontend package installation failed."
            )

            await progress(.buildingFrontendAssets)
            try runManagedCommand(
                npm,
                arguments: ["run", "build"],
                at: destination,
                fallbackError: "Frontend asset build failed."
            )
        }
        if request.initializeGit {
            await progress(.initializingGitRepository)
            try initializeGit(at: destination)
        }
        await progress(.verifyingProject)
        try verifyLaravelProject(
            at: destination,
            requiresFrontendAssets: request.starterKit.requiresFrontendAssets
        )
        return destination
    }

    nonisolated static func laravelArguments(for request: NewProjectRequest) -> [String] {
        let trimmedName = request.name.trimmingCharacters(in: .whitespacesAndNewlines)
        var arguments = ["new", trimmedName, "--no-interaction"]
        arguments.append(request.testingFramework == "Pest" ? "--pest" : "--phpunit")
        if let flag = request.starterKit.commandFlag {
            arguments += [flag, "--no-node"]
        } else if request.starterKit == .custom,
                  let package = request.customStarterKit?.trimmingCharacters(in: .whitespacesAndNewlines),
                  !package.isEmpty {
            arguments += ["--using=\(package)", "--npm"]
        }
        return arguments
    }

    private func verifyLaravelProject(
        at destination: URL,
        requiresFrontendAssets: Bool
    ) throws {
        var requiredFiles = ["artisan", "vendor/autoload.php"]
        if requiresFrontendAssets {
            requiredFiles.append("public/build/manifest.json")
        }
        let missingFiles = requiredFiles.filter {
            !FileManager.default.isReadableFile(
                atPath: destination.appendingPathComponent($0).path
            )
        }
        guard missingFiles.isEmpty else {
            throw ProjectCreationError.incompleteProject(missingFiles)
        }
    }

    private func ensureManagedNPM() async throws -> URL {
        let npm = rootURL.appendingPathComponent("bin/npm")
        if FileManager.default.isExecutableFile(atPath: npm.path) { return npm }

        _ = try await RuntimeInstaller(rootURL: rootURL).installNode(cycle: "22")
        guard FileManager.default.isExecutableFile(atPath: npm.path) else {
            throw ProjectCreationError.commandFailed(
                "HerdMe could not prepare Node.js for the selected starter kit."
            )
        }
        return npm
    }

    private func validateFrontendBuild(at destination: URL) throws {
        let packageURL = destination.appendingPathComponent("package.json")
        guard let data = try? Data(contentsOf: packageURL),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let scripts = object["scripts"] as? [String: Any],
              let build = scripts["build"] as? String,
              !build.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ProjectCreationError.commandFailed(
                "The selected starter kit did not provide a valid npm build script."
            )
        }
    }

    private func runManagedCommand(
        _ executable: URL,
        arguments: [String],
        at directory: URL,
        fallbackError: String
    ) throws {
        let process = Process()
        let output = Pipe()
        process.executableURL = executable
        process.currentDirectoryURL = directory
        process.arguments = arguments
        process.environment = managedEnvironment
        process.standardOutput = output
        process.standardError = output
        try process.run()
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else {
            let text = String(data: data, encoding: .utf8) ?? ""
            throw ProjectCreationError.commandFailed(text.isEmpty ? fallbackError : text)
        }
    }

    private func installBoost(at destination: URL) throws {
        let php = rootURL.appendingPathComponent("bin/php")
        let composer = rootURL.appendingPathComponent("bin/composer")
        guard FileManager.default.isExecutableFile(atPath: php.path),
              FileManager.default.isReadableFile(atPath: composer.path) else {
            throw ProjectCreationError.commandFailed("HerdMe Composer is not installed.")
        }
        let process = Process()
        let output = Pipe()
        process.executableURL = php
        process.currentDirectoryURL = destination
        process.arguments = [
            composer.path,
            "require", "laravel/boost", "--dev", "--no-interaction", "--no-progress", "--no-ansi"
        ]
        process.environment = managedEnvironment
        process.standardOutput = output
        process.standardError = output
        try process.run()
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else {
            throw ProjectCreationError.commandFailed(String(data: data, encoding: .utf8) ?? "Laravel Boost installation failed.")
        }
    }

    private func initializeGit(at destination: URL) throws {
        let git = URL(fileURLWithPath: "/usr/bin/git")
        guard FileManager.default.isExecutableFile(atPath: git.path) else {
            throw ProjectCreationError.commandFailed("Git is not available on this Mac.")
        }

        let process = Process()
        let output = Pipe()
        process.executableURL = git
        process.currentDirectoryURL = destination
        process.arguments = ["init"]
        process.environment = managedEnvironment
        process.standardOutput = output
        process.standardError = output
        try process.run()
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else {
            throw ProjectCreationError.commandFailed(
                String(data: data, encoding: .utf8) ?? "Git repository initialization failed."
            )
        }
    }

    private var managedEnvironment: [String: String] {
        var environment = ProcessInfo.processInfo.environment
        environment["COMPOSER_HOME"] = rootURL.appendingPathComponent("Composer", isDirectory: true).path
        environment["COMPOSER_CACHE_DIR"] = rootURL.appendingPathComponent("Cache/composer", isDirectory: true).path
        environment["PATH"] = [
            rootURL.appendingPathComponent("bin", isDirectory: true).path,
            "/usr/bin", "/bin", "/usr/sbin", "/sbin"
        ].joined(separator: ":")
        return environment
    }
}
