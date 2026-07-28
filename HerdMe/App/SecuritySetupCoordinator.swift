import Combine
import Foundation

@MainActor
final class SecuritySetupCoordinator: ObservableObject {
    @Published var domainResolverState: DomainResolverState = .missing
    @Published var isDNSServerRunning = false
    @Published var networkHelperNeedsUpdate = false
    @Published var certificateTrustState: CertificateTrustState = .missing
    @Published var privilegedOperation: String?
    @Published var isPresentingOnboarding = false
    @Published var onboardingStage: OnboardingStage = .welcome
    @Published var isRunningInitialSetup = false
    @Published var onboardingError: ErrorPresentation?

    let resolverManager: DomainResolverManager
    let certificateManager: LocalCertificateManager
    private nonisolated let dnsServer = LocalDNSServer()

    init(rootURL: URL) {
        resolverManager = DomainResolverManager(rootURL: rootURL)
        certificateManager = LocalCertificateManager(rootURL: rootURL)
    }

    nonisolated func stopDNSServer() {
        dnsServer.stop()
    }
}
