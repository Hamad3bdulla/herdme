import SwiftUI

struct ServicesView: View {
    @EnvironmentObject private var model: AppModel
    @State private var showingAddService = false
    @State private var environmentService: ServiceInstance?

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
                                VStack(alignment: .leading, spacing: 2) {
                                    Label(instance.name, systemImage: definition?.symbol ?? "externaldrive")
                                    Text(
                                        state.isRunning
                                            ? TablePlusConnection.displayAddress(for: instance)
                                                ?? instance.definitionID
                                            : instance.definitionID
                                    )
                                    .font(.caption2.monospaced())
                                    .foregroundStyle(.secondary)
                                    .lineLimit(1)
                                }
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
                                            Button {
                                                model.copyServiceConnectionURL(instance)
                                            } label: {
                                                Label("Copy Connection URL", systemImage: "doc.on.doc")
                                            }
                                            Button {
                                                model.openServiceInTablePlus(instance)
                                            } label: {
                                                Label("Open in TablePlus", systemImage: "arrow.up.forward.app")
                                            }
                                        }
                                        if model.canOpenServiceConsole(instance) {
                                            Button("Open Console") { model.openServiceConsole(instance) }
                                        }
                                    }
                                    Button("Add to .env…") { environmentService = instance }
                                    Button("Open Data Directory") { model.openServiceDataDirectory(instance) }
                                    Divider()
                                    Button("Delete", role: .destructive) { model.removeService(instance) }
                                } label: {
                                    Image(systemName: "ellipsis.circle")
                                }
                                .frame(width: 58)
                                .disabled(isOperating)
                            }
                            .frame(height: 52)
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
        .sheet(item: $environmentService) { instance in
            ServiceEnvironmentSheet(instance: instance)
                .environmentObject(model)
        }
        .onAppear {
            model.refreshServiceStates()
            model.refreshServiceUpdates()
        }
    }
}

private struct ServiceEnvironmentSheet: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss
    let instance: ServiceInstance
    @State private var selectedSiteID = ""
    @State private var update: ServiceEnvironmentUpdate?
    @State private var errorMessage: String?

    private var selectedSite: SiteProject? {
        model.sites.first(where: { $0.id == selectedSiteID })
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Add \(instance.name) to .env")
                .font(.title2.weight(.semibold))

            if model.sites.isEmpty {
                EmptyStateView(
                    symbol: "folder.badge.questionmark",
                    title: "No Sites Available",
                    message: "Add or link a site before updating a .env file."
                )
            } else {
                Picker("Site", selection: $selectedSiteID) {
                    ForEach(model.sites) { site in
                        Text(site.name).tag(site.id)
                    }
                }

                if let selectedSite {
                    Text(selectedSite.path.appendingPathComponent(".env").path)
                        .font(.caption.monospaced())
                        .foregroundStyle(.secondary)
                        .lineLimit(2)
                        .textSelection(.enabled)
                }

                if let update {
                    Label(
                        "Added \(update.addedKeys) and updated \(update.updatedKeys) variables.",
                        systemImage: "checkmark.circle.fill"
                    )
                    .foregroundStyle(.green)
                } else if let errorMessage {
                    Label(errorMessage, systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(.red)
                }
            }

            HStack {
                Spacer()
                Button(update == nil ? "Cancel" : "Done") { dismiss() }
                if !model.sites.isEmpty, update == nil {
                    Button("Add to .env") { writeEnvironment() }
                        .buttonStyle(.borderedProminent)
                        .disabled(selectedSite == nil)
                }
            }
        }
        .padding(24)
        .frame(width: 480)
        .frame(minHeight: 230)
        .onAppear {
            if selectedSiteID.isEmpty {
                selectedSiteID = model.selectedSite?.id ?? model.sites.first?.id ?? ""
            }
        }
    }

    private func writeEnvironment() {
        guard let selectedSite else { return }
        do {
            update = try model.addServiceEnvironment(instance, to: selectedSite)
            errorMessage = nil
        } catch {
            errorMessage = error.localizedDescription
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
                port = model.suggestedServicePort(startingAt: value.defaultPort)
                    ?? value.defaultPort
            }
            TextField("Service Name", text: $name)
            TextField("Port", value: $port, format: .number.grouping(.never))
            HStack {
                Spacer()
                Button("Cancel") { isPresented = false }
                Button("Add") {
                    if model.addService(definition: selected, name: name, port: port) {
                        isPresented = false
                    } else if let suggestion = model.suggestedServicePort(
                        startingAt: port == 65_535 ? 1_024 : port + 1
                    ) {
                        port = suggestion
                    }
                }
                .buttonStyle(.borderedProminent)
                .disabled(name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || port <= 0)
            }
        }
        .padding(24)
        .frame(width: 420)
        .onAppear {
            port = model.suggestedServicePort(startingAt: selected.defaultPort)
                ?? selected.defaultPort
        }
    }
}
