import SwiftUI

struct DebuggerView: View {
    @EnvironmentObject private var model: AppModel
    @State private var selectedSiteID: SiteProject.ID?

    private var selectedSite: SiteProject? {
        model.sites.first { $0.id == selectedSiteID } ?? model.selectedSite
    }

    var body: some View {
        PageContainer("Debugger") {
            SettingsPanel {
                VStack(spacing: 10) {
                    SettingRow("Xdebug") {
                        HStack(spacing: 8) {
                            if let installation = model.xdebugInstallation {
                                Text("v\(installation.version)")
                                    .foregroundStyle(.secondary)
                            } else if model.debuggerOperation != nil {
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
                        Toggle("", isOn: $model.debuggerSettings.enabled)
                            .labelsHidden()
                            .toggleStyle(.switch)
                            .disabled(model.xdebugInstallation == nil)
                            .onChange(of: model.debuggerSettings.enabled) { _ in
                                model.persistDebuggerSettings()
                            }
                    }
                    PanelDivider()
                    SettingRow("Start On Trigger") {
                        Toggle("", isOn: $model.debuggerSettings.detectBreakpoints)
                            .labelsHidden()
                            .toggleStyle(.switch)
                            .onChange(of: model.debuggerSettings.detectBreakpoints) { _ in
                                model.persistDebuggerSettings()
                            }
                    }
                    PanelDivider()
                    SettingRow("Debug Port") {
                        TextField("", value: $model.debuggerSettings.port, format: .number.grouping(.never))
                            .frame(width: 90)
                            .onSubmit { model.persistDebuggerSettings() }
                    }
                    PanelDivider()
                    SettingRow("IDE Key") {
                        TextField("", text: $model.debuggerSettings.ideKey)
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
                            ForEach(model.sites) { site in
                                Text(site.domain(tld: model.configuration.tld))
                                    .tag(Optional(site.id))
                            }
                        }
                        .labelsHidden()
                        .frame(width: 230)
                    }
                    PanelDivider()
                    SettingRow("IDE Endpoint") {
                        Text(PortPresentation.endpoint(port: model.debuggerSettings.normalized.port))
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
            selectedSiteID = selectedSiteID ?? model.selectedSite?.id
        }
    }

    private var sessionReady: Bool {
        model.xdebugInstallation != nil
            && model.debuggerSettings.enabled
            && model.environmentStatus == .running
    }
}
