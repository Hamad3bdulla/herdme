import Foundation

enum TablePlusConnection {
    static let bundleIdentifier = "com.tinyapp.TablePlus"

    static func supports(_ definitionID: String) -> Bool {
        target(for: definitionID) != nil
    }

    static func displayAddress(for instance: ServiceInstance) -> String? {
        guard instance.port > 0, instance.port <= 65_535,
              let target = target(for: instance.definitionID) else { return nil }
        var components = URLComponents()
        components.scheme = target.scheme
        components.host = "127.0.0.1"
        components.port = instance.port
        components.path = "/\(target.database)"
        return components.url?.absoluteString
    }

    static func url(
        for instance: ServiceInstance,
        credentials: ServiceCredentials? = nil
    ) -> URL? {
        guard instance.port > 0, instance.port <= 65_535 else { return nil }

        guard let target = target(for: instance.definitionID),
              !target.requiresCredentials || credentials != nil else { return nil }

        var components = URLComponents()
        components.scheme = target.scheme
        components.host = "127.0.0.1"
        components.port = instance.port
        if target.requiresCredentials {
            components.user = credentials?.username
            components.password = credentials?.secret
        }
        components.path = "/\(target.database)"
        return components.url
    }

    private static func target(for definitionID: String)
        -> (scheme: String, database: String, requiresCredentials: Bool)? {
        switch definitionID {
        case "mysql": ("mysql", "mysql", true)
        case "mariadb": ("mariadb", "mysql", true)
        case "postgresql": ("postgresql", "postgres", true)
        case "mongodb": ("mongodb", "admin", false)
        case "redis", "valkey": ("redis", "0", false)
        default: nil
        }
    }
}
