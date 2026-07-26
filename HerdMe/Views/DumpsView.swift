import SwiftUI

struct DumpsView: View {
    @EnvironmentObject private var model: AppModel
    @AppStorage("dumpShowNewOnTop") private var showNewOnTop = false
    @AppStorage("dumpFontSize") private var fontSize = 14
    @State private var selectedDumpID: CapturedDump.ID?

    private var displayedDumps: [CapturedDump] {
        showNewOnTop ? model.dumps : model.dumps.reversed()
    }

    private var selectedDump: CapturedDump? {
        model.dumps.first { $0.id == selectedDumpID }
    }

    var body: some View {
        PageContainer("Dumps") {
            SettingsPanel {
                VStack(spacing: 7) {
                    SettingRow("Intercept dumps", detail: "Listen for Laravel and Symfony VarDumper payloads on port \(model.configuration.dumpPort).") {
                        Toggle("", isOn: Binding(
                            get: { model.isDumpServerRunning },
                            set: { $0 ? model.startDumpServer() : model.stopDumpServer() }
                        ))
                        .labelsHidden()
                        .toggleStyle(.switch)
                    }
                    PanelDivider()
                    SettingRow("Show new dumps on top") {
                        Toggle("", isOn: $showNewOnTop).labelsHidden().toggleStyle(.switch)
                    }
                    PanelDivider()
                    SettingRow("Font Size") {
                        Stepper(value: $fontSize, in: 10...28) { Text("\(fontSize) pt") }
                    }
                }
            }

            SettingsPanel {
                HStack(spacing: 0) {
                    VStack(alignment: .leading, spacing: 0) {
                        HStack {
                            Text("Captured").font(.headline)
                            Spacer()
                            Text("\(model.dumps.count)").font(.caption).foregroundStyle(.secondary)
                            Button {
                                selectedDumpID = nil
                                model.clearDumps()
                            } label: {
                                Image(systemName: "trash")
                            }
                            .buttonStyle(.borderless)
                            .disabled(model.dumps.isEmpty)
                            .help("Delete all dumps")
                            .accessibilityLabel("Delete all dumps")
                        }
                        .padding(.bottom, 8)

                        if displayedDumps.isEmpty {
                            EmptyStateView(symbol: "shippingbox.and.arrow.backward", title: "No Dumps Yet", message: "")
                        } else {
                            ScrollView {
                                LazyVStack(spacing: 2) {
                                    ForEach(displayedDumps) { dump in
                                        Button { selectedDumpID = dump.id } label: {
                                            VStack(alignment: .leading, spacing: 3) {
                                                Text(dump.source).font(.caption.weight(.medium)).lineLimit(1)
                                                Text(dump.summary).font(.caption2.monospaced()).lineLimit(2)
                                                Text(dump.receivedAt, style: .time).font(.caption2).foregroundStyle(.secondary)
                                            }
                                            .frame(maxWidth: .infinity, alignment: .leading)
                                            .padding(8)
                                            .background(selectedDumpID == dump.id ? Color.accentColor.opacity(0.2) : Color.clear)
                                            .clipShape(RoundedRectangle(cornerRadius: 5))
                                        }
                                        .buttonStyle(.plain)
                                    }
                                }
                            }
                        }
                    }
                    .frame(width: 250)
                    Divider().padding(.horizontal, 14)
                    if let dump = selectedDump {
                        ScrollView {
                            Text(dump.summary)
                                .font(.system(size: CGFloat(fontSize), design: .monospaced))
                                .textSelection(.enabled)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                    } else {
                        EmptyStateView(symbol: "shippingbox", title: "Select a dump", message: "")
                    }
                }
                .frame(height: 250)
            }
        }
        .onAppear { selectedDumpID = selectedDumpID ?? displayedDumps.first?.id }
    }
}
