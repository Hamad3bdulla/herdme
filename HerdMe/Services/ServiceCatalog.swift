import Foundation

enum ServiceCatalog {
    static let all: [ServiceDefinition] = RuntimeCatalog.services.compactMap { service in
        #if arch(arm64)
            let architecture = "arm64"
        #elseif arch(x86_64)
            let architecture = "x86_64"
        #else
            let architecture = "unsupported"
        #endif
        guard service.macOS.architectures.contains(architecture),
            let category = ServiceCategory.catalogValue(service.category)
        else {
            return nil
        }
        return ServiceDefinition(
            id: service.id,
            name: service.name,
            category: category,
            defaultPort: service.defaultPort,
            latestVersion: service.macOS.versionLabel,
            symbol: service.macOS.symbol
        )
    }
}

extension ServiceCategory {
    fileprivate static func catalogValue(_ value: String) -> ServiceCategory? {
        switch value {
        case "database": .database
        case "cache": .cache
        case "search": .search
        case "storage": .storage
        case "realtime": .realtime
        default: nil
        }
    }
}
