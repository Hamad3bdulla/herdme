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
    @Environment(\.layoutDirection) private var layoutDirection
    @State private var search = ""
    @State private var tab = SiteTab.general
    @State private var showPreview = true
    @State private var artisanSite: SiteProject?
    @State private var npmSite: SiteProject?
    @State private var toolsSite: SiteProject?
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
        .sheet(item: $toolsSite, onDismiss: refreshSelectedSiteDetails) { site in
            SiteControlCenterView(site: site, siteTools: model.siteTools)
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
            Image(systemName: layoutDirection == .rightToLeft ? "sidebar.right" : "sidebar.left")
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
                    ? environmentCoordinator.isHTTPSActive
                        ? String(localized: "Running, HTTPS active")
                        : String(localized: "Running, HTTP only")
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
            .help(environmentToggleTitle)
            .accessibilityLabel(environmentToggleTitle)
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
                List(selection: $navigation.selectedSiteID) {
                    Section("Ungrouped") {
                        ForEach(filteredSites) { site in
                            HStack(spacing: 10) {
                                ZStack {
                                    RoundedRectangle(cornerRadius: 6)
                                        .fill(Color.accentColor.opacity(0.10))
                                    Image(systemName: frameworkSymbol(for: site.framework))
                                        .font(.system(size: 14, weight: .medium))
                                        .foregroundStyle(Color.accentColor)
                                }
                                .frame(width: 32, height: 32)
                                .accessibilityHidden(true)
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(site.domain(tld: model.configuration.tld))
                                        .font(.callout.weight(.medium))
                                        .lineLimit(1)
                                    HStack(spacing: 6) {
                                        Text(site.framework)
                                            .lineLimit(1)
                                        if let gitTitle = gitListStatusTitle(for: site) {
                                            Spacer(minLength: 2)
                                            HStack(spacing: 3) {
                                                Image(systemName: "arrow.triangle.branch")
                                                Text(gitTitle)
                                                    .lineLimit(1)
                                            }
                                        }
                                    }
                                    .font(.caption2)
                                    .foregroundStyle(.secondary)
                                }
                                Spacer(minLength: 4)
                                Circle()
                                    .fill(siteStatusColor(for: site))
                                    .frame(width: 7, height: 7)
                                    .help(siteStatusTitle(for: site))
                            }
                            .padding(.vertical, 5)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .contentShape(Rectangle())
                            .tag(site.id)
                            .accessibilityLabel(site.domain(tld: model.configuration.tld))
                            .accessibilityValue(
                                "PHP \(site.phpVersion ?? model.configuration.selectedPHP), \(siteStatusTitle(for: site))"
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
                siteCommandBar(site)
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
                            .accessibilityAddTraits(tab == item ? .isSelected : [])
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
                Toggle("Live Preview", isOn: previewBinding)
                    .toggleStyle(.switch)
                    .controlSize(.small)
                    .fixedSize()
                    .accessibilityIdentifier("sites.preview.toggle")
            }

            HStack(spacing: 6) {
                siteMetadataBadge(site.framework, systemImage: frameworkSymbol(for: site.framework))
                siteMetadataBadge(
                    "PHP \(site.phpVersion ?? model.configuration.selectedPHP)",
                    systemImage: "chevron.left.forwardslash.chevron.right"
                )
                siteMetadataBadge(
                    String.localizedStringWithFormat(
                        String(localized: "Node %@"),
                        site.nodeVersion ?? String(localized: "Project")
                    ),
                    systemImage: "hexagon"
                )
                Spacer(minLength: 4)
            }

        }
        .padding(16)
        .accessibilityElement(children: .contain)
    }

    private var previewBinding: Binding<Bool> {
        Binding(
            get: { showPreview },
            set: { value in
                showPreview = value
                model.configuration.sitePreviews = value
                model.persist()
            }
        )
    }

    private var environmentToggleTitle: String {
        environmentCoordinator.status == .running
            ? String(localized: "Stop all sites")
            : String(localized: "Start all sites")
    }

    private func siteCommandBar(_ site: SiteProject) -> some View {
        HStack(spacing: 8) {
            compactSiteAction(
                "Open in Browser",
                systemImage: "safari",
                accessibilityIdentifier: "sites.command.open"
            ) {
                model.openSite(site)
            }
            Menu {
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
            } label: {
                Image(systemName: "doc.on.doc")
                    .frame(width: 18, height: 18)
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .help("Copy site link or path")
            .accessibilityLabel("Copy site link or path")
            .accessibilityIdentifier("sites.command.copy")
            compactSiteAction(
                "Open Project Folder",
                systemImage: "folder",
                accessibilityIdentifier: "sites.command.folder"
            ) {
                NSWorkspace.shared.open(site.path)
            }
            compactSiteAction(
                "Open Terminal",
                systemImage: "terminal",
                accessibilityIdentifier: "sites.command.terminal"
            ) {
                model.openTerminal(for: site)
            }
            compactSiteAction(
                "Edit .env",
                systemImage: "pencil",
                accessibilityIdentifier: "sites.command.environment"
            ) {
                environmentSite = site
            }
            compactSiteAction(
                "Site Tools",
                systemImage: "wrench.and.screwdriver",
                accessibilityIdentifier: "sites.command.tools"
            ) {
                toolsSite = site
            }
            Spacer(minLength: 8)
            siteMoreActions(site)
        }
        .padding(.horizontal, 16)
        .frame(height: 46)
        .background(Color(nsColor: .controlBackgroundColor).opacity(0.55))
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
        accessibilityIdentifier: String,
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
        .accessibilityIdentifier(accessibilityIdentifier)
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
        .accessibilityIdentifier("sites.command.more")
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
            toolsSite = site
        } label: {
            Label("Site Tools", systemImage: "wrench.and.screwdriver")
        }
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
            model.lastError = String(localized: "The site does not have an active local address.")
            return
        }
        copyToPasteboard(url.absoluteString)
    }

    private func copyToPasteboard(_ value: String) {
        NSPasteboard.general.clearContents()
        guard NSPasteboard.general.setString(value, forType: .string) else {
            model.lastError = String(localized: "HerdMe could not copy the value.")
            return
        }
    }

    private func siteGeneral(_ site: SiteProject) -> some View {
        ScrollView {
            VStack(spacing: 16) {
                if site.framework == "Laravel" {
                    SiteBackgroundControls(
                        site: site,
                        siteTools: model.siteTools,
                        defaultPHP: model.configuration.selectedPHP
                    ) {
                        toolsSite = site
                    }
                }
                Group {
                    if showPreview {
                        SiteWebPreview(
                            url: environmentCoordinator.status == .running
                                ? model.sitePreviewURL(for: site)
                                : nil,
                            isStarting: environmentCoordinator.status == .starting,
                            onStartEnvironment: { model.toggleEnvironment() },
                            onOpenSite: { model.openSite(site) },
                            onOpenLogs: { model.showLogs(for: site) }
                        )
                    } else {
                        Image(systemName: "globe")
                            .font(.system(size: 44))
                            .foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                            .frame(height: 240)
                    }
                }
                .frame(maxWidth: .infinity)
                .background(Color(nsColor: .textBackgroundColor))
                .clipShape(RoundedRectangle(cornerRadius: 8))
                .overlay { RoundedRectangle(cornerRadius: 8).stroke(.separator) }

                SettingsPanel {
                    VStack(spacing: 9) {
                        SettingRow("PHP") {
                            Picker(
                                "PHP",
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
                                "Node",
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
    @State private var status = String(localized: "Ready")
    @State private var hasFailure = false
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
            status = String(localized: "Failed")
            output = error.localizedDescription
            hasFailure = true
            return
        }

        let cancellation = ArtisanCancellation()
        self.cancellation = cancellation
        output = ""
        status = String(localized: "Running")
        hasFailure = false
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
                hasFailure = true
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

private struct SiteBackgroundControls: View {
    let site: SiteProject
    @ObservedObject var siteTools: SiteToolsCoordinator
    let defaultPHP: String
    let showTools: () -> Void

    var body: some View {
        HStack(spacing: 8) {
            ForEach(SiteBackgroundProcessKind.allCases, id: \.rawValue) { kind in
                let snapshot = siteTools.backgroundProcess(for: site, kind: kind)
                Button {
                    siteTools.toggleBackgroundProcess(for: site, kind: kind, defaultPHP: defaultPHP)
                } label: {
                    Label(
                        backgroundActionTitle(kind, isActive: snapshot?.isActive == true),
                        systemImage: snapshot?.isActive == true ? "stop.fill" : kind.systemImage
                    )
                }
                .buttonStyle(.bordered)
                .tint(snapshot?.isActive == true ? .orange : .accentColor)
            }
            Spacer()
            Button(action: showTools) {
                Label("Output and Tools", systemImage: "terminal")
            }
            .buttonStyle(.borderless)
        }
        .padding(.horizontal, 2)
    }

    private func backgroundActionTitle(_ kind: SiteBackgroundProcessKind, isActive: Bool) -> String {
        String.localizedStringWithFormat(
            isActive ? String(localized: "Stop %@") : String(localized: "Start %@"),
            kind.title
        )
    }
}

private struct SiteControlCenterView: View {
    private enum Section: String, CaseIterable {
        case commands
        case database
        case processes
        case workflows
        case health

        var title: String {
            switch self {
            case .commands: String(localized: "Commands")
            case .database: String(localized: "Database")
            case .processes: String(localized: "Background Processes")
            case .workflows: String(localized: "Automations")
            case .health: String(localized: "Site Health")
            }
        }
    }

    private struct ComposerOption: Identifiable {
        let id: String
        let title: String
    }

    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss
    let site: SiteProject
    @ObservedObject var siteTools: SiteToolsCoordinator
    @State private var section = Section.commands
    @State private var composerOption = "install"
    @State private var composerPackage = ""
    @State private var favorites: [SiteCommandFavorite] = []
    @State private var selectedFavoriteID: UUID?
    @State private var healthReport: SiteHealthReport?
    @State private var selectedDatabaseID: UUID?
    @State private var databaseProvisioning: SiteDatabaseProvisioning?
    @State private var databaseInspection: SiteDatabaseInspection?
    @State private var isLoadingHealth = false
    @State private var isRunning = false
    @State private var status = String(localized: "Ready")
    @State private var output = ""
    @State private var cancellation: SiteOperationCancellation?
    @State private var operationTask: Task<Void, Never>?
    @State private var pendingWorkflow: SiteWorkflowOperation?
    @State private var artifactURL: URL?

    private let composerOptions = [
        ComposerOption(id: "install", title: String(localized: "Install dependencies")),
        ComposerOption(id: "update", title: String(localized: "Update dependencies")),
        ComposerOption(id: "dump-autoload", title: String(localized: "Optimize autoloader")),
        ComposerOption(id: "audit", title: String(localized: "Security audit")),
        ComposerOption(id: "require", title: String(localized: "Require package"))
    ]

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 12) {
                VStack(alignment: .leading, spacing: 3) {
                    Text("Site Tools")
                        .font(.title2.weight(.semibold))
                    Text(site.name)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Button("Done") { dismiss() }
                    .keyboardShortcut(.cancelAction)
                    .disabled(isRunning)
            }
            .padding(20)

            Picker("Site tools section", selection: $section) {
                ForEach(Section.allCases, id: \.self) { item in Text(item.title).tag(item) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .padding(.horizontal, 20)
            .padding(.bottom, 14)

            Divider()
            ScrollView {
                VStack(spacing: 16) {
                    switch section {
                    case .commands: commandsSection
                    case .database: databaseSection
                    case .processes: processesSection
                    case .workflows: workflowsSection
                    case .health: healthSection
                    }
                    operationOutput
                }
                .padding(20)
            }
        }
        .frame(minWidth: 760, minHeight: 620)
        .interactiveDismissDisabled(isRunning)
        .task(id: site.id) {
            loadFavorites()
            selectedDatabaseID = databaseServices.first?.id
            await loadHealth()
        }
        .onDisappear {
            cancellation?.cancel()
            operationTask?.cancel()
        }
        .alert(
            pendingWorkflow?.title ?? String(localized: "Confirm Operation"),
            isPresented: Binding(
                get: { pendingWorkflow != nil },
                set: { if !$0 { pendingWorkflow = nil } }
            )
        ) {
            Button("Cancel", role: .cancel) { pendingWorkflow = nil }
            Button("Continue", role: pendingWorkflow?.isDestructive == true ? .destructive : nil) {
                guard let operation = pendingWorkflow else { return }
                pendingWorkflow = nil
                runWorkflow(operation)
            }
        } message: {
            Text(workflowConfirmationMessage)
        }
    }

    private var commandsSection: some View {
        SettingsPanel {
            VStack(alignment: .leading, spacing: 14) {
                HStack {
                    Label("Composer", systemImage: "shippingbox")
                        .font(.headline)
                    Spacer()
                    Button {
                        runComposer()
                    } label: {
                        Label("Run", systemImage: "play.fill")
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(
                        isRunning
                            || (composerOption == "require" && composerPackage.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty))
                }
                HStack(spacing: 10) {
                    Picker("Command", selection: $composerOption) {
                        ForEach(composerOptions) { option in Text(option.title).tag(option.id) }
                    }
                    .frame(width: 220)
                    if composerOption == "require" {
                        TextField("vendor/package", text: $composerPackage)
                            .textFieldStyle(.roundedBorder)
                    }
                }
                HStack(spacing: 8) {
                    Picker("Favorites", selection: $selectedFavoriteID) {
                        Text("Command Favorites").tag(UUID?.none)
                        ForEach(favorites) { favorite in Text(favorite.command).tag(Optional(favorite.id)) }
                    }
                    .onChange(of: selectedFavoriteID) { id in applyFavorite(id) }
                    Button {
                        saveFavorite()
                    } label: {
                        Image(systemName: "star")
                    }
                    .help("Save command favorite")
                    .accessibilityLabel("Save command favorite")
                    Button {
                        removeFavorite()
                    } label: {
                        Image(systemName: "trash")
                    }
                    .disabled(selectedFavoriteID == nil)
                    .help("Delete command favorite")
                    .accessibilityLabel("Delete command favorite")
                }
            }
            .padding(16)
        }
    }

    private var processesSection: some View {
        SettingsPanel {
            VStack(spacing: 0) {
                ForEach(Array(SiteBackgroundProcessKind.allCases.enumerated()), id: \.element.rawValue) { index, kind in
                    if index > 0 { PanelDivider() }
                    let snapshot = siteTools.backgroundProcess(for: site, kind: kind)
                    SettingRow(LocalizedStringKey(kind.title)) {
                        HStack(spacing: 10) {
                            Circle()
                                .fill(backgroundColor(snapshot))
                                .frame(width: 7, height: 7)
                            Text(backgroundStatus(snapshot))
                                .foregroundStyle(.secondary)
                            Button {
                                siteTools.toggleBackgroundProcess(
                                    for: site,
                                    kind: kind,
                                    defaultPHP: model.configuration.selectedPHP
                                )
                            } label: {
                                Label(
                                    snapshot?.isActive == true ? "Stop" : "Start",
                                    systemImage: snapshot?.isActive == true ? "stop.fill" : "play.fill")
                            }
                            .buttonStyle(.bordered)
                            .tint(snapshot?.isActive == true ? .orange : .accentColor)
                        }
                    }
                    if let snapshot, !snapshot.output.isEmpty {
                        Text(snapshot.output)
                            .font(.system(.caption, design: .monospaced))
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.vertical, 10)
                    }
                }
            }
            .padding(16)
        }
    }

    private var databaseSection: some View {
        VStack(spacing: 14) {
            SettingsPanel {
                VStack(spacing: 12) {
                    SettingRow("Database Service") {
                        Picker("Database", selection: $selectedDatabaseID) {
                            Text("Select a running service").tag(Optional<UUID>.none)
                            ForEach(databaseServices) { instance in
                                Text(verbatim: "\(instance.name) :\(instance.port)").tag(Optional(instance.id))
                            }
                        }
                        .labelsHidden()
                        .frame(width: 250)
                    }
                    PanelDivider()
                    SettingRow("Site Database") {
                        Text(databaseProvisioning?.databaseName ?? String(localized: "Not configured"))
                            .foregroundStyle(databaseProvisioning == nil ? Color.secondary : Color.primary)
                            .textSelection(.enabled)
                    }
                    if let inspection = databaseInspection {
                        PanelDivider()
                        SettingRow("Server Version") { Text(inspection.serverVersion).textSelection(.enabled) }
                        PanelDivider()
                        SettingRow("Tables") { Text(inspection.tableCount.formatted()).monospacedDigit() }
                        PanelDivider()
                        SettingRow("Database Size") { Text(formattedBytes(inspection.sizeBytes)).monospacedDigit() }
                        PanelDivider()
                        SettingRow("Response Time") { Text(String(format: "%.1f ms", inspection.responseMilliseconds)).monospacedDigit() }
                    }
                }
                .padding(16)
            }
            HStack(spacing: 8) {
                Button {
                    provisionDatabase()
                } label: {
                    Label(databaseProvisioning == nil ? "Create and Connect" : "Repair Connection", systemImage: "cylinder.split.1x2")
                }
                .buttonStyle(.borderedProminent)
                .disabled(isRunning || selectedDatabaseID == nil)
                Button {
                    inspectDatabase()
                } label: {
                    Label("Inspect", systemImage: "waveform.path.ecg")
                }
                .disabled(isRunning || databaseProvisioning == nil)
                Button {
                    backupDatabase()
                } label: {
                    Label("Back Up", systemImage: "externaldrive.badge.timemachine")
                }
                .disabled(isRunning || databaseProvisioning == nil)
                Spacer()
                Button {
                    openDatabase()
                } label: {
                    Label("Open in TablePlus", systemImage: "arrow.up.right.square")
                }
                .disabled(databaseProvisioning == nil)
            }
        }
    }

    private var workflowsSection: some View {
        SettingsPanel {
            VStack(spacing: 0) {
                ForEach(Array(SiteWorkflowOperation.allCases.filter { $0 != .repair }.enumerated()), id: \.element.id) { index, operation in
                    if index > 0 { PanelDivider() }
                    HStack(spacing: 12) {
                        Image(systemName: operation.systemImage)
                            .foregroundStyle(operation.isDestructive ? Color.red : Color.accentColor)
                            .frame(width: 24)
                        Text(operation.title)
                            .font(.callout.weight(.medium))
                        Spacer()
                        Button("Run") { requestWorkflow(operation) }
                            .disabled(isRunning || (operation == .reset && site.framework != "Laravel"))
                    }
                    .padding(.vertical, 11)
                }
            }
            .padding(.horizontal, 16)
        }
    }

    private var healthSection: some View {
        VStack(spacing: 14) {
            HStack {
                VStack(alignment: .leading, spacing: 3) {
                    Text("Site Health")
                        .font(.headline)
                    Text(healthReport?.summary ?? String(localized: "Checking"))
                        .foregroundStyle(.secondary)
                }
                Spacer()
                if isLoadingHealth { ProgressView().controlSize(.small) }
                Button {
                    Task { await loadHealth() }
                } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .disabled(isLoadingHealth || isRunning)
                .help("Refresh site health")
                Button {
                    requestWorkflow(.repair)
                } label: {
                    Label("Repair", systemImage: "wrench.and.screwdriver")
                }
                .buttonStyle(.borderedProminent)
                .disabled(isRunning)
            }
            SettingsPanel {
                VStack(spacing: 0) {
                    ForEach(Array((healthReport?.checks ?? []).enumerated()), id: \.element.id) { index, check in
                        if index > 0 { PanelDivider() }
                        HStack(alignment: .top, spacing: 12) {
                            Image(systemName: check.isHealthy ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                                .foregroundStyle(check.isHealthy ? Color.green : Color.orange)
                            VStack(alignment: .leading, spacing: 3) {
                                Text(check.title).font(.callout.weight(.medium))
                                Text(check.detail).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                            }
                            Spacer()
                        }
                        .padding(.vertical, 10)
                    }
                }
                .padding(.horizontal, 16)
            }
        }
    }

    private var operationOutput: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                if isRunning { ProgressView().controlSize(.small) }
                Text(status)
                    .font(.callout.weight(.medium))
                Spacer()
                if let artifactURL {
                    Button {
                        NSWorkspace.shared.activateFileViewerSelecting([artifactURL])
                    } label: {
                        Label("Reveal", systemImage: "folder")
                    }
                    .buttonStyle(.borderless)
                }
                Button {
                    cancellation?.cancel()
                    operationTask?.cancel()
                    status = String(localized: "Cancelling")
                } label: {
                    Label("Cancel", systemImage: "xmark")
                }
                .disabled(!isRunning)
            }
            ScrollView {
                Text(output.isEmpty ? String(localized: "Output will appear here.") : output)
                    .font(.system(.caption, design: .monospaced))
                    .foregroundStyle(output.isEmpty ? Color.secondary : Color.primary)
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, minHeight: 120, alignment: .topLeading)
                    .padding(10)
            }
            .frame(maxHeight: 210)
            .background(Color(nsColor: .textBackgroundColor))
            .overlay { RoundedRectangle(cornerRadius: 6).stroke(Color(nsColor: .separatorColor)) }
            .clipShape(RoundedRectangle(cornerRadius: 6))
        }
    }

    private var workflowConfirmationMessage: String {
        guard let operation = pendingWorkflow else { return "" }
        switch operation {
        case .reset:
            return String(
                localized: "This creates a backup, then rebuilds and seeds the local database. Existing local data will be replaced.")
        case .update:
            return String(localized: "This creates a backup, updates project dependencies, runs migrations, and rebuilds frontend assets.")
        case .clean:
            return String(localized: "This creates a backup, removes installed dependencies, and rebuilds the project from lock files.")
        default: return String.localizedStringWithFormat(String(localized: "Run %@ for this site?"), operation.title)
        }
    }

    private func requestWorkflow(_ operation: SiteWorkflowOperation) {
        if [.update, .clean, .reset].contains(operation) {
            pendingWorkflow = operation
        } else {
            runWorkflow(operation)
        }
    }

    private func runWorkflow(_ operation: SiteWorkflowOperation) {
        guard !isRunning else { return }
        beginOperation(status: operation.title)
        let cancellation = SiteOperationCancellation()
        self.cancellation = cancellation
        let runner = SiteWorkflowRunner(rootURL: model.configurationStore.rootURL)
        let defaultPHP = model.configuration.selectedPHP
        let smtpPort = model.configuration.smtpPort
        operationTask = Task {
            do {
                let result = try await runner.run(
                    operation,
                    site: site,
                    defaultPHP: defaultPHP,
                    smtpPort: smtpPort,
                    cancellation: cancellation
                ) { value in
                    Task { @MainActor in appendOutput(value) }
                }
                appendOutput("\n" + result.output + "\n")
                artifactURL = result.artifactURL
                status = String(localized: "Completed")
                if operation == .repair { await loadHealth() }
            } catch let error as ProcessRunnerError {
                status =
                    error.localizedDescription == String(localized: "The command was cancelled.")
                    ? String(localized: "Cancelled") : String(localized: "Failed")
                appendOutput(error.localizedDescription + "\n")
            } catch is CancellationError {
                status = String(localized: "Cancelled")
            } catch {
                status = String(localized: "Failed")
                appendOutput(error.localizedDescription + "\n")
            }
            finishOperation()
        }
    }

    private func runComposer() {
        guard !isRunning else { return }
        let arguments: [String]
        switch composerOption {
        case "install": arguments = ["install", "--no-interaction", "--prefer-dist"]
        case "update": arguments = ["update", "--no-interaction", "--with-all-dependencies"]
        case "dump-autoload": arguments = ["dump-autoload", "--optimize", "--no-interaction"]
        case "audit": arguments = ["audit", "--no-interaction"]
        case "require": arguments = ["require", composerPackage.trimmingCharacters(in: .whitespacesAndNewlines), "--no-interaction"]
        default: return
        }
        let invocation: SiteToolInvocation
        do {
            invocation = try SiteToolchain(rootURL: model.configurationStore.rootURL).composer(
                site: site,
                defaultPHP: model.configuration.selectedPHP,
                arguments: arguments
            )
        } catch {
            status = String(localized: "Failed")
            output = error.localizedDescription
            return
        }
        beginOperation(
            status: String.localizedStringWithFormat(
                String(localized: "Composer %@"),
                arguments.joined(separator: " ")
            )
        )
        let cancellation = SiteOperationCancellation()
        self.cancellation = cancellation
        operationTask = Task {
            do {
                let result = try await SiteCommandRunner.run(invocation, cancellation: cancellation) { data in
                    let value = String(decoding: data, as: UTF8.self)
                    Task { @MainActor in appendOutput(value) }
                }
                if output.isEmpty { appendOutput(result.output) }
                status =
                    result.status == 0
                    ? String(localized: "Completed")
                    : String.localizedStringWithFormat(String(localized: "Failed (exit %lld)"), Int64(result.status))
            } catch is CancellationError {
                status = String(localized: "Cancelled")
            } catch {
                status = String(localized: "Failed")
                appendOutput(error.localizedDescription)
            }
            finishOperation()
        }
    }

    private var databaseServices: [ServiceInstance] {
        model.configuration.serviceInstances.filter {
            DatabaseServiceAuthenticator.protectedDefinitions.contains($0.definitionID)
                && model.services.state(for: $0) == .running
        }
    }

    private func selectedDatabaseService() -> ServiceInstance? {
        databaseServices.first { $0.id == selectedDatabaseID }
    }

    private func provisionDatabase() {
        guard !isRunning, let instance = selectedDatabaseService() else { return }
        beginOperation(status: String(localized: "Creating site database"))
        operationTask = Task {
            do {
                let provisioning = try await model.services.provisionDatabase(for: site, using: instance)
                databaseProvisioning = provisioning
                databaseInspection = try await model.services.inspectDatabase(provisioning)
                appendOutput("[OK] \(provisioning.databaseName)\n[OK] Updated \(provisioning.environmentURL.path)\n")
                status = String(localized: "Completed")
                await loadHealth()
            } catch is CancellationError {
                status = String(localized: "Cancelled")
            } catch {
                status = String(localized: "Failed")
                appendOutput(error.localizedDescription + "\n")
            }
            finishOperation()
        }
    }

    private func inspectDatabase() {
        guard !isRunning, let provisioning = databaseProvisioning else { return }
        beginOperation(status: String(localized: "Inspecting database"))
        operationTask = Task {
            do {
                databaseInspection = try await model.services.inspectDatabase(provisioning)
                status = String(localized: "Completed")
                appendOutput("[OK] Database connection is healthy.\n")
            } catch is CancellationError {
                status = String(localized: "Cancelled")
            } catch {
                status = String(localized: "Failed")
                appendOutput(error.localizedDescription + "\n")
            }
            finishOperation()
        }
    }

    private func backupDatabase() {
        guard !isRunning, let provisioning = databaseProvisioning else { return }
        beginOperation(status: String(localized: "Backing up database"))
        let cancellation = SiteOperationCancellation()
        self.cancellation = cancellation
        operationTask = Task {
            do {
                let url = try await model.services.backupDatabase(
                    for: site,
                    provisioning: provisioning,
                    cancellation: cancellation
                )
                artifactURL = url
                status = String(localized: "Completed")
                appendOutput("[OK] Backup: \(url.path)\n")
            } catch is CancellationError {
                status = String(localized: "Cancelled")
            } catch {
                status = String(localized: "Failed")
                appendOutput(error.localizedDescription + "\n")
            }
            finishOperation()
        }
    }

    private func openDatabase() {
        guard let provisioning = databaseProvisioning else { return }
        do {
            guard let url = try model.services.siteDatabaseConnectionURL(provisioning) else {
                throw SiteDatabaseError.unsupported
            }
            guard NSWorkspace.shared.open(url) else {
                throw SiteDatabaseError.commandFailed(String(localized: "Install TablePlus before opening this database."))
            }
        } catch {
            status = error.localizedDescription
        }
    }

    private func formattedBytes(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }

    @MainActor
    private func loadHealth() async {
        isLoadingHealth = true
        let site = site
        let defaultPHP = model.configuration.selectedPHP
        let environmentStatus = model.environment.status
        let rootURL = model.configurationStore.rootURL
        let report = await Task.detached(priority: .utility) {
            SiteHealthInspector.inspect(site: site, defaultPHP: defaultPHP, environmentStatus: environmentStatus, rootURL: rootURL)
        }.value
        guard !Task.isCancelled else { return }
        healthReport = report
        isLoadingHealth = false
    }

    private func beginOperation(status: String) {
        output = ""
        artifactURL = nil
        self.status = status
        isRunning = true
    }

    private func finishOperation() {
        isRunning = false
        cancellation = nil
        operationTask = nil
    }

    private func appendOutput(_ value: String) {
        guard !value.isEmpty else { return }
        output.append(contentsOf: value)
        let data = Data(output.utf8)
        if data.count > 1_048_576 { output = String(decoding: data.suffix(1_048_576), as: UTF8.self) }
    }

    private func backgroundStatus(_ snapshot: SiteBackgroundProcessSnapshot?) -> String {
        guard let snapshot else { return String(localized: "Stopped") }
        switch snapshot.status {
        case .running: return String(localized: "Running")
        case .stopping: return String(localized: "Stopping")
        case .stopped: return String(localized: "Stopped")
        case .failed: return String(localized: "Failed")
        }
    }

    private func backgroundColor(_ snapshot: SiteBackgroundProcessSnapshot?) -> Color {
        guard let snapshot else { return .secondary }
        switch snapshot.status {
        case .running: return .green
        case .stopping: return .orange
        case .stopped: return .secondary
        case .failed: return .red
        }
    }

    private func currentFavoriteCommand() -> String? {
        switch composerOption {
        case "require":
            let package = composerPackage.trimmingCharacters(in: .whitespacesAndNewlines)
            return package.isEmpty ? nil : "require " + package
        default: return composerOption
        }
    }

    private func loadFavorites() {
        do {
            favorites = try SiteCommandFavoritesStore(rootURL: model.configurationStore.rootURL).load(site: site.path, tool: "composer")
        } catch {
            status = error.localizedDescription
        }
    }

    private func saveFavorite() {
        guard let command = currentFavoriteCommand() else { return }
        do {
            try SiteCommandFavoritesStore(rootURL: model.configurationStore.rootURL).add(
                site: site.path, tool: "composer", command: command)
            loadFavorites()
            selectedFavoriteID = favorites.first(where: { $0.command == command })?.id
        } catch {
            status = error.localizedDescription
        }
    }

    private func removeFavorite() {
        guard let favorite = favorites.first(where: { $0.id == selectedFavoriteID }) else { return }
        do {
            try SiteCommandFavoritesStore(rootURL: model.configurationStore.rootURL).remove(
                site: site.path, tool: "composer", command: favorite.command)
            selectedFavoriteID = nil
            loadFavorites()
        } catch {
            status = error.localizedDescription
        }
    }

    private func applyFavorite(_ id: UUID?) {
        guard let favorite = favorites.first(where: { $0.id == id }) else { return }
        let parts = favorite.command.split(separator: " ", maxSplits: 1).map(String.init)
        guard composerOptions.contains(where: { $0.id == parts[0] }) else { return }
        composerOption = parts[0]
        composerPackage = parts.count > 1 ? parts[1] : ""
    }
}

private enum SitePreviewPhase: Equatable {
    case loading
    case ready
    case failed
}

private struct SiteWebPreview: View {
    let url: URL?
    let isStarting: Bool
    let onStartEnvironment: () -> Void
    let onOpenSite: () -> Void
    let onOpenLogs: () -> Void
    @State private var phase = SitePreviewPhase.loading
    @State private var requestID = UUID()

    var body: some View {
        Group {
            if usesBrowserViewport {
                previewContent
                    .aspectRatio(16 / 9, contentMode: .fit)
            } else {
                previewContent
                    .frame(height: 240)
            }
        }
        .frame(maxWidth: .infinity)
        .onChange(of: url) { _ in
            phase = .loading
            requestID = UUID()
        }
    }

    private var usesBrowserViewport: Bool {
        url != nil && phase != .failed
    }

    private var previewContent: some View {
        ZStack {
            if let url {
                SiteWebPreviewRepresentable(
                    url: url,
                    requestID: requestID,
                    onPhaseChange: { phase = $0 }
                )

                if phase == .loading {
                    ProgressView("Loading preview")
                        .padding(16)
                        .background(.regularMaterial)
                        .clipShape(RoundedRectangle(cornerRadius: 7))
                } else if phase == .failed {
                    previewFailure
                }
            } else {
                VStack(spacing: 12) {
                    Image(systemName: "play.rectangle")
                        .font(.system(size: 34, weight: .light))
                        .foregroundStyle(.secondary)
                    Text("Start Sites to Use Live Preview")
                        .font(.headline)
                    Text("The local site environment must be running before HerdMe can load this preview.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .frame(maxWidth: 420)
                    Button {
                        onStartEnvironment()
                    } label: {
                        Label(
                            isStarting ? "Starting" : "Start Sites",
                            systemImage: isStarting ? "hourglass" : "play.fill"
                        )
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(isStarting)
                }
                .padding(24)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var previewFailure: some View {
        VStack(spacing: 12) {
            Image(systemName: "exclamationmark.triangle")
                .font(.system(size: 32, weight: .light))
                .foregroundStyle(.orange)
            Text("Preview Unavailable")
                .font(.headline)
            Text("HerdMe could not load this site in Live Preview.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
            HStack(spacing: 8) {
                Button {
                    phase = .loading
                    requestID = UUID()
                } label: {
                    Label("Retry", systemImage: "arrow.clockwise")
                }
                .buttonStyle(.borderedProminent)

                Button(action: onOpenSite) {
                    Label("Open in Browser", systemImage: "safari")
                }
                .buttonStyle(.bordered)

                Button(action: onOpenLogs) {
                    Label("View Logs", systemImage: "doc.text.magnifyingglass")
                }
                .buttonStyle(.bordered)
            }
        }
        .padding(24)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(.regularMaterial)
    }
}

private struct SiteWebPreviewRepresentable: NSViewRepresentable {
    let url: URL
    let requestID: UUID
    let onPhaseChange: (SitePreviewPhase) -> Void

    func makeCoordinator() -> Coordinator {
        Coordinator(onPhaseChange: onPhaseChange)
    }

    func makeNSView(context: Context) -> DesktopPreviewScrollView {
        let configuration = WKWebViewConfiguration()
        let preview = DesktopPreviewScrollView(configuration: configuration)
        preview.webView.navigationDelegate = context.coordinator
        return preview
    }

    func updateNSView(_ preview: DesktopPreviewScrollView, context: Context) {
        context.coordinator.onPhaseChange = onPhaseChange
        context.coordinator.load(url, requestID: requestID, in: preview.webView)
    }

    @MainActor
    final class Coordinator: NSObject, WKNavigationDelegate {
        var onPhaseChange: (SitePreviewPhase) -> Void
        private var requestedID: UUID?

        init(onPhaseChange: @escaping (SitePreviewPhase) -> Void) {
            self.onPhaseChange = onPhaseChange
        }

        func load(_ url: URL, requestID: UUID, in webView: WKWebView) {
            guard requestedID != requestID else { return }
            requestedID = requestID
            onPhaseChange(.loading)
            webView.stopLoading()
            webView.load(URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 6))
        }

        func webView(_ webView: WKWebView, didFinish navigation: WKNavigation?) {
            onPhaseChange(.ready)
        }

        func webView(
            _ webView: WKWebView,
            didFailProvisionalNavigation navigation: WKNavigation?,
            withError error: Error
        ) {
            onPhaseChange(.failed)
        }

        func webView(_ webView: WKWebView, didFail navigation: WKNavigation?, withError error: Error) {
            onPhaseChange(.failed)
        }

        func webView(
            _ webView: WKWebView,
            decidePolicyFor navigationResponse: WKNavigationResponse
        ) async -> WKNavigationResponsePolicy {
            if let response = navigationResponse.response as? HTTPURLResponse,
                response.statusCode >= 400
            {
                onPhaseChange(.failed)
                return .cancel
            }
            return .allow
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
