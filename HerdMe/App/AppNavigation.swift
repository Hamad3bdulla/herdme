import Combine
import Foundation

@MainActor
final class AppNavigation: ObservableObject {
    @Published var selectedPage: SidebarPage = .dashboard
    @Published var selectedSiteID: SiteProject.ID?
    @Published var selectedLogSiteID: SiteProject.ID?

    func showApplicationLogs() {
        selectedLogSiteID = nil
        selectedPage = .logs
    }

    func showLogs(for site: SiteProject) {
        selectedSiteID = site.id
        selectedLogSiteID = site.id
        selectedPage = .logs
    }
}
