import AppKit
import SwiftUI
import WebKit

struct SitesView: View {
    private let defaultPHPTag = "__herdme_default_php__"
    private let defaultNodeTag = "__herdme_project_node__"
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var navigation: AppNavigation
    @EnvironmentObject private var runtimeCoordinator: RuntimeCoordinator
    @EnvironmentObject private var sitesCoordinator: SitesCoordinator
    @EnvironmentObject private var environmentCoordinator: EnvironmentCoordinator
    @EnvironmentObject private var securityCoordinator: SecuritySetupCoordinator
    @Environment(\.openWindow) private var openWindow
    @State private var search = ""
    @State private var tab = SiteTab.general
    @State private var showPreview = true
    @State private var artisanSite: SiteProject?
    @State private var npmSite: SiteProject?
    @State private var environmentSite: SiteProject?
    @State private var sitePendingRemoval: SiteProject?
    @State private var siteDetails: SiteDetailsSnapshot?
    @State private var isLoadingSiteDetails = false
    @State private var gitSnapshots: [String: SiteGitSnapshot] = [:]
    @State private var gitRefreshID = UUID()

    private enum SiteTab: String, CaseIterable {
        case general = "General"
        case information = "Information"

        var localizedTitle: String {
            switch self {
            case .general: String(localized: "General")
            case .information: String(localized: "Information")
            }
        }
    }

    private var filteredSites: [SiteProject] {
        search.isEmpty
            ? sitesCoordinator.sites
            : sitesCoordinator.sites.filter { $0.name.localizedCaseInsensitiveContains(search) }
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
        .task(id: gitInspectionID) {
            await loadGitSnapshots()
        }
        .sheet(item: $artisanSite) { site in
            ArtisanRunnerView(site: site)
                .environmentObject(model)
        }
        .sheet(item: $npmSite) { site in
            NPMScriptRunnerView(site: site)
                .environmentObject(model)
        }
        .sheet(item: $environmentSite, onDismiss: refreshSelectedSiteDetails) { site in
            SiteEnvironmentEditor(site: site)
        }
        .alert(
            removalAlertTitle,
            isPresented: Binding(
                get: { sitePendingRemoval != nil },
                set: { if !$0 { sitePendingRemoval = nil } }
            )
        ) {
            Button("Cancel", role: .cancel) { sitePendingRemoval = nil }
            Button("Move to Trash", role: .destructive) {
                guard let site = sitePendingRemoval else { return }
                sitePendingRemoval = nil
                model.moveSiteToTrash(site)
            }
        } message: {
            Text("The project folder will be moved to Trash and can be restored from there.")
        }
    }

    private var toolbar: some View {
        HStack(spacing: 14) {
            Image(systemName: "sidebar.left")
                .foregroundStyle(.secondary)
            Text("Sites")
                .font(.headline)
            HStack(spacing: 5) {
                Circle()
                    .fill(environmentCoordinator.status.color)
                    .frame(width: 7, height: 7)
                Text(environmentCoordinator.status.localizedTitle)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                if environmentCoordinator.isHTTPSActive {
                    Image(systemName: "lock.fill")
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                        .help("HTTPS active")
                } else if environmentCoordinator.status == .running {
                    Button {
                        navigation.selectedPage = .general
                    } label: {
                        Image(systemName: "lock.open.fill")
                            .font(.caption2)
                            .foregroundStyle(.orange)
                    }
                    .buttonStyle(.plain)
                    .help("HTTP only. Open General to enable HTTPS.")
                    .accessibilityLabel("HTTP only. Enable HTTPS")
                }
            }
            .accessibilityElement(children: .combine)
            .accessibilityLabel("Sites environment")
            .accessibilityValue(
                environmentCoordinator.status == .running
                    ? environmentCoordinator.isHTTPSActive ? "Running, HTTPS active" : "Running, HTTP only"
                    : environmentCoordinator.status.localizedTitle
            )
            Button {
                model.toggleEnvironment()
            } label: {
                Image(systemName: environmentCoordinator.status == .running ? "stop.fill" : "play.fill")
            }
            .buttonStyle(.borderless)
            .disabled(
                sitesCoordinator.sites.isEmpty
                    || environmentCoordinator.status == .starting
                    || environmentCoordinator.status == .stopping
            )
            .help(environmentCoordinator.status == .running ? "Stop all sites" : "Start all sites")
            .accessibilityLabel(environmentCoordinator.status == .running ? "Stop all sites" : "Start all sites")
            Spacer()
            Button {
                gitRefreshID = UUID()
                model.refresh()
            } label: {
                Image(systemName: "arrow.clockwise")
            }
            .buttonStyle(.borderless)
            .help("Refresh sites")
            .accessibilityLabel("Refresh sites")
            Button {
                showPreview.toggle()
                model.configuration.sitePreviews = showPreview
                model.persist()
            } label: {
                Image(systemName: showPreview ? "photo" : "photo.slash")
            }
            .buttonStyle(.borderless)
            .help("Toggle site previews")
            .accessibilityLabel(showPreview ? "Hide site previews" : "Show site previews")
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
                                navigation.selectedSiteID = site.id
                            } label: {
                                HStack(spacing: 8) {
                                    Image(systemName: frameworkSymbol(for: site.framework))
                                        .foregroundStyle(.secondary)
                                        .frame(width: 16)
                                    VStack(alignment: .leading, spacing: 2) {
                                        Text(site.domain(tld: model.configuration.tld))
                                            .lineLimit(1)
                                        Text("PHP \(site.phpVersion ?? model.configuration.selectedPHP)")
                                            .font(.caption2.monospacedDigit())
                                            .foregroundStyle(.secondary)
                                        if let gitTitle = gitListStatusTitle(for: site) {
                                            HStack(spacing: 4) {
                                                Image(systemName: "arrow.triangle.branch")
                                                Text(gitTitle)
                                                    .lineLimit(1)
                                            }
                                            .font(.caption2)
                                            .foregroundStyle(.secondary)
                                        }
                                    }
                                    Spacer(minLength: 4)
                                    Circle()
                                        .fill(siteStatusColor(for: site))
                                        .frame(width: 7, height: 7)
                                        .help(siteStatusTitle(for: site))
                                }
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                            .accessibilityLabel(site.domain(tld: model.configuration.tld))
                            .accessibilityValue(
                                "PHP \(site.phpVersion ?? model.configuration.selectedPHP), \(siteStatusTitle(for: site))"
                            )
                            .listRowBackground(
                                navigation.selectedSiteID == site.id
                                    ? Color.accentColor.opacity(0.18)
                                    : Color.clear
                            )
                            .contextMenu {
                                siteActionMenu(site)
                            }
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

    private func frameworkSymbol(for framework: String) -> String {
        switch framework {
        case "Laravel": "shippingbox.fill"
        case "WordPress": "w.circle.fill"
        case "Node.js": "hexagon.fill"
        case "PHP": "chevron.left.forwardslash.chevron.right"
        default: "globe"
        }
    }

    private func siteStatusColor(for site: SiteProject) -> Color {
        if environmentCoordinator.status == .running, sitesCoordinator.runtimePorts[site.id] != nil {
            return .green
        }
        if environmentCoordinator.status == .conflict { return .orange }
        if environmentCoordinator.status == .starting || environmentCoordinator.status == .stopping {
            return .yellow
        }
        return .secondary
    }

    private func siteStatusTitle(for site: SiteProject) -> String {
        if environmentCoordinator.status == .running, sitesCoordinator.runtimePorts[site.id] != nil {
            return String(localized: "Running")
        }
        switch environmentCoordinator.status {
        case .conflict: return String(localized: "Port conflict")
        case .starting: return String(localized: "Starting")
        case .stopping: return String(localized: "Stopping")
        case .running, .stopped: return String(localized: "Stopped")
        }
    }

    private var gitInspectionID: String {
        let sites = sitesCoordinator.sites.map(\.id).sorted().joined(separator: "\n")
        return gitRefreshID.uuidString + "\n" + sites
    }

    private func gitListStatusTitle(for site: SiteProject) -> String? {
        guard let snapshot = gitSnapshots[site.id], snapshot.isRepository else { return nil }
        let branch = snapshot.branch ?? String(localized: "Detached HEAD")
        if snapshot.changeCount == 0 {
            return String.localizedStringWithFormat(String(localized: "%@, clean"), branch)
        }
        return String.localizedStringWithFormat(
            String(localized: "%@, %lld changes"),
            branch,
            Int64(snapshot.changeCount)
        )
    }

    @MainActor
    private func loadGitSnapshots() async {
        let sites = sitesCoordinator.sites
        guard !sites.isEmpty else {
            gitSnapshots = [:]
            return
        }
        let snapshots = await SiteDetailsInspector.inspectGit(for: sites)
        guard !Task.isCancelled else { return }
        gitSnapshots = snapshots
    }

    @ViewBuilder
    private var detail: some View {
        if let site = sitesCoordinator.selectedSite(identifier: navigation.selectedSiteID) {
            VStack(spacing: 0) {
                siteHeader(site)
                Divider()
                HStack(spacing: 22) {
                    ForEach(SiteTab.allCases, id: \.self) { item in
                        Button(item.localizedTitle) { tab = item }
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
            .task(id: site.id) {
                await loadSiteDetails(for: site)
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

    private func siteHeader(_ site: SiteProject) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .top, spacing: 12) {
                ZStack {
                    RoundedRectangle(cornerRadius: 7)
                        .fill(Color.accentColor.opacity(0.14))
                    Image(systemName: frameworkSymbol(for: site.framework))
                        .font(.system(size: 19, weight: .medium))
                        .foregroundStyle(Color.accentColor)
                }
                .frame(width: 42, height: 42)
                .accessibilityHidden(true)

                VStack(alignment: .leading, spacing: 5) {
                    HStack(spacing: 7) {
                        Text(site.name)
                            .font(.title3.weight(.semibold))
                            .lineLimit(1)
                        Circle()
                            .fill(siteStatusColor(for: site))
                            .frame(width: 7, height: 7)
                        Text(siteStatusTitle(for: site))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Button(model.siteDisplayAddress(for: site)) {
                        model.openSite(site)
                    }
                    .buttonStyle(.link)
                    .lineLimit(1)
                }
                Spacer(minLength: 8)
            }

            HStack(spacing: 6) {
                siteMetadataBadge(site.framework, systemImage: frameworkSymbol(for: site.framework))
                siteMetadataBadge(
                    "PHP \(site.phpVersion ?? model.configuration.selectedPHP)",
                    systemImage: "chevron.left.forwardslash.chevron.right"
                )
                siteMetadataBadge(
                    "Node \(site.nodeVersion ?? "Project")",
                    systemImage: "hexagon"
                )
                Spacer(minLength: 4)
            }

            ViewThatFits(in: .horizontal) {
                HStack(spacing: 8) {
                    Button {
                        model.openSite(site)
                    } label: {
                        Label("Open", systemImage: "safari")
                    }
                    .buttonStyle(.borderedProminent)
                    Button {
                        model.openTerminal(for: site)
                    } label: {
                        Label("Terminal", systemImage: "terminal")
                    }
                    .buttonStyle(.bordered)
                    Button {
                        model.openTinker(for: site)
                    } label: {
                        Label("Tinker", systemImage: "chevron.left.forwardslash.chevron.right")
                    }
                    .buttonStyle(.bordered)
                    .disabled(site.framework != "Laravel")
                    Button {
                        artisanSite = site
                    } label: {
                        Label("Artisan", systemImage: "hammer")
                    }
                    .buttonStyle(.bordered)
                    .disabled(site.framework != "Laravel")
                    Button {
                        npmSite = site
                    } label: {
                        Label("npm", systemImage: "play.rectangle")
                    }
                    .buttonStyle(.bordered)
                    .disabled(!hasPackageJSON(site))
                    Button {
                        environmentSite = site
                    } label: {
                        Label("Edit .env", systemImage: "doc.text")
                    }
                    .buttonStyle(.bordered)
                    Button(role: .destructive) {
                        requestRemoval(of: site)
                    } label: {
                        Label(
                            siteRemovalTitle(for: site),
                            systemImage: site.isLinked ? "link.badge.minus" : "trash"
                        )
                    }
                    .buttonStyle(.bordered)
                    siteMoreActions(site)
                    Spacer(minLength: 0)
                }

                HStack(spacing: 8) {
                    compactSiteAction("Open in Browser", systemImage: "safari") {
                        model.openSite(site)
                    }
                    compactSiteAction("Open Terminal", systemImage: "terminal") {
                        model.openTerminal(for: site)
                    }
                    compactSiteAction(
                        "Open Tinker",
                        systemImage: "chevron.left.forwardslash.chevron.right",
                        disabled: site.framework != "Laravel"
                    ) {
                        model.openTinker(for: site)
                    }
                    compactSiteAction(
                        "Run Artisan Command",
                        systemImage: "hammer",
                        disabled: site.framework != "Laravel"
                    ) {
                        artisanSite = site
                    }
                    compactSiteAction(
                        "Run npm Script",
                        systemImage: "play.rectangle",
                        disabled: !hasPackageJSON(site)
                    ) {
                        npmSite = site
                    }
                    compactSiteAction("Edit .env", systemImage: "doc.text") {
                        environmentSite = site
                    }
                    compactSiteAction(
                        siteRemovalTitle(for: site),
                        systemImage: site.isLinked ? "link.badge.minus" : "trash"
                    ) {
                        requestRemoval(of: site)
                    }
                    siteMoreActions(site)
                    Spacer(minLength: 0)
                }
            }
        }
        .padding(16)
        .accessibilityElement(children: .contain)
    }

    private func siteMetadataBadge(_ title: String, systemImage: String) -> some View {
        Label(title, systemImage: systemImage)
            .font(.caption)
            .foregroundStyle(.secondary)
            .lineLimit(1)
            .padding(.horizontal, 7)
            .padding(.vertical, 4)
            .background(Color(nsColor: .controlBackgroundColor))
            .clipShape(RoundedRectangle(cornerRadius: 5))
            .overlay {
                RoundedRectangle(cornerRadius: 5)
                    .stroke(Color(nsColor: .separatorColor), lineWidth: 1)
            }
    }

    private func compactSiteAction(
        _ title: String,
        systemImage: String,
        disabled: Bool = false,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            Image(systemName: systemImage)
                .frame(width: 18, height: 18)
        }
        .buttonStyle(.bordered)
        .disabled(disabled)
        .help(title)
        .accessibilityLabel(title)
    }

    private func siteMoreActions(_ site: SiteProject) -> some View {
        Menu {
            siteActionMenu(site)
        } label: {
            Image(systemName: "ellipsis.circle")
                .frame(width: 18, height: 18)
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .help("More site actions")
        .accessibilityLabel("More site actions")
    }

    @ViewBuilder
    private func siteActionMenu(_ site: SiteProject) -> some View {
        Button {
            model.openSite(site)
        } label: {
            Label("Open in Browser", systemImage: "safari")
        }
        Button {
            copySiteURL(site)
        } label: {
            Label("Copy Link", systemImage: "link")
        }
        Button {
            copyToPasteboard(site.path.path)
        } label: {
            Label("Copy Path", systemImage: "doc.on.doc")
        }
        Divider()
        Button {
            model.openTerminal(for: site)
        } label: {
            Label("Open Terminal", systemImage: "terminal")
        }
        Button {
            model.openIDE(for: site)
        } label: {
            Label("Open in IDE", systemImage: "hammer")
        }
        Button {
            model.showLogs(for: site)
        } label: {
            Label("Logs", systemImage: "doc.text.magnifyingglass")
        }
        Button {
            artisanSite = site
        } label: {
            Label("Artisan", systemImage: "hammer")
        }
        .disabled(site.framework != "Laravel")
        Button {
            npmSite = site
        } label: {
            Label("Run npm Script", systemImage: "play.rectangle")
        }
        .disabled(!hasPackageJSON(site))
        Button {
            environmentSite = site
        } label: {
            Label("Edit .env", systemImage: "doc.text")
        }
        Button {
            navigation.selectedPage = .debugger
        } label: {
            Label("Debugger", systemImage: "ladybug")
        }
        if site.isLinked {
            Divider()
            Button("Unlink Project", role: .destructive) { model.unlinkSite(site) }
        } else {
            Divider()
            Button(role: .destructive) {
                sitePendingRemoval = site
            } label: {
                Label("Move to Trash", systemImage: "trash")
            }
        }
    }

    private var removalAlertTitle: String {
        guard let sitePendingRemoval else { return String(localized: "Move Site to Trash?") }
        return String.localizedStringWithFormat(
            String(localized: "Move “%@” to Trash?"),
            sitePendingRemoval.name
        )
    }

    private func siteRemovalTitle(for site: SiteProject) -> String {
        site.isLinked
            ? String(localized: "Unlink Project")
            : String(localized: "Move to Trash")
    }

    private func hasPackageJSON(_ site: SiteProject) -> Bool {
        FileManager.default.isReadableFile(
            atPath: site.path.appendingPathComponent("package.json").path
        )
    }

    private func requestRemoval(of site: SiteProject) {
        if site.isLinked {
            model.unlinkSite(site)
        } else {
            sitePendingRemoval = site
        }
    }

    private func copySiteURL(_ site: SiteProject) {
        guard let url = model.siteURL(for: site) else {
            model.lastError = "The site does not have an active local address."
            return
        }
        copyToPasteboard(url.absoluteString)
    }

    private func copyToPasteboard(_ value: String) {
        NSPasteboard.general.clearContents()
        guard NSPasteboard.general.setString(value, forType: .string) else {
            model.lastError = "HerdMe could not copy the value."
            return
        }
    }

    private func siteGeneral(_ site: SiteProject) -> some View {
        ScrollView {
            VStack(spacing: 16) {
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
                .aspectRatio(16 / 9, contentMode: .fit)
                .frame(maxWidth: .infinity)
                .background(Color(nsColor: .textBackgroundColor))
                .clipShape(RoundedRectangle(cornerRadius: 8))
                .overlay { RoundedRectangle(cornerRadius: 8).stroke(.separator) }

                SettingsPanel {
                    VStack(spacing: 9) {
                        SettingRow("PHP") {
                            Picker(
                                "",
                                selection: Binding(
                                    get: { site.phpVersion ?? defaultPHPTag },
                                    set: { model.setSitePHPVersion($0 == defaultPHPTag ? nil : $0, for: site) }
                                )
                            ) {
                                Text("Default (\(model.configuration.selectedPHP))").tag(defaultPHPTag)
                                ForEach(runtimeCoordinator.phpVersions.filter(\.isInstalled)) { Text($0.cycle).tag($0.cycle) }
                                if let cycle = site.phpVersion,
                                    !runtimeCoordinator.phpVersions.contains(where: { $0.cycle == cycle && $0.isInstalled })
                                {
                                    Text("\(cycle) (Unavailable)").tag(cycle).disabled(true)
                                }
                            }
                            .labelsHidden()
                            .frame(width: 150)
                        }
                        PanelDivider()
                        SettingRow("Node") {
                            Picker(
                                "",
                                selection: Binding(
                                    get: { site.nodeVersion ?? defaultNodeTag },
                                    set: { model.setSiteNodeVersion($0 == defaultNodeTag ? nil : $0, for: site) }
                                )
                            ) {
                                Text("Project Default").tag(defaultNodeTag)
                                ForEach(runtimeCoordinator.nodeVersions.filter(\.isInstalled)) { Text($0.cycle).tag($0.cycle) }
                                if let cycle = site.nodeVersion,
                                    !runtimeCoordinator.nodeVersions.contains(where: { $0.cycle == cycle && $0.isInstalled })
                                {
                                    Text("\(cycle) (Unavailable)").tag(cycle).disabled(true)
                                }
                            }
                            .labelsHidden()
                            .frame(width: 150)
                        }
                        PanelDivider()
                        SettingRow("Path") {
                            Button(site.path.path.replacingOccurrences(of: FileManager.default.homeDirectoryForCurrentUser.path, with: "~"))
                            {
                                NSWorkspace.shared.open(site.path)
                            }
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
            VStack(spacing: 16) {
                HStack {
                    Text("Project Details")
                        .font(.headline)
                    Spacer()
                    if isLoadingSiteDetails {
                        ProgressView()
                            .controlSize(.small)
                    }
                    Button {
                        Task { await loadSiteDetails(for: site) }
                    } label: {
                        Image(systemName: "arrow.clockwise")
                    }
                    .buttonStyle(.borderless)
                    .disabled(isLoadingSiteDetails)
                    .help("Refresh site details")
                    .accessibilityLabel("Refresh site details")
                }

                SettingsPanel {
                    VStack(spacing: 10) {
                        SettingRow("Project Path") { Text(site.path.path).textSelection(.enabled) }
                        PanelDivider()
                        SettingRow("Registration") {
                            Text(site.isLinked ? String(localized: "Linked") : String(localized: "Parked"))
                        }
                        PanelDivider()
                        SettingRow("Framework") { Text(site.framework) }
                        PanelDivider()
                        SettingRow("PHP Version") { Text(phpVersionTitle(for: site)) }
                        PanelDivider()
                        SettingRow("Node Version") { Text(nodeVersionTitle(for: site)) }
                    }
                }

                SettingsPanel {
                    VStack(spacing: 10) {
                        SettingRow("Environment File") {
                            HStack(spacing: 8) {
                                Text(environmentStatusTitle)
                                    .foregroundStyle(.secondary)
                                Button {
                                    environmentSite = site
                                } label: {
                                    Image(systemName: "pencil")
                                }
                                .buttonStyle(.borderless)
                                .help("Edit .env")
                                .accessibilityLabel("Edit .env")
                            }
                        }
                        PanelDivider()
                        SettingRow("Logs") {
                            HStack(spacing: 8) {
                                Text(logStatusTitle)
                                    .foregroundStyle(.secondary)
                                    .lineLimit(1)
                                Button {
                                    model.showLogs(for: site)
                                } label: {
                                    Image(systemName: "arrow.up.right.square")
                                }
                                .buttonStyle(.borderless)
                                .help("Open site logs")
                                .accessibilityLabel("Open site logs")
                            }
                        }
                        PanelDivider()
                        SettingRow("Laravel Routes") {
                            HStack(spacing: 8) {
                                Text(routeStatusTitle)
                                    .foregroundStyle(.secondary)
                                    .lineLimit(1)
                                Button {
                                    artisanSite = site
                                } label: {
                                    Image(systemName: "list.bullet.rectangle")
                                }
                                .buttonStyle(.borderless)
                                .disabled(site.framework != "Laravel")
                                .help("Run route:list")
                                .accessibilityLabel("Run route:list")
                            }
                        }
                    }
                }

                SettingsPanel {
                    VStack(spacing: 10) {
                        SettingRow("Git") {
                            Text(gitStatusTitle)
                                .foregroundStyle(.secondary)
                                .textSelection(.enabled)
                        }
                        PanelDivider()
                        SettingRow("Associated Services") {
                            Text(associatedServicesTitle)
                                .foregroundStyle(.secondary)
                                .multilineTextAlignment(.trailing)
                                .textSelection(.enabled)
                        }
                    }
                }
            }
            .padding(16)
        }
    }

    private var environmentStatusTitle: String {
        guard let siteDetails else {
            return isLoadingSiteDetails ? String(localized: "Checking") : String(localized: "Unavailable")
        }
        if siteDetails.environmentUnreadable { return String(localized: "Unreadable") }
        return siteDetails.environmentExists ? String(localized: "Present") : String(localized: "Missing")
    }

    private var logStatusTitle: String {
        guard let siteDetails else { return String(localized: "Checking") }
        guard siteDetails.logFileCount > 0 else { return String(localized: "No log files") }
        if let latest = siteDetails.latestLogName {
            return String.localizedStringWithFormat(
                String(localized: "%lld files, latest: %@"),
                Int64(siteDetails.logFileCount),
                latest
            )
        }
        return String.localizedStringWithFormat(
            String(localized: "%lld log files"),
            Int64(siteDetails.logFileCount)
        )
    }

    private var routeStatusTitle: String {
        guard let siteDetails else { return String(localized: "Checking") }
        guard !siteDetails.routeFileNames.isEmpty else { return String(localized: "No route files") }
        return siteDetails.routeFileNames.joined(separator: ", ")
    }

    private var gitStatusTitle: String {
        guard let siteDetails else { return String(localized: "Checking") }
        guard siteDetails.isGitRepository else { return String(localized: "Not a Git repository") }
        let branch = siteDetails.gitBranch ?? String(localized: "Detached HEAD")
        if siteDetails.gitChangeCount == 0 {
            return String.localizedStringWithFormat(String(localized: "%@, clean"), branch)
        }
        return String.localizedStringWithFormat(
            String(localized: "%@, %lld changes"),
            branch,
            Int64(siteDetails.gitChangeCount)
        )
    }

    private var associatedServicesTitle: String {
        guard let siteDetails else { return String(localized: "Checking") }
        if siteDetails.associatedServices.isEmpty {
            return String(localized: "No associated services")
        }
        return siteDetails.associatedServices.joined(separator: ", ")
    }

    private func phpVersionTitle(for site: SiteProject) -> String {
        let cycle = site.phpVersion ?? model.configuration.selectedPHP
        guard let installed = runtimeCoordinator.phpVersions.first(where: { $0.cycle == cycle })?.installedVersion else {
            return cycle
        }
        return "\(cycle) (\(installed))"
    }

    private func nodeVersionTitle(for site: SiteProject) -> String {
        let runtime: RuntimeVersion?
        if let cycle = site.nodeVersion {
            runtime = runtimeCoordinator.nodeVersions.first(where: { $0.cycle == cycle })
        } else {
            runtime =
                runtimeCoordinator.nodeVersions.first(where: \.isActive)
                ?? runtimeCoordinator.nodeVersions.first(where: \.isInstalled)
        }
        return runtime?.installedVersion ?? site.nodeVersion ?? String(localized: "Project Default")
    }

    private func refreshSelectedSiteDetails() {
        guard let site = sitesCoordinator.selectedSite(identifier: navigation.selectedSiteID) else { return }
        Task { await loadSiteDetails(for: site) }
    }

    @MainActor
    private func loadSiteDetails(for site: SiteProject) async {
        isLoadingSiteDetails = true
        let instances = model.configuration.serviceInstances
        let snapshot = await Task.detached(priority: .utility) {
            SiteDetailsInspector.inspect(site: site, services: instances)
        }.value
        guard !Task.isCancelled, navigation.selectedSiteID == nil || navigation.selectedSiteID == site.id else {
            return
        }
        siteDetails = snapshot
        isLoadingSiteDetails = false
    }
}

struct SiteDetailsSnapshot: Sendable {
    let environmentExists: Bool
    let environmentUnreadable: Bool
    let logFileCount: Int
    let latestLogName: String?
    let routeFileNames: [String]
    let isGitRepository: Bool
    let gitBranch: String?
    let gitChangeCount: Int
    let associatedServices: [String]
}

struct SiteGitSnapshot: Sendable, Equatable {
    let isRepository: Bool
    let branch: String?
    let changeCount: Int

    static let unavailable = SiteGitSnapshot(
        isRepository: false,
        branch: nil,
        changeCount: 0
    )
}

enum SiteDetailsInspector {
    private static let maximumEnvironmentBytes = 4 * 1_024 * 1_024

    static func inspect(site: SiteProject, services: [ServiceInstance]) -> SiteDetailsSnapshot {
        let environment = inspectEnvironment(at: site.path.appendingPathComponent(".env"))
        let logs = inspectLogs(at: site.path.appendingPathComponent("storage/logs", isDirectory: true))
        let routes = inspectRoutes(at: site.path.appendingPathComponent("routes", isDirectory: true))
        let git = inspectGit(at: site.path)
        let associatedServices = services.compactMap { service -> String? in
            guard matches(service: service, environment: environment.values) else { return nil }
            return "\(service.name) (\(service.port))"
        }
        return SiteDetailsSnapshot(
            environmentExists: environment.exists,
            environmentUnreadable: environment.unreadable,
            logFileCount: logs.count,
            latestLogName: logs.latest,
            routeFileNames: routes,
            isGitRepository: git.isRepository,
            gitBranch: git.branch,
            gitChangeCount: git.changeCount,
            associatedServices: associatedServices
        )
    }

    private static func inspectEnvironment(at url: URL) -> (
        exists: Bool,
        unreadable: Bool,
        values: [String: String]
    ) {
        let fileManager = FileManager.default
        guard fileManager.fileExists(atPath: url.path) else { return (false, false, [:]) }
        do {
            let values = try url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey])
            guard values.isRegularFile == true, values.isSymbolicLink != true,
                (values.fileSize ?? 0) <= maximumEnvironmentBytes
            else { return (true, true, [:]) }
            let contents = try String(contentsOf: url, encoding: .utf8)
            var environment: [String: String] = [:]
            for line in contents.split(whereSeparator: \.isNewline) {
                let trimmed = line.trimmingCharacters(in: .whitespaces)
                guard !trimmed.isEmpty, !trimmed.hasPrefix("#"),
                    let separator = trimmed.firstIndex(of: "=")
                else { continue }
                let key = String(trimmed[..<separator]).trimmingCharacters(in: .whitespaces)
                var value = String(trimmed[trimmed.index(after: separator)...])
                    .trimmingCharacters(in: .whitespaces)
                if value.count >= 2,
                    value.hasPrefix("\"") && value.hasSuffix("\"")
                        || value.hasPrefix("'") && value.hasSuffix("'")
                {
                    value.removeFirst()
                    value.removeLast()
                }
                if !key.isEmpty { environment[key] = value }
            }
            return (true, false, environment)
        } catch {
            return (true, true, [:])
        }
    }

    private static func inspectLogs(at directory: URL) -> (count: Int, latest: String?) {
        guard
            let files = try? FileManager.default.contentsOfDirectory(
                at: directory,
                includingPropertiesForKeys: [.isRegularFileKey, .contentModificationDateKey],
                options: [.skipsHiddenFiles]
            )
        else { return (0, nil) }
        let logs = files.compactMap { url -> (URL, Date)? in
            guard let values = try? url.resourceValues(forKeys: [.isRegularFileKey, .contentModificationDateKey]),
                values.isRegularFile == true
            else { return nil }
            return (url, values.contentModificationDate ?? .distantPast)
        }
        return (logs.count, logs.max(by: { $0.1 < $1.1 })?.0.lastPathComponent)
    }

    private static func inspectRoutes(at directory: URL) -> [String] {
        guard
            let files = try? FileManager.default.contentsOfDirectory(
                at: directory,
                includingPropertiesForKeys: [.isRegularFileKey],
                options: [.skipsHiddenFiles]
            )
        else { return [] }
        return files.filter { url in
            url.pathExtension.lowercased() == "php"
                && (try? url.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) == true
        }.map(\.lastPathComponent).sorted()
    }

    static func inspectGit(for sites: [SiteProject]) async -> [String: SiteGitSnapshot] {
        await withTaskGroup(of: (String, SiteGitSnapshot).self) { group in
            var nextIndex = 0
            let workerCount = min(4, sites.count)
            for _ in 0..<workerCount {
                let site = sites[nextIndex]
                nextIndex += 1
                group.addTask(priority: .utility) {
                    (site.id, inspectGit(at: site.path))
                }
            }

            var snapshots: [String: SiteGitSnapshot] = [:]
            while let result = await group.next() {
                snapshots[result.0] = result.1
                if nextIndex < sites.count {
                    let site = sites[nextIndex]
                    nextIndex += 1
                    group.addTask(priority: .utility) {
                        (site.id, inspectGit(at: site.path))
                    }
                }
            }
            return snapshots
        }
    }

    static func inspectGit(at directory: URL) -> SiteGitSnapshot {
        let git = URL(fileURLWithPath: "/usr/bin/git")
        guard FileManager.default.isExecutableFile(atPath: git.path),
            let result = try? ProcessRunner.run(
                git,
                arguments: ["-C", directory.path, "status", "--porcelain=v1", "--branch"],
                timeout: 5
            ),
            result.status == 0
        else { return .unavailable }
        return parseGitStatus(result.output)
    }

    static func parseGitStatus(_ output: String) -> SiteGitSnapshot {
        let lines = output.split(whereSeparator: \.isNewline).map(String.init)
        let header = lines.first(where: { $0.hasPrefix("## ") })
        let branch = header.flatMap { line -> String? in
            let value = String(line.dropFirst(3))
            for prefix in ["No commits yet on ", "Initial commit on "]
            where value.hasPrefix(prefix) {
                return String(value.dropFirst(prefix.count))
            }
            if value.hasPrefix("HEAD ") { return nil }
            return value.components(separatedBy: "...").first ?? value
        }
        return SiteGitSnapshot(
            isRepository: true,
            branch: branch,
            changeCount: lines.filter { !$0.hasPrefix("## ") }.count
        )
    }

    private static func matches(service: ServiceInstance, environment: [String: String]) -> Bool {
        let port = String(service.port)
        switch service.definitionID {
        case "mysql", "mariadb":
            return environment["DB_PORT"] == port && environment["DB_CONNECTION"] == "mysql"
        case "postgresql":
            return environment["DB_PORT"] == port && environment["DB_CONNECTION"] == "pgsql"
        case "mongodb":
            return environment["MONGODB_URI"]?.contains(":\(port)") == true
        case "redis", "valkey":
            return environment["REDIS_PORT"] == port
        case "meilisearch":
            return environment["MEILISEARCH_HOST"]?.contains(":\(port)") == true
        case "typesense":
            return environment["TYPESENSE_PORT"] == port
        case "minio", "rustfs":
            return environment["AWS_ENDPOINT"]?.contains(":\(port)") == true
        default:
            return false
        }
    }
}

private struct SiteEnvironmentEditor: View {
    @Environment(\.dismiss) private var dismiss
    let site: SiteProject
    @State private var document: ProjectEnvironmentDocument?
    @State private var contents = ""
    @State private var statusMessage = ""
    @State private var errorMessage: String?
    @State private var isLoading = true
    @State private var isSaving = false
    @State private var confirmingClose = false
    @State private var confirmingReload = false

    private var isDirty: Bool {
        guard let document else { return false }
        return document.contents != contents
    }

    private var environmentPath: String {
        site.path.appendingPathComponent(".env").path
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            VStack(alignment: .leading, spacing: 4) {
                Text("Environment File")
                    .font(.title2.weight(.semibold))
                Text(site.name)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                Text(environmentPath)
                    .font(.caption.monospaced())
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .textSelection(.enabled)
            }

            HStack(spacing: 8) {
                if isLoading || isSaving {
                    ProgressView()
                        .controlSize(.small)
                }
                Text(statusMessage)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Spacer()
                if isDirty {
                    Text("Unsaved")
                        .font(.caption.weight(.medium))
                        .foregroundStyle(.orange)
                }
            }
            .frame(minHeight: 18)

            TextEditor(text: $contents)
                .font(.system(.body, design: .monospaced))
                .scrollContentBackground(.hidden)
                .padding(8)
                .background(Color(nsColor: .textBackgroundColor))
                .overlay {
                    RoundedRectangle(cornerRadius: 6)
                        .stroke(Color(nsColor: .separatorColor), lineWidth: 1)
                }
                .clipShape(RoundedRectangle(cornerRadius: 6))
                .disabled(isLoading || isSaving || document == nil)
                .accessibilityLabel("Environment file contents")

            if let errorMessage {
                Label(errorMessage, systemImage: "exclamationmark.triangle.fill")
                    .font(.caption)
                    .foregroundStyle(.red)
                    .textSelection(.enabled)
            }

            HStack(spacing: 8) {
                Button("Reload") {
                    if isDirty {
                        confirmingReload = true
                    } else {
                        Task { await load() }
                    }
                }
                .disabled(isLoading || isSaving)
                Spacer()
                Button(isDirty ? "Cancel" : "Done") {
                    if isDirty {
                        confirmingClose = true
                    } else {
                        dismiss()
                    }
                }
                Button("Save") {
                    Task { await save() }
                }
                .buttonStyle(.borderedProminent)
                .disabled(!isDirty || isLoading || isSaving)
            }
        }
        .padding(20)
        .frame(minWidth: 720, minHeight: 520)
        .interactiveDismissDisabled(isDirty)
        .task(id: site.id) {
            await load()
        }
        .alert("Discard unsaved changes?", isPresented: $confirmingClose) {
            Button("Keep Editing", role: .cancel) {}
            Button("Discard", role: .destructive) { dismiss() }
        } message: {
            Text("Your edits will be lost.")
        }
        .alert("Discard unsaved changes?", isPresented: $confirmingReload) {
            Button("Keep Editing", role: .cancel) {}
            Button("Discard and Reload", role: .destructive) {
                Task { await load() }
            }
        } message: {
            Text("Your edits will be lost.")
        }
    }

    @MainActor
    private func load() async {
        isLoading = true
        errorMessage = nil
        do {
            let projectURL = site.path
            let loaded = try await Task.detached(priority: .userInitiated) {
                try ProjectEnvironmentFile.load(projectURL: projectURL)
            }.value
            try Task.checkCancellation()
            document = loaded
            contents = loaded.contents
            if loaded.loadedFromExample {
                statusMessage = String(localized: "Loaded from .env.example. Save to create .env.")
            } else if loaded.exists {
                statusMessage = String(localized: "Loaded .env")
            } else {
                statusMessage = String(localized: ".env does not exist. Save to create it.")
            }
        } catch is CancellationError {
        } catch {
            document = nil
            contents = ""
            statusMessage = ""
            errorMessage = error.localizedDescription
        }
        isLoading = false
    }

    @MainActor
    private func save() async {
        guard let document, isDirty else { return }
        isSaving = true
        errorMessage = nil
        do {
            let projectURL = site.path
            let editedContents = contents
            let expectedRevision = document.revision
            let saved = try await Task.detached(priority: .userInitiated) {
                try ProjectEnvironmentFile.save(
                    editedContents,
                    projectURL: projectURL,
                    expectedRevision: expectedRevision
                )
            }.value
            try Task.checkCancellation()
            self.document = saved
            contents = saved.contents
            statusMessage = String(localized: "Saved .env")
        } catch is CancellationError {
        } catch {
            errorMessage = error.localizedDescription
        }
        isSaving = false
    }
}

private struct ArtisanRunnerView: View {
    private static let maximumVisibleOutputBytes = 1 * 1_024 * 1_024

    @EnvironmentObject private var model: AppModel
    let site: SiteProject
    @State private var selectedPresetID = "route-list"
    @State private var customCommand = ""
    @State private var output = ""
    @State private var status = "Ready"
    @State private var isRunning = false
    @State private var cancellation: ArtisanCancellation?
    @State private var commandTask: Task<Void, Never>?

    private var isCustomCommand: Bool { selectedPresetID == "custom" }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            VStack(alignment: .leading, spacing: 3) {
                Text("Artisan")
                    .font(.title2.weight(.semibold))
                Text(site.name)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }

            HStack(alignment: .bottom, spacing: 10) {
                Picker("Command", selection: $selectedPresetID) {
                    ForEach(ArtisanCommandPreset.all) { preset in
                        Text(preset.title).tag(preset.id)
                    }
                }
                .frame(width: 220)
                if isCustomCommand {
                    TextField("route:list --path=api", text: $customCommand)
                        .textFieldStyle(.roundedBorder)
                }
            }

            HStack(spacing: 8) {
                if isRunning {
                    ProgressView()
                        .controlSize(.small)
                }
                Text(status)
                    .font(.callout.weight(.medium))
                    .foregroundStyle(status.hasPrefix("Failed") ? Color.red : Color.secondary)
                Spacer()
            }
            .frame(height: 20)

            ScrollView {
                Text(output.isEmpty ? "Output will appear here." : output)
                    .font(.system(.body, design: .monospaced))
                    .foregroundStyle(output.isEmpty ? Color.secondary : Color.primary)
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .topLeading)
                    .padding(10)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(Color(nsColor: .textBackgroundColor))
            .overlay {
                RoundedRectangle(cornerRadius: 6)
                    .stroke(Color(nsColor: .separatorColor), lineWidth: 1)
            }
            .clipShape(RoundedRectangle(cornerRadius: 6))

            HStack {
                Spacer()
                Button {
                    cancellation?.cancel()
                    status = "Cancelling"
                } label: {
                    Label("Cancel", systemImage: "xmark")
                }
                .disabled(!isRunning)
                Button {
                    runCommand()
                } label: {
                    Label("Run", systemImage: "play.fill")
                }
                .buttonStyle(.borderedProminent)
                .disabled(
                    isRunning
                        || (isCustomCommand
                            && customCommand.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                )
            }
        }
        .padding(18)
        .frame(minWidth: 680, minHeight: 460)
        .interactiveDismissDisabled(isRunning)
        .onDisappear {
            cancellation?.cancel()
            commandTask?.cancel()
        }
    }

    private func runCommand() {
        let invocation: ArtisanInvocation
        do {
            invocation = try model.artisanInvocation(
                for: site,
                presetID: selectedPresetID,
                customCommand: customCommand
            )
        } catch {
            status = "Failed"
            output = error.localizedDescription
            return
        }

        let cancellation = ArtisanCancellation()
        self.cancellation = cancellation
        output = ""
        status = "Running"
        isRunning = true
        commandTask = Task {
            do {
                let result = try await ArtisanCommandRunner.run(
                    invocation,
                    cancellation: cancellation
                ) { data in
                    let chunk = String(decoding: data, as: UTF8.self)
                    Task { @MainActor in
                        guard !cancellation.isCancelled else { return }
                        appendOutput(chunk)
                    }
                }
                if output.isEmpty { appendOutput(result.output) }
                status = result.status == 0 ? "Completed" : "Failed (exit \(result.status))"
            } catch let error as ProcessRunnerError {
                switch error {
                case .cancelled(let capturedOutput):
                    if output.isEmpty { appendOutput(capturedOutput) }
                    status = "Cancelled"
                case .timedOut(_, let capturedOutput):
                    if output.isEmpty { appendOutput(capturedOutput) }
                    status = "Timed out"
                }
            } catch is CancellationError {
                status = "Cancelled"
            } catch {
                status = "Failed"
                appendOutput(error.localizedDescription)
            }
            isRunning = false
            self.cancellation = nil
            commandTask = nil
        }
    }

    private func appendOutput(_ value: String) {
        guard !value.isEmpty else { return }
        output.append(contentsOf: value)
        let data = Data(output.utf8)
        if data.count > Self.maximumVisibleOutputBytes {
            output = String(decoding: data.suffix(Self.maximumVisibleOutputBytes), as: UTF8.self)
        }
    }
}

private struct NPMScriptRunnerView: View {
    private static let maximumVisibleOutputBytes = 1 * 1_024 * 1_024

    @EnvironmentObject private var model: AppModel
    let site: SiteProject
    @State private var scripts: [NPMScript] = []
    @State private var selectedScriptName = ""
    @State private var output = ""
    @State private var status = String(localized: "Loading npm scripts")
    @State private var isLoading = true
    @State private var isRunning = false
    @State private var hasFailure = false
    @State private var cancellation: NPMScriptCancellation?
    @State private var commandTask: Task<Void, Never>?

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            VStack(alignment: .leading, spacing: 3) {
                Text("npm Scripts")
                    .font(.title2.weight(.semibold))
                Text(site.name)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }

            HStack(alignment: .bottom, spacing: 10) {
                Picker("Script", selection: $selectedScriptName) {
                    ForEach(scripts) { script in
                        Text(script.name).tag(script.name)
                    }
                }
                .frame(maxWidth: .infinity)
                .disabled(isLoading || isRunning || scripts.isEmpty)

                Button {
                    loadScripts()
                } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .disabled(isLoading || isRunning)
                .help("Reload npm scripts")
                .accessibilityLabel("Reload npm scripts")
            }

            HStack(spacing: 8) {
                if isLoading || isRunning {
                    ProgressView()
                        .controlSize(.small)
                }
                Text(status)
                    .font(.callout.weight(.medium))
                    .foregroundStyle(hasFailure ? Color.red : Color.secondary)
                Spacer()
            }
            .frame(height: 20)

            ScrollView {
                Text(output.isEmpty ? String(localized: "Output will appear here.") : output)
                    .font(.system(.body, design: .monospaced))
                    .foregroundStyle(output.isEmpty ? Color.secondary : Color.primary)
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .topLeading)
                    .padding(10)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(Color(nsColor: .textBackgroundColor))
            .overlay {
                RoundedRectangle(cornerRadius: 6)
                    .stroke(Color(nsColor: .separatorColor), lineWidth: 1)
            }
            .clipShape(RoundedRectangle(cornerRadius: 6))

            HStack {
                Spacer()
                Button {
                    cancellation?.cancel()
                    status = String(localized: "Cancelling")
                } label: {
                    Label("Cancel", systemImage: "xmark")
                }
                .disabled(!isRunning)
                Button {
                    runScript()
                } label: {
                    Label("Run", systemImage: "play.fill")
                }
                .buttonStyle(.borderedProminent)
                .disabled(isLoading || isRunning || selectedScriptName.isEmpty)
            }
        }
        .padding(18)
        .frame(minWidth: 680, minHeight: 460)
        .interactiveDismissDisabled(isRunning)
        .onAppear(perform: loadScripts)
        .onDisappear {
            cancellation?.cancel()
            commandTask?.cancel()
        }
    }

    private func loadScripts() {
        guard !isRunning else { return }
        isLoading = true
        hasFailure = false
        status = String(localized: "Loading npm scripts")
        do {
            let discovered = try NPMScriptCatalog.scripts(in: site.path)
            scripts = discovered
            if !discovered.contains(where: { $0.name == selectedScriptName }) {
                selectedScriptName = discovered[0].name
            }
            status = String(localized: "Ready")
            output = ""
        } catch {
            scripts = []
            selectedScriptName = ""
            status = String(localized: "Unavailable")
            output = error.localizedDescription
            hasFailure = true
        }
        isLoading = false
    }

    private func runScript() {
        let invocation: NPMScriptInvocation
        do {
            invocation = try model.npmInvocation(for: site, scriptName: selectedScriptName)
        } catch {
            status = String(localized: "Failed")
            output = error.localizedDescription
            hasFailure = true
            return
        }

        let cancellation = NPMScriptCancellation()
        self.cancellation = cancellation
        output = ""
        status = String(localized: "Running")
        hasFailure = false
        isRunning = true
        commandTask = Task {
            do {
                let result = try await NPMScriptRunner.run(
                    invocation,
                    cancellation: cancellation
                ) { data in
                    let chunk = String(decoding: data, as: UTF8.self)
                    Task { @MainActor in
                        guard !cancellation.isCancelled else { return }
                        appendOutput(chunk)
                    }
                }
                if output.isEmpty { appendOutput(result.output) }
                hasFailure = result.status != 0
                status =
                    result.status == 0
                    ? String(localized: "Completed")
                    : String.localizedStringWithFormat(
                        String(localized: "Failed (exit %lld)"),
                        Int64(result.status)
                    )
            } catch let error as ProcessRunnerError {
                switch error {
                case .cancelled(let capturedOutput):
                    if output.isEmpty { appendOutput(capturedOutput) }
                    status = String(localized: "Cancelled")
                case .timedOut(_, let capturedOutput):
                    if output.isEmpty { appendOutput(capturedOutput) }
                    status = String(localized: "Timed out")
                    hasFailure = true
                }
            } catch is CancellationError {
                status = String(localized: "Cancelled")
            } catch {
                status = String(localized: "Failed")
                appendOutput(error.localizedDescription)
                hasFailure = true
            }
            isRunning = false
            self.cancellation = nil
            commandTask = nil
        }
    }

    private func appendOutput(_ value: String) {
        guard !value.isEmpty else { return }
        output.append(contentsOf: value)
        let data = Data(output.utf8)
        if data.count > Self.maximumVisibleOutputBytes {
            output = String(decoding: data.suffix(Self.maximumVisibleOutputBytes), as: UTF8.self)
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

        configurePreview()
    }

    required init?(coder: NSCoder) {
        webView = WKWebView(
            frame: NSRect(origin: .zero, size: Self.desktopViewport),
            configuration: WKWebViewConfiguration()
        )
        super.init(coder: coder)

        configurePreview()
    }

    private func configurePreview() {
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
