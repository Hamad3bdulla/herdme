import SwiftUI

struct DebuggerView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var runtimeCoordinator: RuntimeCoordinator
    @EnvironmentObject private var sitesCoordinator: SitesCoordinator
    @EnvironmentObject private var environmentCoordinator: EnvironmentCoordinator
    @State private var selectedSiteID: SiteProject.ID?

    private var selectedSite: SiteProject? {
        sitesCoordinator.sites.first { $0.id == selectedSiteID }
            ?? sitesCoordinator.selectedSite(identifier: model.selectedSiteID)
    }

    var body: some View {
        PageContainer("Debugger") {
            SettingsPanel {
                VStack(spacing: 10) {
                    SettingRow("Xdebug") {
                        HStack(spacing: 8) {
                            if let installation = runtimeCoordinator.xdebugInstallation {
                                Text("v\(installation.version)")
                                    .foregroundStyle(.secondary)
                            } else if runtimeCoordinator.debuggerOperation != nil {
                                ProgressView().controlSize(.small)
                                Text("Installing")
                                    .foregroundStyle(.secondary)
                            } else {
                                Text("Not installed")
                                    .foregroundStyle(.secondary)
                                Button {
                                    model.installXdebug()
                                } label: {
                                    Label("Install", systemImage: "square.and.arrow.down")
                                }
                            }
                        }
                    }
                    PanelDivider()
                    SettingRow("Enable Xdebug") {
                        Toggle("", isOn: $runtimeCoordinator.debuggerSettings.enabled)
                            .labelsHidden()
                            .toggleStyle(.switch)
                            .disabled(runtimeCoordinator.xdebugInstallation == nil)
                            .onChange(of: runtimeCoordinator.debuggerSettings.enabled) { _ in
                                model.persistDebuggerSettings()
                            }
                    }
                    PanelDivider()
                    SettingRow("Require Debug Trigger") {
                        Toggle("", isOn: $runtimeCoordinator.debuggerSettings.startOnlyOnTrigger)
                            .labelsHidden()
                            .toggleStyle(.switch)
                            .help("Start Xdebug only for requests that include XDEBUG_TRIGGER")
                            .onChange(of: runtimeCoordinator.debuggerSettings.startOnlyOnTrigger) { _ in
                                model.persistDebuggerSettings()
                            }
                    }
                    PanelDivider()
                    SettingRow("Debug Port") {
                        TextField("", value: $runtimeCoordinator.debuggerSettings.port, format: .number.grouping(.never))
                            .frame(width: 90)
                            .onSubmit { model.persistDebuggerSettings() }
                    }
                    PanelDivider()
                    SettingRow("IDE Key") {
                        TextField("", text: $runtimeCoordinator.debuggerSettings.ideKey)
                            .frame(width: 120)
                            .onSubmit { model.persistDebuggerSettings() }
                    }
                }
            }

            SettingsPanel {
                VStack(alignment: .leading, spacing: 10) {
                    HStack {
                        Text("Debug Session").font(.headline)
                        Spacer()
                        Circle()
                            .fill(sessionReady ? .green : .secondary)
                            .frame(width: 8, height: 8)
                        Text(sessionReady ? "Ready" : "Stopped")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    PanelDivider()
                    SettingRow("Site") {
                        Picker("", selection: $selectedSiteID) {
                            ForEach(sitesCoordinator.sites) { site in
                                Text(site.domain(tld: model.configuration.tld))
                                    .tag(Optional(site.id))
                            }
                        }
                        .labelsHidden()
                        .frame(width: 230)
                    }
                    PanelDivider()
                    SettingRow("IDE Endpoint") {
                        Text(PortPresentation.endpoint(port: runtimeCoordinator.debuggerSettings.normalized.port))
                            .foregroundStyle(.secondary)
                    }
                    PanelDivider()
                    HStack {
                        Spacer()
                        Button {
                            if let selectedSite { model.openIDE(for: selectedSite) }
                        } label: {
                            Label("Open IDE", systemImage: "chevron.left.forwardslash.chevron.right")
                        }
                        .disabled(selectedSite == nil)
                        Button {
                            if let selectedSite { model.openDebugSession(for: selectedSite) }
                        } label: {
                            Label("Start Session", systemImage: "ladybug.fill")
                        }
                        .buttonStyle(.borderedProminent)
                        .disabled(!sessionReady || selectedSite == nil)
                    }
                }
            }
        }
        .onAppear {
            model.refreshXdebugInstallation()
            selectedSiteID =
                selectedSiteID
                ?? sitesCoordinator.selectedSite(identifier: model.selectedSiteID)?.id
        }
    }

    private var sessionReady: Bool {
        runtimeCoordinator.xdebugInstallation != nil
            && runtimeCoordinator.debuggerSettings.enabled
            && environmentCoordinator.status == .running
    }
}
