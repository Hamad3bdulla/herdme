import Foundation
import SwiftUI

enum PortPresentation {
    static func number(_ port: Int) -> String {
        String(port)
    }

    static func endpoint(host: String = "127.0.0.1", port: Int) -> String {
        host + ":" + number(port)
    }
}

struct ProductLink: Identifiable, Equatable, Sendable {
    let title: String
    let address: String
    let systemImage: String

    var id: String { address }

    var url: URL? {
        guard let url = URL(string: address),
            url.scheme == "https",
            url.host != nil
        else { return nil }
        return url
    }
}

enum ProductLinks {
    static let repository = ProductLink(
        title: String(localized: "Repository"),
        address: "https://github.com/Hamad3bdulla/herdme",
        systemImage: "chevron.left.forwardslash.chevron.right"
    )
    static let documentation = ProductLink(
        title: String(localized: "Documentation"),
        address: "https://github.com/Hamad3bdulla/herdme/tree/master/docs",
        systemImage: "book.closed"
    )
    static let releaseNotes = ProductLink(
        title: String(localized: "Release Notes"),
        address: "https://github.com/Hamad3bdulla/herdme/releases",
        systemImage: "doc.text"
    )

    static let all = [repository, documentation, releaseNotes]
}

enum SidebarPage: String, CaseIterable, Identifiable {
    case dashboard = "Dashboard"
    case general = "General"
    case sites = "Sites"
    case php = "PHP"
    case node = "Node"
    case services = "Services"
    case mail = "Mail"
    case dumps = "Dumps"
    case debugger = "Debugger"
    case logs = "Logs"
    case about = "About"

    var id: String { rawValue }

    var localizedTitle: String {
        switch self {
        case .dashboard: String(localized: "Dashboard")
        case .general: String(localized: "General")
        case .sites: String(localized: "Sites")
        case .php: String(localized: "PHP")
        case .node: String(localized: "Node")
        case .services: String(localized: "Services")
        case .mail: String(localized: "Mail")
        case .dumps: String(localized: "Dumps")
        case .debugger: String(localized: "Debugger")
        case .logs: String(localized: "Logs")
        case .about: String(localized: "About")
        }
    }

    var symbol: String {
        switch self {
        case .dashboard: "gauge.with.dots.needle.67percent"
        case .general: "person.crop.circle"
        case .sites: "server.rack"
        case .php: "chevron.left.forwardslash.chevron.right"
        case .node: "hexagon"
        case .services: "externaldrive"
        case .mail: "envelope"
        case .dumps: "shippingbox.and.arrow.backward"
        case .debugger: "ladybug"
        case .logs: "doc.text.magnifyingglass"
        case .about: "questionmark.square"
        }
    }

    var tint: Color {
        switch self {
        case .dashboard: Color(red: 0.16, green: 0.55, blue: 0.36)
        case .general, .sites: .gray
        case .php: Color(red: 0.37, green: 0.40, blue: 0.58)
        case .node: Color(red: 0.27, green: 0.58, blue: 0.29)
        case .services, .mail, .dumps, .debugger: Color(red: 0.88, green: 0.12, blue: 0.13)
        case .logs: Color(red: 0.28, green: 0.50, blue: 0.90)
        case .about: .black
        }
    }

    static var visibleCases: [SidebarPage] {
        [.dashboard, .general, .sites, .php, .node, .services, .mail, .dumps, .debugger, .logs, .about]
    }
}

struct SiteProject: Identifiable, Hashable, Sendable {
    let path: URL
    let name: String
    let framework: String
    let isLinked: Bool
    var phpVersion: String?
    var nodeVersion: String?
    var registrationPath: URL? = nil

    var id: String { path.path }

    func domain(tld: String) -> String {
        "\(Self.dnsLabel(for: name)).\(tld)"
    }

    static func dnsLabel(for name: String) -> String {
        let original = name.lowercased()
        if isValidDNSLabel(original) { return original }

        let allowed = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyz0123456789-")
        var normalized = String(original.unicodeScalars.map { allowed.contains($0) ? Character(String($0)) : "-" })
        while normalized.contains("--") {
            normalized = normalized.replacingOccurrences(of: "--", with: "-")
        }
        normalized = normalized.trimmingCharacters(in: CharacterSet(charactersIn: "-"))

        let hash = original.utf8.reduce(UInt32(2_166_136_261)) { value, byte in
            (value ^ UInt32(byte)) &* 16_777_619
        }
        let suffix = String(format: "%06x", hash & 0x00ff_ffff)
        let base = String(normalized.prefix(56)).trimmingCharacters(in: CharacterSet(charactersIn: "-"))
        return base.isEmpty ? "site-\(suffix)" : "\(base)-\(suffix)"
    }

    private static func isValidDNSLabel(_ value: String) -> Bool {
        guard !value.isEmpty, value.utf8.count <= 63,
            value.first?.isASCII == true, value.first?.isLetter == true || value.first?.isNumber == true,
            value.last?.isASCII == true, value.last?.isLetter == true || value.last?.isNumber == true
        else {
            return false
        }
        return value.utf8.allSatisfy {
            ($0 >= 97 && $0 <= 122) || ($0 >= 48 && $0 <= 57) || $0 == 45
        }
    }
}

struct RuntimeVersion: Identifiable, Hashable, Sendable {
    let cycle: String
    var installedVersion: String?
    var isActive: Bool

    var id: String { cycle }
    var isInstalled: Bool { installedVersion != nil }
}

struct AppUpdateNotice: Identifiable {
    let id = UUID()
    let title: String
    let message: String
    let downloadURL: URL?
}

enum ServiceCategory: String, CaseIterable {
    case database = "Databases"
    case cache = "Queues & Cache"
    case search = "Search"
    case storage = "Storage"
    case realtime = "Realtime"
}

struct ServiceDefinition: Identifiable, Hashable, Sendable {
    let id: String
    let name: String
    let category: ServiceCategory
    let defaultPort: Int
    let latestVersion: String
    let symbol: String
}

struct ServiceInstance: Identifiable, Hashable, Codable, Sendable {
    let id: UUID
    var definitionID: String
    var name: String
    var version: String
    var port: Int
    var isRunning: Bool
    var startAutomatically: Bool

    init(
        id: UUID,
        definitionID: String,
        name: String,
        version: String,
        port: Int,
        isRunning: Bool,
        startAutomatically: Bool = false
    ) {
        self.id = id
        self.definitionID = definitionID
        self.name = name
        self.version = version
        self.port = port
        self.isRunning = isRunning
        self.startAutomatically = startAutomatically
    }

    private enum CodingKeys: String, CodingKey {
        case id
        case definitionID
        case name
        case version
        case port
        case isRunning
        case startAutomatically
    }

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        id = try values.decode(UUID.self, forKey: .id)
        definitionID = try values.decode(String.self, forKey: .definitionID)
        name = try values.decode(String.self, forKey: .name)
        version = try values.decode(String.self, forKey: .version)
        port = try values.decode(Int.self, forKey: .port)
        isRunning = try values.decodeIfPresent(Bool.self, forKey: .isRunning) ?? false
        startAutomatically = try values.decodeIfPresent(Bool.self, forKey: .startAutomatically) ?? true
    }
}

enum AppTheme: String, CaseIterable, Codable, Sendable {
    case automatic = "auto"
    case light
    case dark

    init(from decoder: Decoder) throws {
        let value = try decoder.singleValueContainer().decode(String.self).lowercased()
        self = Self(rawValue: value) ?? .automatic
    }

    var localizedTitle: String {
        switch self {
        case .automatic: String(localized: "Auto", comment: "Follow the system appearance")
        case .light: String(localized: "Light", comment: "Use the light appearance")
        case .dark: String(localized: "Dark", comment: "Use the dark appearance")
        }
    }
}

struct AppConfiguration: Codable {
    var configSchemaVersion: Int
    var parkPaths: [String]
    var tld: String
    var selectedPHP: String
    var startAutomatically: Bool
    var launchAtLogin: Bool
    var automaticUpdates: Bool
    var updateChannel: String
    var ide: String
    var theme: AppTheme
    var smtpPort: Int
    var dumpPort: Int
    var sitePreviews: Bool
    var serviceInstances: [ServiceInstance]
    var independenceMigrationVersion: Int
    var onboardingCompleted: Bool

    init(
        configSchemaVersion: Int,
        parkPaths: [String],
        tld: String,
        selectedPHP: String,
        startAutomatically: Bool,
        launchAtLogin: Bool,
        automaticUpdates: Bool,
        updateChannel: String,
        ide: String,
        theme: AppTheme,
        smtpPort: Int,
        dumpPort: Int,
        sitePreviews: Bool,
        serviceInstances: [ServiceInstance],
        independenceMigrationVersion: Int,
        onboardingCompleted: Bool = true
    ) {
        self.configSchemaVersion = configSchemaVersion
        self.parkPaths = parkPaths
        self.tld = tld
        self.selectedPHP = selectedPHP
        self.startAutomatically = startAutomatically
        self.launchAtLogin = launchAtLogin
        self.automaticUpdates = automaticUpdates
        self.updateChannel = updateChannel
        self.ide = ide
        self.theme = theme
        self.smtpPort = smtpPort
        self.dumpPort = dumpPort
        self.sitePreviews = sitePreviews
        self.serviceInstances = serviceInstances
        self.independenceMigrationVersion = independenceMigrationVersion
        self.onboardingCompleted = onboardingCompleted
    }

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        let defaults = Self.default
        configSchemaVersion =
            try values.decodeIfPresent(
                Int.self,
                forKey: .configSchemaVersion
            ) ?? 0
        parkPaths = try values.decodeIfPresent([String].self, forKey: .parkPaths) ?? defaults.parkPaths
        tld = try values.decodeIfPresent(String.self, forKey: .tld) ?? defaults.tld
        selectedPHP = try values.decodeIfPresent(String.self, forKey: .selectedPHP) ?? defaults.selectedPHP
        startAutomatically = try values.decodeIfPresent(Bool.self, forKey: .startAutomatically) ?? false
        launchAtLogin = try values.decodeIfPresent(Bool.self, forKey: .launchAtLogin) ?? defaults.launchAtLogin
        automaticUpdates = try values.decodeIfPresent(Bool.self, forKey: .automaticUpdates) ?? defaults.automaticUpdates
        updateChannel = try values.decodeIfPresent(String.self, forKey: .updateChannel) ?? defaults.updateChannel
        ide = try values.decodeIfPresent(String.self, forKey: .ide) ?? defaults.ide
        theme = try values.decodeIfPresent(AppTheme.self, forKey: .theme) ?? defaults.theme
        smtpPort = try values.decodeIfPresent(Int.self, forKey: .smtpPort) ?? defaults.smtpPort
        dumpPort = try values.decodeIfPresent(Int.self, forKey: .dumpPort) ?? defaults.dumpPort
        sitePreviews = try values.decodeIfPresent(Bool.self, forKey: .sitePreviews) ?? defaults.sitePreviews
        serviceInstances = try values.decodeIfPresent([ServiceInstance].self, forKey: .serviceInstances) ?? []
        independenceMigrationVersion =
            try values.decodeIfPresent(
                Int.self,
                forKey: .independenceMigrationVersion
            ) ?? 0
        // A missing key belongs to an installation created before onboarding existed.
        onboardingCompleted =
            try values.decodeIfPresent(
                Bool.self,
                forKey: .onboardingCompleted
            ) ?? true
    }

    static var `default`: AppConfiguration {
        let home = FileManager.default.homeDirectoryForCurrentUser
        return AppConfiguration(
            configSchemaVersion: ConfigurationStore.currentConfigSchemaVersion,
            parkPaths: [home.appendingPathComponent("HerdMe").path],
            tld: "test",
            selectedPHP: RuntimeCatalog.defaultPHPCycle,
            startAutomatically: false,
            launchAtLogin: false,
            automaticUpdates: true,
            updateChannel: "Stable",
            ide: "VSCode",
            theme: .automatic,
            smtpPort: 2525,
            dumpPort: 9912,
            sitePreviews: true,
            serviceInstances: [],
            independenceMigrationVersion: ConfigurationStore.currentIndependenceMigrationVersion,
            onboardingCompleted: false
        )
    }
}

enum OnboardingStage: String, CaseIterable, Equatable {
    case welcome
    case localDomains
    case certificate
    case php
    case composer
    case node
    case finishing
    case completed

    static let installationStages: [OnboardingStage] = [
        .localDomains,
        .certificate,
        .php,
        .composer,
        .node,
        .finishing
    ]

    var title: String {
        switch self {
        case .welcome: String(localized: "Welcome to HerdMe")
        case .localDomains: String(localized: "Setting up local .test domains")
        case .certificate: String(localized: "Trusting the local HTTPS certificate")
        case .php:
            String.localizedStringWithFormat(
                String(localized: "Installing PHP %@"),
                RuntimeCatalog.defaultPHPCycle
            )
        case .composer: String(localized: "Installing Composer and Laravel Installer")
        case .node:
            String.localizedStringWithFormat(
                String(localized: "Installing Node.js %@"),
                RuntimeCatalog.defaultNodeMajor
            )
        case .finishing: String(localized: "Finishing setup")
        case .completed: String(localized: "HerdMe is ready")
        }
    }

    var detail: String {
        switch self {
        case .welcome:
            String(localized: "HerdMe needs to prepare the local development environment. macOS may ask for administrator approval.")
        case .localDomains:
            String(localized: "Preparing private routing for projects that use the .test domain.")
        case .certificate:
            String(localized: "Adding the HerdMe local certificate authority to the system keychain.")
        case .php:
            String(localized: "Installing the default runtime and checking every extension required by Laravel.")
        case .composer:
            String(localized: "Preparing the managed PHP tools used to create Laravel projects.")
        case .node:
            String(localized: "Preparing the default JavaScript runtime and npm.")
        case .finishing:
            String(localized: "Saving the verified setup and refreshing the local environment.")
        case .completed:
            String(localized: "The default local development environment has been installed successfully.")
        }
    }
}

enum EnvironmentStatus: String {
    case running = "Running"
    case stopped = "Stopped"
    case conflict = "Port Conflict"
    case starting = "Starting"
    case stopping = "Stopping"

    var localizedTitle: String {
        switch self {
        case .running: String(localized: "Running")
        case .stopped: String(localized: "Stopped")
        case .conflict: String(localized: "Port Conflict")
        case .starting: String(localized: "Starting")
        case .stopping: String(localized: "Stopping")
        }
    }

    var color: Color {
        switch self {
        case .running: .green
        case .stopped: .secondary
        case .conflict: .orange
        case .starting, .stopping: .yellow
        }
    }
}

struct CapturedMail: Identifiable, Hashable, Codable, Sendable {
    let id: UUID
    let sender: String
    let recipients: [String]
    let subject: String
    let receivedAt: Date
    let body: String
    let raw: String
    let htmlBody: String?

    var summary: CapturedMailSummary {
        CapturedMailSummary(
            id: id,
            sender: sender,
            recipients: recipients,
            subject: subject,
            receivedAt: receivedAt
        )
    }

    func matchesSearch(_ query: String) -> Bool {
        summary.matchesSearch(query)
    }

    static func parse(sender: String, recipients: [String], raw: String) -> CapturedMail {
        let normalized = raw.replacingOccurrences(of: "\r\n", with: "\n")
        let sections = normalized.components(separatedBy: "\n\n")
        let headerText = sections.first ?? ""
        let rawBody = sections.dropFirst().joined(separator: "\n\n")
        var headers: [String: String] = [:]
        var currentKey: String?

        for line in headerText.components(separatedBy: "\n") {
            if line.first?.isWhitespace == true, let currentKey {
                headers[currentKey, default: ""] += " " + line.trimmingCharacters(in: .whitespaces)
                continue
            }
            guard let separator = line.firstIndex(of: ":") else { continue }
            let key = String(line[..<separator]).lowercased()
            let value = String(line[line.index(after: separator)...]).trimmingCharacters(in: .whitespaces)
            headers[key] = value
            currentKey = key
        }

        let content = MailMIMEParser.parse(raw)
        let body =
            content.plainText?.trimmingCharacters(in: .whitespacesAndNewlines)
            ?? content.html.map(MailMIMEParser.plainText(fromHTML:))
            ?? rawBody
        return CapturedMail(
            id: UUID(),
            sender: headers["from"] ?? sender,
            recipients: recipients.isEmpty ? [headers["to"] ?? "Unknown recipient"] : recipients,
            subject: MailMIMEParser.decodedHeader(headers["subject"] ?? "(No subject)"),
            receivedAt: Date(),
            body: body,
            raw: raw,
            htmlBody: content.html
        )
    }
}

struct CapturedMailSummary: Identifiable, Hashable, Codable, Sendable {
    let id: UUID
    let sender: String
    let recipients: [String]
    let subject: String
    let receivedAt: Date

    func matchesSearch(_ query: String) -> Bool {
        let normalized = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !normalized.isEmpty else { return true }
        let searchableValues = [
            sender,
            recipients.joined(separator: " "),
            subject,
            receivedAt.formatted(date: .numeric, time: .shortened)
        ]
        return searchableValues.contains {
            $0.localizedCaseInsensitiveContains(normalized)
        }
    }
}

struct CapturedDump: Identifiable, Hashable, Codable, Sendable {
    let id: UUID
    let receivedAt: Date
    let source: String
    let summary: String
    let payload: String

    static func decode(payload: String) -> CapturedDump {
        guard let data = Data(base64Encoded: payload) else {
            return CapturedDump(
                id: UUID(),
                receivedAt: Date(),
                source: "Unknown source",
                summary: "Unable to decode VarDumper payload.",
                payload: payload
            )
        }
        do {
            var parser = PHPSerializationParser(data: data)
            let value = try parser.parse()
            let source = value.firstString(forKeysContaining: ["file", "source"]) ?? "Local application"
            return CapturedDump(
                id: UUID(),
                receivedAt: Date(),
                source: source,
                summary: value.rendered(),
                payload: payload
            )
        } catch {
            return CapturedDump(
                id: UUID(),
                receivedAt: Date(),
                source: "Unknown source",
                summary: "Unable to parse VarDumper payload: " + error.localizedDescription,
                payload: payload
            )
        }
    }
}
