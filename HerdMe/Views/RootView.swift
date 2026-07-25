import AppKit
import SwiftUI

struct RootView: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        HStack(spacing: 0) {
            SidebarView()
            Divider()
            page
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(Color(nsColor: .windowBackgroundColor))
        }
        .herdTheme(model.configuration.theme)
        .background(WindowSizeController(page: model.selectedPage))
        .alert("HerdMe", isPresented: Binding(
            get: { model.lastError != nil },
            set: { if !$0 { model.lastError = nil } }
        )) {
            Button("OK", role: .cancel) { model.lastError = nil }
        } message: {
            Text(ErrorPresentation(model.lastError ?? "Unknown error").message)
        }
        .alert(item: $model.updateNotice) { notice in
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
        switch model.selectedPage {
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

private struct WindowSizeController: NSViewRepresentable {
    let page: SidebarPage

    func makeNSView(context: Context) -> NSView {
        NSView(frame: .zero)
    }

    func updateNSView(_ view: NSView, context: Context) {
        DispatchQueue.main.async {
            guard let window = view.window else { return }
            let widePages: Set<SidebarPage> = [.sites, .services, .mail, .dumps, .debugger, .logs]
            let width: CGFloat = widePages.contains(page) ? 900 : 730
            let target = NSSize(width: width, height: 527)
            let current = window.contentView?.frame.size ?? .zero
            window.contentMinSize = NSSize(width: 730, height: 527)
            if abs(current.width - width) > 1 || abs(current.height - target.height) > 1 {
                window.setContentSize(target)
            }
        }
    }
}
