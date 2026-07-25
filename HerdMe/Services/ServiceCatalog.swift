import Foundation

enum ServiceCatalog {
    static let all: [ServiceDefinition] = {
        var definitions: [ServiceDefinition] = [
            .init(id: "mariadb", name: "MariaDB", category: .database, defaultPort: 3306, latestVersion: "12.3", symbol: "cylinder.split.1x2"),
            .init(id: "mysql", name: "MySQL", category: .database, defaultPort: 3306, latestVersion: "9.7", symbol: "cylinder"),
            .init(id: "postgresql", name: "PostgreSQL", category: .database, defaultPort: 5432, latestVersion: "18", symbol: "server.rack"),
            .init(id: "mongodb", name: "MongoDB", category: .database, defaultPort: 27017, latestVersion: "7.0", symbol: "leaf"),
            .init(id: "redis", name: "Redis", category: .cache, defaultPort: 6379, latestVersion: "8.8", symbol: "square.stack.3d.up"),
            .init(id: "valkey", name: "Valkey", category: .cache, defaultPort: 6379, latestVersion: "9.1", symbol: "square.stack.3d.down.right"),
            .init(id: "meilisearch", name: "Meilisearch", category: .search, defaultPort: 7700, latestVersion: "1.50", symbol: "magnifyingglass"),
            .init(id: "typesense", name: "Typesense", category: .search, defaultPort: 8108, latestVersion: "30.2", symbol: "text.magnifyingglass"),
            .init(id: "minio", name: "MinIO", category: .storage, defaultPort: 9000, latestVersion: "Latest", symbol: "shippingbox")
        ]
#if arch(arm64)
        definitions.append(
            .init(id: "rustfs", name: "RustFS", category: .storage, defaultPort: 9000, latestVersion: "1.0 Beta", symbol: "archivebox")
        )
#endif
        return definitions
    }()
}
