import SwiftUI

struct GeneralView: View {
    @EnvironmentObject private var model: AppModel
    @State private var selectedPath: String?
    @State private var tld = "test"

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
                            Text(path)
                                .font(.system(size: 13))
                                .tag(path)
                        }
                    }
                    .listStyle(.bordered(alternatesRowBackgrounds: true))
                    .frame(height: 78)

                    HStack(spacing: 0) {
                        Button {
                            model.addParkPath()
                        } label: {
                            Image(systemName: "plus")
                        }
                        .buttonStyle(.borderless)
                        .help("Add a sites folder")
                        .frame(width: 30)

                        Divider().frame(height: 20)

                        Button {
                            guard let selectedPath,
                                  let index = model.configuration.parkPaths.firstIndex(of: selectedPath) else { return }
                            model.removeParkPath(at: IndexSet(integer: index))
                            self.selectedPath = nil
                        } label: {
                            Image(systemName: "minus")
                        }
                        .buttonStyle(.borderless)
                        .disabled(selectedPath == nil)
                        .help("Remove the selected folder")
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
                            if model.domainResolverState != .managed || model.networkHelperNeedsUpdate {
                                if model.privilegedOperation == "domains" {
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
                            Text(model.certificateTrustState.title)
                                .foregroundStyle(.secondary)
                            if model.certificateTrustState != .trusted {
                                if model.privilegedOperation == "certificate" {
                                    ProgressView().controlSize(.small)
                                } else {
                                    Button("Trust") { model.installCertificateAuthority() }
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
                        detail: model.launchAtLoginRequiresApproval ? "Approval is required in System Settings." : nil
                    ) {
                        HStack(spacing: 8) {
                            if model.launchAtLoginRequiresApproval {
                                Button("Open Settings") { model.openLoginItemsSettings() }
                            }
                            Toggle("", isOn: Binding(
                                get: { model.configuration.launchAtLogin },
                                set: { model.setLaunchAtLogin($0) }
                            ))
                            .labelsHidden()
                            .toggleStyle(.switch)
                        }
                    }
                    PanelDivider()
                    SettingRow("Update HerdMe automatically") {
                        Toggle("", isOn: Binding(
                            get: { model.configuration.automaticUpdates },
                            set: { model.setAutomaticUpdates($0) }
                        ))
                            .labelsHidden()
                            .toggleStyle(.switch)
                    }
                    PanelDivider()
                    SettingRow("Update Channel") {
                        Picker("", selection: Binding(
                            get: { model.configuration.updateChannel },
                            set: { model.setUpdateChannel($0) }
                        )) {
                            Text("Stable").tag("Stable")
                            Text("Beta").tag("Beta")
                        }
                        .labelsHidden()
                        .frame(width: 120)
                    }
                    PanelDivider()
                    SettingRow("Application updates") {
                        if model.isCheckingForUpdates {
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
                            ForEach(["Auto", "Light", "Dark"], id: \.self) { Text($0) }
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
    }

    private var resolverTitle: String {
        if model.domainResolverState == .managed {
            if model.networkHelperNeedsUpdate { return "Update available" }
            return model.isDNSServerRunning ? "Active" : "Configured"
        }
        return model.domainResolverState.title
    }

    private var resolverActionTitle: String {
        if model.domainResolverState == .managed { return "Update" }
        return model.domainResolverState == .external ? "Use HerdMe" : "Set Up"
    }

    private var resolverColor: Color {
        switch model.domainResolverState {
        case .managed: model.isDNSServerRunning && !model.networkHelperNeedsUpdate ? .green : .orange
        case .external: .blue
        case .missing: .secondary
        }
    }

    private var certificateColor: Color {
        switch model.certificateTrustState {
        case .trusted: .green
        case .untrusted: .orange
        case .missing: .secondary
        }
    }
}
