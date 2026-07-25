import AppKit
import SwiftUI
import WebKit

struct SitesView: View {
    private let defaultPHPTag = "__herdme_default_php__"
    private let defaultNodeTag = "__herdme_project_node__"
    @EnvironmentObject private var model: AppModel
    @Environment(\.openWindow) private var openWindow
    @State private var search = ""
    @State private var tab = SiteTab.general
    @State private var showPreview = true

    private enum SiteTab: String, CaseIterable {
        case general = "General"
        case information = "Information"
    }

    private var filteredSites: [SiteProject] {
        search.isEmpty ? model.sites : model.sites.filter { $0.name.localizedCaseInsensitiveContains(search) }
    }

    var body: some View {
        VStack(spacing: 0) {
            toolbar
            Divider()
            HStack(spacing: 0) {
                siteList
                Divider()
                detail
            }
        }
        .onAppear {
            showPreview = model.configuration.sitePreviews
            model.refresh()
        }
    }

    private var toolbar: some View {
        HStack(spacing: 14) {
            Image(systemName: "sidebar.left")
                .foregroundStyle(.secondary)
            Text(model.selectedSite?.domain(tld: model.configuration.tld) ?? "Sites")
                .font(.system(size: 18, weight: .medium))
            if model.environmentStatus == .running {
                HStack(spacing: 5) {
                    Circle().fill(.green).frame(width: 7, height: 7)
                    Text("Running").font(.caption).foregroundStyle(.secondary)
                    if model.certificateTrustState == .trusted {
                        Image(systemName: "lock.fill")
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                    }
                }
            }
            Button {
                model.toggleEnvironment()
            } label: {
                Image(systemName: model.environmentStatus == .running ? "stop.fill" : "play.fill")
            }
            .buttonStyle(.borderless)
            .disabled(
                model.sites.isEmpty
                    || model.environmentStatus == .starting
                    || model.environmentStatus == .stopping
            )
            .help(model.environmentStatus == .running ? "Stop all sites" : "Start all sites")
            Spacer()
            Button {
                model.refresh()
            } label: {
                Image(systemName: "arrow.clockwise")
            }
            .buttonStyle(.borderless)
            .help("Refresh sites")
            Button {
                showPreview.toggle()
                model.configuration.sitePreviews = showPreview
                model.persist()
            } label: {
                Image(systemName: showPreview ? "photo" : "photo.slash")
            }
            .buttonStyle(.borderless)
            .help("Toggle site previews")
            TextField("Search", text: $search)
                .textFieldStyle(.roundedBorder)
                .frame(width: 230)
        }
        .padding(.horizontal, 16)
        .frame(height: 58)
    }

    private var siteList: some View {
        VStack(spacing: 0) {
            if filteredSites.isEmpty {
                VStack(spacing: 8) {
                    Spacer()
                    Image(systemName: "server.rack")
                        .font(.title)
                        .foregroundStyle(.secondary)
                    Text("No sites found")
                        .foregroundStyle(.secondary)
                    Spacer()
                }
            } else {
                List {
                    Section("Ungrouped") {
                        ForEach(filteredSites) { site in
                            Button {
                                model.selectedSiteID = site.id
                            } label: {
                                Text(site.domain(tld: model.configuration.tld))
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                    .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                            .listRowBackground(
                                model.selectedSiteID == site.id
                                    ? Color.accentColor.opacity(0.18)
                                    : Color.clear
                            )
                        }
                    }
                }
                .listStyle(.sidebar)
                .scrollContentBackground(.hidden)
                .background(Color(nsColor: .textBackgroundColor))
            }
            Divider()
            Button {
                openWindow(id: "create-site")
                NSApplication.shared.activate(ignoringOtherApps: true)
            } label: {
                Label("Add Site", systemImage: "plus")
            }
            .buttonStyle(.bordered)
            .padding(8)
        }
        .frame(width: 270)
        .background(Color(nsColor: .textBackgroundColor))
    }

    @ViewBuilder
    private var detail: some View {
        if let site = model.selectedSite {
            VStack(spacing: 0) {
                HStack(spacing: 22) {
                    ForEach(SiteTab.allCases, id: \.self) { item in
                        Button(item.rawValue) { tab = item }
                            .buttonStyle(.plain)
                            .foregroundStyle(tab == item ? Color.primary : Color.secondary)
                            .padding(.vertical, 10)
                            .overlay(alignment: .bottom) {
                                if tab == item {
                                    Rectangle().fill(Color.accentColor).frame(height: 2)
                                }
                            }
                    }
                    Spacer()
                }
                .padding(.horizontal, 16)
                Divider()

                if tab == .general {
                    siteGeneral(site)
                } else {
                    siteInformation(site)
                }
            }
        } else {
            EmptyStateView(
                symbol: "server.rack",
                title: "No Site Selected",
                message: "Add a sites folder or create a Laravel project to get started.",
                actionTitle: "Add Site"
            ) {
                openWindow(id: "create-site")
            }
        }
    }

    private func siteGeneral(_ site: SiteProject) -> some View {
        ScrollView {
            VStack(spacing: 16) {
                HStack(alignment: .top, spacing: 10) {
                    Group {
                        if showPreview {
                            SiteWebPreview(url: model.sitePreviewURL(for: site))
                        } else {
                            Image(systemName: "globe")
                                .font(.system(size: 44))
                                .foregroundStyle(.secondary)
                                .frame(maxWidth: .infinity, maxHeight: .infinity)
                        }
                    }
                    .frame(width: 162, height: 105)
                    .background(Color(nsColor: .textBackgroundColor))
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                    .overlay { RoundedRectangle(cornerRadius: 8).stroke(.separator) }

                    VStack(spacing: 10) {
                        HStack {
                            Text(site.name).font(.headline)
                            Spacer()
                            Menu("Actions") {
                                Button("Terminal") { model.openTerminal(for: site) }
                                Button("IDE") { model.openIDE(for: site) }
                                Button("Open in Browser") { model.openSite(site) }
                                Divider()
                                Button("Tinker") { model.openTinker(for: site) }
                                    .disabled(site.framework != "Laravel")
                                Button("Logs") { model.selectedPage = .logs }
                                Button("Debugger") { model.selectedPage = .debugger }
                                if site.isLinked {
                                    Divider()
                                    Button("Unlink Project", role: .destructive) { model.unlinkSite(site) }
                                }
                            }
                        }
                        .padding(12)
                        .background(Color(nsColor: .controlBackgroundColor))
                        .clipShape(RoundedRectangle(cornerRadius: 8))
                        Toggle("Show Preview", isOn: $showPreview)
                            .toggleStyle(.switch)
                            .padding(12)
                            .background(Color(nsColor: .controlBackgroundColor))
                            .clipShape(RoundedRectangle(cornerRadius: 8))
                    }
                }

                Divider()

                SettingsPanel {
                    VStack(spacing: 9) {
                        SettingRow("PHP Version:") {
                            Picker("", selection: Binding(
                                get: { site.phpVersion ?? defaultPHPTag },
                                set: { model.setSitePHPVersion($0 == defaultPHPTag ? nil : $0, for: site) }
                            )) {
                                Text("Default (\(model.configuration.selectedPHP))").tag(defaultPHPTag)
                                ForEach(model.phpVersions.filter(\.isInstalled)) { Text($0.cycle).tag($0.cycle) }
                                if let cycle = site.phpVersion,
                                   !model.phpVersions.contains(where: { $0.cycle == cycle && $0.isInstalled }) {
                                    Text("\(cycle) (Unavailable)").tag(cycle).disabled(true)
                                }
                            }
                            .labelsHidden()
                            .frame(width: 150)
                        }
                        PanelDivider()
                        SettingRow("Node Version:") {
                            Picker("", selection: Binding(
                                get: { site.nodeVersion ?? defaultNodeTag },
                                set: { model.setSiteNodeVersion($0 == defaultNodeTag ? nil : $0, for: site) }
                            )) {
                                Text("Project Default").tag(defaultNodeTag)
                                ForEach(model.nodeVersions.filter(\.isInstalled)) { Text($0.cycle).tag($0.cycle) }
                                if let cycle = site.nodeVersion,
                                   !model.nodeVersions.contains(where: { $0.cycle == cycle && $0.isInstalled }) {
                                    Text("\(cycle) (Unavailable)").tag(cycle).disabled(true)
                                }
                            }
                            .labelsHidden()
                            .frame(width: 150)
                        }
                        PanelDivider()
                        SettingRow("Path:") {
                            Button(site.path.path.replacingOccurrences(of: FileManager.default.homeDirectoryForCurrentUser.path, with: "~")) {
                                NSWorkspace.shared.open(site.path)
                            }
                            .buttonStyle(.link)
                        }
                        PanelDivider()
                        SettingRow("URL:") {
                            Button(model.siteDisplayAddress(for: site)) { model.openSite(site) }
                                .buttonStyle(.link)
                        }
                    }
                }
            }
            .padding(16)
        }
    }

    private func siteInformation(_ site: SiteProject) -> some View {
        ScrollView {
            SettingsPanel {
                VStack(spacing: 10) {
                    SettingRow("Framework") { Text(site.framework) }
                    PanelDivider()
                    SettingRow("Domain") { Text(site.domain(tld: model.configuration.tld)) }
                    PanelDivider()
                    SettingRow("Project Path") { Text(site.path.path).textSelection(.enabled) }
                    PanelDivider()
                    SettingRow("Registration") { Text(site.isLinked ? "Linked" : "Parked") }
                }
            }
            .padding(16)
        }
    }
}

private struct SiteWebPreview: NSViewRepresentable {
    let url: URL?

    func makeCoordinator() -> Coordinator {
        Coordinator()
    }

    func makeNSView(context: Context) -> DesktopPreviewScrollView {
        let configuration = WKWebViewConfiguration()
        return DesktopPreviewScrollView(configuration: configuration)
    }

    func updateNSView(_ preview: DesktopPreviewScrollView, context: Context) {
        context.coordinator.load(url, in: preview.webView)
    }

    @MainActor
    final class Coordinator {
        private var requestedURL: URL?

        func load(_ url: URL?, in webView: WKWebView) {
            guard requestedURL != url else { return }
            requestedURL = url
            guard let url else {
                webView.loadHTMLString("", baseURL: nil)
                return
            }
            webView.load(URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 6))
        }
    }
}

private final class DesktopPreviewScrollView: NSScrollView {
    private static let desktopViewport = NSSize(width: 1_440, height: 934)
    let webView: WKWebView

    init(configuration: WKWebViewConfiguration) {
        webView = WKWebView(
            frame: NSRect(origin: .zero, size: Self.desktopViewport),
            configuration: configuration
        )
        super.init(frame: .zero)

        borderType = .noBorder
        drawsBackground = false
        contentView.drawsBackground = false
        hasHorizontalScroller = false
        hasVerticalScroller = false
        allowsMagnification = true
        minMagnification = 0.05
        maxMagnification = 1

        webView.setValue(false, forKey: "drawsBackground")
        documentView = webView
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    override func layout() {
        super.layout()
        guard contentSize.width > 0, contentSize.height > 0 else { return }

        let scale = min(
            min(
                contentSize.width / Self.desktopViewport.width,
                contentSize.height / Self.desktopViewport.height
            ),
            1
        )
        if abs(magnification - scale) > 0.001 {
            setMagnification(
                scale,
                centeredAt: NSPoint(
                    x: Self.desktopViewport.width / 2,
                    y: Self.desktopViewport.height / 2
                )
            )
        }
    }
}
