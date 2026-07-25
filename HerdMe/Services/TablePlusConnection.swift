import Foundation

enum TablePlusConnection {
    static let bundleIdentifier = "com.tinyapp.TablePlus"

    static func url(
        for instance: ServiceInstance,
        postgreSQLUsername: String = NSUserName()
    ) -> URL? {
        guard instance.port > 0, instance.port <= 65_535 else { return nil }

        let scheme: String
        let username: String?
        let database: String
        switch instance.definitionID {
        case "mysql":
            scheme = "mysql"
            username = "root"
            database = "mysql"
        case "mariadb":
            scheme = "mariadb"
            username = "root"
            database = "mysql"
        case "postgresql":
            scheme = "postgresql"
            username = postgreSQLUsername
            database = "postgres"
        case "mongodb":
            scheme = "mongodb"
            username = nil
            database = "admin"
        case "redis", "valkey":
            scheme = "redis"
            username = nil
            database = "0"
        default:
            return nil
        }

        var components = URLComponents()
        components.scheme = scheme
        components.host = "127.0.0.1"
        components.port = instance.port
        components.user = username
        components.path = "/\(database)"
        return components.url
    }
}
