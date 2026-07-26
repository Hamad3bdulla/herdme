import AppKit
import SwiftUI

struct MenuBarContentView: View {
    @EnvironmentObject private var model: AppModel
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
        Button(model.environmentStatus == .running ? "Stop all" : "Start all") {
            model.toggleEnvironment()
        }
        Divider()
        Menu("Use PHP \(model.configuration.selectedPHP)") {
            ForEach(model.phpVersions.filter(\.isInstalled)) { runtime in
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
            .disabled(model.isCheckingForUpdates)
        Divider()
        Button("Quit HerdMe") {
            NSApplication.shared.terminate(nil)
        }
    }

    private func show(_ page: SidebarPage) {
        if page == .logs {
            model.showApplicationLogs()
        } else {
            model.selectedPage = page
        }
        openWindow(id: "main")
        NSApplication.shared.activate(ignoringOtherApps: true)
    }
}
