import Foundation
import Security

protocol CertificateTrustBacking: Sendable {
    func state(for certificateData: Data) -> CertificateTrustState
    func install(_ certificateData: Data) throws
}

struct UserCertificateTrustBackend: CertificateTrustBacking {
    func state(for certificateData: Data) -> CertificateTrustState {
        guard let certificate = Self.certificate(from: certificateData) else {
            return .untrusted
        }
        let policy = SecPolicyCreateBasicX509()
        var trust: SecTrust?
        guard SecTrustCreateWithCertificates(certificate, policy, &trust) == errSecSuccess,
              let trust else {
            return .untrusted
        }
        return SecTrustEvaluateWithError(trust, nil) ? .trusted : .untrusted
    }

    func install(_ certificateData: Data) throws {
        guard let certificate = Self.certificate(from: certificateData) else {
            throw LocalCertificateError.authorizationFailed("HerdMe could not read its local certificate authority.")
        }

        let query: [CFString: Any] = [
            kSecClass: kSecClassCertificate,
            kSecValueRef: certificate,
            kSecReturnPersistentRef: true
        ]
        var persistentReference: CFTypeRef?
        let addStatus = SecItemAdd(query as CFDictionary, &persistentReference)
        guard addStatus == errSecSuccess || addStatus == errSecDuplicateItem else {
            throw LocalCertificateError.authorizationFailed(securityMessage(for: addStatus))
        }

        let trustStatus = SecTrustSettingsSetTrustSettings(
            certificate,
            .user,
            nil
        )
        guard trustStatus == errSecSuccess else {
            if addStatus == errSecSuccess, let persistentReference {
                _ = SecItemDelete([
                    kSecClass: kSecClassCertificate,
                    kSecValuePersistentRef: persistentReference
                ] as CFDictionary)
            }
            throw LocalCertificateError.authorizationFailed(securityMessage(for: trustStatus))
        }
    }

    static func certificate(from data: Data) -> SecCertificate? {
        if let certificate = SecCertificateCreateWithData(nil, data as CFData) {
            return certificate
        }
        guard let pem = String(data: data, encoding: .utf8),
              let begin = pem.range(of: "-----BEGIN CERTIFICATE-----"),
              let end = pem.range(
                  of: "-----END CERTIFICATE-----",
                  range: begin.upperBound..<pem.endIndex
              ),
              let decoded = Data(
                  base64Encoded: String(pem[begin.upperBound..<end.lowerBound]),
                  options: .ignoreUnknownCharacters
              ) else {
            return nil
        }
        return SecCertificateCreateWithData(nil, decoded as CFData)
    }

    private func securityMessage(for status: OSStatus) -> String {
        (SecCopyErrorMessageString(status, nil) as String?)
            ?? "Security framework failed with status \(status)."
    }
}

enum CertificateTrustState: Equatable, Sendable {
    case missing
    case untrusted
    case trusted

    var title: String {
        switch self {
        case .missing: "Not created"
        case .untrusted: "Not trusted"
        case .trusted: "Trusted"
        }
    }
}

enum LocalCertificateError: LocalizedError {
    case invalidTLD
    case toolFailed(String)
    case identityImportFailed(OSStatus)
    case identityMissing
    case authorizationFailed(String)

    var errorDescription: String? {
        switch self {
        case .invalidTLD:
            "The top-level domain may only contain letters, numbers, and hyphens."
        case let .toolFailed(message):
            message.isEmpty ? "HerdMe could not create its local certificate." : message
        case let .identityImportFailed(status):
            "HerdMe could not load its HTTPS identity (Security status \(status))."
        case .identityMissing:
            "HerdMe created a certificate bundle, but it did not contain a usable identity."
        case let .authorizationFailed(message):
            message.isEmpty ? "The HerdMe local certificate authority was not trusted." : message
        }
    }
}

final class LocalCertificateManager: @unchecked Sendable {
    private let fileManager: FileManager
    private let certificatesURL: URL
    private let secretStore: CertificateSecretStore
    private let trustBackend: any CertificateTrustBacking
    private let identityLock = NSLock()
    private var cachedIdentity: (marker: String, identity: SecIdentity)?
    private let opensslURL = URL(fileURLWithPath: "/usr/bin/openssl")

    private var legacyAuthorityKeyURL: URL { certificatesURL.appendingPathComponent("herdme-ca.key") }
    private var authorityCertificateURL: URL { certificatesURL.appendingPathComponent("herdme-ca.pem") }
    private var legacyLeafKeyURL: URL { certificatesURL.appendingPathComponent("local-sites.key") }
    private var legacyLeafRequestURL: URL { certificatesURL.appendingPathComponent("local-sites.csr") }
    private var leafCertificateURL: URL { certificatesURL.appendingPathComponent("local-sites.pem") }
    private var identityURL: URL { certificatesURL.appendingPathComponent("local-sites.p12") }
    private var tldMarkerURL: URL { certificatesURL.appendingPathComponent("local-sites.tld") }

    init(
        rootURL: URL,
        fileManager: FileManager = .default,
        secretStore: CertificateSecretStore? = nil,
        trustBackend: any CertificateTrustBacking = UserCertificateTrustBackend()
    ) {
        self.fileManager = fileManager
        certificatesURL = rootURL.appendingPathComponent("Certificates", isDirectory: true)
        self.secretStore = secretStore ?? CertificateSecretStore(rootURL: rootURL)
        self.trustBackend = trustBackend
    }

    func trustState() -> CertificateTrustState {
        guard fileManager.fileExists(atPath: authorityCertificateURL.path) else { return .missing }
        guard let data = try? Data(contentsOf: authorityCertificateURL) else { return .untrusted }
        return trustBackend.state(for: data)
    }

    func prepareIdentity(
        tld: String,
        domains: [String] = [],
        allowKeychainInteraction: Bool = true
    ) throws -> SecIdentity {
        guard DomainResolverManager.isValid(tld: tld) else { throw LocalCertificateError.invalidTLD }
        let normalizedTLD = tld.lowercased()
        let normalizedDomains = Self.normalizedDomains(domains, tld: normalizedTLD)
        let marker = Self.leafMarker(tld: normalizedTLD, domains: normalizedDomains)
        identityLock.lock()
        defer { identityLock.unlock() }
        if let cachedIdentity,
           cachedIdentity.marker == marker,
           (try? String(contentsOf: tldMarkerURL, encoding: .utf8)) == marker,
           fileManager.fileExists(atPath: leafCertificateURL.path),
           fileManager.fileExists(atPath: identityURL.path) {
            return cachedIdentity.identity
        }
        try fileManager.createDirectory(at: certificatesURL, withIntermediateDirectories: true)
        try fileManager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: certificatesURL.path)
        try ensureAuthority(allowKeychainInteraction: allowKeychainInteraction)
        let passphrase = try secretStore.identityPassphrase(
            allowInteraction: allowKeychainInteraction
        )
        try ensureLeaf(
            tld: normalizedTLD,
            domains: normalizedDomains,
            passphrase: passphrase,
            allowKeychainInteraction: allowKeychainInteraction
        )

        let data = try Data(contentsOf: identityURL)
        var options: [CFString: Any] = [
            kSecImportExportPassphrase: passphrase
        ]
        if #available(macOS 15.0, *) {
            options[kSecImportToMemoryOnly] = true
        }
        var importedItems: CFArray?
        let status = SecPKCS12Import(
            data as CFData,
            options as CFDictionary,
            &importedItems
        )
        guard status == errSecSuccess else { throw LocalCertificateError.identityImportFailed(status) }
        guard let items = importedItems as? [[CFString: Any]],
              let identity = items.compactMap(Self.identity(from:)).first else {
            throw LocalCertificateError.identityMissing
        }
        cachedIdentity = (marker, identity)
        return identity
    }

    private static func identity(from importedItem: [CFString: Any]) -> SecIdentity? {
        guard let value = importedItem[kSecImportItemIdentity] else { return nil }
        let reference = value as CFTypeRef
        guard CFGetTypeID(reference) == SecIdentityGetTypeID() else { return nil }
        return unsafeDowncast(reference, to: SecIdentity.self)
    }

    @discardableResult
    func installAuthority(tld: String) throws -> Bool {
        _ = try prepareIdentity(tld: tld)
        if trustState() == .trusted { return false }
        try trustBackend.install(Data(contentsOf: authorityCertificateURL))
        guard trustState() == .trusted else {
            throw LocalCertificateError.authorizationFailed(
                "Keychain accepted the certificate, but macOS did not report it as trusted."
            )
        }
        return true
    }

    private func ensureAuthority(allowKeychainInteraction: Bool) throws {
        let certificateExists = fileManager.fileExists(atPath: authorityCertificateURL.path)
        let certificateIsCurrent = certificateExists
            && run(opensslURL, arguments: [
                "x509", "-checkend", "2592000", "-noout", "-in", authorityCertificateURL.path
            ]).status == 0
        if certificateIsCurrent {
            if let storedKey = try secretStore.data(
                for: .authorityPrivateKey,
                allowInteraction: allowKeychainInteraction
            ),
               authorityKeyMatchesCertificate(storedKey, certificateURL: authorityCertificateURL) {
                try removeLegacyAuthorityKey()
                return
            }
            if fileManager.fileExists(atPath: legacyAuthorityKeyURL.path) {
                let legacyKey = try Data(contentsOf: legacyAuthorityKeyURL)
                if authorityKeyMatchesCertificate(legacyKey, certificateURL: authorityCertificateURL) {
                    try secretStore.store(legacyKey, for: .authorityPrivateKey)
                    try removeLegacyAuthorityKey()
                    return
                }
            }
        }

        let staging = try makeStagingDirectory()
        defer { try? fileManager.removeItem(at: staging) }
        let configurationURL = staging.appendingPathComponent("authority.cnf")
        let keyURL = staging.appendingPathComponent("herdme-ca.key")
        let certificateURL = staging.appendingPathComponent("herdme-ca.pem")
        let configuration = """
        [req]
        distinguished_name = distinguished_name
        x509_extensions = authority_extensions
        prompt = no

        [distinguished_name]
        CN = HerdMe Local CA
        O = HerdMe

        [authority_extensions]
        basicConstraints = critical,CA:TRUE
        keyUsage = critical,keyCertSign,cRLSign
        subjectKeyIdentifier = hash
        authorityKeyIdentifier = keyid:always
        """
        try configuration.write(to: configurationURL, atomically: true, encoding: .utf8)
        let result = run(opensslURL, arguments: [
            "req", "-x509", "-newkey", "rsa:3072", "-nodes", "-days", "3650",
            "-set_serial", "0x\(try randomHex(byteCount: 16))", "-keyout", keyURL.path,
            "-out", certificateURL.path, "-config", configurationURL.path
        ])
        guard result.status == 0 else { throw LocalCertificateError.toolFailed(result.output) }
        try setPrivatePermissions(on: keyURL)
        let keyData = try Data(contentsOf: keyURL)
        guard authorityKeyMatchesCertificate(keyData, certificateURL: certificateURL) else {
            throw LocalCertificateError.toolFailed("HerdMe generated a certificate authority with a mismatched private key.")
        }
        try secretStore.store(keyData, for: .authorityPrivateKey)
        try writeAtomically(contentsOf: certificateURL, to: authorityCertificateURL)
        try removeLegacyAuthorityKey()
    }

    private func ensureLeaf(
        tld: String,
        domains: [String],
        passphrase: String,
        allowKeychainInteraction: Bool
    ) throws {
        let normalizedDomains = Self.normalizedDomains(domains, tld: tld)
        let marker = Self.leafMarker(tld: tld, domains: normalizedDomains)
        let storedMarker = try? String(contentsOf: tldMarkerURL, encoding: .utf8)
        let requiredFiles = [leafCertificateURL, identityURL]
        let filesExist = requiredFiles.allSatisfy { fileManager.fileExists(atPath: $0.path) }
        if storedMarker == marker, filesExist,
           run(opensslURL, arguments: [
               "x509", "-checkend", "2592000", "-noout", "-in", leafCertificateURL.path
           ]).status == 0,
           run(opensslURL, arguments: [
               "verify", "-CAfile", authorityCertificateURL.path, leafCertificateURL.path
           ]).status == 0,
           identityBundleIsValid(passphrase: passphrase) {
            try removeLegacyLeafPrivateFiles()
            return
        }

        guard let authorityKey = try secretStore.data(
            for: .authorityPrivateKey,
            allowInteraction: allowKeychainInteraction
        ),
              authorityKeyMatchesCertificate(authorityKey, certificateURL: authorityCertificateURL) else {
            throw CertificateSecretError.invalidStoredValue
        }
        let staging = try makeStagingDirectory()
        defer { try? fileManager.removeItem(at: staging) }
        let configurationURL = staging.appendingPathComponent("local-sites.cnf")
        let keyURL = staging.appendingPathComponent("local-sites.key")
        let requestURL = staging.appendingPathComponent("local-sites.csr")
        let certificateURL = staging.appendingPathComponent("local-sites.pem")
        let identityStagingURL = staging.appendingPathComponent("local-sites.p12")
        let alternateNames = (["*.\(tld)", tld] + normalizedDomains).enumerated()
            .map { "DNS.\($0.offset + 1) = \($0.element)" }
            .joined(separator: "\n")
        let configuration = """
        [req]
        distinguished_name = distinguished_name
        req_extensions = leaf_extensions
        prompt = no

        [distinguished_name]
        CN = *.\(tld)
        O = HerdMe

        [leaf_extensions]
        basicConstraints = critical,CA:FALSE
        keyUsage = critical,digitalSignature,keyEncipherment
        extendedKeyUsage = serverAuth
        subjectAltName = @alternate_names

        [alternate_names]
        \(alternateNames)
        """
        try configuration.write(to: configurationURL, atomically: true, encoding: .utf8)

        var result = run(opensslURL, arguments: [
            "req", "-new", "-newkey", "rsa:2048", "-nodes", "-keyout", keyURL.path,
            "-out", requestURL.path, "-config", configurationURL.path
        ])
        guard result.status == 0 else { throw LocalCertificateError.toolFailed(result.output) }
        try setPrivatePermissions(on: keyURL)

        result = run(opensslURL, arguments: [
            "x509", "-req", "-in", requestURL.path, "-CA", authorityCertificateURL.path,
            "-CAkey", "/dev/stdin", "-set_serial", "0x\(try randomHex(byteCount: 16))",
            "-out", certificateURL.path, "-days", "825", "-extfile", configurationURL.path,
            "-extensions", "leaf_extensions"
        ], standardInput: authorityKey)
        guard result.status == 0 else { throw LocalCertificateError.toolFailed(result.output) }

        result = run(opensslURL, arguments: [
            "pkcs12", "-export", "-out", identityStagingURL.path, "-inkey", keyURL.path,
            "-in", certificateURL.path, "-certfile", authorityCertificateURL.path,
            "-name", "HerdMe Local Sites", "-passout", "stdin"
        ], standardInput: secretStandardInput(passphrase))
        guard result.status == 0 else { throw LocalCertificateError.toolFailed(result.output) }
        try writeAtomically(contentsOf: certificateURL, to: leafCertificateURL)
        try writeAtomically(contentsOf: identityStagingURL, to: identityURL)
        try marker.write(to: tldMarkerURL, atomically: true, encoding: .utf8)
        try setPrivatePermissions(on: identityURL)
        try removeLegacyLeafPrivateFiles()
    }

    private static func normalizedDomains(_ domains: [String], tld: String) -> [String] {
        let normalizedTLD = tld.lowercased()
        let suffix = "." + normalizedTLD
        return Array(Set(domains.map { $0.lowercased() }
            .filter { $0.hasSuffix(suffix) && $0.count > suffix.count && isValidDNSName($0) })).sorted()
    }

    private static func leafMarker(tld: String, domains: [String]) -> String {
        ([tld.lowercased()] + domains).joined(separator: "\n") + "\n"
    }

    private func identityBundleIsValid(passphrase: String) -> Bool {
        run(
            opensslURL,
            arguments: [
                "pkcs12", "-in", identityURL.path, "-noout",
                "-passin", "stdin"
            ],
            standardInput: secretStandardInput(passphrase)
        ).status == 0
    }

    private func authorityKeyMatchesCertificate(_ key: Data, certificateURL: URL) -> Bool {
        let keyResult = run(
            opensslURL,
            arguments: ["pkey", "-pubout", "-in", "/dev/stdin"],
            standardInput: key
        )
        guard keyResult.status == 0 else { return false }
        let certificateResult = run(
            opensslURL,
            arguments: ["x509", "-pubkey", "-noout", "-in", certificateURL.path]
        )
        return certificateResult.status == 0 && keyResult.output == certificateResult.output
    }

    private func makeStagingDirectory() throws -> URL {
        let url = certificatesURL.appendingPathComponent(".staging-\(UUID().uuidString)", isDirectory: true)
        try fileManager.createDirectory(at: url, withIntermediateDirectories: false)
        try fileManager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: url.path)
        return url
    }

    private func writeAtomically(contentsOf source: URL, to destination: URL) throws {
        try Data(contentsOf: source).write(to: destination, options: .atomic)
    }

    private func removeLegacyAuthorityKey() throws {
        if fileManager.fileExists(atPath: legacyAuthorityKeyURL.path) {
            try fileManager.removeItem(at: legacyAuthorityKeyURL)
        }
        try? fileManager.removeItem(at: certificatesURL.appendingPathComponent("authority.cnf"))
    }

    private func removeLegacyLeafPrivateFiles() throws {
        for url in [legacyLeafKeyURL, legacyLeafRequestURL] where fileManager.fileExists(atPath: url.path) {
            try fileManager.removeItem(at: url)
        }
        try? fileManager.removeItem(at: certificatesURL.appendingPathComponent("local-sites.cnf"))
        try? fileManager.removeItem(at: certificatesURL.appendingPathComponent("herdme-ca.srl"))
    }

    private func randomHex(byteCount: Int) throws -> String {
        var bytes = [UInt8](repeating: 0, count: byteCount)
        let status = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        guard status == errSecSuccess else {
            throw CertificateSecretError.randomGenerationFailed(status)
        }
        return bytes.map { String(format: "%02x", $0) }.joined()
    }

    private func secretStandardInput(_ value: String) -> Data {
        Data((value + "\n").utf8)
    }

    private func setPrivatePermissions(on url: URL) throws {
        try fileManager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }

    private func run(
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
                standardInput: standardInput,
                timeout: 30
            )
            return (result.status, result.output
                .trimmingCharacters(in: .whitespacesAndNewlines))
        } catch {
            return (-1, error.localizedDescription)
        }
    }

    private static func isValidDNSName(_ value: String) -> Bool {
        guard !value.isEmpty, value.utf8.count <= 253 else { return false }
        return value.split(separator: ".", omittingEmptySubsequences: false).allSatisfy { label in
            guard !label.isEmpty, label.utf8.count <= 63,
                  let first = label.utf8.first, let last = label.utf8.last,
                  isASCIIAlphanumeric(first), isASCIIAlphanumeric(last) else { return false }
            return label.utf8.allSatisfy { isASCIIAlphanumeric($0) || $0 == 45 }
        }
    }

    private static func isASCIIAlphanumeric(_ value: UInt8) -> Bool {
        (value >= 97 && value <= 122) || (value >= 48 && value <= 57)
    }
}
