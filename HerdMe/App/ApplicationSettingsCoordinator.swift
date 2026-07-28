import Combine
import Foundation

@MainActor
final class ApplicationSettingsCoordinator: ObservableObject {
    @Published private(set) var launchAtLoginRequiresApproval = false
    @Published private(set) var isCheckingForUpdates = false
    @Published var updateNotice: AppUpdateNotice?

    private let appUpdateManager: (any AppUpdateChecking)?
    private let launchAtLoginManager: any LaunchAtLoginManaging
    private var updateTask: Task<Void, Never>?
    private var didShutdown = false

    init(
        appUpdateManager: (any AppUpdateChecking)? = AppUpdateManager.configured(),
        launchAtLoginManager: any LaunchAtLoginManaging = LaunchAtLoginManager()
    ) {
        self.appUpdateManager = appUpdateManager
        self.launchAtLoginManager = launchAtLoginManager
    }

    deinit {
        updateTask?.cancel()
    }

    func refreshLaunchAtLogin() -> LaunchAtLoginStatus {
        let status = launchAtLoginManager.status()
        launchAtLoginRequiresApproval = status.requiresApproval
        return status
    }

    func setLaunchAtLogin(_ enabled: Bool) throws -> LaunchAtLoginStatus {
        try launchAtLoginManager.setEnabled(enabled)
        return refreshLaunchAtLogin()
    }

    func openLoginItemsSettings() {
        launchAtLoginManager.openSystemSettings()
    }

    func checkForUpdates(channel: String, userInitiated: Bool) {
        guard !didShutdown, updateTask == nil else { return }
        guard let appUpdateManager else {
            if userInitiated {
                updateNotice = AppUpdateNotice(
                    title: "Updates unavailable",
                    message: "This build does not contain an update feed.",
                    downloadURL: nil
                )
            }
            return
        }

        isCheckingForUpdates = true
        updateTask = Task { [weak self, appUpdateManager] in
            do {
                let result = try await appUpdateManager.check(channel: channel)
                guard !Task.isCancelled else {
                    self?.finishUpdateCheck()
                    return
                }
                self?.apply(result, channel: channel, userInitiated: userInitiated)
            } catch {
                guard !Task.isCancelled else {
                    self?.finishUpdateCheck()
                    return
                }
                if userInitiated {
                    self?.updateNotice = AppUpdateNotice(
                        title: "Update check failed",
                        message: error.localizedDescription,
                        downloadURL: nil
                    )
                }
            }
            self?.finishUpdateCheck()
        }
    }

    func waitForUpdateCheck() async {
        await updateTask?.value
    }

    func shutdown() {
        didShutdown = true
        updateTask?.cancel()
        updateTask = nil
        isCheckingForUpdates = false
    }

    private func apply(
        _ result: AppUpdateResult,
        channel: String,
        userInitiated: Bool
    ) {
        guard !didShutdown else { return }
        switch result {
        case .upToDate(let version):
            if userInitiated {
                updateNotice = AppUpdateNotice(
                    title: "HerdMe is up to date",
                    message: "Version \(version) is the newest \(channel.lowercased()) release.",
                    downloadURL: nil
                )
            }
        case .available(let release):
            updateNotice = AppUpdateNotice(
                title: "HerdMe \(release.version) is available",
                message: release.notes,
                downloadURL: release.platformDownloadURL
            )
        }
    }

    private func finishUpdateCheck() {
        updateTask = nil
        isCheckingForUpdates = false
    }
}
