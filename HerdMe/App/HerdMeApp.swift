import AppKit
import Darwin
import SwiftUI

@MainActor
final class HerdMeApplicationDelegate: NSObject, NSApplicationDelegate {
    weak var model: AppModel?

    func applicationDidFinishLaunching(_ notification: Notification) {
        guard let iconURL = Bundle.main.url(forResource: "AppIcon", withExtension: "icns"),
              let icon = NSImage(contentsOf: iconURL) else { return }
        NSApplication.shared.applicationIconImage = icon
    }

    func applicationWillTerminate(_ notification: Notification) {
        model?.shutdown()
    }
}

@main
struct HerdMeApp: App {
    @NSApplicationDelegateAdaptor(HerdMeApplicationDelegate.self) private var applicationDelegate
    @StateObject private var model: AppModel
    private let singleInstanceGuard: SingleInstanceGuard

    init() {
        let configurationStore = ConfigurationStore()
        let guardInstance = SingleInstanceGuard(
            lockURL: configurationStore.rootURL.appendingPathComponent("herdme.lock")
        )
        guard guardInstance.acquired else {
            Self.activateExistingInstance()
            exit(EXIT_SUCCESS)
        }
        singleInstanceGuard = guardInstance
        _model = StateObject(wrappedValue: AppModel(configurationStore: configurationStore))
    }

    var body: some Scene {
        Window("HerdMe", id: "main") {
            RootView()
                .environmentObject(model)
                .frame(minWidth: 730, idealWidth: 730, minHeight: 527, idealHeight: 527)
                .onAppear { applicationDelegate.model = model }
        }
        .defaultSize(width: 730, height: 527)
        .windowToolbarStyle(.unifiedCompact(showsTitle: false))
        .commands {
            CommandGroup(replacing: .appSettings) {
                Button("Settings...") {
                    model.selectedPage = .general
                    NSApplication.shared.activate(ignoringOtherApps: true)
                }
                .keyboardShortcut(",", modifiers: .command)
            }
        }

        Window("Create a New Site", id: "create-site") {
            CreateSiteWizardView()
                .environmentObject(model)
                .frame(minWidth: 800, idealWidth: 800, minHeight: 600, idealHeight: 600)
        }
        .defaultSize(width: 800, height: 600)
        .windowResizability(.contentSize)

        MenuBarExtra {
            MenuBarContentView()
                .environmentObject(model)
        } label: {
            Image(systemName: "h.square.fill")
        }
        .menuBarExtraStyle(.menu)
    }

    private static func activateExistingInstance() {
        guard let bundleIdentifier = Bundle.main.bundleIdentifier else { return }
        NSRunningApplication.runningApplications(withBundleIdentifier: bundleIdentifier)
            .first { $0.processIdentifier != getpid() }?
            .activate(options: [.activateAllWindows])
    }
}
