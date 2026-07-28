import AppKit
import SwiftUI

struct GeneralView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var applicationSettings: ApplicationSettingsCoordinator
    @EnvironmentObject private var sitesCoordinator: SitesCoordinator
    @EnvironmentObject private var environmentCoordinator: EnvironmentCoordinator
    @EnvironmentObject private var securityCoordinator: SecuritySetupCoordinator
    @State private var selectedPath: String?
    @State private var tld = "test"
    @State private var isDroppingParkPath = false

    var body: some View {
        PageContainer("General") {
            SettingsPanel {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Sites Folders")
                        .font(.headline)
                    Text("All sub-folders in these directories will be available via HerdMe's local domains.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Divider()

                    List(selection: $selectedPath) {
                        ForEach(model.configuration.parkPaths, id: \.self) { path in
                            HStack {
                                Text(path)
                                    .lineLimit(1)
                                    .truncationMode(.middle)
                                Spacer(minLength: 8)
                                Text("\(siteCount(in: path))")
                                    .font(.caption.monospacedDigit())
                                    .foregroundStyle(.secondary)
                            }
                            .tag(path)
                            .accessibilityLabel(path)
                            .accessibilityValue("\(siteCount(in: path)) sites")
                        }
                    }
                    .listStyle(.bordered(alternatesRowBackgrounds: true))
                    .frame(minHeight: 150, idealHeight: 150, maxHeight: 220)
                    .overlay {
                        RoundedRectangle(cornerRadius: 6)
                            .stroke(
                                isDroppingParkPath ? Color.accentColor : Color.clear,
                                style: StrokeStyle(lineWidth: 2, dash: [6, 4])
                            )
                            .allowsHitTesting(false)
                    }
                    .dropDestination(for: URL.self) { urls, _ in
                        addDroppedParkPaths(urls)
                    } isTargeted: { isTargeted in
                        isDroppingParkPath = isTargeted
                    }

                    HStack(spacing: 0) {
                        Button {
                            chooseParkPath()
                        } label: {
                            Image(systemName: "plus")
                        }
                        .buttonStyle(.borderless)
                        .help("Add a sites folder")
                        .accessibilityLabel("Add a sites folder")
                        .frame(width: 30)

                        Divider().frame(height: 20)

                        Button {
                            guard let selectedPath,
                                let index = model.configuration.parkPaths.firstIndex(of: selectedPath)
                            else { return }
                            model.removeParkPath(at: IndexSet(integer: index))
                            self.selectedPath = nil
                        } label: {
                            Image(systemName: "minus")
                        }
                        .buttonStyle(.borderless)
                        .disabled(selectedPath == nil)
                        .help("Remove the selected folder")
                        .accessibilityLabel("Remove the selected folder")
                        .frame(width: 30)
                    }
                }
            }

            SettingsPanel {
                VStack(spacing: 6) {
                    SettingRow("Top-level domain") {
                        TextField("test", text: $tld)
                            .textFieldStyle(.roundedBorder)
                            .frame(width: 110)
                            .onSubmit { model.updateTLD(tld) }
                    }
                    PanelDivider()
                    SettingRow("Local domains") {
                        HStack(spacing: 8) {
                            Circle()
                                .fill(resolverColor)
                                .frame(width: 7, height: 7)
                            Text(resolverTitle)
                                .foregroundStyle(.secondary)
                            if securityCoordinator.domainResolverState != .managed
                                || securityCoordinator.networkHelperNeedsUpdate
                            {
                                if securityCoordinator.privilegedOperation == "domains" {
                                    ProgressView().controlSize(.small)
                                } else {
                                    Button(
                                        resolverActionTitle
                                    ) {
                                        model.installDomainResolver()
                                    }
                                    .buttonStyle(.bordered)
                                }
                            }
                        }
                    }
                    PanelDivider()
                    SettingRow("HTTPS certificate") {
                        HStack(spacing: 8) {
                            Circle()
                                .fill(certificateColor)
                                .frame(width: 7, height: 7)
                            Text(httpsStatusTitle)
                                .foregroundStyle(.secondary)
                            if model.shouldOfferHTTPSAction {
                                if securityCoordinator.privilegedOperation == "certificate" {
                                    ProgressView().controlSize(.small)
                                } else {
                                    Button(model.httpsActionTitle) {
                                        model.installCertificateAuthority()
                                    }
                                    .buttonStyle(.bordered)
                                }
                            }
                        }
                    }
                }
            }

            SettingsPanel {
                VStack(spacing: 5) {
                    SettingRow(
                        "Launch at Login",
                        detail: applicationSettings.launchAtLoginRequiresApproval
                            ? "Approval is required in System Settings." : nil
                    ) {
                        HStack(spacing: 8) {
                            if applicationSettings.launchAtLoginRequiresApproval {
                                Button("Open Settings") { model.openLoginItemsSettings() }
                            }
                            Toggle(
                                "",
                                isOn: Binding(
                                    get: { model.configuration.launchAtLogin },
                                    set: { model.setLaunchAtLogin($0) }
                                )
                            )
                            .labelsHidden()
                            .toggleStyle(.switch)
                        }
                    }
                    PanelDivider()
                    SettingRow("Update HerdMe automatically") {
                        Toggle(
                            "",
                            isOn: Binding(
                                get: { model.configuration.automaticUpdates },
                                set: { model.setAutomaticUpdates($0) }
                            )
                        )
                        .labelsHidden()
                        .toggleStyle(.switch)
                    }
                    PanelDivider()
                    SettingRow("Update Channel") {
                        Picker(
                            "",
                            selection: Binding(
                                get: { model.configuration.updateChannel },
                                set: { model.setUpdateChannel($0) }
                            )
                        ) {
                            Text("Stable").tag("Stable")
                            Text("Beta").tag("Beta")
                        }
                        .labelsHidden()
                        .frame(width: 120)
                    }
                    PanelDivider()
                    SettingRow("Application updates") {
                        if applicationSettings.isCheckingForUpdates {
                            ProgressView().controlSize(.small)
                        } else {
                            Button("Check Now") { model.checkForUpdates() }
                        }
                    }
                    PanelDivider()
                    SettingRow("IDE", detail: "The default IDE to open from the sites list and log viewer.") {
                        Picker("", selection: $model.configuration.ide) {
                            ForEach(["VSCode", "PhpStorm", "Sublime Text"], id: \.self) { Text($0) }
                        }
                        .labelsHidden()
                        .frame(width: 150)
                        .onChange(of: model.configuration.ide) { _ in model.persist() }
                    }
                    PanelDivider()
                    SettingRow("Theme", detail: "Choose between auto, light, or dark theme.") {
                        Picker("", selection: $model.configuration.theme) {
                            ForEach(AppTheme.allCases, id: \.self) { theme in
                                Text(theme.localizedTitle).tag(theme)
                            }
                        }
                        .labelsHidden()
                        .frame(width: 120)
                        .onChange(of: model.configuration.theme) { _ in model.persist() }
                    }
                }
            }
        }
        .onAppear {
            tld = model.configuration.tld
            model.refreshDomainResolver()
            model.refreshCertificateTrust()
            model.refreshLaunchAtLogin()
        }
        .onDisappear {
            if tld.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
                != model.configuration.tld
            {
                model.updateTLD(tld)
            }
        }
    }

    private func chooseParkPath() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        panel.prompt = "Add"
        panel.begin { response in
            guard response == .OK, let url = panel.url else { return }
            model.addParkPath(url)
        }
    }

    private func addDroppedParkPaths(_ urls: [URL]) -> Bool {
        var addedPath = false
        for url in urls {
            guard (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true else {
                continue
            }
            addedPath = model.addParkPath(url) || addedPath
        }
        return addedPath
    }

    private func siteCount(in path: String) -> Int {
        let root = URL(fileURLWithPath: path).standardizedFileURL.path
        let prefix = root.hasSuffix("/") ? root : root + "/"
        return sitesCoordinator.sites.filter {
            let sitePath = $0.path.standardizedFileURL.path
            return sitePath == root || sitePath.hasPrefix(prefix)
        }.count
    }

    private var resolverTitle: String {
        if securityCoordinator.domainResolverState == .managed {
            if securityCoordinator.networkHelperNeedsUpdate { return "Update available" }
            return securityCoordinator.isDNSServerRunning ? "Active" : "Configured"
        }
        return securityCoordinator.domainResolverState.title
    }

    private var resolverActionTitle: String {
        if securityCoordinator.domainResolverState == .managed { return "Update" }
        return securityCoordinator.domainResolverState == .external ? "Use HerdMe" : "Set Up"
    }

    private var resolverColor: Color {
        switch securityCoordinator.domainResolverState {
        case .managed:
            securityCoordinator.isDNSServerRunning && !securityCoordinator.networkHelperNeedsUpdate
                ? .green : .orange
        case .external: .blue
        case .missing: .secondary
        }
    }

    private var certificateColor: Color {
        switch securityCoordinator.certificateTrustState {
        case .trusted:
            if environmentCoordinator.status == .running {
                environmentCoordinator.isHTTPSActive ? .green : .orange
            } else {
                model.automaticHTTPSEnabled ? .green : .secondary
            }
        case .untrusted: .orange
        case .missing: .secondary
        }
    }

    private var httpsStatusTitle: String {
        AppModel.httpsStatusTitle(
            certificateTrustState: securityCoordinator.certificateTrustState,
            environmentStatus: environmentCoordinator.status,
            hasHTTPSPort: environmentCoordinator.httpsPort != nil,
            automaticHTTPSEnabled: model.automaticHTTPSEnabled,
            needsUserApproval: environmentCoordinator.httpsStartupNeedsApproval
        )
    }
}
