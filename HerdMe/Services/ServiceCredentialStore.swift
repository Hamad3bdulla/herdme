import Foundation
import Security

struct ServiceCredentials: Codable, Equatable, Sendable {
    let username: String
    let secret: String

    static func generate(for identifier: UUID) throws -> ServiceCredentials {
        var bytes = [UInt8](repeating: 0, count: 32)
        let status = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        guard status == errSecSuccess else {
            throw ServiceCredentialError.randomGenerationFailed(status)
        }
        let secret = Data(bytes).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
        let compactIdentifier = identifier.uuidString
            .replacingOccurrences(of: "-", with: "")
            .lowercased()
        return ServiceCredentials(
            username: "herdme_" + compactIdentifier.prefix(12),
            secret: secret
        )
    }

    var isValid: Bool {
        username.count >= 3
            && secret.count >= 32
            && username.unicodeScalars.allSatisfy(Self.isEnvironmentSafe)
            && secret.unicodeScalars.allSatisfy(Self.isEnvironmentSafe)
    }

    private static func isEnvironmentSafe(_ scalar: UnicodeScalar) -> Bool {
        switch scalar.value {
        case 45, 48...57, 65...90, 95, 97...122:
            true
        default:
            false
        }
    }
}

enum ServiceCredentialError: LocalizedError {
    case randomGenerationFailed(OSStatus)
    case keychainReadFailed(OSStatus)
    case keychainWriteFailed(OSStatus)
    case keychainDeleteFailed(OSStatus)
    case interactionRequired
    case invalidStoredValue

    var errorDescription: String? {
        switch self {
        case .randomGenerationFailed:
            String(localized: "HerdMe could not generate secure credentials for this service.")
        case .keychainReadFailed:
            String(localized: "HerdMe could not read this service's credentials from Keychain.")
        case .keychainWriteFailed:
            String(localized: "HerdMe could not save this service's credentials in Keychain.")
        case .keychainDeleteFailed:
            String(localized: "HerdMe could not remove this service's credentials from Keychain.")
        case .interactionRequired:
            String(localized: "Open HerdMe and start this service once to allow Keychain access.")
        case .invalidStoredValue:
            String(localized: "This service's saved credentials are invalid. Delete and add the service again.")
        }
    }
}

protocol ServiceCredentialBacking: AnyObject {
    func read(account: String, allowInteraction: Bool) throws -> Data?
    func write(_ data: Data, account: String) throws
    func delete(account: String) throws
}

final class ServiceCredentialStore: @unchecked Sendable {
    private let backend: ServiceCredentialBacking
    private let lock = NSLock()

    init(backend: ServiceCredentialBacking = KeychainServiceCredentialBackend()) {
        self.backend = backend
    }

    func credentials(
        for identifier: UUID,
        allowInteraction: Bool = true
    ) throws -> ServiceCredentials {
        lock.lock()
        defer { lock.unlock() }
        let account = Self.account(for: identifier)
        if let data = try backend.read(account: account, allowInteraction: allowInteraction) {
            let credentials = try JSONDecoder().decode(ServiceCredentials.self, from: data)
            guard credentials.isValid else { throw ServiceCredentialError.invalidStoredValue }
            return credentials
        }
        guard allowInteraction else { throw ServiceCredentialError.interactionRequired }

        let credentials = try ServiceCredentials.generate(for: identifier)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        try backend.write(encoder.encode(credentials), account: account)
        return credentials
    }

    func delete(for identifier: UUID) throws {
        lock.lock()
        defer { lock.unlock() }
        try backend.delete(account: Self.account(for: identifier))
    }

    private static func account(for identifier: UUID) -> String {
        identifier.uuidString.lowercased()
    }
}

private final class KeychainServiceCredentialBackend: ServiceCredentialBacking {
    private let service = "app.herdme.desktop.service-credentials.v1"

    func read(account: String, allowInteraction: Bool) throws -> Data? {
        var query = baseQuery(account: account)
        query[kSecReturnData] = true
        query[kSecMatchLimit] = kSecMatchLimitOne
        KeychainQueryInteraction.apply(allowInteraction: allowInteraction, to: &query)
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data else {
            throw ServiceCredentialError.keychainReadFailed(status)
        }
        return data
    }

    func write(_ data: Data, account: String) throws {
        var attributes = baseQuery(account: account)
        attributes[kSecValueData] = data
        attributes[kSecAttrAccessible] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        let status = SecItemAdd(attributes as CFDictionary, nil)
        if status == errSecDuplicateItem {
            let updateStatus = SecItemUpdate(
                baseQuery(account: account) as CFDictionary,
                [kSecValueData: data] as CFDictionary
            )
            guard updateStatus == errSecSuccess else {
                throw ServiceCredentialError.keychainWriteFailed(updateStatus)
            }
            return
        }
        guard status == errSecSuccess else {
            throw ServiceCredentialError.keychainWriteFailed(status)
        }
    }

    func delete(account: String) throws {
        let status = SecItemDelete(baseQuery(account: account) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw ServiceCredentialError.keychainDeleteFailed(status)
        }
    }

    private func baseQuery(account: String) -> [CFString: Any] {
        [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: account
        ]
    }
}
