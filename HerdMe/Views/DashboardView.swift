import SwiftUI

struct DashboardView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var navigation: AppNavigation
    @EnvironmentObject private var sitesCoordinator: SitesCoordinator
    @EnvironmentObject private var environmentCoordinator: EnvironmentCoordinator
    @EnvironmentObject private var servicesCoordinator: ServicesCoordinator
    @EnvironmentObject private var mailCoordinator: MailCoordinator
    @EnvironmentObject private var dumpsCoordinator: DumpsCoordinator
    @EnvironmentObject private var securityCoordinator: SecuritySetupCoordinator

    private let metricColumns = [
        GridItem(.flexible(minimum: 150), spacing: 12),
        GridItem(.flexible(minimum: 150), spacing: 12),
        GridItem(.flexible(minimum: 150), spacing: 12),
        GridItem(.flexible(minimum: 150), spacing: 12)
    ]

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                header
                LazyVGrid(columns: metricColumns, spacing: 12) {
                    DashboardMetricCard(
                        title: "Sites",
                        value: String(sitesCoordinator.sites.count),
                        detail: String.localizedStringWithFormat(
                            String(localized: "%1$lld of %2$lld running"),
                            Int64(runningSiteCount),
                            Int64(sitesCoordinator.sites.count)
                        ),
                        symbol: "server.rack",
                        tint: .blue
                    ) { navigation.selectedPage = .sites }
                    DashboardMetricCard(
                        title: "Services",
                        value: String(model.configuration.serviceInstances.count),
                        detail: String.localizedStringWithFormat(
                            String(localized: "%1$lld of %2$lld running"),
                            Int64(runningServiceCount),
                            Int64(model.configuration.serviceInstances.count)
                        ),
                        symbol: "externaldrive",
                        tint: .green
                    ) { navigation.selectedPage = .services }
                    DashboardMetricCard(
                        title: "Mail",
                        value: String(mailCoordinator.messages.count),
                        detail: mailCoordinator.isServerRunning
                            ? String(localized: "Capture server running")
                            : String(localized: "Capture server stopped"),
                        symbol: "envelope",
                        tint: .indigo
                    ) { navigation.selectedPage = .mail }
                    DashboardMetricCard(
                        title: "Dumps",
                        value: String(dumpsCoordinator.dumps.count),
                        detail: dumpsCoordinator.isServerRunning
                            ? String(localized: "Capture server running")
                            : String(localized: "Capture server stopped"),
                        symbol: "shippingbox.and.arrow.backward",
                        tint: .orange
                    ) { navigation.selectedPage = .dumps }
                }

                environmentPanel
                healthPanel
                recentActivity
            }
            .padding(.horizontal, 24)
            .padding(.top, 20)
            .padding(.bottom, 20)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .scrollIndicators(.visible)
        .onAppear { model.refresh() }
    }

    private var header: some View {
        HStack(alignment: .firstTextBaseline) {
            VStack(alignment: .leading, spacing: 4) {
                Text("Dashboard")
                    .font(.title.weight(.semibold))
                Text("Your local development environment at a glance.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            if model.isRefreshing {
                ProgressView()
                    .controlSize(.small)
                    .accessibilityLabel("Refreshing dashboard")
            }
            Button {
                model.refresh()
            } label: {
                Image(systemName: "arrow.clockwise")
            }
            .buttonStyle(.bordered)
            .disabled(model.isRefreshing)
            .help("Refresh dashboard")
            .accessibilityLabel("Refresh dashboard")
        }
    }

    private var environmentPanel: some View {
        SettingsPanel {
            VStack(alignment: .leading, spacing: 10) {
                Text("Environment")
                    .font(.headline)
                DashboardStatusRow(
                    title: String(localized: "Sites environment"),
                    status: environmentCoordinator.status.localizedTitle,
                    detail: environmentDetail,
                    color: environmentCoordinator.status.color
                ) { navigation.selectedPage = .sites }
                Divider()
                DashboardStatusRow(
                    title: String(localized: "Local domains"),
                    status: resolverStatus,
                    detail: String.localizedStringWithFormat(
                        String(localized: "Projects use the .%@ domain"),
                        model.configuration.tld
                    ),
                    color: resolverColor
                ) { navigation.selectedPage = .general }
                Divider()
                DashboardStatusRow(
                    title: String(localized: "HTTPS certificate"),
                    status: certificateStatus,
                    detail: environmentCoordinator.isHTTPSActive
                        ? String(localized: "HTTPS is active for local sites")
                        : String(localized: "Open General to configure HTTPS"),
                    color: certificateColor
                ) { navigation.selectedPage = .general }
            }
        }
    }

    private var healthPanel: some View {
        SettingsPanel {
            VStack(alignment: .leading, spacing: 10) {
                HStack(spacing: 8) {
                    Image(systemName: healthIssues.isEmpty ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                        .foregroundStyle(healthIssues.isEmpty ? Color.green : Color.orange)
                    Text(healthIssues.isEmpty ? "Everything is ready" : "Needs attention")
                        .font(.headline)
                }
                if healthIssues.isEmpty {
                    Text("Local domains, certificates, and managed services are ready.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                } else {
                    ForEach(Array(healthIssues.enumerated()), id: \.offset) { _, issue in
                        HStack(alignment: .top, spacing: 9) {
                            Circle()
                                .fill(Color.orange)
                                .frame(width: 6, height: 6)
                                .padding(.top, 6)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(issue.title)
                                    .font(.callout.weight(.medium))
                                Text(issue.detail)
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                            }
                        }
                    }
                }
            }
        }
    }

    private var recentActivity: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Recent Activity")
                .font(.headline)
            HStack(alignment: .top, spacing: 12) {
                DashboardActivityPanel(
                    title: "Mail",
                    symbol: "envelope",
                    emptyMessage: "No captured mail yet",
                    items: mailCoordinator.messages.prefix(4).map {
                        DashboardActivityItem(
                            title: $0.subject,
                            subtitle: $0.sender,
                            date: $0.receivedAt
                        )
                    }
                ) { navigation.selectedPage = .mail }
                DashboardActivityPanel(
                    title: "Dumps",
                    symbol: "shippingbox.and.arrow.backward",
                    emptyMessage: "No captured dumps yet",
                    items: dumpsCoordinator.dumps.prefix(4).map {
                        DashboardActivityItem(
                            title: $0.summary,
                            subtitle: $0.source,
                            date: $0.receivedAt
                        )
                    }
                ) { navigation.selectedPage = .dumps }
            }
        }
    }

    private var runningSiteCount: Int {
        guard environmentCoordinator.status == .running else { return 0 }
        return sitesCoordinator.sites.filter { sitesCoordinator.runtimePorts[$0.id] != nil }.count
    }

    private var runningServiceCount: Int {
        model.configuration.serviceInstances.filter { servicesCoordinator.state(for: $0).isRunning }.count
    }

    private var environmentDetail: String {
        guard environmentCoordinator.status == .running else {
            return String(localized: "Local sites are not currently being served")
        }
        return environmentCoordinator.isHTTPSActive
            ? String(localized: "Local sites are available over HTTPS")
            : String(localized: "Local sites are available over HTTP")
    }

    private var resolverStatus: String {
        if securityCoordinator.networkHelperNeedsUpdate { return String(localized: "Update available") }
        if securityCoordinator.domainResolverState == .managed {
            return securityCoordinator.isDNSServerRunning
                ? String(localized: "Active") : String(localized: "Configured")
        }
        return securityCoordinator.domainResolverState.title
    }

    private var resolverColor: Color {
        if securityCoordinator.domainResolverState == .managed,
            securityCoordinator.isDNSServerRunning,
            !securityCoordinator.networkHelperNeedsUpdate
        {
            return .green
        }
        return securityCoordinator.domainResolverState == .external ? .blue : .orange
    }

    private var certificateStatus: String {
        switch securityCoordinator.certificateTrustState {
        case .trusted: String(localized: "Trusted")
        case .untrusted: String(localized: "Not trusted")
        case .missing: String(localized: "Not created")
        }
    }

    private var certificateColor: Color {
        securityCoordinator.certificateTrustState == .trusted ? .green : .orange
    }

    private var healthIssues: [(title: String, detail: String)] {
        var issues: [(String, String)] = []
        if environmentCoordinator.status == .conflict {
            issues.append(
                (
                    String(localized: "Port conflict"),
                    String(localized: "Another process is using a port required by the local environment.")
                ))
        }
        if securityCoordinator.domainResolverState != .managed {
            issues.append(
                (
                    String(localized: "Local domains are not configured"),
                    String(localized: "Open General and set up local domains.")
                ))
        } else if securityCoordinator.networkHelperNeedsUpdate {
            issues.append(
                (
                    String(localized: "Local domains need an update"),
                    String(localized: "Open General and update the local network helper.")
                ))
        } else if !securityCoordinator.isDNSServerRunning {
            issues.append(
                (
                    String(localized: "Local domain service is stopped"),
                    String(localized: "Refresh or restart the local environment.")
                ))
        }
        if securityCoordinator.certificateTrustState != .trusted {
            issues.append(
                (
                    String(localized: "HTTPS is not trusted"),
                    String(localized: "Open General and trust the HerdMe certificate.")
                ))
        } else if environmentCoordinator.httpsStartupNeedsApproval {
            issues.append(
                (
                    String(localized: "HTTPS needs approval"),
                    String(localized: "Approve Keychain access, then start the environment again.")
                ))
        }
        return issues
    }
}

private struct DashboardMetricCard: View {
    let title: LocalizedStringKey
    let value: String
    let detail: String
    let symbol: String
    let tint: Color
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 10) {
                HStack {
                    Image(systemName: symbol)
                        .foregroundStyle(tint)
                    Spacer()
                    Image(systemName: "chevron.forward")
                        .font(.caption)
                        .foregroundStyle(.tertiary)
                }
                Text(value)
                    .font(.title2.weight(.semibold).monospacedDigit())
                Text(title)
                    .font(.callout.weight(.medium))
                Text(detail)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
            }
            .frame(maxWidth: .infinity, minHeight: 116, alignment: .leading)
            .padding(14)
            .background(Color(nsColor: .controlBackgroundColor))
            .overlay {
                RoundedRectangle(cornerRadius: 8)
                    .stroke(Color(nsColor: .separatorColor), lineWidth: 1)
            }
            .clipShape(RoundedRectangle(cornerRadius: 8))
        }
        .buttonStyle(.plain)
    }
}

private struct DashboardStatusRow: View {
    let title: String
    let status: String
    let detail: String
    let color: Color
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 12) {
                Circle()
                    .fill(color)
                    .frame(width: 8, height: 8)
                VStack(alignment: .leading, spacing: 2) {
                    Text(title)
                        .font(.callout.weight(.medium))
                    Text(detail)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer(minLength: 12)
                Text(status)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                Image(systemName: "chevron.forward")
                    .font(.caption)
                    .foregroundStyle(.tertiary)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

private struct DashboardActivityItem {
    let title: String
    let subtitle: String
    let date: Date
}

private struct DashboardActivityPanel: View {
    let title: LocalizedStringKey
    let symbol: String
    let emptyMessage: LocalizedStringKey
    let items: [DashboardActivityItem]
    let action: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Label(title, systemImage: symbol)
                    .font(.callout.weight(.semibold))
                Spacer()
                Button(action: action) {
                    Image(systemName: "arrow.up.forward")
                }
                .buttonStyle(.borderless)
                .help("View all")
                .accessibilityLabel("View all")
            }
            Divider()
            if items.isEmpty {
                Text(emptyMessage)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, minHeight: 112, alignment: .center)
            } else {
                VStack(spacing: 9) {
                    ForEach(Array(items.enumerated()), id: \.offset) { _, item in
                        HStack(alignment: .firstTextBaseline, spacing: 8) {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(item.title)
                                    .font(.callout)
                                    .lineLimit(1)
                                Text(item.subtitle)
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                                    .lineLimit(1)
                            }
                            Spacer(minLength: 8)
                            Text(item.date, style: .relative)
                                .font(.caption2)
                                .foregroundStyle(.tertiary)
                        }
                    }
                }
                .frame(minHeight: 112, alignment: .top)
            }
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color(nsColor: .controlBackgroundColor))
        .overlay {
            RoundedRectangle(cornerRadius: 8)
                .stroke(Color(nsColor: .separatorColor), lineWidth: 1)
        }
        .clipShape(RoundedRectangle(cornerRadius: 8))
    }
}
