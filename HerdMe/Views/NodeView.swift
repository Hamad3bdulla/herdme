import SwiftUI

struct NodeView: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        PageContainer("Node.js") {
            SettingsPanel {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Node.js Versions").font(.headline)
                    Text("Install and update Node.js.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Divider()
                    HStack {
                        Text("Major Version").fontWeight(.medium).frame(width: 150, alignment: .leading)
                        Text("Installed Version").fontWeight(.medium)
                        Spacer()
                    }
                    .padding(.horizontal, 8)

                    ForEach(Array(model.nodeVersions.enumerated()), id: \.element.id) { index, runtime in
                        HStack {
                            Text(runtime.cycle).frame(width: 150, alignment: .leading)
                            Text(runtime.installedVersion ?? "Not installed")
                                .foregroundStyle(runtime.isInstalled ? .primary : .secondary)
                            Spacer()
                            if model.runtimeOperation == "node-\(runtime.cycle)" {
                                ProgressView()
                                    .controlSize(.small)
                                Text("Working...")
                                    .foregroundStyle(.secondary)
                            } else if runtime.isInstalled {
                                if runtime.isActive {
                                    Label("Active", systemImage: "checkmark.circle.fill")
                                        .foregroundStyle(.green)
                                }
                                Menu {
                                    Button("Use") { model.activateNode(runtime.cycle) }
                                        .disabled(runtime.isActive)
                                    if model.isNodeUpdateAvailable(runtime) {
                                        Button("Update") { model.installNode(runtime.cycle) }
                                    }
                                    Divider()
                                    Button("Delete", role: .destructive) { model.removeNode(runtime.cycle) }
                                } label: {
                                    Image(systemName: "ellipsis.circle")
                                }
                            } else {
                                Button("Install") { model.installNode(runtime.cycle) }
                            }
                        }
                        .padding(.horizontal, 8)
                        .frame(height: 38)
                        .background(index.isMultiple(of: 2) ? Color.primary.opacity(0.04) : Color.clear)
                    }
                }
            }
        }
        .onAppear {
            model.refresh()
            model.refreshNodeUpdates()
        }
    }
}
