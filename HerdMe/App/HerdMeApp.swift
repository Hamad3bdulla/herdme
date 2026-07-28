import AppKit
import Darwin
import SwiftUI

@MainActor
final class HerdMeApplicationDelegate: NSObject, NSApplicationDelegate {
    weak var model: AppModel?
    private var isFinishingTermination = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        guard let iconURL = Bundle.main.url(forResource: "AppIcon", withExtension: "icns"),
            let icon = NSImage(contentsOf: iconURL)
        else { return }
        NSApplication.shared.applicationIconImage = icon
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard let model else { return .terminateNow }
        guard !isFinishingTermination else { return .terminateLater }
        isFinishingTermination = true
        Task {
            await model.shutdown()
            sender.reply(toApplicationShouldTerminate: true)
        }
        return .terminateLater
    }
}

@main
struct HerdMeApp: App {
    @NSApplicationDelegateAdaptor(HerdMeApplicationDelegate.self) private var applicationDelegate
    @StateObject private var model: AppModel
    private let singleInstanceGuard: SingleInstanceGuard?

    init() {
        let environment = ProcessInfo.processInfo.environment
        let configurationStore = AppExecutionContext.configurationStore(environment: environment)
        if AppExecutionContext.isTesting(environment: environment) {
            singleInstanceGuard = nil
        } else {
            let guardInstance = SingleInstanceGuard(
                lockURL: configurationStore.rootURL.appendingPathComponent("herdme.lock")
            )
            guard guardInstance.acquired else {
                Self.activateExistingInstance()
                exit(EXIT_SUCCESS)
            }
            singleInstanceGuard = guardInstance
        }
        _model = StateObject(wrappedValue: AppModel(configurationStore: configurationStore))
    }

    var body: some Scene {
        Window("HerdMe", id: "main") {
            RootView()
                .environmentObject(model)
                .environmentObject(model.applicationSettings)
                .environmentObject(model.navigation)
                .environmentObject(model.mail)
                .environmentObject(model.dumpsCoordinator)
                .environmentObject(model.services)
                .environmentObject(model.runtime)
                .environmentObject(model.sitesCoordinator)
                .environmentObject(model.environment)
                .environmentObject(model.security)
                .frame(minWidth: 730, idealWidth: 730, minHeight: 527, idealHeight: 527)
                .onAppear { applicationDelegate.model = model }
        }
        .defaultSize(width: 730, height: 527)
        .windowToolbarStyle(.unifiedCompact(showsTitle: false))
        .commands {
            CommandGroup(replacing: .appSettings) {
                Button("Settings...") {
                    model.navigation.selectedPage = .general
                    NSApplication.shared.activate(ignoringOtherApps: true)
                }
                .keyboardShortcut(",", modifiers: .command)
            }
        }

        Window("Create a New Site", id: "create-site") {
            CreateSiteWizardView()
                .environmentObject(model)
                .environmentObject(model.navigation)
                .frame(minWidth: 800, idealWidth: 800, minHeight: 600, idealHeight: 600)
        }
        .defaultSize(width: 800, height: 600)
        .windowResizability(.contentSize)

        MenuBarExtra {
            MenuBarContentView()
                .environmentObject(model)
                .environmentObject(model.applicationSettings)
                .environmentObject(model.navigation)
                .environmentObject(model.runtime)
                .environmentObject(model.environment)
        } label: {
            EnvironmentMenuBarStatusLabel()
                .environmentObject(model.environment)
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
