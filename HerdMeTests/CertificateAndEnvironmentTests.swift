import Combine
import CryptoKit
import Darwin
import Security
import XCTest

@testable import HerdMe

extension ConfigurationAndSiteScannerTests {
    func testListenerErrorsIdentifyTheService() {
        let error = LocalListenerError.invalidPort(service: "dump listener")

        XCTAssertEqual(error.errorDescription, "The configured dump listener port is invalid.")
    }

    func testCertificateKeychainFallsBackWhenDataProtectionEntitlementIsUnavailable() throws {
        let keychain = TestCertificateKeychainAccess()
        keychain.readStatuses[true] = errSecMissingEntitlement
        keychain.writeStatuses[true] = errSecMissingEntitlement
        let store = CertificateSecretStore(
            rootURL: URL(fileURLWithPath: "/tmp/herdme-keychain-fallback"),
            backend: KeychainCertificateSecretBackend(keychain: keychain)
        )
        let secret = Data("fallback-secret".utf8)

        XCTAssertNil(try store.data(for: .authorityPrivateKey))
        try store.store(secret, for: .authorityPrivateKey)

        XCTAssertEqual(keychain.values[false], secret)
        XCTAssertEqual(
            keychain.operations,
            [
                .read(dataProtection: true),
                .read(dataProtection: false),
                .write(dataProtection: true),
                .write(dataProtection: false)
            ]
        )
    }

    @MainActor
    func testRuntimeCoordinatorOwnsRefreshState() async {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let installer = TestRuntimeInstaller()
        let coordinator = RuntimeCoordinator(rootURL: root, installer: installer)

        await coordinator.refreshTooling(
            cycle: "8.4",
            php: root.appendingPathComponent("missing-php")
        )
        await coordinator.refreshAvailableUpdates(
            phpCycles: ["8.3", "8.4"],
            nodeCycles: ["20", "22"]
        )

        XCTAssertNil(coordinator.xdebugInstallation)
        XCTAssertEqual(coordinator.composerVersion, "2.9.0")
        XCTAssertEqual(coordinator.latestComposerVersion, "2.9.1")
        XCTAssertEqual(coordinator.laravelInstallerVersion, "5.20.0")
        XCTAssertEqual(coordinator.latestLaravelInstallerVersion, "5.21.0")
        XCTAssertEqual(
            coordinator.latestPHPVersions,
            ["8.3": "8.3.test", "8.4": "8.4.test"]
        )
        XCTAssertEqual(
            coordinator.latestNodeVersions,
            ["20": "20.test", "22": "22.test"]
        )

        let calls = Set(await installer.calls)
        XCTAssertEqual(
            calls,
            [
                "composer-version:8.4",
                "latest-composer:8.4",
                "laravel-version:8.4",
                "latest-laravel",
                "latest-php:8.3,8.4",
                "latest-node:20,22"
            ]
        )
    }

    func testAutomaticCertificateReadUsesApprovedLegacyKeychainWithoutMigrating() throws {
        let keychain = TestCertificateKeychainAccess()
        keychain.readStatuses[true] = errSecMissingEntitlement
        let legacy = Data("legacy-secret".utf8)
        keychain.values[false] = legacy
        let store = CertificateSecretStore(
            rootURL: URL(fileURLWithPath: "/tmp/herdme-keychain-no-ui"),
            backend: KeychainCertificateSecretBackend(keychain: keychain)
        )

        XCTAssertEqual(
            try store.data(for: .authorityPrivateKey, allowInteraction: false),
            legacy
        )
        XCTAssertEqual(
            keychain.operations,
            [.read(dataProtection: true), .read(dataProtection: false)]
        )
        XCTAssertEqual(keychain.readAllowsInteraction, [false, false])
    }

    func testAutomaticCertificateReadReportsLegacyInteractionRequirement() {
        let keychain = TestCertificateKeychainAccess()
        keychain.readStatuses[false] = errSecInteractionNotAllowed
        let store = CertificateSecretStore(
            rootURL: URL(fileURLWithPath: "/tmp/herdme-keychain-interaction-required"),
            backend: KeychainCertificateSecretBackend(keychain: keychain)
        )

        XCTAssertThrowsError(
            try store.data(
                for: .authorityPrivateKey,
                allowInteraction: false
            )
        ) { error in
            guard case CertificateSecretError.interactionRequired = error else {
                return XCTFail("Unexpected error: \(error)")
            }
        }
        XCTAssertEqual(
            keychain.operations,
            [.read(dataProtection: true), .read(dataProtection: false)]
        )
        XCTAssertEqual(keychain.readAllowsInteraction, [false, false])
    }

    func testNonInteractiveKeychainPolicySuppressesLegacyAndProtectedPrompts() {
        var query: [CFString: Any] = [:]

        KeychainQueryInteraction.apply(allowInteraction: false, to: &query)

        XCTAssertEqual(query[kSecUseAuthenticationUI] as? String, "u_AuthUIS")
        XCTAssertNotNil(query[kSecUseAuthenticationContext])

        var interactiveQuery: [CFString: Any] = [:]
        KeychainQueryInteraction.apply(allowInteraction: true, to: &interactiveQuery)
        XCTAssertNil(interactiveQuery[kSecUseAuthenticationUI])
        XCTAssertNil(interactiveQuery[kSecUseAuthenticationContext])
    }

    func testAutomaticCertificateReadDoesNotMutateKeychain() throws {
        let keychain = TestCertificateKeychainAccess()
        let protected = Data("protected-secret".utf8)
        keychain.values[true] = protected
        keychain.values[false] = Data("legacy-secret".utf8)
        let store = CertificateSecretStore(
            rootURL: URL(fileURLWithPath: "/tmp/herdme-keychain-read-only"),
            backend: KeychainCertificateSecretBackend(keychain: keychain)
        )

        XCTAssertEqual(
            try store.data(for: .authorityPrivateKey, allowInteraction: false),
            protected
        )
        XCTAssertEqual(keychain.values[false], Data("legacy-secret".utf8))
        XCTAssertEqual(keychain.operations, [.read(dataProtection: true)])
    }

    func testCertificateKeychainDoesNotFallbackForUnrelatedErrors() {
        let keychain = TestCertificateKeychainAccess()
        keychain.values[false] = Data("legacy-secret".utf8)
        keychain.readStatuses[true] = errSecAuthFailed
        let store = CertificateSecretStore(
            rootURL: URL(fileURLWithPath: "/tmp/herdme-keychain-error"),
            backend: KeychainCertificateSecretBackend(keychain: keychain)
        )

        XCTAssertThrowsError(try store.data(for: .authorityPrivateKey)) { error in
            guard case CertificateSecretError.keychainReadFailed(let status) = error else {
                return XCTFail("Unexpected error: \(error)")
            }
            XCTAssertEqual(status, errSecAuthFailed)
        }
        XCTAssertEqual(keychain.operations, [.read(dataProtection: true)])
    }

    func testNoninteractiveProtectedCertificateReadReportsApprovalRequirement() {
        let keychain = TestCertificateKeychainAccess()
        keychain.readStatuses[true] = errSecAuthFailed
        let store = CertificateSecretStore(
            rootURL: URL(fileURLWithPath: "/tmp/herdme-protected-keychain-approval"),
            backend: KeychainCertificateSecretBackend(keychain: keychain)
        )

        XCTAssertThrowsError(
            try store.data(
                for: .authorityPrivateKey,
                allowInteraction: false
            )
        ) { error in
            guard case CertificateSecretError.interactionRequired = error else {
                return XCTFail("Unexpected error: \(error)")
            }
        }
        XCTAssertEqual(keychain.operations, [.read(dataProtection: true)])
    }

    func testCertificateKeychainPrefersProtectedValue() throws {
        let keychain = TestCertificateKeychainAccess()
        let protected = Data("protected-secret".utf8)
        keychain.values[true] = protected
        keychain.values[false] = Data("stale-legacy-secret".utf8)
        let store = CertificateSecretStore(
            rootURL: URL(fileURLWithPath: "/tmp/herdme-keychain-preferred"),
            backend: KeychainCertificateSecretBackend(keychain: keychain)
        )

        XCTAssertEqual(try store.data(for: .authorityPrivateKey), protected)
        XCTAssertNil(keychain.values[false])
        XCTAssertEqual(
            keychain.operations,
            [.read(dataProtection: true), .delete(dataProtection: false)]
        )
    }

    func testCertificateKeychainMigratesLegacyValueAfterProtectedWriteSucceeds() throws {
        let keychain = TestCertificateKeychainAccess()
        let legacy = Data("legacy-secret".utf8)
        keychain.values[false] = legacy
        let store = CertificateSecretStore(
            rootURL: URL(fileURLWithPath: "/tmp/herdme-keychain-migration"),
            backend: KeychainCertificateSecretBackend(keychain: keychain)
        )

        XCTAssertEqual(try store.data(for: .authorityPrivateKey), legacy)
        XCTAssertEqual(keychain.values[true], legacy)
        XCTAssertNil(keychain.values[false])
        XCTAssertEqual(
            keychain.operations,
            [
                .read(dataProtection: true),
                .read(dataProtection: false),
                .write(dataProtection: true),
                .delete(dataProtection: false)
            ]
        )
    }

    func testCertificateKeychainUsesLegacyValueWhenMigrationNeedsMissingEntitlement() throws {
        let keychain = TestCertificateKeychainAccess()
        let legacy = Data("legacy-secret".utf8)
        keychain.values[false] = legacy
        keychain.writeStatuses[true] = errSecMissingEntitlement
        let store = CertificateSecretStore(
            rootURL: URL(fileURLWithPath: "/tmp/herdme-keychain-migration-fallback"),
            backend: KeychainCertificateSecretBackend(keychain: keychain)
        )

        XCTAssertEqual(try store.data(for: .authorityPrivateKey), legacy)
        XCTAssertEqual(keychain.values[false], legacy)
        XCTAssertNil(keychain.values[true])
        XCTAssertEqual(
            keychain.operations,
            [
                .read(dataProtection: true),
                .read(dataProtection: false),
                .write(dataProtection: true)
            ]
        )
    }

    func testCertificateKeychainKeepsLegacyValueWhenMigrationFails() {
        let keychain = TestCertificateKeychainAccess()
        let legacy = Data("legacy-secret".utf8)
        keychain.values[false] = legacy
        keychain.writeStatuses[true] = errSecAuthFailed
        let store = CertificateSecretStore(
            rootURL: URL(fileURLWithPath: "/tmp/herdme-keychain-migration-failure"),
            backend: KeychainCertificateSecretBackend(keychain: keychain)
        )

        XCTAssertThrowsError(try store.data(for: .authorityPrivateKey)) { error in
            guard case CertificateSecretError.keychainWriteFailed(let status) = error else {
                return XCTFail("Unexpected error: \(error)")
            }
            XCTAssertEqual(status, errSecAuthFailed)
        }
        XCTAssertEqual(keychain.values[false], legacy)
        XCTAssertNil(keychain.values[true])
        XCTAssertEqual(
            keychain.operations,
            [
                .read(dataProtection: true),
                .read(dataProtection: false),
                .write(dataProtection: true)
            ]
        )
    }

    func testDumpCaptureServerReportsPortConflicts() async throws {
        let occupiedPort = try TestHTTPBackend(responseBody: "occupied")
        let server = DumpCaptureServer()
        let failed = expectation(description: "dump listener reports the occupied port")
        defer {
            server.stop()
            occupiedPort.stop()
        }

        try server.start(
            port: occupiedPort.port,
            onStateChange: { running, errorMessage in
                if !running, errorMessage != nil { failed.fulfill() }
            },
            onDump: { _ in }
        )

        await fulfillment(of: [failed], timeout: 3)
        XCTAssertFalse(server.isRunning)
    }

    func testHTTPProxyExtractsAndNormalizesHost() {
        let request = Data("GET / HTTP/1.1\r\nHost: Demo.TEST:8080\r\nConnection: close\r\n\r\n".utf8)

        XCTAssertEqual(LocalHTTPProxy.host(in: request), "demo.test")
    }

    func testHTTPProxyReplacesForwardedHeadersAndPreservesBody() {
        let request = Data(
            "POST /submit HTTP/1.1\r\nHost: demo.test\r\nX-Forwarded-Proto: untrusted\r\nContent-Length: 5\r\n\r\nhello".utf8
        )

        let forwarded = LocalHTTPProxy.addingForwardedHeaders(to: request, secure: true)
        let rendered = String(decoding: forwarded, as: UTF8.self)

        XCTAssertTrue(rendered.contains("X-Forwarded-Proto: https\r\n"))
        XCTAssertTrue(rendered.contains("X-Forwarded-For: 127.0.0.1\r\n"))
        XCTAssertFalse(rendered.contains("untrusted"))
        XCTAssertTrue(rendered.hasSuffix("\r\n\r\nhello"))
    }

    func testHTTPProxyRoutesRequestToMatchingBackend() throws {
        let backend = try TestHTTPBackend(responseBody: "proxied-by-herdme")
        let proxy = LocalHTTPProxy()
        defer {
            proxy.stop()
            backend.stop()
        }
        let proxyPort = try proxy.start(routes: ["demo.test": backend.port])

        let response = try Self.sendHTTPRequest(
            port: proxyPort,
            request: "GET / HTTP/1.1\r\nHost: demo.test\r\nConnection: close\r\n\r\n"
        )

        XCTAssertTrue(response.contains("200 OK"))
        XCTAssertTrue(response.contains("proxied-by-herdme"))
        XCTAssertTrue(proxy.isHealthy)
        proxy.stop()
        XCTAssertFalse(proxy.isHealthy)
    }

    func testHTTPSProxyUsesGeneratedCertificateForLocalDomain() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let secretStore = CertificateSecretStore(
            rootURL: root,
            backend: TestCertificateSecretBackend()
        )
        let certificateManager = LocalCertificateManager(rootURL: root, secretStore: secretStore)
        let backend = try TestHTTPBackend(responseBody: "secured-by-herdme")
        let proxy = LocalHTTPProxy()
        defer {
            proxy.stop()
            backend.stop()
        }
        let (importedSubject, proxyPort) = try await AppModel.performBlockingOperation {
            let identity = try certificateManager.prepareIdentity(
                tld: "test",
                domains: ["demo.test"]
            )
            var importedCertificate: SecCertificate?
            guard SecIdentityCopyCertificate(identity, &importedCertificate) == errSecSuccess else {
                throw LocalCertificateError.identityMissing
            }
            let subject = importedCertificate.flatMap { SecCertificateCopySubjectSummary($0) as String? }
            let preferredPort = try XCTUnwrap(LocalEnvironmentEngine.availablePort(startingAt: 9_443))
            let port = try proxy.start(
                routes: ["demo.test": backend.port],
                identity: identity,
                preferredPort: preferredPort,
                fallbackPort: 9_543
            )
            return (subject, port)
        }
        XCTAssertEqual(importedSubject, "*.test")
        let certificateURL = root.appendingPathComponent("Certificates/herdme-ca.pem")
        let leafCertificateURL = root.appendingPathComponent("Certificates/local-sites.pem")
        let privateKeyURL = root.appendingPathComponent("Certificates/herdme-ca.key")
        let leafKeyURL = root.appendingPathComponent("Certificates/local-sites.key")
        XCTAssertTrue(FileManager.default.fileExists(atPath: certificateURL.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: privateKeyURL.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: leafKeyURL.path))
        XCTAssertNotNil(try secretStore.data(for: .authorityPrivateKey))
        let identityPassphrase = try secretStore.identityPassphrase()
        XCTAssertGreaterThanOrEqual(identityPassphrase.count, 32)
        XCTAssertNotEqual(identityPassphrase, "HerdMe-Local-Identity")
        var identityEnvironment = ProcessInfo.processInfo.environment
        identityEnvironment["HERDME_TEST_IDENTITY_PASSPHRASE"] = identityPassphrase
        let identityInspection = try ProcessRunner.run(
            URL(fileURLWithPath: "/usr/bin/openssl"),
            arguments: [
                "pkcs12", "-in", root.appendingPathComponent("Certificates/local-sites.p12").path,
                "-noout", "-passin", "env:HERDME_TEST_IDENTITY_PASSPHRASE"
            ],
            environment: identityEnvironment,
            timeout: 10
        )
        XCTAssertEqual(identityInspection.status, 0, identityInspection.output)
        let inspection = try Self.runProcess(
            executable: "/usr/bin/openssl",
            arguments: ["x509", "-in", leafCertificateURL.path, "-noout", "-text"]
        )
        XCTAssertEqual(inspection.status, 0, inspection.output)
        XCTAssertTrue(inspection.output.contains("DNS:*.test"), inspection.output)
        XCTAssertTrue(inspection.output.contains("DNS:demo.test"), inspection.output)

        let process = Process()
        let output = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/curl")
        process.arguments = [
            "--silent", "--show-error", "--fail", "--max-time", "5",
            "--retry", "20", "--retry-connrefused", "--retry-delay", "0",
            "--noproxy", "*", "--cacert", certificateURL.path,
            "--resolve", "demo.test:\(proxyPort):127.0.0.1",
            "https://demo.test:\(proxyPort)/"
        ]
        process.standardOutput = output
        process.standardError = output
        try process.run()
        process.waitUntilExit()
        let response = String(decoding: output.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)

        XCTAssertEqual(process.terminationStatus, 0, response)
        XCTAssertEqual(response, "secured-by-herdme")
    }

    func testUserCertificateTrustBackendParsesPEMCertificate() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let keyURL = root.appendingPathComponent("certificate.key")
        let certificateURL = root.appendingPathComponent("certificate.pem")
        let generated = try Self.runProcess(
            executable: "/usr/bin/openssl",
            arguments: [
                "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
                "-subj", "/CN=HerdMe Test Certificate", "-keyout", keyURL.path,
                "-out", certificateURL.path
            ]
        )
        XCTAssertEqual(generated.status, 0, generated.output)

        let certificateData = try Data(contentsOf: certificateURL)
        XCTAssertNotNil(UserCertificateTrustBackend.certificate(from: certificateData))
    }

    func testCertificateManagerMigratesLegacyAuthorityKeyWithoutReplacingAuthority() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let certificates = root.appendingPathComponent("Certificates", isDirectory: true)
        try FileManager.default.createDirectory(at: certificates, withIntermediateDirectories: true)
        let keyURL = certificates.appendingPathComponent("herdme-ca.key")
        let certificateURL = certificates.appendingPathComponent("herdme-ca.pem")
        let generated = try Self.runProcess(
            executable: "/usr/bin/openssl",
            arguments: [
                "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "3650",
                "-subj", "/CN=HerdMe Legacy CA/O=HerdMe", "-addext", "basicConstraints=critical,CA:TRUE",
                "-addext", "keyUsage=critical,keyCertSign,cRLSign", "-keyout", keyURL.path,
                "-out", certificateURL.path
            ]
        )
        XCTAssertEqual(generated.status, 0, generated.output)
        let fingerprintBefore = try Self.runProcess(
            executable: "/usr/bin/openssl",
            arguments: ["x509", "-in", certificateURL.path, "-noout", "-fingerprint", "-sha256"]
        )
        XCTAssertEqual(fingerprintBefore.status, 0, fingerprintBefore.output)

        let secretStore = CertificateSecretStore(
            rootURL: root,
            backend: TestCertificateSecretBackend()
        )
        let manager = LocalCertificateManager(rootURL: root, secretStore: secretStore)
        _ = try await AppModel.performBlockingOperation {
            _ = try manager.prepareIdentity(tld: "test", domains: ["legacy.test"])
            return ()
        }

        let fingerprintAfter = try Self.runProcess(
            executable: "/usr/bin/openssl",
            arguments: ["x509", "-in", certificateURL.path, "-noout", "-fingerprint", "-sha256"]
        )
        XCTAssertEqual(fingerprintAfter.status, 0, fingerprintAfter.output)
        XCTAssertEqual(fingerprintAfter.output, fingerprintBefore.output)
        XCTAssertFalse(FileManager.default.fileExists(atPath: keyURL.path))
        XCTAssertNotNil(try secretStore.data(for: .authorityPrivateKey))
    }

    func testCertificateManagerReusesApprovedIdentityWithoutASecondKeychainRead() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let backend = TestCertificateSecretBackend()
        let manager = LocalCertificateManager(
            rootURL: root,
            secretStore: CertificateSecretStore(rootURL: root, backend: backend)
        )

        _ = try await AppModel.performBlockingOperation {
            _ = try manager.prepareIdentity(
                tld: "test",
                domains: ["first.test"],
                allowKeychainInteraction: true
            )
            return ()
        }
        let approvedReadCount = backend.readCount

        _ = try await AppModel.performBlockingOperation {
            _ = try manager.prepareIdentity(
                tld: "test",
                domains: ["first.test"],
                allowKeychainInteraction: false
            )
            return ()
        }

        XCTAssertGreaterThan(approvedReadCount, 0)
        XCTAssertEqual(backend.readCount, approvedReadCount)
    }

    func testCertificateManagerRegeneratesIdentityWhenDomainsChange() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let backend = TestCertificateSecretBackend()
        let manager = LocalCertificateManager(
            rootURL: root,
            secretStore: CertificateSecretStore(rootURL: root, backend: backend)
        )

        _ = try await AppModel.performBlockingOperation {
            _ = try manager.prepareIdentity(tld: "test", domains: ["first.test"])
            _ = try manager.prepareIdentity(tld: "test", domains: ["second.test"])
            return ()
        }

        let marker = try String(
            contentsOf: root.appendingPathComponent("Certificates/local-sites.tld"),
            encoding: .utf8
        )
        XCTAssertEqual(marker, "test\nsecond.test\n")
        let inspection = try Self.runProcess(
            executable: "/usr/bin/openssl",
            arguments: [
                "x509", "-in", root.appendingPathComponent("Certificates/local-sites.pem").path,
                "-noout", "-text"
            ]
        )
        XCTAssertEqual(inspection.status, 0, inspection.output)
        XCTAssertTrue(inspection.output.contains("DNS:second.test"), inspection.output)
        XCTAssertFalse(inspection.output.contains("DNS:first.test"), inspection.output)
    }

    func testCertificateAuthorityUsesUserTrustBackendOnlyOnce() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let trust = TestCertificateTrustBackend(state: .untrusted)
        let manager = LocalCertificateManager(
            rootURL: root,
            secretStore: CertificateSecretStore(
                rootURL: root,
                backend: TestCertificateSecretBackend()
            ),
            trustBackend: trust
        )

        let installed = try await AppModel.performBlockingOperation {
            try manager.installAuthority(tld: "test")
        }
        XCTAssertTrue(installed)
        XCTAssertEqual(trust.installCount, 1)
        XCTAssertEqual(manager.trustState(), .trusted)

        let repeated = try await AppModel.performBlockingOperation {
            try manager.installAuthority(tld: "test")
        }
        XCTAssertFalse(repeated)
        XCTAssertEqual(trust.installCount, 1)
    }

    func testCertificateAuthorityCachesIdentityForApprovedSiteDomains() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let backend = TestCertificateSecretBackend()
        let manager = LocalCertificateManager(
            rootURL: root,
            secretStore: CertificateSecretStore(rootURL: root, backend: backend),
            trustBackend: TestCertificateTrustBackend(state: .untrusted)
        )
        let domains = ["first.test", "second.test"]

        _ = try await AppModel.performBlockingOperation {
            try manager.installAuthority(tld: "test", domains: domains)
        }
        let approvedReadCount = backend.readCount

        try await AppModel.performBlockingOperation {
            _ = try manager.prepareIdentity(
                tld: "test",
                domains: domains,
                allowKeychainInteraction: false
            )
            return ()
        }

        XCTAssertGreaterThan(approvedReadCount, 0)
        XCTAssertEqual(backend.readCount, approvedReadCount)
    }

    func testStaticGatewayStreamsLargeFilesAndSupportsByteRanges() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        var asset = Data(count: 2 * 1_024 * 1_024)
        asset.withUnsafeMutableBytes { rawBuffer in
            let bytes = rawBuffer.bindMemory(to: UInt8.self)
            for index in bytes.indices { bytes[index] = UInt8(65 + index % 26) }
        }
        try asset.write(to: root.appendingPathComponent("large.bin"))

        let gateway = LocalFastCGIGateway(documentRoot: root, fpmPort: 1)
        let port = try gateway.start(preferredPort: 42_000)
        defer { gateway.stop() }

        func split(_ response: Data) throws -> (headers: String, body: Data) {
            let delimiter = Data("\r\n\r\n".utf8)
            let range = try XCTUnwrap(response.range(of: delimiter))
            return (
                String(decoding: response[..<range.lowerBound], as: UTF8.self),
                Data(response[range.upperBound...])
            )
        }

        let full = try split(
            Self.sendHTTPRequestData(
                port: port,
                request: "GET /large.bin HTTP/1.1\r\nHost: static.test\r\nConnection: close\r\n\r\n"
            ))
        XCTAssertTrue(full.headers.hasPrefix("HTTP/1.1 200 OK\r\n"))
        XCTAssertTrue(full.headers.contains("Accept-Ranges: bytes"))
        XCTAssertTrue(full.headers.contains("Content-Length: \(asset.count)"))
        XCTAssertEqual(full.body, asset)

        let start = 1_048_570
        let end = 1_048_633
        let partial = try split(
            Self.sendHTTPRequestData(
                port: port,
                request: "GET /large.bin HTTP/1.1\r\nHost: static.test\r\nRange: bytes=\(start)-\(end)\r\nConnection: close\r\n\r\n"
            ))
        XCTAssertTrue(partial.headers.hasPrefix("HTTP/1.1 206 Partial Content\r\n"))
        XCTAssertTrue(partial.headers.contains("Content-Range: bytes \(start)-\(end)/\(asset.count)"))
        XCTAssertEqual(partial.body, asset.subdata(in: start..<(end + 1)))

        let openStart = asset.count - 37
        let openEnded = try split(
            Self.sendHTTPRequestData(
                port: port,
                request: "GET /large.bin HTTP/1.1\r\nHost: static.test\r\nRange: bytes=\(openStart)-\r\nConnection: close\r\n\r\n"
            ))
        XCTAssertEqual(openEnded.body, asset.suffix(37))

        let suffix = try split(
            Self.sendHTTPRequestData(
                port: port,
                request: "GET /large.bin HTTP/1.1\r\nHost: static.test\r\nRange: bytes=-64\r\nConnection: close\r\n\r\n"
            ))
        XCTAssertEqual(suffix.body, asset.suffix(64))

        let head = try split(
            Self.sendHTTPRequestData(
                port: port,
                request: "HEAD /large.bin HTTP/1.1\r\nHost: static.test\r\nRange: bytes=10-19\r\nConnection: close\r\n\r\n"
            ))
        XCTAssertTrue(head.headers.hasPrefix("HTTP/1.1 206 Partial Content\r\n"))
        XCTAssertTrue(head.headers.contains("Content-Length: 10"))
        XCTAssertTrue(head.body.isEmpty)

        let unsatisfiable = try split(
            Self.sendHTTPRequestData(
                port: port,
                request: "GET /large.bin HTTP/1.1\r\nHost: static.test\r\nRange: bytes=\(asset.count)-\r\nConnection: close\r\n\r\n"
            ))
        XCTAssertTrue(unsatisfiable.headers.hasPrefix("HTTP/1.1 416 Range Not Satisfiable\r\n"))
        XCTAssertTrue(unsatisfiable.headers.contains("Content-Range: bytes */\(asset.count)"))
        XCTAssertTrue(unsatisfiable.body.isEmpty)
    }

    func testHTTPProxyAndGatewayReusePersistentConnectionForStaticResponses() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try Data("first-static-response".utf8).write(to: root.appendingPathComponent("first.txt"))
        try Data("second-static-response".utf8).write(to: root.appendingPathComponent("second.txt"))

        let gateway = LocalFastCGIGateway(documentRoot: root, fpmPort: 1)
        let gatewayPort = try gateway.start(preferredPort: 42_050)
        defer { gateway.stop() }
        let proxy = LocalHTTPProxy()
        let proxyPort = try proxy.start(
            routes: ["keepalive.test": gatewayPort],
            preferredPort: 42_060,
            fallbackPort: 42_070
        )
        defer { proxy.stop() }

        var descriptor: Int32 = -1
        for _ in 0..<100 {
            let candidate = socket(AF_INET, SOCK_STREAM, 0)
            guard candidate >= 0 else { throw POSIXError(.EIO) }
            var address = sockaddr_in()
            address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
            address.sin_family = sa_family_t(AF_INET)
            address.sin_port = in_port_t(UInt16(proxyPort).bigEndian)
            address.sin_addr = in_addr(s_addr: inet_addr("127.0.0.1"))
            let connected = withUnsafePointer(to: &address) { pointer in
                pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    Darwin.connect(candidate, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
            if connected == 0 {
                descriptor = candidate
                break
            }
            Darwin.close(candidate)
            usleep(10_000)
        }
        guard descriptor >= 0 else { throw POSIXError(.ECONNREFUSED) }
        defer { Darwin.close(descriptor) }
        var timeout = timeval(tv_sec: 5, tv_usec: 0)
        setsockopt(descriptor, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))

        let requests = Data(
            ("GET /first.txt HTTP/1.1\r\nHost: keepalive.test\r\n\r\n"
                + "GET /second.txt HTTP/1.1\r\nHost: keepalive.test\r\nConnection: close\r\n\r\n").utf8
        )
        try Self.writeAll(requests, to: descriptor)

        var pending = Data()
        func readResponse(_ label: String) throws -> (headers: String, body: Data) {
            let delimiter = Data("\r\n\r\n".utf8)
            var buffer = [UInt8](repeating: 0, count: 4_096)
            while pending.range(of: delimiter) == nil {
                let count = Darwin.read(descriptor, &buffer, buffer.count)
                guard count > 0 else {
                    throw NSError(
                        domain: "HerdMePersistentHTTPFixture",
                        code: 1,
                        userInfo: [NSLocalizedDescriptionKey: "Timed out reading \(label) headers with \(pending.count) pending bytes."]
                    )
                }
                pending.append(contentsOf: buffer.prefix(count))
            }
            let headerRange = try XCTUnwrap(pending.range(of: delimiter))
            let headers = String(decoding: pending[..<headerRange.lowerBound], as: UTF8.self)
            let contentLength =
                headers
                .components(separatedBy: "\r\n")
                .first { $0.lowercased().hasPrefix("content-length:") }
                .flatMap { Int($0.dropFirst("content-length:".count).trimmingCharacters(in: .whitespaces)) }
            let length = try XCTUnwrap(contentLength)
            let headerLength = pending.distance(from: pending.startIndex, to: headerRange.upperBound)
            let responseLength = headerLength + length
            while pending.count < responseLength {
                let count = Darwin.read(descriptor, &buffer, buffer.count)
                guard count > 0 else {
                    throw NSError(
                        domain: "HerdMePersistentHTTPFixture",
                        code: 2,
                        userInfo: [
                            NSLocalizedDescriptionKey: "Timed out reading \(label) body with \(pending.count) of \(responseLength) bytes."
                        ]
                    )
                }
                pending.append(contentsOf: buffer.prefix(count))
            }
            let bodyEnd = pending.index(headerRange.upperBound, offsetBy: length)
            let body = Data(pending[headerRange.upperBound..<bodyEnd])
            pending.removeFirst(responseLength)
            return (headers, body)
        }

        let first = try readResponse("first")
        let second = try readResponse("second")
        XCTAssertTrue(first.headers.contains("Connection: keep-alive"), first.headers)
        XCTAssertEqual(first.body, Data("first-static-response".utf8))
        XCTAssertTrue(second.headers.contains("Connection: close"), second.headers)
        XCTAssertEqual(second.body, Data("second-static-response".utf8))
        XCTAssertTrue(pending.isEmpty)
        var terminalByte: UInt8 = 0
        XCTAssertEqual(Darwin.read(descriptor, &terminalByte, 1), 0)
    }

    func testFastCGIGatewayRejectsAmbiguousFramingWithoutProcessingSmuggledRequests() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try Data("must-not-be-smuggled".utf8).write(to: root.appendingPathComponent("safe.txt"))

        let gateway = LocalFastCGIGateway(documentRoot: root, fpmPort: 1)
        let port = try gateway.start(preferredPort: 42_080)
        defer { gateway.stop() }
        let smuggled = "GET /safe.txt HTTP/1.1\r\nHost: framing.test\r\nConnection: close\r\n\r\n"
        let cases = [
            (
                "duplicate Content-Length",
                "POST / HTTP/1.1\r\nHost: framing.test\r\nContent-Length: 0\r\nContent-Length: 4\r\n\r\n"
                    + smuggled
            ),
            (
                "Content-Length with Transfer-Encoding",
                "POST / HTTP/1.1\r\nHost: framing.test\r\nContent-Length: 0\r\n"
                    + "Transfer-Encoding: chunked\r\n\r\n0\r\n\r\n"
                    + smuggled
            ),
            (
                "malformed chunk delimiter",
                "POST / HTTP/1.1\r\nHost: framing.test\r\nTransfer-Encoding: chunked\r\n\r\n"
                    + "4\r\ntestX\r\n0\r\n\r\n"
                    + smuggled
            )
        ]

        for (label, request) in cases {
            let exchange = try Self.exchangeRawHTTP(port: port, request: Data(request.utf8))
            let response = String(decoding: exchange.response, as: UTF8.self)
            XCTAssertTrue(response.hasPrefix("HTTP/1.1 400 Bad Request\r\n"), "\(label): \(response)")
            XCTAssertEqual(
                response.components(separatedBy: "HTTP/1.1 ").count - 1,
                1,
                "\(label) produced more than one response: \(response)"
            )
            XCTAssertFalse(response.contains("must-not-be-smuggled"), label)
            XCTAssertTrue(exchange.reachedEOF, "\(label) did not close the connection.")
        }
    }

    func testFastCGIGatewayRejectsInvalidHostTransferCodingAndHTTPVersion() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try Data("safe".utf8).write(to: root.appendingPathComponent("safe.txt"))

        let gateway = LocalFastCGIGateway(documentRoot: root, fpmPort: 1)
        let port = try gateway.start(preferredPort: 42_090)
        defer { gateway.stop() }
        let cases = [
            (
                "missing HTTP/1.1 Host",
                "GET /safe.txt HTTP/1.1\r\nConnection: close\r\n\r\n",
                "400 Bad Request"
            ),
            (
                "duplicate Host",
                "GET /safe.txt HTTP/1.1\r\nHost: first.test\r\nHost: second.test\r\nConnection: close\r\n\r\n",
                "400 Bad Request"
            ),
            (
                "unsupported transfer coding",
                "POST / HTTP/1.1\r\nHost: framing.test\r\nTransfer-Encoding: gzip\r\n\r\n",
                "501 Not Implemented"
            ),
            (
                "duplicate chunked transfer coding",
                "POST / HTTP/1.1\r\nHost: framing.test\r\nTransfer-Encoding: chunked, chunked\r\n\r\n",
                "400 Bad Request"
            ),
            (
                "chunked HTTP/1.0 request",
                "POST / HTTP/1.0\r\nHost: framing.test\r\nTransfer-Encoding: chunked\r\n\r\n",
                "400 Bad Request"
            ),
            (
                "unsupported HTTP version",
                "GET /safe.txt HTTP/2.0\r\nHost: framing.test\r\nConnection: close\r\n\r\n",
                "505 HTTP Version Not Supported"
            ),
            (
                "invalid protocol token",
                "GET /safe.txt H2\r\nHost: framing.test\r\nConnection: close\r\n\r\n",
                "400 Bad Request"
            ),
            (
                "oversized Content-Length",
                "POST / HTTP/1.1\r\nHost: framing.test\r\nContent-Length: 33554433\r\n\r\n",
                "413 Payload Too Large"
            )
        ]

        for (label, request, expectedStatus) in cases {
            let exchange = try Self.exchangeRawHTTP(port: port, request: Data(request.utf8))
            let response = String(decoding: exchange.response, as: UTF8.self)
            XCTAssertTrue(
                response.hasPrefix("HTTP/1.1 \(expectedStatus)\r\n"),
                "\(label): \(response)"
            )
            XCTAssertEqual(response.components(separatedBy: "HTTP/1.1 ").count - 1, 1, label)
            XCTAssertTrue(exchange.reachedEOF, "\(label) did not close the connection.")
        }
    }

    func testFastCGIGatewayDecodesChunkedPostTrailersAndPreservesPipelinedRequest() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try Data("<?php echo 'unused';".utf8).write(to: root.appendingPathComponent("index.php"))
        try Data("second-static-response".utf8).write(to: root.appendingPathComponent("second.txt"))

        let backend = try TestFastCGIRecordingBackend()
        let gateway = LocalFastCGIGateway(documentRoot: root, fpmPort: backend.port)
        let port = try gateway.start(preferredPort: 42_100)
        defer {
            gateway.stop()
            backend.stop()
        }
        let request = Data(
            ("POST /index.php HTTP/1.1\r\nHost: chunked.test\r\n"
                + "Transfer-Encoding: chunked\r\nContent-Type: text/plain\r\n\r\n"
                + "5\r\nhello\r\n6\r\n world\r\n0\r\nX-HerdMe-Trailer: accepted\r\n\r\n"
                + "GET /second.txt HTTP/1.1\r\nHost: chunked.test\r\nConnection: close\r\n\r\n").utf8
        )

        let exchange = try Self.exchangeRawHTTP(port: port, request: request)
        let response = String(decoding: exchange.response, as: UTF8.self)
        XCTAssertEqual(backend.waitForBody(), Data("hello world".utf8))
        XCTAssertEqual(response.components(separatedBy: "HTTP/1.1 200 OK").count - 1, 2, response)
        XCTAssertEqual(response.components(separatedBy: "Connection: keep-alive").count - 1, 1, response)
        XCTAssertEqual(response.components(separatedBy: "Connection: close").count - 1, 1, response)
        let firstBody = try XCTUnwrap(response.range(of: "fastcgi-response"))
        let secondStatus = try XCTUnwrap(
            response.range(of: "HTTP/1.1 200 OK", range: firstBody.upperBound..<response.endIndex)
        )
        let secondBody = try XCTUnwrap(
            response.range(of: "second-static-response", range: secondStatus.upperBound..<response.endIndex)
        )
        XCTAssertLessThan(firstBody.lowerBound, secondStatus.lowerBound)
        XCTAssertLessThan(secondStatus.lowerBound, secondBody.lowerBound)
        XCTAssertTrue(exchange.reachedEOF)
    }

    func testFastCGIGatewayStreamsBeforeEndRequest() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try Data("<?php echo 'unused';".utf8).write(to: root.appendingPathComponent("index.php"))

        let backend = try TestFastCGIStreamingBackend()
        let gateway = LocalFastCGIGateway(documentRoot: root, fpmPort: backend.port)
        let port = try gateway.start(preferredPort: 42_100)
        defer {
            backend.allowCompletion()
            gateway.stop()
            backend.stop()
        }

        var descriptor: Int32 = -1
        for _ in 0..<100 {
            let candidate = socket(AF_INET, SOCK_STREAM, 0)
            guard candidate >= 0 else { throw POSIXError(.EIO) }
            var address = sockaddr_in()
            address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
            address.sin_family = sa_family_t(AF_INET)
            address.sin_port = in_port_t(UInt16(port).bigEndian)
            address.sin_addr = in_addr(s_addr: inet_addr("127.0.0.1"))
            let status = withUnsafePointer(to: &address) { pointer in
                pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    Darwin.connect(candidate, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
            if status == 0 {
                descriptor = candidate
                break
            }
            Darwin.close(candidate)
            usleep(10_000)
        }
        guard descriptor >= 0 else { throw POSIXError(.ECONNREFUSED) }
        defer { Darwin.close(descriptor) }
        var timeout = timeval(tv_sec: 5, tv_usec: 0)
        setsockopt(descriptor, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
        let request = Data("GET / HTTP/1.1\r\nHost: stream.test\r\n\r\n".utf8)
        try Self.writeAll(request, to: descriptor)

        var response = Data()
        var buffer = [UInt8](repeating: 0, count: 4_096)
        while response.range(of: Data("first-".utf8)) == nil {
            let count = Darwin.read(descriptor, &buffer, buffer.count)
            guard count > 0 else { throw POSIXError(.ETIMEDOUT) }
            response.append(contentsOf: buffer.prefix(count))
        }
        XCTAssertEqual(backend.endWasSent.wait(timeout: .now()), .timedOut)
        backend.allowCompletion()
        while true {
            let count = Darwin.read(descriptor, &buffer, buffer.count)
            if count <= 0 { break }
            response.append(contentsOf: buffer.prefix(count))
        }

        let delimiter = try XCTUnwrap(response.range(of: Data("\r\n\r\n".utf8)))
        let headers = String(decoding: response[..<delimiter.lowerBound], as: UTF8.self)
        XCTAssertTrue(headers.hasPrefix("HTTP/1.1 200 OK\r\n"))
        XCTAssertTrue(headers.contains("X-HerdMe-Stream: yes"))
        XCTAssertTrue(headers.contains("Connection: close"))
        XCTAssertFalse(headers.lowercased().contains("transfer-encoding:"))
        XCTAssertFalse(headers.lowercased().contains("content-length:"))
        XCTAssertEqual(String(decoding: response[delimiter.upperBound...], as: UTF8.self), "first-second")
        XCTAssertEqual(backend.endWasSent.wait(timeout: .now() + .seconds(1)), .success)
    }

    func testEnvironmentServesPHPThroughFPMOverHTTPAndHTTPS() async throws {
        let managedRoot = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/HerdMe")
        let php = managedRoot.appendingPathComponent("Runtimes/php/8.4/bin/php")
        let fpm = managedRoot.appendingPathComponent("Runtimes/php/8.4/sbin/php-fpm")
        guard FileManager.default.isExecutableFile(atPath: php.path),
            FileManager.default.isExecutableFile(atPath: fpm.path)
        else {
            throw XCTSkip("HerdMe-managed PHP-FPM 8.4 is not installed on this machine.")
        }

        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let app = root.appendingPathComponent("fpm-demo")
        let publicDirectory = app.appendingPathComponent("public")
        let engineRoot = root.appendingPathComponent("HerdMeData")
        try FileManager.default.createDirectory(at: publicDirectory, withIntermediateDirectories: true)
        try Data(
            """
            <?php
            header('Content-Type: application/json');
            echo json_encode([
                'sapi' => PHP_SAPI,
                'method' => $_SERVER['REQUEST_METHOD'],
                'uri' => $_SERVER['REQUEST_URI'],
                'https' => $_SERVER['HTTPS'] ?? '',
                'body' => file_get_contents('php://input'),
            ]);
            """.utf8
        ).write(to: publicDirectory.appendingPathComponent("index.php"))
        try Data("static-from-herdme".utf8).write(to: publicDirectory.appendingPathComponent("asset.txt"))
        defer { try? FileManager.default.removeItem(at: root) }

        let certificateManager = LocalCertificateManager(
            rootURL: engineRoot,
            secretStore: CertificateSecretStore(
                rootURL: engineRoot,
                backend: TestCertificateSecretBackend()
            )
        )
        let engine = LocalEnvironmentEngine(rootURL: engineRoot, certificateManager: certificateManager)
        defer { engine.stopAllImmediately() }
        let site = SiteProject(path: app, name: "fpm-demo", framework: "Laravel", isLinked: false)
        let routes = try await engine.start(sites: [site], defaultPHP: php, tld: "test")
        XCTAssertNotNil(routes[site.id])
        XCTAssertTrue(engine.isRunning)
        XCTAssertTrue(engine.hasManagedState)

        let httpPort = try XCTUnwrap(engine.proxyPort)
        let getResponse = try Self.sendHTTPRequest(
            port: httpPort,
            request: "GET /hello?name=HerdMe HTTP/1.1\r\nHost: fpm-demo.test\r\nConnection: close\r\n\r\n"
        )
        XCTAssertTrue(getResponse.contains("200 OK"), getResponse)
        XCTAssertTrue(getResponse.contains("\"sapi\":\"fpm-fcgi\""), getResponse)
        XCTAssertTrue(getResponse.contains("hello?name=HerdMe"), getResponse)

        let postBody = "laravel=13"
        let postResponse = try Self.sendHTTPRequest(
            port: httpPort,
            request:
                "POST /submit HTTP/1.1\r\nHost: fpm-demo.test\r\nContent-Type: application/x-www-form-urlencoded\r\nContent-Length: \(postBody.utf8.count)\r\nConnection: close\r\n\r\n\(postBody)"
        )
        XCTAssertTrue(postResponse.contains("\"method\":\"POST\""), postResponse)
        XCTAssertTrue(postResponse.contains("\"body\":\"laravel=13\""), postResponse)

        let staticResponse = try Self.sendHTTPRequest(
            port: httpPort,
            request: "GET /asset.txt HTTP/1.1\r\nHost: fpm-demo.test\r\nConnection: close\r\n\r\n"
        )
        XCTAssertTrue(staticResponse.contains("static-from-herdme"), staticResponse)

        let httpsPort = try XCTUnwrap(engine.httpsProxyPort)
        let certificateURL = engineRoot.appendingPathComponent("Certificates/herdme-ca.pem")
        let secure = try Self.runProcess(
            executable: "/usr/bin/curl",
            arguments: [
                "--silent", "--show-error", "--fail", "--max-time", "8",
                "--retry", "30", "--retry-connrefused", "--retry-delay", "0",
                "--noproxy", "*", "--cacert", certificateURL.path,
                "--resolve", "fpm-demo.test:\(httpsPort):127.0.0.1",
                "https://fpm-demo.test:\(httpsPort)/secure"
            ]
        )
        XCTAssertEqual(secure.status, 0, secure.output)
        XCTAssertTrue(secure.output.contains("\"https\":\"on\""), secure.output)
        XCTAssertTrue(secure.output.contains("\"sapi\":\"fpm-fcgi\""), secure.output)

        await engine.stopAll()
        XCTAssertFalse(engine.isRunning)
        XCTAssertFalse(engine.hasManagedState)
    }

    func testEnvironmentKeepsHTTPRunningWhenHTTPSNeedsKeychainInteraction() async throws {
        let managedRoot = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/HerdMe")
        let php = managedRoot.appendingPathComponent("Runtimes/php/8.4/bin/php")
        let fpm = managedRoot.appendingPathComponent("Runtimes/php/8.4/sbin/php-fpm")
        guard FileManager.default.isExecutableFile(atPath: php.path),
            FileManager.default.isExecutableFile(atPath: fpm.path)
        else {
            throw XCTSkip("HerdMe-managed PHP-FPM 8.4 is not installed on this machine.")
        }

        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let app = root.appendingPathComponent("http-fallback")
        let publicDirectory = app.appendingPathComponent("public")
        let engineRoot = root.appendingPathComponent("HerdMeData")
        try FileManager.default.createDirectory(at: publicDirectory, withIntermediateDirectories: true)
        try Data("<?php echo 'http-fallback-ok';".utf8).write(
            to: publicDirectory.appendingPathComponent("index.php")
        )
        defer { try? FileManager.default.removeItem(at: root) }

        let secretBackend = InteractionRequiredCertificateSecretBackend()
        let certificateManager = LocalCertificateManager(
            rootURL: engineRoot,
            secretStore: CertificateSecretStore(
                rootURL: engineRoot,
                backend: secretBackend
            )
        )
        let engine = LocalEnvironmentEngine(rootURL: engineRoot, certificateManager: certificateManager)
        defer { engine.stopAllImmediately() }
        let site = SiteProject(
            path: app,
            name: "http-fallback",
            framework: "PHP",
            isLinked: false
        )

        _ = try await engine.start(
            sites: [site],
            defaultPHP: php,
            tld: "test",
            enableHTTPS: false
        )

        XCTAssertTrue(engine.isRunning)
        XCTAssertNil(engine.httpsProxyPort)
        XCTAssertNotNil(engine.httpsStartupError)
        XCTAssertTrue(engine.httpsStartupNeedsApproval)
        XCTAssertEqual(secretBackend.readCount, 0)

        await engine.stopAll()
        _ = try await engine.start(sites: [site], defaultPHP: php, tld: "test")

        XCTAssertTrue(engine.isRunning)
        XCTAssertNil(engine.httpsProxyPort)
        XCTAssertNotNil(engine.httpsStartupError)
        XCTAssertTrue(engine.httpsStartupNeedsApproval)
        XCTAssertGreaterThan(secretBackend.readCount, 0)
        let response = try Self.sendHTTPRequest(
            port: try XCTUnwrap(engine.proxyPort),
            request: "GET / HTTP/1.1\r\nHost: http-fallback.test\r\nConnection: close\r\n\r\n"
        )
        XCTAssertTrue(response.contains("200 OK"), response)
        XCTAssertTrue(response.contains("http-fallback-ok"), response)
    }

    func testEnvironmentLoadsInstalledXdebugAndPHPSettings() async throws {
        let managedRoot = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/HerdMe")
        let php = managedRoot.appendingPathComponent("Runtimes/php/8.4/bin/php")
        let extensionURL = XdebugManager.extensionURL(rootURL: managedRoot, cycle: "8.4")
        guard FileManager.default.isExecutableFile(atPath: php.path),
            FileManager.default.isReadableFile(atPath: extensionURL.path)
        else {
            throw XCTSkip("HerdMe-managed Xdebug for PHP 8.4 is not installed on this machine.")
        }

        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let app = root.appendingPathComponent("xdebug-demo")
        let publicDirectory = app.appendingPathComponent("public")
        let engineRoot = root.appendingPathComponent("HerdMeData")
        let testExtension = XdebugManager.extensionURL(rootURL: engineRoot, cycle: "8.4")
        try FileManager.default.createDirectory(
            at: testExtension.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try FileManager.default.copyItem(at: extensionURL, to: testExtension)
        try FileManager.default.createDirectory(at: publicDirectory, withIntermediateDirectories: true)
        try Data(
            """
            <?php
            header('Content-Type: application/json');
            echo json_encode([
                'xdebug' => phpversion('xdebug') ?: '',
                'memory' => ini_get('memory_limit'),
                'upload' => ini_get('upload_max_filesize'),
            ]);
            """.utf8
        ).write(to: publicDirectory.appendingPathComponent("index.php"))
        defer { try? FileManager.default.removeItem(at: root) }

        let certificateManager = LocalCertificateManager(
            rootURL: engineRoot,
            secretStore: CertificateSecretStore(
                rootURL: engineRoot,
                backend: TestCertificateSecretBackend()
            )
        )
        let engine = LocalEnvironmentEngine(rootURL: engineRoot, certificateManager: certificateManager)
        defer { engine.stopAllImmediately() }
        let site = SiteProject(path: app, name: "xdebug-demo", framework: "PHP", isLinked: false)
        _ = try await engine.start(
            sites: [site],
            defaultPHP: php,
            defaultPHPCycle: "8.4",
            tld: "test",
            debuggerSettings: DebuggerSettings(
                enabled: true,
                detectBreakpoints: true,
                port: 9_003,
                ideKey: "HERDME_TEST"
            ),
            phpRequestSettings: PHPRequestSettings(
                maxUploadMegabytes: 17,
                memoryLimitMegabytes: 321
            )
        )

        let response = try Self.sendHTTPRequest(
            port: try XCTUnwrap(engine.proxyPort),
            request: "GET /?XDEBUG_TRIGGER=OTHER HTTP/1.1\r\nHost: xdebug-demo.test\r\nConnection: close\r\n\r\n"
        )
        XCTAssertTrue(response.contains("\"xdebug\":\"3."), response)
        XCTAssertTrue(response.contains("\"memory\":\"321M\""), response)
        XCTAssertTrue(response.contains("\"upload\":\"17M\""), response)
    }

    @MainActor
    func testExistingLaravelProjectThroughFPMWhenRequested() async throws {
        let managedRoot = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/HerdMe")
        let php = managedRoot.appendingPathComponent("Runtimes/php/8.4/bin/php")
        let fpm = managedRoot.appendingPathComponent("Runtimes/php/8.4/sbin/php-fpm")
        guard FileManager.default.isExecutableFile(atPath: php.path),
            FileManager.default.isExecutableFile(atPath: fpm.path)
        else {
            throw XCTSkip("HerdMe-managed PHP-FPM 8.4 is not installed on this machine.")
        }

        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let app = root.appendingPathComponent("real-laravel")
        let engineRoot = root.appendingPathComponent("HerdMeData")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }

        let environment = ProcessInfo.processInfo.environment
        let source: URL
        if let sourcePath = environment["HERDME_LARAVEL_PROJECT"], !sourcePath.isEmpty {
            source = URL(fileURLWithPath: sourcePath).standardizedFileURL
        } else if environment["HERDME_CREATE_LARAVEL_INTEGRATION"] == "1" {
            let parent = root.appendingPathComponent("created", isDirectory: true)
            try FileManager.default.createDirectory(at: parent, withIntermediateDirectories: true)
            var stages: [ProjectCreationStage] = []
            source = try await ProjectCreator(rootURL: managedRoot).create(
                NewProjectRequest(
                    name: "laravel-live-" + String(UUID().uuidString.prefix(8)).lowercased(),
                    parentDirectory: parent,
                    starterKit: .react,
                    testingFramework: "Pest",
                    installBoost: false,
                    initializeGit: false
                )
            ) { stage in
                stages.append(stage)
            }
            XCTAssertEqual(
                stages,
                [
                    .creatingLaravelProject,
                    .preparingNodeRuntime,
                    .installingFrontendDependencies,
                    .buildingFrontendAssets,
                    .verifyingProject
                ]
            )
            XCTAssertTrue(
                FileManager.default.fileExists(
                    atPath: source.appendingPathComponent("public/build/manifest.json").path
                ))
        } else {
            throw XCTSkip(
                "Set HERDME_LARAVEL_PROJECT for a read-only project or "
                    + "HERDME_CREATE_LARAVEL_INTEGRATION=1 to create a temporary Laravel project."
            )
        }

        guard FileManager.default.fileExists(atPath: source.appendingPathComponent("artisan").path),
            FileManager.default.fileExists(atPath: source.appendingPathComponent("vendor/autoload.php").path),
            FileManager.default.fileExists(atPath: source.appendingPathComponent("public/index.php").path)
        else {
            XCTFail("The Laravel integration source must contain artisan, vendor/autoload.php, and public/index.php.")
            return
        }

        let copied = try Self.runProcess(
            executable: "/bin/cp",
            arguments: ["-cR", source.path, app.path]
        )
        XCTAssertEqual(copied.status, 0, copied.output)

        let version = try Self.runProcess(
            executable: php.path,
            arguments: ["artisan", "--version"],
            currentDirectory: app
        )
        XCTAssertEqual(version.status, 0, version.output)
        XCTAssertTrue(version.output.contains("Laravel Framework 13."), version.output)
        try Data("herdme-static-ok".utf8).write(
            to: app.appendingPathComponent("public/herdme-static.txt")
        )

        let certificateManager = LocalCertificateManager(
            rootURL: engineRoot,
            secretStore: CertificateSecretStore(
                rootURL: engineRoot,
                backend: TestCertificateSecretBackend()
            )
        )
        let engine = LocalEnvironmentEngine(rootURL: engineRoot, certificateManager: certificateManager)
        defer { engine.stopAllImmediately() }
        let site = SiteProject(path: app, name: "real-laravel", framework: "Laravel", isLinked: false)
        _ = try await engine.start(sites: [site], defaultPHP: php, tld: "test")

        let httpPort = try XCTUnwrap(engine.proxyPort)
        let http = try Self.runProcess(
            executable: "/usr/bin/curl",
            arguments: [
                "--silent", "--show-error", "--fail", "--max-time", "15",
                "--retry", "30", "--retry-connrefused", "--retry-delay", "0", "--noproxy", "*",
                "--resolve", "real-laravel.test:\(httpPort):127.0.0.1",
                "http://real-laravel.test:\(httpPort)/"
            ]
        )
        XCTAssertEqual(http.status, 0, http.output)
        XCTAssertFalse(http.output.isEmpty)

        let staticAsset = try Self.runProcess(
            executable: "/usr/bin/curl",
            arguments: [
                "--silent", "--show-error", "--fail", "--max-time", "8", "--noproxy", "*",
                "--resolve", "real-laravel.test:\(httpPort):127.0.0.1",
                "http://real-laravel.test:\(httpPort)/herdme-static.txt"
            ]
        )
        XCTAssertEqual(staticAsset.status, 0, staticAsset.output)
        XCTAssertEqual(staticAsset.output, "herdme-static-ok")

        let httpsPort = try XCTUnwrap(engine.httpsProxyPort)
        let certificateURL = engineRoot.appendingPathComponent("Certificates/herdme-ca.pem")
        let https = try Self.runProcess(
            executable: "/usr/bin/curl",
            arguments: [
                "--silent", "--show-error", "--fail", "--max-time", "15",
                "--retry", "30", "--retry-connrefused", "--retry-delay", "0", "--noproxy", "*",
                "--cacert", certificateURL.path,
                "--resolve", "real-laravel.test:\(httpsPort):127.0.0.1",
                "https://real-laravel.test:\(httpsPort)/"
            ]
        )
        XCTAssertEqual(https.status, 0, https.output)
        XCTAssertFalse(https.output.isEmpty)
    }

    func testDomainResolverRecognizesOnlyHerdMeConfiguration() {
        XCTAssertEqual(
            DomainResolverManager.state(contents: DomainResolverManager.contents(port: 53), port: 53),
            .managed
        )
        XCTAssertEqual(
            DomainResolverManager.state(contents: "nameserver 127.0.0.1", port: 53),
            .external
        )
    }

    func testDomainResolverRequiresApplicationBundleUnderApplications() {
        XCTAssertTrue(
            DomainResolverManager.applicationIsInstalled(
                at: URL(fileURLWithPath: "/Applications/HerdMe.app")
            )
        )
        XCTAssertTrue(
            DomainResolverManager.applicationIsInstalled(
                at: URL(fileURLWithPath: "/Applications/Developer Tools/HerdMe.app")
            )
        )
        XCTAssertFalse(
            DomainResolverManager.applicationIsInstalled(
                at: URL(fileURLWithPath: "/Volumes/HerdMe/HerdMe.app")
            )
        )
        XCTAssertFalse(
            DomainResolverManager.applicationIsInstalled(
                at: URL(fileURLWithPath: "/Applications-old/HerdMe.app")
            )
        )
        XCTAssertFalse(
            DomainResolverManager.applicationIsInstalled(
                at: URL(fileURLWithPath: "/Applications/HerdMe")
            )
        )
    }

    func testDomainResolverInstallRejectsTransientBundleBeforeChangingService() {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let controller = TestNetworkServiceController(status: .notRegistered)
        let manager = DomainResolverManager(
            rootURL: root,
            networkService: controller,
            bundleURL: URL(fileURLWithPath: "/Volumes/HerdMe/HerdMe.app")
        )

        XCTAssertThrowsError(try manager.install(tld: "test")) { error in
            guard case DomainResolverError.applicationMustBeInstalled = error else {
                return XCTFail("Unexpected error: \(error)")
            }
        }
        XCTAssertEqual(controller.operations, [])
        XCTAssertFalse(
            FileManager.default.fileExists(
                atPath: root.appendingPathComponent("Runtime/network-helper.conf").path
            )
        )
    }

    func testDomainResolverReadinessRequiresCurrentRunningHelper() {
        XCTAssertTrue(
            AppModel.domainResolverIsReady(
                state: .managed,
                helperRunning: true,
                helperNeedsUpdate: false
            ))
        XCTAssertFalse(
            AppModel.domainResolverIsReady(
                state: .managed,
                helperRunning: false,
                helperNeedsUpdate: false
            ))
        XCTAssertFalse(
            AppModel.domainResolverIsReady(
                state: .managed,
                helperRunning: true,
                helperNeedsUpdate: true
            ))
        XCTAssertFalse(
            AppModel.domainResolverIsReady(
                state: .external,
                helperRunning: true,
                helperNeedsUpdate: false
            ))
    }

    func testAutomaticDomainRepairOnlyTargetsUnhealthyManagedHelper() {
        XCTAssertFalse(
            AppModel.shouldRepairDomainResolverAutomatically(
                state: .managed,
                helperRunning: true,
                helperNeedsUpdate: false
            ))
        XCTAssertTrue(
            AppModel.shouldRepairDomainResolverAutomatically(
                state: .managed,
                helperRunning: false,
                helperNeedsUpdate: false
            ))
        XCTAssertTrue(
            AppModel.shouldRepairDomainResolverAutomatically(
                state: .managed,
                helperRunning: true,
                helperNeedsUpdate: true
            ))
        XCTAssertFalse(
            AppModel.shouldRepairDomainResolverAutomatically(
                state: .missing,
                helperRunning: false,
                helperNeedsUpdate: true
            ))
        XCTAssertFalse(
            AppModel.shouldRepairDomainResolverAutomatically(
                state: .external,
                helperRunning: false,
                helperNeedsUpdate: true
            ))
    }

    func testModernNetworkServiceManifestUsesFixedBundleProgramAndMode() throws {
        let manifest = Bundle.main.bundleURL
            .appendingPathComponent("Contents/Library/LaunchDaemons")
            .appendingPathComponent(DomainResolverManager.modernPlistName)

        XCTAssertTrue(FileManager.default.isReadableFile(atPath: manifest.path))
        XCTAssertTrue(DomainResolverManager.modernServiceManifestIsValid(at: manifest))

        let invalid = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: invalid) }
        var contents = try String(contentsOf: manifest, encoding: .utf8)
        contents = contents.replacingOccurrences(
            of: "Contents/Helpers/herdme-network-helper",
            with: "/tmp/untrusted-helper"
        )
        try contents.write(to: invalid, atomically: true, encoding: .utf8)
        XCTAssertFalse(DomainResolverManager.modernServiceManifestIsValid(at: invalid))
    }

    func testModernNetworkServiceRegistersAndRestartsThroughFixedController() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let controller = TestNetworkServiceController(status: .notRegistered)
        let manager = DomainResolverManager(rootURL: root, networkService: controller)

        try manager.registerModernService(restart: false)
        XCTAssertEqual(controller.operations, [.register])
        XCTAssertEqual(controller.status(), .enabled)

        try manager.registerModernService(restart: true)
        XCTAssertEqual(controller.operations, [.register, .unregister, .register])
        XCTAssertEqual(controller.status(), .enabled)
    }

    func testModernNetworkServiceRetriesTransientRegistrationAfterRestart() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let controller = TestNetworkServiceController(
            status: .enabled,
            registrationFailures: 2
        )
        let manager = DomainResolverManager(rootURL: root, networkService: controller)

        try manager.registerModernService(restart: true)

        XCTAssertEqual(
            controller.operations,
            [.unregister, .register, .register, .register]
        )
        XCTAssertEqual(controller.status(), .enabled)
    }

    func testModernNetworkServiceReportsApprovalWithoutRegisteringAgain() {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let controller = TestNetworkServiceController(status: .requiresApproval)
        let manager = DomainResolverManager(rootURL: root, networkService: controller)

        XCTAssertThrowsError(try manager.registerModernService(restart: false)) { error in
            guard case DomainResolverError.serviceRequiresApproval = error else {
                return XCTFail("Unexpected error: \(error)")
            }
        }
        XCTAssertEqual(controller.operations, [])
    }

    func testModernNetworkServiceReportsApprovalRaisedDuringRegistration() {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let controller = TestNetworkServiceController(
            status: .notRegistered,
            statusAfterRegister: .requiresApproval,
            failRegistration: true
        )
        let manager = DomainResolverManager(rootURL: root, networkService: controller)

        XCTAssertThrowsError(try manager.registerModernService(restart: false)) { error in
            guard case DomainResolverError.serviceRequiresApproval = error else {
                return XCTFail("Unexpected error: \(error)")
            }
        }
        XCTAssertEqual(controller.operations, [.register])
        XCTAssertEqual(controller.status(), .requiresApproval)
    }

    func testModernNetworkServiceRejectsRegistrationThatNeverBecomesEnabled() {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let controller = TestNetworkServiceController(
            status: .notRegistered,
            statusAfterRegister: .notRegistered
        )
        let manager = DomainResolverManager(rootURL: root, networkService: controller)

        XCTAssertThrowsError(try manager.registerModernService(restart: false)) { error in
            guard case DomainResolverError.installationVerificationFailed = error else {
                return XCTFail("Unexpected error: \(error)")
            }
        }
        XCTAssertEqual(controller.operations, [.register])
    }

    func testNetworkHelperConfigurationAndLaunchDaemonAreBounded() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let manager = DomainResolverManager(rootURL: root)
        try manager.updateNetworkRouting(httpPort: 8_081, httpsPort: 8_444, tld: "local-test")
        let configuration = try String(
            contentsOf: root.appendingPathComponent("Runtime/network-helper.conf"),
            encoding: .utf8
        )
        XCTAssertEqual(configuration, "http=8081\nhttps=8444\ntld=local-test\n")
        try manager.updateNetworkRouting(httpPort: 8_082, httpsPort: nil, tld: "local-test")
        let httpOnlyConfiguration = try String(
            contentsOf: root.appendingPathComponent("Runtime/network-helper.conf"),
            encoding: .utf8
        )
        XCTAssertEqual(httpOnlyConfiguration, "http=8082\nhttps=0\ntld=local-test\n")
        XCTAssertThrowsError(
            try manager.updateNetworkRouting(httpPort: 80, httpsPort: 443, tld: "local-test")
        )

        let plist = DomainResolverManager.launchDaemonPlist(
            configurationPath: "/Users/Demo & QA/HerdMe.conf",
            uid: 501,
            gid: 20
        )
        let decoded =
            try PropertyListSerialization.propertyList(
                from: Data(plist.utf8),
                format: nil
            ) as? [String: Any]
        let arguments = try XCTUnwrap(decoded?["ProgramArguments"] as? [String])
        XCTAssertEqual(decoded?["AssociatedBundleIdentifiers"] as? [String], ["app.herdme.desktop"])
        XCTAssertEqual(arguments.first, DomainResolverManager.helperDestination)
        XCTAssertTrue(arguments.contains("/Users/Demo & QA/HerdMe.conf"))
        XCTAssertTrue(arguments.contains("501"))
        XCTAssertTrue(arguments.contains("20"))
    }

    func testLegacyNetworkHelperUpdateDetectionComparesBinaryAndDaemonIdentity() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let controller = TestNetworkServiceController(status: .notRegistered)
        let manager = DomainResolverManager(rootURL: root, networkService: controller)
        let bundledHelper = root.appendingPathComponent("bundled-helper")
        let installedHelper = root.appendingPathComponent("installed-helper")
        let installedDaemon = root.appendingPathComponent("installed-daemon.plist")
        let helper = Data([0x48, 0x65, 0x72, 0x64, 0x4D, 0x65])
        try helper.write(to: bundledHelper)
        try helper.write(to: installedHelper)
        try DomainResolverManager.launchDaemonPlist(
            configurationPath: root.appendingPathComponent("Runtime/network-helper.conf").path,
            uid: getuid(),
            gid: getgid()
        ).write(to: installedDaemon, atomically: true, encoding: .utf8)

        XCTAssertTrue(
            manager.isNetworkHelperCurrent(
                bundledHelperURL: bundledHelper,
                installedHelperURL: installedHelper,
                installedDaemonURL: installedDaemon
            ))

        try Data([0x6F, 0x6C, 0x64]).write(to: installedHelper)
        XCTAssertFalse(
            manager.isNetworkHelperCurrent(
                bundledHelperURL: bundledHelper,
                installedHelperURL: installedHelper,
                installedDaemonURL: installedDaemon
            ))
    }

    func testModernNetworkHelperUpdateDetectionTracksBundleLocationAndContents() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let controller = TestNetworkServiceController(status: .enabled)
        let originalBundle = root.appendingPathComponent("Installer/HerdMe.app")
        let installedBundle = root.appendingPathComponent("Applications/HerdMe.app")
        let manager = DomainResolverManager(
            rootURL: root,
            networkService: controller,
            bundleURL: originalBundle
        )
        let helper = root.appendingPathComponent("herdme-network-helper")
        let manifest = root.appendingPathComponent(DomainResolverManager.modernPlistName)
        try Data("first-helper".utf8).write(to: helper)
        try Data(
            contentsOf: Bundle.main.bundleURL
                .appendingPathComponent("Contents/Library/LaunchDaemons")
                .appendingPathComponent(DomainResolverManager.modernPlistName)
        )
        .write(to: manifest)

        XCTAssertFalse(
            manager.isNetworkHelperCurrent(
                bundledHelperURL: helper,
                bundledManifestURL: manifest,
                modernServiceRunning: true
            ))
        try manager.recordCurrentNetworkHelperIdentity(
            bundledHelperURL: helper,
            bundledManifestURL: manifest
        )
        XCTAssertTrue(
            manager.isNetworkHelperCurrent(
                bundledHelperURL: helper,
                bundledManifestURL: manifest,
                modernServiceRunning: true
            ))

        let relocatedManager = DomainResolverManager(
            rootURL: root,
            networkService: controller,
            bundleURL: installedBundle
        )
        XCTAssertFalse(
            relocatedManager.isNetworkHelperCurrent(
                bundledHelperURL: helper,
                bundledManifestURL: manifest,
                modernServiceRunning: true
            ))

        try Data("updated-helper".utf8).write(to: helper)
        XCTAssertFalse(
            manager.isNetworkHelperCurrent(
                bundledHelperURL: helper,
                bundledManifestURL: manifest,
                modernServiceRunning: true
            ))
    }

    func testDNSResponseMapsTestDomainToLoopback() throws {
        let query = Data([
            0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x04, 0x64, 0x65, 0x6D, 0x6F,
            0x04, 0x74, 0x65, 0x73, 0x74,
            0x00, 0x00, 0x01, 0x00, 0x01
        ])

        let response = try XCTUnwrap(DNSMessage.response(to: query, tld: "test"))

        XCTAssertEqual(response.prefix(2), query.prefix(2))
        XCTAssertEqual(response[7], 1)
        XCTAssertEqual(response.suffix(4), Data([127, 0, 0, 1]))
    }

}
