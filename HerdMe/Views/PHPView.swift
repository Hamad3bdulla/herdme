import SwiftUI

struct PHPView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var runtimeCoordinator: RuntimeCoordinator

    var body: some View {
        PageContainer("PHP") {
            SettingsPanel {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Available Versions").font(.headline)
                    Text("Install, update and manage PHP.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Divider()
                    HStack {
                        Text("Version").fontWeight(.medium)
                        Spacer()
                        Text("Status").fontWeight(.medium).frame(width: 170, alignment: .leading)
                    }
                    .padding(.horizontal, 8)

                    ForEach(Array(runtimeCoordinator.phpVersions.enumerated()), id: \.element.id) { index, runtime in
                        HStack {
                            Text(runtime.installedVersion.map { "\(runtime.cycle) (\($0))" } ?? runtime.cycle)
                            Spacer()
                            if runtimeCoordinator.operation == "php-\(runtime.cycle)" {
                                ProgressView()
                                    .controlSize(.small)
                                Text("Working...")
                                    .foregroundStyle(.secondary)
                                    .frame(width: 100, alignment: .leading)
                            } else if runtime.isActive {
                                Label("Active", systemImage: "checkmark.circle.fill")
                                    .foregroundStyle(.blue)
                                    .frame(width: 80, alignment: .leading)
                            }
                            if runtime.isInstalled {
                                if runtimeCoordinator.isPHPUpdateAvailable(runtime) {
                                    Button("Update") { model.installPHP(runtime.cycle) }
                                }
                                Button("Use") { model.setActivePHP(runtime.cycle) }
                                    .disabled(runtime.isActive)
                            } else {
                                Button("Install") {
                                    model.installPHP(runtime.cycle)
                                }
                            }
                        }
                        .padding(.horizontal, 8)
                        .frame(height: 32)
                        .background(index.isMultiple(of: 2) ? Color.primary.opacity(0.04) : Color.clear)
                    }
                }
            }

            SettingsPanel {
                VStack(spacing: 8) {
                    SettingRow("Composer") {
                        HStack(spacing: 8) {
                            if runtimeCoordinator.operation == "composer" {
                                ProgressView().controlSize(.small)
                                Text("Updating")
                                    .foregroundStyle(.secondary)
                            } else {
                                Text(runtimeCoordinator.composerVersion.map { "v\($0)" } ?? "Not installed")
                                    .foregroundStyle(.secondary)
                                    .lineLimit(1)
                                if runtimeCoordinator.composerVersion == nil {
                                    Button("Install") { model.updateComposer() }
                                } else if runtimeCoordinator.isComposerUpdateAvailable {
                                    Button("Update") { model.updateComposer() }
                                }
                            }
                        }
                    }
                    PanelDivider()
                    SettingRow("Laravel Installer") {
                        HStack(spacing: 8) {
                            if runtimeCoordinator.operation == "laravel-installer" {
                                ProgressView().controlSize(.small)
                                Text("Updating")
                                    .foregroundStyle(.secondary)
                            } else {
                                Text(runtimeCoordinator.laravelInstallerVersion.map { "v\($0)" } ?? "Not installed")
                                    .foregroundStyle(.secondary)
                                    .lineLimit(1)
                                if runtimeCoordinator.laravelInstallerVersion == nil {
                                    Button("Install") { model.updateLaravelInstaller() }
                                } else if runtimeCoordinator.isLaravelInstallerUpdateAvailable {
                                    Button("Update") { model.updateLaravelInstaller() }
                                }
                            }
                        }
                    }
                }
            }

            SettingsPanel {
                VStack(spacing: 8) {
                    SettingRow("Max File Upload Size:", detail: "Maximum upload size accepted by PHP, in MB.") {
                        TextField("", value: $runtimeCoordinator.phpRequestSettings.maxUploadMegabytes, format: .number)
                            .frame(width: 100)
                            .onSubmit { model.persistPHPRequestSettings() }
                    }
                    PanelDivider()
                    SettingRow("Memory Limit:", detail: "Maximum memory available to PHP scripts, in MB.") {
                        TextField("", value: $runtimeCoordinator.phpRequestSettings.memoryLimitMegabytes, format: .number)
                            .frame(width: 100)
                            .onSubmit { model.persistPHPRequestSettings() }
                    }
                    PanelDivider()
                    SettingRow("php.ini") {
                        Button("Open Configuration Directory") { model.openConfigurationDirectory() }
                    }
                }
            }
        }
        .onAppear {
            model.refresh()
            model.refreshPHPUpdates()
            model.refreshComposer()
            model.refreshLaravelInstaller()
        }
    }
}
