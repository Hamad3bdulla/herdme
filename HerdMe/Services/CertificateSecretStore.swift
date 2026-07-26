import Foundation
import LocalAuthentication
import Security

enum CertificateSecret: String, Sendable {
    case authorityPrivateKey = "authority-private-key"
    case identityPassphrase = "identity-passphrase"
}

enum CertificateSecretError: LocalizedError {
    case randomGenerationFailed(OSStatus)
    case keychainReadFailed(OSStatus)
    case keychainWriteFailed(OSStatus)
    case interactionRequired
    case invalidStoredValue

    var errorDescription: String? {
        switch self {
        case .randomGenerationFailed:
            "HerdMe could not generate secure certificate credentials."
        case .keychainReadFailed:
            "HerdMe could not read its certificate credentials from Keychain."
        case .keychainWriteFailed:
            "HerdMe could not save its certificate credentials in Keychain."
        case .interactionRequired:
            "HerdMe needs explicit Keychain approval before HTTPS can start."
        case .invalidStoredValue:
            "HerdMe's saved certificate credentials are invalid."
        }
    }
}

protocol CertificateSecretBacking: AnyObject {
    func read(account: String, allowInteraction: Bool) throws -> Data?
    func write(_ data: Data, account: String) throws
}

final class CertificateSecretStore: @unchecked Sendable {
    private let backend: CertificateSecretBacking
    private let accountPrefix: String
    private let lock = NSLock()

    init(
        rootURL: URL,
        backend: CertificateSecretBacking = KeychainCertificateSecretBackend()
    ) {
        self.backend = backend
        accountPrefix = rootURL.standardizedFileURL.path + "|"
    }

    func data(for secret: CertificateSecret, allowInteraction: Bool = true) throws -> Data? {
        lock.lock()
        defer { lock.unlock() }
        return try backend.read(
            account: accountPrefix + secret.rawValue,
            allowInteraction: allowInteraction
        )
    }

    func store(_ data: Data, for secret: CertificateSecret) throws {
        guard !data.isEmpty else { throw CertificateSecretError.invalidStoredValue }
        lock.lock()
        defer { lock.unlock() }
        try backend.write(data, account: accountPrefix + secret.rawValue)
    }

    func identityPassphrase(allowInteraction: Bool = true) throws -> String {
        if let stored = try data(
            for: .identityPassphrase,
            allowInteraction: allowInteraction
        ) {
            guard let value = String(data: stored, encoding: .utf8), Self.isValidPassphrase(value) else {
                throw CertificateSecretError.invalidStoredValue
            }
            return value
        }

        var bytes = [UInt8](repeating: 0, count: 32)
        let status = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        guard status == errSecSuccess else {
            throw CertificateSecretError.randomGenerationFailed(status)
        }
        let passphrase = Data(bytes).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
        try store(Data(passphrase.utf8), for: .identityPassphrase)
        return passphrase
    }

    private static func isValidPassphrase(_ value: String) -> Bool {
        value.count >= 32 && value.unicodeScalars.allSatisfy { scalar in
            switch scalar.value {
            case 45, 48...57, 65...90, 95, 97...122:
                true
            default:
                false
            }
        }
    }
}

protocol CertificateKeychainAccess: AnyObject {
    func read(
        service: String,
        account: String,
        dataProtection: Bool,
        allowInteraction: Bool
    ) -> (OSStatus, Data?)
    func write(_ data: Data, service: String, account: String, dataProtection: Bool) -> OSStatus
    func delete(service: String, account: String, dataProtection: Bool) -> OSStatus
}

enum KeychainQueryInteraction {
    // LAContext alone does not suppress legacy macOS Keychain ACL prompts.
    // This is the stable CFString value of kSecUseAuthenticationUIFail, whose
    // Swift symbol is deprecated even though the SDK still requires both keys.
    static var failAuthenticationUI: CFString { "u_AuthUIF" as CFString }

    static func apply(allowInteraction: Bool, to query: inout [CFString: Any]) {
        guard !allowInteraction else { return }
        let context = LAContext()
        context.interactionNotAllowed = true
        query[kSecUseAuthenticationContext] = context
        query[kSecUseAuthenticationUI] = failAuthenticationUI
    }
}

final class KeychainCertificateSecretBackend: CertificateSecretBacking {
    private let service = "app.herdme.desktop.certificate-secrets.v1"
    private let keychain: CertificateKeychainAccess

    init(keychain: CertificateKeychainAccess = SystemCertificateKeychainAccess()) {
        self.keychain = keychain
    }

    func read(account: String, allowInteraction: Bool) throws -> Data? {
        let protected = keychain.read(
            service: service,
            account: account,
            dataProtection: true,
            allowInteraction: allowInteraction
        )
        switch protected.0 {
        case errSecSuccess:
            guard let data = protected.1 else {
                throw CertificateSecretError.keychainReadFailed(errSecDecode)
            }
            if allowInteraction {
                removeLegacyCopy(account: account)
            }
            return data
        case errSecItemNotFound:
            let legacy = try readLegacy(
                account: account,
                migrate: allowInteraction,
                allowInteraction: allowInteraction
            )
            if legacy == nil, !allowInteraction {
                throw CertificateSecretError.interactionRequired
            }
            return legacy
        case errSecMissingEntitlement:
            let legacy = try readLegacy(
                account: account,
                migrate: false,
                allowInteraction: allowInteraction
            )
            if legacy == nil, !allowInteraction {
                throw CertificateSecretError.interactionRequired
            }
            return legacy
        default:
            throw CertificateSecretError.keychainReadFailed(protected.0)
        }
    }

    func write(_ data: Data, account: String) throws {
        let protectedStatus = keychain.write(
            data,
            service: service,
            account: account,
            dataProtection: true
        )
        switch protectedStatus {
        case errSecSuccess:
            removeLegacyCopy(account: account)
        case errSecMissingEntitlement:
            let legacyStatus = keychain.write(
                data,
                service: service,
                account: account,
                dataProtection: false
            )
            guard legacyStatus == errSecSuccess else {
                throw CertificateSecretError.keychainWriteFailed(legacyStatus)
            }
        default:
            throw CertificateSecretError.keychainWriteFailed(protectedStatus)
        }
    }

    private func readLegacy(
        account: String,
        migrate: Bool,
        allowInteraction: Bool
    ) throws -> Data? {
        let legacy = keychain.read(
            service: service,
            account: account,
            dataProtection: false,
            allowInteraction: allowInteraction
        )
        if legacy.0 == errSecItemNotFound { return nil }
        if !allowInteraction,
           (legacy.0 == errSecInteractionNotAllowed || legacy.0 == errSecAuthFailed) {
            throw CertificateSecretError.interactionRequired
        }
        guard legacy.0 == errSecSuccess, let data = legacy.1 else {
            throw CertificateSecretError.keychainReadFailed(legacy.0)
        }
        guard migrate else { return data }

        let migrationStatus = keychain.write(
            data,
            service: service,
            account: account,
            dataProtection: true
        )
        if migrationStatus == errSecMissingEntitlement {
            return data
        }
        guard migrationStatus == errSecSuccess else {
            throw CertificateSecretError.keychainWriteFailed(migrationStatus)
        }
        removeLegacyCopy(account: account)
        return data
    }

    private func removeLegacyCopy(account: String) {
        _ = keychain.delete(service: service, account: account, dataProtection: false)
    }
}

private final class SystemCertificateKeychainAccess: CertificateKeychainAccess {
    func read(
        service: String,
        account: String,
        dataProtection: Bool,
        allowInteraction: Bool
    ) -> (OSStatus, Data?) {
        var query = baseQuery(service: service, account: account, dataProtection: dataProtection)
        query[kSecReturnData] = true
        query[kSecMatchLimit] = kSecMatchLimitOne
        KeychainQueryInteraction.apply(allowInteraction: allowInteraction, to: &query)
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        return (status, result as? Data)
    }

    func write(_ data: Data, service: String, account: String, dataProtection: Bool) -> OSStatus {
        var attributes = baseQuery(service: service, account: account, dataProtection: dataProtection)
        attributes[kSecValueData] = data
        attributes[kSecAttrAccessible] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        let status = SecItemAdd(attributes as CFDictionary, nil)
        guard status == errSecDuplicateItem else { return status }
        return SecItemUpdate(
            baseQuery(service: service, account: account, dataProtection: dataProtection) as CFDictionary,
            [kSecValueData: data] as CFDictionary
        )
    }

    func delete(service: String, account: String, dataProtection: Bool) -> OSStatus {
        SecItemDelete(
            baseQuery(service: service, account: account, dataProtection: dataProtection) as CFDictionary
        )
    }

    private func baseQuery(
        service: String,
        account: String,
        dataProtection: Bool
    ) -> [CFString: Any] {
        var query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: account
        ]
        if dataProtection {
            query[kSecUseDataProtectionKeychain] = true
        }
        return query
    }
}
