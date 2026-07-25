import SwiftUI

struct ServicesView: View {
    @EnvironmentObject private var model: AppModel
    @State private var showingAddService = false

    var body: some View {
        PageContainer("Services") {
            if model.configuration.serviceInstances.isEmpty {
                SettingsPanel {
                    EmptyStateView(
                        symbol: "externaldrive.badge.plus",
                        title: "No Services Yet",
                        message: "Create databases, queues, search engines, storage, and realtime services for local development.",
                        actionTitle: "Add Service"
                    ) {
                        showingAddService = true
                    }
                    .frame(minHeight: 310)
                }
            } else {
                SettingsPanel {
                    VStack(spacing: 0) {
                        HStack {
                            Text("Service").fontWeight(.medium)
                            Spacer()
                            Text("Port").fontWeight(.medium).frame(width: 80)
                            Text("Status").fontWeight(.medium).frame(width: 100)
                            Text("Auto").fontWeight(.medium).frame(width: 58)
                            Spacer().frame(width: 58)
                        }
                        .padding(.bottom, 10)

                        ForEach(model.configuration.serviceInstances) { instance in
                            Divider()
                            HStack {
                                let definition = ServiceCatalog.all.first(where: { $0.id == instance.definitionID })
                                let state = model.serviceState(for: instance)
                                let isOperating = model.serviceOperation == instance.id
                                Label(instance.name, systemImage: definition?.symbol ?? "externaldrive")
                                Spacer()
                                Text(PortPresentation.number(instance.port)).frame(width: 80)
                                Group {
                                    if isOperating {
                                        ProgressView()
                                            .controlSize(.small)
                                    } else {
                                        Text(state.rawValue)
                                            .foregroundStyle(state.isRunning ? .green : .secondary)
                                    }
                                }
                                .frame(width: 100)
                                Toggle("Start automatically", isOn: Binding(
                                    get: { instance.startAutomatically },
                                    set: { model.setServiceAutomaticStart(instance, enabled: $0) }
                                ))
                                .labelsHidden()
                                .toggleStyle(.switch)
                                .controlSize(.small)
                                .frame(width: 58)
                                .help("Start this service automatically with the local environment")
                                Menu {
                                    switch state {
                                    case .notInstalled:
                                        Button("Install") { model.installService(instance) }
                                    case .stopped:
                                        Button("Start") { model.startService(instance) }
                                        if model.isServiceUpdateAvailable(instance) {
                                            Button("Update") { model.installService(instance) }
                                        }
                                    case .running:
                                        Button("Stop") { model.stopService(instance) }
                                        if model.canOpenServiceInTablePlus(instance) {
                                            Button("Open in TablePlus") {
                                                model.openServiceInTablePlus(instance)
                                            }
                                        }
                                        if model.canOpenServiceConsole(instance) {
                                            Button("Open Console") { model.openServiceConsole(instance) }
                                        }
                                    }
                                    Button("Open Data Directory") { model.openServiceDataDirectory(instance) }
                                    Divider()
                                    Button("Delete", role: .destructive) { model.removeService(instance) }
                                } label: {
                                    Image(systemName: "ellipsis.circle")
                                }
                                .frame(width: 58)
                                .disabled(isOperating)
                            }
                            .frame(height: 44)
                        }
                    }
                }

                Button {
                    showingAddService = true
                } label: {
                    Label("Add Service", systemImage: "plus")
                }
                .buttonStyle(.borderedProminent)
                .tint(.red)
            }
        }
        .sheet(isPresented: $showingAddService) {
            AddServiceSheet(isPresented: $showingAddService)
                .environmentObject(model)
        }
        .onAppear {
            model.refreshServiceStates()
            model.refreshServiceUpdates()
        }
    }
}

private struct AddServiceSheet: View {
    @EnvironmentObject private var model: AppModel
    @Binding var isPresented: Bool
    @State private var selected = ServiceCatalog.all[0]
    @State private var name = ServiceCatalog.all[0].name
    @State private var port = ServiceCatalog.all[0].defaultPort

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Add Service").font(.title2.weight(.semibold))
            Picker("Service Type", selection: $selected) {
                ForEach(ServiceCatalog.all) { definition in
                    Text(definition.name).tag(definition)
                }
            }
            .onChange(of: selected) { value in
                name = value.name
                port = value.defaultPort
            }
            TextField("Service Name", text: $name)
            TextField("Port", value: $port, format: .number.grouping(.never))
            HStack {
                Spacer()
                Button("Cancel") { isPresented = false }
                Button("Add") {
                    model.addService(definition: selected, name: name, port: port)
                    isPresented = false
                }
                .buttonStyle(.borderedProminent)
                .disabled(name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || port <= 0)
            }
        }
        .padding(24)
        .frame(width: 420)
    }
}
