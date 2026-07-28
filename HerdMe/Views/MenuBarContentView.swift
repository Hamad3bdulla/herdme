import AppKit
import SwiftUI

struct MenuBarStatusLabel: View {
    let status: EnvironmentStatus

    var body: some View {
        ZStack(alignment: .bottomTrailing) {
            Image(systemName: "h.square.fill")
                .font(.system(size: 16, weight: .semibold))
                .symbolRenderingMode(.monochrome)

            Circle()
                .fill(indicatorColor)
                .overlay {
                    Circle()
                        .stroke(.primary.opacity(0.75), lineWidth: 0.75)
                }
                .frame(width: 6, height: 6)
        }
        .frame(width: 18, height: 18)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("HerdMe")
        .accessibilityValue(status.localizedTitle)
        .help("HerdMe: \(status.localizedTitle)")
    }

    private var indicatorColor: Color {
        switch status {
        case .running:
            .green
        case .stopped:
            .red
        case .conflict:
            .orange
        case .starting, .stopping:
            .yellow
        }
    }
}

struct EnvironmentMenuBarStatusLabel: View {
    @EnvironmentObject private var environmentCoordinator: EnvironmentCoordinator

    var body: some View {
        MenuBarStatusLabel(status: environmentCoordinator.status)
    }
}

struct MenuBarContentView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var applicationSettings: ApplicationSettingsCoordinator
    @EnvironmentObject private var navigation: AppNavigation
    @EnvironmentObject private var runtimeCoordinator: RuntimeCoordinator
    @EnvironmentObject private var environmentCoordinator: EnvironmentCoordinator
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Button("Sites") { show(.sites) }
        Button("Create New Site") {
            openWindow(id: "create-site")
            NSApplication.shared.activate(ignoringOtherApps: true)
        }
        Divider()
        Button("Dumps") { show(.dumps) }
        Button("Mail") { show(.mail) }
        Button("Logs") { show(.logs) }
        Button("Services") { show(.services) }
        Divider()
        Button(environmentCoordinator.status == .running ? "Stop all" : "Start all") {
            model.toggleEnvironment()
        }
        Divider()
        Menu("Use PHP \(model.configuration.selectedPHP)") {
            ForEach(runtimeCoordinator.phpVersions.filter(\.isInstalled)) { runtime in
                Button {
                    model.setActivePHP(runtime.cycle)
                } label: {
                    if runtime.cycle == model.configuration.selectedPHP {
                        Label("PHP \(runtime.cycle)", systemImage: "checkmark")
                    } else {
                        Text("PHP \(runtime.cycle)")
                    }
                }
            }
        }
        Button("Open configuration files") { model.openConfigurationDirectory() }
        Divider()
        Button("Settings") { show(.general) }
        Button("Check for Updates...") { model.checkForUpdates() }
            .disabled(applicationSettings.isCheckingForUpdates)
        Divider()
        Button("Quit HerdMe") {
            NSApplication.shared.terminate(nil)
        }
    }

    private func show(_ page: SidebarPage) {
        if page == .logs {
            navigation.showApplicationLogs()
        } else {
            navigation.selectedPage = page
        }
        openWindow(id: "main")
        NSApplication.shared.activate(ignoringOtherApps: true)
    }
}
