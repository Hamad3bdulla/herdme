import Foundation

enum DatabaseAuthenticationError: LocalizedError {
    case clientMissing(String)
    case migrationUnavailable(String)
    case verificationFailed(String)

    var errorDescription: String? {
        switch self {
        case .clientMissing(let name):
            String.localizedStringWithFormat(
                String(localized: "The %@ runtime is missing its command-line client, so HerdMe cannot secure this database."),
                name
            )
        case .migrationUnavailable(let name):
            String.localizedStringWithFormat(
                String(
                    localized: "HerdMe could not migrate the existing %@ data to managed authentication. The data was preserved unchanged."),
                name
            )
        case .verificationFailed(let name):
            String.localizedStringWithFormat(
                String(
                    localized: "HerdMe could not verify managed authentication for %@. The service was stopped and its data was preserved."),
                name
            )
        }
    }
}

enum DatabaseServiceAuthenticator {
    static let protectedDefinitions: Set<String> = ["mysql", "mariadb", "postgresql"]
    private static let markerName = ".herdme-auth-v1"

    static func secure(
        instance: ServiceInstance,
        executable: URL,
        dataURL: URL,
        credentials: ServiceCredentials,
        fileManager: FileManager = .default
    ) async throws {
        guard protectedDefinitions.contains(instance.definitionID) else { return }
        let markerURL = dataURL.appendingPathComponent(markerName)
        switch instance.definitionID {
        case "mysql", "mariadb":
            try await secureMySQLCompatible(
                instance: instance,
                executable: executable,
                dataURL: dataURL,
                credentials: credentials,
                markerURL: markerURL,
                fileManager: fileManager
            )
        case "postgresql":
            try await securePostgreSQL(
                instance: instance,
                executable: executable,
                dataURL: dataURL,
                credentials: credentials,
                markerURL: markerURL,
                fileManager: fileManager
            )
        default:
            return
        }
    }

    static func mysqlProvisioningSQL(credentials: ServiceCredentials) -> String {
        let username = credentials.username
        let secret = credentials.secret
        return [
            "CREATE DATABASE IF NOT EXISTS laravel",
            "CREATE USER IF NOT EXISTS '\(username)'@'127.0.0.1' IDENTIFIED BY '\(secret)'",
            "ALTER USER '\(username)'@'127.0.0.1' IDENTIFIED BY '\(secret)'",
            "GRANT ALL PRIVILEGES ON *.* TO '\(username)'@'127.0.0.1' WITH GRANT OPTION",
            "CREATE USER IF NOT EXISTS 'root'@'127.0.0.1' IDENTIFIED BY '\(secret)'",
            "ALTER USER 'root'@'127.0.0.1' IDENTIFIED BY '\(secret)'",
            "ALTER USER 'root'@'localhost' IDENTIFIED BY '\(secret)'",
            "DELETE FROM mysql.user WHERE User = ''",
            "FLUSH PRIVILEGES"
        ].joined(separator: "; ") + ";"
    }

    static func securedPostgreSQLHBA(_ contents: String) -> (contents: String, changed: Bool) {
        var changed = false
        let newline = contents.contains("\r\n") ? "\r\n" : "\n"
        let terminated = contents.hasSuffix("\n")
        let lines = contents.replacingOccurrences(of: "\r\n", with: "\n").split(
            separator: "\n",
            omittingEmptySubsequences: false
        ).map(String.init)
        let secured = lines.map { line -> String in
            let body = line.split(separator: "#", maxSplits: 1, omittingEmptySubsequences: false).first.map(String.init) ?? line
            let fields = body.split(whereSeparator: \.isWhitespace)
            guard let kind = fields.first,
                kind == "local" && fields.count >= 4 || kind.hasPrefix("host") && fields.count >= 5,
                fields.last == "trust"
            else {
                return line
            }
            guard let trustRange = body.range(of: "trust", options: .backwards),
                let swiftRange = Range(NSRange(trustRange, in: body), in: line)
            else {
                return line
            }
            changed = true
            var updated = line
            updated.replaceSubrange(swiftRange, with: "scram-sha-256")
            return updated
        }
        var result = secured.joined(separator: newline)
        if terminated && !result.hasSuffix(newline) { result += newline }
        return (result, changed)
    }

    private static func secureMySQLCompatible(
        instance: ServiceInstance,
        executable: URL,
        dataURL: URL,
        credentials: ServiceCredentials,
        markerURL: URL,
        fileManager: FileManager
    ) async throws {
        let binURL = executable.deletingLastPathComponent()
        let names = instance.definitionID == "mariadb" ? ["mariadb", "mysql"] : ["mysql"]
        guard
            let client = names.map({ binURL.appendingPathComponent($0) })
                .first(where: { fileManager.isExecutableFile(atPath: $0.path) })
        else {
            throw DatabaseAuthenticationError.clientMissing(instance.name)
        }

        guard
            try await waitForMySQL(
                client: client,
                instance: instance,
                credentials: credentials
            )
        else {
            throw DatabaseAuthenticationError.migrationUnavailable(instance.name)
        }

        if mysqlLogin(
            client: client,
            instance: instance,
            username: credentials.username,
            password: credentials.secret
        ), !mysqlLogin(client: client, instance: instance, username: credentials.username, password: nil),
            !mysqlSocketLogin(client: client, instance: instance, username: "root", password: nil)
        {
            try writeMarker(markerURL, fileManager: fileManager)
            return
        }

        guard !fileManager.fileExists(atPath: markerURL.path) else {
            throw DatabaseAuthenticationError.verificationFailed(instance.name)
        }
        let bootstrap = run(
            client,
            arguments: [
                "--no-defaults",
                "--protocol=SOCKET",
                "--socket=\(ServiceProcessManager.databaseSocketPath(for: instance.id))",
                "--user=root",
                "--connect-timeout=1",
                "--batch"
            ],
            environment: passwordEnvironment(variable: "MYSQL_PWD", value: nil),
            standardInput: Data(mysqlProvisioningSQL(credentials: credentials).utf8)
        )
        guard bootstrap.status == 0 else {
            throw DatabaseAuthenticationError.migrationUnavailable(instance.name)
        }
        guard
            mysqlLogin(
                client: client,
                instance: instance,
                username: credentials.username,
                password: credentials.secret
            ), !mysqlLogin(client: client, instance: instance, username: credentials.username, password: nil),
            !mysqlSocketLogin(client: client, instance: instance, username: "root", password: nil)
        else {
            throw DatabaseAuthenticationError.verificationFailed(instance.name)
        }
        try writeMarker(markerURL, fileManager: fileManager)
    }

    private static func mysqlLogin(
        client: URL,
        instance: ServiceInstance,
        username: String,
        password: String?
    ) -> Bool {
        run(
            client,
            arguments: [
                "--no-defaults", "--protocol=TCP", "--host=127.0.0.1", "--port=\(instance.port)",
                "--user=\(username)", "--connect-timeout=1", "--batch", "--skip-column-names",
                "--execute=SELECT 1"
            ],
            environment: passwordEnvironment(variable: "MYSQL_PWD", value: password)
        ).status == 0
    }

    private static func waitForMySQL(
        client: URL,
        instance: ServiceInstance,
        credentials: ServiceCredentials
    ) async throws -> Bool {
        for _ in 0..<30 {
            try Task.checkCancellation()
            if mysqlLogin(
                client: client,
                instance: instance,
                username: credentials.username,
                password: credentials.secret
            ) || mysqlSocketLogin(client: client, instance: instance, username: "root", password: nil) {
                return true
            }
            try await Task.sleep(for: .milliseconds(100))
        }
        return false
    }

    private static func mysqlSocketLogin(
        client: URL,
        instance: ServiceInstance,
        username: String,
        password: String?
    ) -> Bool {
        run(
            client,
            arguments: [
                "--no-defaults", "--protocol=SOCKET", "--socket=\(ServiceProcessManager.databaseSocketPath(for: instance.id))",
                "--user=\(username)", "--connect-timeout=1", "--batch", "--execute=SELECT 1"
            ],
            environment: passwordEnvironment(variable: "MYSQL_PWD", value: password)
        ).status == 0
    }

    private static func securePostgreSQL(
        instance: ServiceInstance,
        executable: URL,
        dataURL: URL,
        credentials: ServiceCredentials,
        markerURL: URL,
        fileManager: FileManager
    ) async throws {
        let binURL = executable.deletingLastPathComponent()
        let client = binURL.appendingPathComponent("psql")
        let pgControl = binURL.appendingPathComponent("pg_ctl")
        guard fileManager.isExecutableFile(atPath: client.path),
            fileManager.isExecutableFile(atPath: pgControl.path)
        else {
            throw DatabaseAuthenticationError.clientMissing(instance.name)
        }

        var passwordWorks = false
        for _ in 0..<30 where !passwordWorks {
            try Task.checkCancellation()
            passwordWorks = postgreSQLLogin(
                client: client,
                instance: instance,
                username: credentials.username,
                password: credentials.secret,
                dataURL: dataURL
            )
            if !passwordWorks { try await Task.sleep(for: .milliseconds(100)) }
        }
        let passwordlessWorks = postgreSQLLogin(
            client: client,
            instance: instance,
            username: credentials.username,
            password: nil,
            dataURL: dataURL
        )
        let hbaURL = dataURL.appendingPathComponent("pg_hba.conf")
        guard let original = try? String(contentsOf: hbaURL, encoding: .utf8) else {
            throw DatabaseAuthenticationError.migrationUnavailable(instance.name)
        }
        let secured = securedPostgreSQLHBA(original)
        if passwordWorks && !passwordlessWorks && !secured.changed {
            try writeMarker(markerURL, fileManager: fileManager)
            return
        }
        guard !fileManager.fileExists(atPath: markerURL.path) else {
            throw DatabaseAuthenticationError.verificationFailed(instance.name)
        }

        if !passwordWorks {
            guard
                let bootstrapUser = try await waitForPostgreSQLBootstrapUser(
                    client: client,
                    instance: instance,
                    dataURL: dataURL
                )
            else {
                throw DatabaseAuthenticationError.migrationUnavailable(instance.name)
            }
            let exists = postgreSQLLocalCommand(
                client: client,
                instance: instance,
                username: bootstrapUser,
                command: "SELECT 1 FROM pg_roles WHERE rolname = '\(credentials.username)'",
                dataURL: dataURL
            ).output.split(whereSeparator: \.isWhitespace).contains("1")
            let roleCommand =
                exists
                ? "ALTER ROLE \"\(credentials.username)\" WITH LOGIN SUPERUSER PASSWORD '\(credentials.secret)'"
                : "CREATE ROLE \"\(credentials.username)\" WITH LOGIN SUPERUSER PASSWORD '\(credentials.secret)'"
            guard
                postgreSQLLocalCommand(
                    client: client,
                    instance: instance,
                    username: bootstrapUser,
                    command: roleCommand,
                    dataURL: dataURL
                ).status == 0
            else {
                throw DatabaseAuthenticationError.migrationUnavailable(instance.name)
            }
        }

        guard secured.changed else {
            throw DatabaseAuthenticationError.migrationUnavailable(instance.name)
        }
        let permissions = try? fileManager.attributesOfItem(atPath: hbaURL.path)[.posixPermissions]
        try Data(secured.contents.utf8).write(to: hbaURL, options: .atomic)
        if let permissions { try? fileManager.setAttributes([.posixPermissions: permissions], ofItemAtPath: hbaURL.path) }
        _ = run(pgControl, arguments: ["reload", "-D", dataURL.path])
        try await Task.sleep(for: .milliseconds(200))

        let verified =
            postgreSQLLogin(
                client: client,
                instance: instance,
                username: credentials.username,
                password: credentials.secret,
                dataURL: dataURL
            )
            && !postgreSQLLogin(
                client: client,
                instance: instance,
                username: credentials.username,
                password: nil,
                dataURL: dataURL
            )
        guard verified else {
            try? Data(original.utf8).write(to: hbaURL, options: .atomic)
            if let permissions { try? fileManager.setAttributes([.posixPermissions: permissions], ofItemAtPath: hbaURL.path) }
            _ = run(pgControl, arguments: ["reload", "-D", dataURL.path])
            throw DatabaseAuthenticationError.verificationFailed(instance.name)
        }
        try writeMarker(markerURL, fileManager: fileManager)
    }

    private static func postgreSQLLogin(
        client: URL,
        instance: ServiceInstance,
        username: String,
        password: String?,
        dataURL: URL
    ) -> Bool {
        run(
            client,
            arguments: [
                "--host=127.0.0.1", "--port=\(instance.port)", "--username=\(username)",
                "--dbname=postgres", "--no-password", "--tuples-only", "--no-align", "--command=SELECT 1"
            ],
            environment: postgreSQLEnvironment(password: password, dataURL: dataURL)
        ).status == 0
    }

    private static func postgreSQLLocalCommand(
        client: URL,
        instance: ServiceInstance,
        username: String,
        command: String,
        dataURL: URL
    ) -> (status: Int32, output: String) {
        run(
            client,
            arguments: [
                "--port=\(instance.port)", "--username=\(username)", "--dbname=postgres",
                "--no-password", "--tuples-only", "--no-align", "--set=ON_ERROR_STOP=1"
            ],
            environment: postgreSQLEnvironment(password: nil, dataURL: dataURL),
            standardInput: Data((command + ";\n").utf8)
        )
    }

    private static func waitForPostgreSQLBootstrapUser(
        client: URL,
        instance: ServiceInstance,
        dataURL: URL
    ) async throws -> String? {
        for _ in 0..<30 {
            try Task.checkCancellation()
            if let username = [NSUserName(), "postgres"].first(where: { candidate in
                postgreSQLLocalCommand(
                    client: client,
                    instance: instance,
                    username: candidate,
                    command: "SELECT 1",
                    dataURL: dataURL
                ).status == 0
            }) {
                return username
            }
            try await Task.sleep(for: .milliseconds(100))
        }
        return nil
    }

    private static func postgreSQLEnvironment(password: String?, dataURL: URL) -> [String: String] {
        var environment = passwordEnvironment(variable: "PGPASSWORD", value: password)
        environment["PGPASSFILE"] = dataURL.appendingPathComponent(".herdme-no-pgpass").path
        environment["PGCONNECT_TIMEOUT"] = "2"
        return environment
    }

    private static func passwordEnvironment(variable: String, value: String?) -> [String: String] {
        var environment = ProcessInfo.processInfo.environment
        environment[variable] = value
        return environment
    }

    private static func writeMarker(_ markerURL: URL, fileManager: FileManager) throws {
        try Data("auth-v1\n".utf8).write(to: markerURL, options: .atomic)
        try? fileManager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: markerURL.path)
    }

    private static func run(
        _ executable: URL,
        arguments: [String],
        environment: [String: String]? = nil,
        standardInput: Data? = nil
    ) -> (status: Int32, output: String) {
        do {
            let result = try ProcessRunner.run(
                executable,
                arguments: arguments,
                environment: environment,
                standardInput: standardInput
            )
            return (result.status, result.output.trimmingCharacters(in: .whitespacesAndNewlines))
        } catch {
            return (-1, "")
        }
    }
}
