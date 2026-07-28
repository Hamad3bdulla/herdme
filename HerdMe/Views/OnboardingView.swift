import AppKit
import SwiftUI

struct OnboardingView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var securityCoordinator: SecuritySetupCoordinator
    @State private var isShowingFailureDetails = false

    var body: some View {
        ZStack(alignment: .bottom) {
            Color(nsColor: .windowBackgroundColor)
                .ignoresSafeArea()

            Rectangle()
                .fill(Color(red: 0.93, green: 0.12, blue: 0.16))
                .frame(height: 7)

            VStack(spacing: 26) {
                Spacer(minLength: 42)
                content
                    .frame(maxWidth: 500)
                Spacer(minLength: 42)
            }
            .padding(.horizontal, 48)
        }
        .frame(minWidth: 730, minHeight: 527)
    }

    @ViewBuilder
    private var content: some View {
        if securityCoordinator.onboardingStage == .welcome {
            welcome
        } else if let error = securityCoordinator.onboardingError {
            failure(error)
        } else if securityCoordinator.onboardingStage == .completed {
            completed
        } else {
            progress
        }
    }

    private var welcome: some View {
        VStack(spacing: 20) {
            appIcon
            Text(securityCoordinator.onboardingStage.title)
                .font(.system(size: 34, weight: .bold))
                .accessibilityLabel(Text(verbatim: securityCoordinator.onboardingStage.title))
                .accessibilityIdentifier("onboarding.welcome.title")
            Text(securityCoordinator.onboardingStage.detail)
                .font(.system(size: 14))
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .lineSpacing(4)
            Text(
                verbatim: String.localizedStringWithFormat(
                    String(localized: "PHP %@  |  Composer  |  Laravel Installer  |  Node.js %@  |  HTTPS"),
                    RuntimeCatalog.defaultPHPCycle,
                    RuntimeCatalog.defaultNodeMajor
                )
            )
            .font(.system(size: 12, weight: .medium))
            .foregroundStyle(.secondary)
            primaryButton("Set up HerdMe", systemImage: "arrow.right") {
                model.beginInitialSetup()
            }
            .accessibilityIdentifier("onboarding.setup")
        }
    }

    private var progress: some View {
        VStack(spacing: 20) {
            appIcon
            ProgressView()
                .controlSize(.large)
                .tint(Color(red: 0.93, green: 0.12, blue: 0.16))
                .frame(width: 44, height: 44)
            Text(securityCoordinator.onboardingStage.title)
                .font(.system(size: 24, weight: .semibold))
                .multilineTextAlignment(.center)
            Text(securityCoordinator.onboardingStage.detail)
                .font(.system(size: 13))
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .lineSpacing(3)
            ProgressView(value: progressValue)
                .tint(Color(red: 0.93, green: 0.12, blue: 0.16))
                .frame(width: 300)
            Text(progressLabel)
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private func failure(_ failure: ErrorPresentation) -> some View {
        VStack(spacing: 20) {
            Image(systemName: "exclamationmark.triangle.fill")
                .font(.system(size: 42))
                .foregroundStyle(Color(red: 0.93, green: 0.12, blue: 0.16))
            Text("Setup could not finish")
                .font(.system(size: 26, weight: .bold))
            Text(failure.message)
                .font(.system(size: 13))
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .lineSpacing(3)
                .textSelection(.enabled)
            if let details = failure.technicalDetails {
                DisclosureGroup(
                    "Technical Details",
                    isExpanded: $isShowingFailureDetails
                ) {
                    VStack(alignment: .trailing, spacing: 6) {
                        ScrollView {
                            Text(details)
                                .font(.system(.caption, design: .monospaced))
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .textSelection(.enabled)
                        }
                        .frame(maxHeight: 130)
                        Button {
                            NSPasteboard.general.clearContents()
                            NSPasteboard.general.setString(details, forType: .string)
                        } label: {
                            Image(systemName: "doc.on.doc")
                        }
                        .buttonStyle(.borderless)
                        .help("Copy Technical Details")
                        .accessibilityLabel("Copy Technical Details")
                    }
                    .padding(.top, 6)
                }
                .frame(maxWidth: 460)
            }
            primaryButton("Try Again", systemImage: "arrow.clockwise") {
                isShowingFailureDetails = false
                model.beginInitialSetup()
            }
        }
    }

    private var completed: some View {
        VStack(spacing: 20) {
            Image(systemName: "checkmark.circle.fill")
                .font(.system(size: 52))
                .foregroundStyle(.green)
            Text(securityCoordinator.onboardingStage.title)
                .font(.system(size: 30, weight: .bold))
            Text(securityCoordinator.onboardingStage.detail)
                .font(.system(size: 14))
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
            primaryButton("Open HerdMe", systemImage: "arrow.right") {
                model.finishOnboarding()
            }
        }
    }

    private var appIcon: some View {
        Image(nsImage: NSApplication.shared.applicationIconImage)
            .resizable()
            .scaledToFit()
            .frame(width: 70, height: 70)
            .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
    }

    private func primaryButton(
        _ title: LocalizedStringKey,
        systemImage: String,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            Label(title, systemImage: systemImage)
                .font(.system(size: 13, weight: .semibold))
                .frame(minWidth: 150)
                .padding(.vertical, 4)
        }
        .buttonStyle(.borderedProminent)
        .tint(Color(red: 0.93, green: 0.12, blue: 0.16))
        .controlSize(.large)
    }

    private var progressValue: Double {
        guard
            let index = OnboardingStage.installationStages.firstIndex(
                of: securityCoordinator.onboardingStage
            )
        else {
            return 0
        }
        return Double(index + 1) / Double(OnboardingStage.installationStages.count)
    }

    private var progressLabel: String {
        guard
            let index = OnboardingStage.installationStages.firstIndex(
                of: securityCoordinator.onboardingStage
            )
        else {
            return String(localized: "Preparing")
        }
        return String.localizedStringWithFormat(
            String(localized: "Step %lld of %lld"),
            Int64(index + 1),
            Int64(OnboardingStage.installationStages.count)
        )
    }
}
