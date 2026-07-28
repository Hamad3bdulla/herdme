import Foundation

struct RuntimeCatalogDocument: Decodable, Sendable {
    struct Defaults: Decodable, Sendable {
        let phpCycle: String
        let nodeMajor: String
    }

    struct PHPPolicy: Decodable, Sendable {
        let installableCycles: [String]
    }

    struct NodePolicy: Decodable, Sendable {
        let macOSMajors: [String]
        let windowsMajors: [String]
    }

    struct Service: Decodable, Sendable {
        struct MacOS: Decodable, Sendable {
            let versionLabel: String
            let symbol: String
            let architectures: [String]
        }

        struct Windows: Decodable, Sendable {
            let versionLabel: String
            let installable: Bool
            let unavailableReason: String?
        }

        let id: String
        let name: String
        let category: String
        let defaultPort: Int
        let macOS: MacOS
        let windows: Windows
    }

    let schemaVersion: Int
    let defaults: Defaults
    let php: PHPPolicy
    let node: NodePolicy
    let services: [Service]
}

enum RuntimeCatalogError: LocalizedError {
    case missingResource
    case unsupportedSchema(Int)
    case invalidValue(String)

    var errorDescription: String? {
        switch self {
        case .missingResource:
            "The bundled HerdMe runtime catalog is missing."
        case .unsupportedSchema(let version):
            "The bundled HerdMe runtime catalog uses unsupported schema version \(version)."
        case .invalidValue(let detail):
            "The bundled HerdMe runtime catalog is invalid: \(detail)"
        }
    }
}

private final class RuntimeCatalogBundleMarker: NSObject {}

enum RuntimeCatalog {
    private static let loadResult: Result<RuntimeCatalogDocument, Error> = Result {
        let bundles = [Bundle.main, Bundle(for: RuntimeCatalogBundleMarker.self)]
        guard
            let url = bundles.lazy.compactMap({
                $0.url(forResource: "runtime-catalog", withExtension: "json")
            }).first
        else {
            throw RuntimeCatalogError.missingResource
        }
        return try decodeAndValidate(Data(contentsOf: url))
    }

    static var loadIssue: String? {
        guard case .failure(let error) = loadResult else { return nil }
        return error.localizedDescription
    }

    static var defaultPHPCycle: String {
        document?.defaults.phpCycle ?? "8.4"
    }

    static var defaultNodeMajor: String {
        document?.defaults.nodeMajor ?? "22"
    }

    static var installablePHPCycles: [String] {
        document?.php.installableCycles ?? []
    }

    static var macOSNodeMajors: [String] {
        document?.node.macOSMajors ?? []
    }

    static var windowsNodeMajors: [String] {
        document?.node.windowsMajors ?? []
    }

    static var services: [RuntimeCatalogDocument.Service] {
        document?.services ?? []
    }

    static func decodeAndValidate(_ data: Data) throws -> RuntimeCatalogDocument {
        let document = try JSONDecoder().decode(RuntimeCatalogDocument.self, from: data)
        try validate(document)
        return document
    }

    private static var document: RuntimeCatalogDocument? {
        try? loadResult.get()
    }

    private static func validate(_ document: RuntimeCatalogDocument) throws {
        guard document.schemaVersion == 1 else {
            throw RuntimeCatalogError.unsupportedSchema(document.schemaVersion)
        }
        try validateVersions(document)
        try validateServices(document.services)
    }

    private static func validateVersions(_ document: RuntimeCatalogDocument) throws {
        guard hasUniqueValues(document.php.installableCycles),
            document.php.installableCycles.allSatisfy(isPHPVersion)
        else {
            throw RuntimeCatalogError.invalidValue("PHP cycles must be unique major.minor values.")
        }
        guard document.php.installableCycles.contains(document.defaults.phpCycle) else {
            throw RuntimeCatalogError.invalidValue("the default PHP cycle is not installable.")
        }
        for (name, majors) in [
            ("macOS", document.node.macOSMajors),
            ("Windows", document.node.windowsMajors)
        ] {
            guard hasUniqueValues(majors),
                majors.allSatisfy({ Int($0).map { $0 >= 1 } == true })
            else {
                throw RuntimeCatalogError.invalidValue(
                    "\(name) Node.js majors must be unique positive integers."
                )
            }
            guard majors.contains(document.defaults.nodeMajor) else {
                throw RuntimeCatalogError.invalidValue(
                    "the default Node.js major is unavailable on \(name)."
                )
            }
        }
    }

    private static func validateServices(
        _ services: [RuntimeCatalogDocument.Service]
    ) throws {
        guard !services.isEmpty else {
            throw RuntimeCatalogError.invalidValue("the service list is empty.")
        }
        guard hasUniqueValues(services.map(\.id)) else {
            throw RuntimeCatalogError.invalidValue("service identifiers must be unique.")
        }
        let categories = Set(["database", "cache", "search", "storage", "realtime"])
        let architectures = Set(["arm64", "x86_64"])
        for service in services {
            guard
                service.id.range(
                    of: #"^[a-z][a-z0-9-]*$"#,
                    options: .regularExpression
                ) != nil
            else {
                throw RuntimeCatalogError.invalidValue(
                    "service identifier \(service.id) is malformed."
                )
            }
            guard !service.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                categories.contains(service.category),
                (1...65_535).contains(service.defaultPort),
                !service.macOS.versionLabel.isEmpty,
                !service.macOS.symbol.isEmpty,
                !service.macOS.architectures.isEmpty,
                Set(service.macOS.architectures).isSubset(of: architectures),
                !service.windows.versionLabel.isEmpty
            else {
                throw RuntimeCatalogError.invalidValue(
                    "service \(service.id) has incomplete platform metadata."
                )
            }
            let reason = service.windows.unavailableReason?
                .trimmingCharacters(in: .whitespacesAndNewlines)
            guard service.windows.installable ? reason == nil : reason?.isEmpty == false else {
                throw RuntimeCatalogError.invalidValue(
                    "service \(service.id) has an inconsistent Windows availability reason."
                )
            }
        }
    }

    private static func isPHPVersion(_ value: String) -> Bool {
        value.range(of: #"^[0-9]+\.[0-9]+$"#, options: .regularExpression) != nil
    }

    private static func hasUniqueValues(_ values: [String]) -> Bool {
        !values.isEmpty && Set(values).count == values.count
    }
}
