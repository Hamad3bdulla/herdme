import Foundation
import Security

enum CertificateTrustState: Equatable {
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

final class LocalCertificateManager {
    private static let identityPassphrase = "HerdMe-Local-Identity"
    private let fileManager: FileManager
    private let certificatesURL: URL
    private let opensslURL = URL(fileURLWithPath: "/usr/bin/openssl")
    private let securityURL = URL(fileURLWithPath: "/usr/bin/security")

    private var authorityKeyURL: URL { certificatesURL.appendingPathComponent("herdme-ca.key") }
    private var authorityCertificateURL: URL { certificatesURL.appendingPathComponent("herdme-ca.pem") }
    private var leafKeyURL: URL { certificatesURL.appendingPathComponent("local-sites.key") }
    private var leafRequestURL: URL { certificatesURL.appendingPathComponent("local-sites.csr") }
    private var leafCertificateURL: URL { certificatesURL.appendingPathComponent("local-sites.pem") }
    private var identityURL: URL { certificatesURL.appendingPathComponent("local-sites.p12") }
    private var tldMarkerURL: URL { certificatesURL.appendingPathComponent("local-sites.tld") }

    init(rootURL: URL, fileManager: FileManager = .default) {
        self.fileManager = fileManager
        certificatesURL = rootURL.appendingPathComponent("Certificates", isDirectory: true)
    }

    func trustState() -> CertificateTrustState {
        guard fileManager.fileExists(atPath: authorityCertificateURL.path) else { return .missing }
        let result = run(
            securityURL,
            arguments: ["verify-cert", "-c", authorityCertificateURL.path, "-p", "basic", "-l", "-L", "-q"]
        )
        return result.status == 0 ? .trusted : .untrusted
    }

    func prepareIdentity(tld: String, domains: [String] = []) throws -> SecIdentity {
        guard DomainResolverManager.isValid(tld: tld) else { throw LocalCertificateError.invalidTLD }
        try fileManager.createDirectory(at: certificatesURL, withIntermediateDirectories: true)
        try ensureAuthority()
        try ensureLeaf(tld: tld, domains: domains)

        let data = try Data(contentsOf: identityURL)
        var options: [CFString: Any] = [
            kSecImportExportPassphrase: Self.identityPassphrase
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
              let identity = items.compactMap({ $0[kSecImportItemIdentity] as! SecIdentity? }).first else {
            throw LocalCertificateError.identityMissing
        }
        return identity
    }

    @discardableResult
    func installAuthority(tld: String) throws -> Bool {
        _ = try prepareIdentity(tld: tld)
        if trustState() == .trusted { return false }

        try executeWithPrivileges(
            securityURL,
            arguments: [
                "add-trusted-cert", "-d", "-r", "trustRoot",
                "-k", "/Library/Keychains/System.keychain", authorityCertificateURL.path
            ]
        )
        return true
    }

    private func ensureAuthority() throws {
        let keyExists = fileManager.fileExists(atPath: authorityKeyURL.path)
        let certificateExists = fileManager.fileExists(atPath: authorityCertificateURL.path)
        if keyExists, certificateExists,
           run(opensslURL, arguments: ["x509", "-checkend", "2592000", "-noout", "-in", authorityCertificateURL.path]).status == 0 {
            return
        }

        let configurationURL = certificatesURL.appendingPathComponent("authority.cnf")
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
            "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "3650", "-set_serial", "1",
            "-keyout", authorityKeyURL.path, "-out", authorityCertificateURL.path, "-config", configurationURL.path
        ])
        guard result.status == 0 else { throw LocalCertificateError.toolFailed(result.output) }
        try setPrivatePermissions(on: authorityKeyURL)
    }

    private func ensureLeaf(tld: String, domains: [String]) throws {
        let suffix = "." + tld.lowercased()
        let normalizedDomains = Array(Set(domains.map { $0.lowercased() }
            .filter { $0.hasSuffix(suffix) && $0.count > suffix.count && Self.isValidDNSName($0) })).sorted()
        let marker = ([tld.lowercased()] + normalizedDomains).joined(separator: "\n") + "\n"
        let storedMarker = try? String(contentsOf: tldMarkerURL, encoding: .utf8)
        let requiredFiles = [leafKeyURL, leafCertificateURL, identityURL]
        let filesExist = requiredFiles.allSatisfy { fileManager.fileExists(atPath: $0.path) }
        if storedMarker == marker, filesExist,
           run(opensslURL, arguments: ["x509", "-checkend", "2592000", "-noout", "-in", leafCertificateURL.path]).status == 0 {
            return
        }

        let configurationURL = certificatesURL.appendingPathComponent("local-sites.cnf")
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
            "req", "-new", "-newkey", "rsa:2048", "-nodes", "-keyout", leafKeyURL.path,
            "-out", leafRequestURL.path, "-config", configurationURL.path
        ])
        guard result.status == 0 else { throw LocalCertificateError.toolFailed(result.output) }

        result = run(opensslURL, arguments: [
            "x509", "-req", "-in", leafRequestURL.path, "-CA", authorityCertificateURL.path,
            "-CAkey", authorityKeyURL.path, "-CAcreateserial", "-out", leafCertificateURL.path,
            "-days", "825", "-extfile", configurationURL.path, "-extensions", "leaf_extensions"
        ])
        guard result.status == 0 else { throw LocalCertificateError.toolFailed(result.output) }

        result = run(opensslURL, arguments: [
            "pkcs12", "-export", "-out", identityURL.path, "-inkey", leafKeyURL.path,
            "-in", leafCertificateURL.path, "-certfile", authorityCertificateURL.path,
            "-name", "HerdMe Local Sites", "-passout", "pass:\(Self.identityPassphrase)"
        ])
        guard result.status == 0 else { throw LocalCertificateError.toolFailed(result.output) }
        try marker.write(to: tldMarkerURL, atomically: true, encoding: .utf8)
        try setPrivatePermissions(on: leafKeyURL)
        try setPrivatePermissions(on: identityURL)
    }

    private func setPrivatePermissions(on url: URL) throws {
        try fileManager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }

    private func run(_ executable: URL, arguments: [String]) -> (status: Int32, output: String) {
        do {
            let result = try ProcessRunner.run(executable, arguments: arguments, timeout: 30)
            return (result.status, result.output
                .trimmingCharacters(in: .whitespacesAndNewlines))
        } catch {
            return (-1, error.localizedDescription)
        }
    }

    private func executeWithPrivileges(_ executable: URL, arguments: [String]) throws {
        do {
            try PrivilegedCommandRunner.execute(executable, arguments: arguments)
        } catch {
            throw LocalCertificateError.authorizationFailed(error.localizedDescription)
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
