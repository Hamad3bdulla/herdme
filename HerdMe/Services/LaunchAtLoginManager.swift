import Foundation
import ServiceManagement

struct LaunchAtLoginStatus: Equatable {
    let isEnabled: Bool
    let requiresApproval: Bool
}

@MainActor
protocol LaunchAtLoginManaging {
    func status() -> LaunchAtLoginStatus
    func setEnabled(_ enabled: Bool) throws
    func openSystemSettings()
}

@MainActor
struct LaunchAtLoginManager: LaunchAtLoginManaging {
    func status() -> LaunchAtLoginStatus {
        switch SMAppService.mainApp.status {
        case .enabled:
            LaunchAtLoginStatus(isEnabled: true, requiresApproval: false)
        case .requiresApproval:
            LaunchAtLoginStatus(isEnabled: false, requiresApproval: true)
        case .notRegistered, .notFound:
            LaunchAtLoginStatus(isEnabled: false, requiresApproval: false)
        @unknown default:
            LaunchAtLoginStatus(isEnabled: false, requiresApproval: false)
        }
    }

    func setEnabled(_ enabled: Bool) throws {
        let service = SMAppService.mainApp
        if enabled {
            guard service.status != .enabled else { return }
            try service.register()
        } else {
            guard service.status != .notRegistered else { return }
            try service.unregister()
        }
    }

    func openSystemSettings() {
        SMAppService.openSystemSettingsLoginItems()
    }
}
