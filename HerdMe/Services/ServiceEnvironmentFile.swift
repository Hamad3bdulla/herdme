import Foundation

struct ServiceEnvironmentVariable: Equatable, Sendable {
    let key: String
    let value: String
}

struct ServiceEnvironmentUpdate: Equatable, Sendable {
    let environmentURL: URL
    let addedKeys: Int
    let updatedKeys: Int
    let createdFile: Bool
}

enum ServiceEnvironmentError: LocalizedError {
    case unsupported(String)
    case projectMissing
    case symbolicLink
    case invalidFile
    case fileTooLarge

    var errorDescription: String? {
        switch self {
        case .unsupported(let service):
            String.localizedStringWithFormat(
                String(localized: "HerdMe does not have .env variables for %@."),
                service
            )
        case .projectMissing:
            String(localized: "The selected project directory is no longer available.")
        case .symbolicLink:
            String(localized: "HerdMe will not modify a symbolic .env file. Replace it with a project-owned file first.")
        case .invalidFile:
            String(localized: "The project's .env file must be a regular UTF-8 text file.")
        case .fileTooLarge:
            String(localized: "The project's .env file is larger than the supported 4 MB limit.")
        }
    }
}

enum ServiceEnvironmentConfiguration {
    static func variables(
        for instance: ServiceInstance,
        credentials: ServiceCredentials
    ) -> [ServiceEnvironmentVariable] {
        let host = "127.0.0.1"
        let port = String(instance.port)

        switch instance.definitionID {
        case "mysql", "mariadb":
            return variables([
                ("DB_CONNECTION", "mysql"),
                ("DB_HOST", host),
                ("DB_PORT", port),
                ("DB_DATABASE", "laravel"),
                ("DB_USERNAME", credentials.username),
                ("DB_PASSWORD", credentials.secret)
            ])
        case "postgresql":
            return variables([
                ("DB_CONNECTION", "pgsql"),
                ("DB_HOST", host),
                ("DB_PORT", port),
                ("DB_DATABASE", "postgres"),
                ("DB_USERNAME", credentials.username),
                ("DB_PASSWORD", credentials.secret)
            ])
        case "mongodb":
            return variables([
                ("MONGODB_URI", "mongodb://\(host):\(port)/admin"),
                ("MONGODB_DATABASE", "admin")
            ])
        case "redis", "valkey":
            return variables([
                ("REDIS_CLIENT", "phpredis"),
                ("REDIS_HOST", host),
                ("REDIS_PASSWORD", "null"),
                ("REDIS_PORT", port)
            ])
        case "meilisearch":
            return variables([
                ("MEILISEARCH_HOST", "http://\(host):\(port)"),
                ("MEILISEARCH_KEY", "")
            ])
        case "typesense":
            return variables([
                ("SCOUT_DRIVER", "typesense"),
                ("TYPESENSE_API_KEY", credentials.secret),
                ("TYPESENSE_SEARCH_ONLY_KEY", credentials.secret),
                ("TYPESENSE_HOST", host),
                ("TYPESENSE_PORT", port),
                ("TYPESENSE_PROTOCOL", "http")
            ])
        case "minio", "rustfs":
            return variables([
                ("AWS_ACCESS_KEY_ID", credentials.username),
                ("AWS_SECRET_ACCESS_KEY", credentials.secret),
                ("AWS_DEFAULT_REGION", "us-east-1"),
                ("AWS_ENDPOINT", "http://\(host):\(port)"),
                ("AWS_USE_PATH_STYLE_ENDPOINT", "true")
            ])
        default:
            return []
        }
    }

    private static func variables(
        _ values: [(String, String)]
    ) -> [ServiceEnvironmentVariable] {
        values.map(ServiceEnvironmentVariable.init(key:value:))
    }
}

enum ServiceEnvironmentFile {
    private static let maximumEnvironmentFileBytes = 4 * 1_024 * 1_024

    static func update(
        projectURL: URL,
        instance: ServiceInstance,
        credentials: ServiceCredentials,
        fileManager: FileManager = .default
    ) throws -> ServiceEnvironmentUpdate {
        var isDirectory: ObjCBool = false
        guard fileManager.fileExists(atPath: projectURL.path, isDirectory: &isDirectory),
            isDirectory.boolValue
        else {
            throw ServiceEnvironmentError.projectMissing
        }

        let variables = ServiceEnvironmentConfiguration.variables(
            for: instance,
            credentials: credentials
        )
        guard !variables.isEmpty else {
            throw ServiceEnvironmentError.unsupported(instance.name)
        }

        let environmentURL = projectURL.appendingPathComponent(".env")
        let exampleURL = projectURL.appendingPathComponent(".env.example")
        try rejectSymbolicLink(environmentURL)
        let environmentExists = fileManager.fileExists(atPath: environmentURL.path)
        let initialContents: String

        if environmentExists {
            initialContents = try readUTF8(environmentURL)
        } else if fileManager.fileExists(atPath: exampleURL.path) {
            try rejectSymbolicLink(exampleURL)
            initialContents = try readUTF8(exampleURL)
        } else {
            initialContents = ""
        }

        let merged = merging(
            initialContents,
            variables: variables,
            serviceName: instance.name
        )
        try Data(merged.contents.utf8).write(to: environmentURL, options: .atomic)
        return ServiceEnvironmentUpdate(
            environmentURL: environmentURL,
            addedKeys: merged.addedKeys,
            updatedKeys: merged.updatedKeys,
            createdFile: !environmentExists
        )
    }

    static func merging(
        _ contents: String,
        variables: [ServiceEnvironmentVariable],
        serviceName: String
    ) -> (contents: String, addedKeys: Int, updatedKeys: Int) {
        let newline = contents.contains("\r\n") ? "\r\n" : "\n"
        var lines =
            contents
            .replacingOccurrences(of: "\r\n", with: "\n")
            .components(separatedBy: "\n")
        if lines.last == "" { lines.removeLast() }

        let values = Dictionary(uniqueKeysWithValues: variables.map { ($0.key, $0.value) })
        var foundKeys = Set<String>()
        for index in lines.indices {
            guard let key = environmentKey(in: lines[index]), let value = values[key] else {
                continue
            }
            lines[index] = "\(key)=\(encoded(value))"
            foundKeys.insert(key)
        }

        let missing = variables.filter { !foundKeys.contains($0.key) }
        if !missing.isEmpty {
            if lines.last?.isEmpty == false { lines.append("") }
            let safeName =
                serviceName
                .replacingOccurrences(of: "\r", with: " ")
                .replacingOccurrences(of: "\n", with: " ")
                .prefix(80)
            lines.append("# Added by HerdMe for \(safeName)")
            lines.append(contentsOf: missing.map { "\($0.key)=\(encoded($0.value))" })
        }

        return (
            lines.joined(separator: newline) + newline,
            missing.count,
            foundKeys.count
        )
    }

    private static func rejectSymbolicLink(_ url: URL) throws {
        if (try? url.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true {
            throw ServiceEnvironmentError.symbolicLink
        }
    }

    private static func readUTF8(_ url: URL) throws -> String {
        guard let values = try? url.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey]),
            values.isRegularFile == true
        else {
            throw ServiceEnvironmentError.invalidFile
        }
        guard (values.fileSize ?? 0) <= maximumEnvironmentFileBytes else {
            throw ServiceEnvironmentError.fileTooLarge
        }
        guard let contents = try? String(contentsOf: url, encoding: .utf8) else {
            throw ServiceEnvironmentError.invalidFile
        }
        return contents
    }

    private static func environmentKey(in line: String) -> String? {
        var candidate = line.trimmingCharacters(in: .whitespaces)
        if candidate.hasPrefix("export ") {
            candidate.removeFirst("export ".count)
            candidate = candidate.trimmingCharacters(in: .whitespaces)
        }
        guard !candidate.hasPrefix("#"),
            let separator = candidate.firstIndex(of: "=")
        else {
            return nil
        }
        let key = String(candidate[..<separator]).trimmingCharacters(in: .whitespaces)
        guard let first = key.first, first == "_" || first.isLetter,
            key.allSatisfy({ $0 == "_" || $0.isLetter || $0.isNumber })
        else {
            return nil
        }
        return key
    }

    private static func encoded(_ value: String) -> String {
        guard !value.isEmpty else { return "" }
        let safe = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "_./:@+-"))
        if value.unicodeScalars.allSatisfy(safe.contains) { return value }
        return "\""
            + value
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
            .replacingOccurrences(of: "\n", with: "\\n") + "\""
    }
}
