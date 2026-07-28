import AppKit
import SwiftUI

struct RootView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var applicationSettings: ApplicationSettingsCoordinator
    @EnvironmentObject private var navigation: AppNavigation
    @EnvironmentObject private var securityCoordinator: SecuritySetupCoordinator
    @Environment(\.locale) private var locale

    var body: some View {
        Group {
            if securityCoordinator.isPresentingOnboarding {
                OnboardingView()
            } else {
                HStack(spacing: 0) {
                    SidebarView()
                    Divider()
                    page
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .background(Color(nsColor: .windowBackgroundColor))
                }
            }
        }
        .environment(\.layoutDirection, AppLocalization.layoutDirection(for: locale))
        .herdTheme(model.configuration.theme)
        .background(WindowSizeController(page: navigation.selectedPage))
        .overlay(alignment: .top) {
            if let error = model.lastError {
                ErrorBanner(presentation: ErrorPresentation(error)) {
                    model.lastError = nil
                }
                .padding(.horizontal, 20)
                .padding(.top, 12)
                .transition(.move(edge: .top).combined(with: .opacity))
                .zIndex(10)
            }
        }
        .animation(.easeOut(duration: 0.18), value: model.lastError != nil)
        .alert(item: $applicationSettings.updateNotice) { notice in
            if let downloadURL = notice.downloadURL {
                return Alert(
                    title: Text(notice.title),
                    message: Text(notice.message),
                    primaryButton: .default(Text("Download")) {
                        NSWorkspace.shared.open(downloadURL)
                    },
                    secondaryButton: .cancel()
                )
            }
            return Alert(
                title: Text(notice.title),
                message: Text(notice.message),
                dismissButton: .default(Text("OK"))
            )
        }
    }

    @ViewBuilder
    private var page: some View {
        switch navigation.selectedPage {
        case .dashboard: DashboardView()
        case .general: GeneralView()
        case .sites: SitesView()
        case .php: PHPView()
        case .node: NodeView()
        case .services: ServicesView()
        case .mail: MailView()
        case .dumps: DumpsView()
        case .debugger: DebuggerView()
        case .logs: LogsView()
        case .about: AboutView()
        }
    }
}

private struct ErrorBanner: View {
    let presentation: ErrorPresentation
    let dismiss: () -> Void
    @State private var isShowingDetails = false

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundStyle(.red)
                .accessibilityHidden(true)
            Text(presentation.message)
                .font(.callout)
                .frame(maxWidth: .infinity, alignment: .leading)
                .textSelection(.enabled)
            if presentation.technicalDetails != nil {
                Button {
                    isShowingDetails = true
                } label: {
                    Image(systemName: "info.circle")
                }
                .buttonStyle(.borderless)
                .help("Show technical details")
                .accessibilityLabel("Show technical details")
            }
            Button(action: dismiss) {
                Image(systemName: "xmark")
            }
            .buttonStyle(.borderless)
            .help("Dismiss error")
            .accessibilityLabel("Dismiss error")
            .keyboardShortcut(.cancelAction)
        }
        .padding(12)
        .frame(maxWidth: 620)
        .background(.regularMaterial)
        .overlay {
            RoundedRectangle(cornerRadius: 8)
                .stroke(Color.red.opacity(0.45), lineWidth: 1)
        }
        .clipShape(RoundedRectangle(cornerRadius: 8))
        .shadow(color: .black.opacity(0.14), radius: 8, y: 3)
        .accessibilityElement(children: .contain)
        .sheet(isPresented: $isShowingDetails) {
            ErrorDetailsView(details: presentation.technicalDetails ?? "")
        }
    }
}

private struct ErrorDetailsView: View {
    let details: String
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Technical Details")
                .font(.headline)
            ScrollView {
                Text(details)
                    .font(.system(.caption, design: .monospaced))
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .textSelection(.enabled)
            }
            Divider()
            HStack {
                Button {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(details, forType: .string)
                } label: {
                    Label("Copy Details", systemImage: "doc.on.doc")
                }
                Spacer()
                Button("Done") { dismiss() }
                    .buttonStyle(.borderedProminent)
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 560, height: 360)
    }
}

private struct WindowSizeController: NSViewRepresentable {
    let page: SidebarPage

    func makeCoordinator() -> Coordinator {
        Coordinator()
    }

    func makeNSView(context: Context) -> NSView {
        NSView(frame: .zero)
    }

    func updateNSView(_ view: NSView, context: Context) {
        DispatchQueue.main.async {
            guard let window = view.window else { return }
            context.coordinator.update(window: window, page: page)
        }
    }

    @MainActor
    final class Coordinator {
        private weak var window: NSWindow?
        private var lastPage: SidebarPage?

        func update(window: NSWindow, page: SidebarPage) {
            if self.window !== window {
                self.window = window
                lastPage = nil
                window.setFrameAutosaveName("HerdMeMainWindow")
                window.contentMinSize = NSSize(width: 730, height: 527)
            }
            guard lastPage != page else { return }
            lastPage = page

            let preferredWidth: CGFloat =
                switch page {
                case .dashboard: 980
                case .sites: 1_100
                case .services: 980
                case .mail, .dumps, .debugger, .logs: 900
                default: 730
                }
            let current = window.contentView?.frame.size ?? window.frame.size
            guard current.width + 1 < preferredWidth else { return }
            window.setContentSize(
                NSSize(width: preferredWidth, height: max(current.height, 527))
            )
        }
    }
}
