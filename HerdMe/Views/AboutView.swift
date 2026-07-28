import AppKit
import SwiftUI

private struct AboutDocument: Identifiable {
    let id = UUID()
    let title: String
    let body: String
}

struct AboutView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var applicationSettings: ApplicationSettingsCoordinator
    @State private var presentedDocument: AboutDocument?
    @State private var versionCopied = false
    @State private var copyResetTask: Task<Void, Never>?

    private var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "Development"
    }

    private var build: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "0"
    }

    private var versionSummary: String {
        "HerdMe " + version + " (Build " + build + ")"
    }

    var body: some View {
        PageContainer("About") {
            VStack(alignment: .leading, spacing: 18) {
                HStack(spacing: 28) {
                    Image(nsImage: NSApplication.shared.applicationIconImage)
                        .resizable()
                        .scaledToFit()
                        .shadow(color: .black.opacity(0.18), radius: 8, y: 3)
                        .frame(width: 160, height: 160)
                    VStack(alignment: .leading, spacing: 8) {
                        Text("HerdMe").font(.system(size: 28, weight: .semibold))
                        Text("Version \(version) (Build \(build))")
                        Text("Independent open-source local development environment")
                            .foregroundStyle(.secondary)
                        Text("MIT License")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Spacer()
                }
                Divider()
                HStack(spacing: 10) {
                    ForEach(ProductLinks.all) { link in
                        if let destination = link.url {
                            Link(destination: destination) {
                                Label(link.title, systemImage: link.systemImage)
                            }
                            .buttonStyle(.bordered)
                        }
                    }
                    Spacer()
                }
                HStack(spacing: 10) {
                    Button(action: copyVersion) {
                        Label(
                            versionCopied
                                ? String(localized: "Copied")
                                : String(localized: "Copy Version"),
                            systemImage: versionCopied ? "checkmark" : "doc.on.doc"
                        )
                        .frame(minWidth: 92)
                    }
                    .buttonStyle(.bordered)
                    .help("Copy the HerdMe version and build number")

                    Button {
                        model.checkForUpdates()
                    } label: {
                        HStack(spacing: 6) {
                            if applicationSettings.isCheckingForUpdates {
                                ProgressView().controlSize(.small)
                            }
                            Label(
                                applicationSettings.isCheckingForUpdates
                                    ? String(localized: "Checking")
                                    : String(localized: "Check for Updates"),
                                systemImage: "arrow.triangle.2.circlepath"
                            )
                        }
                        .frame(minWidth: 126)
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(applicationSettings.isCheckingForUpdates)

                    Text(
                        String.localizedStringWithFormat(
                            String(localized: "%@ channel"),
                            localizedUpdateChannel
                        )
                    )
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    Spacer()
                }
                Divider()
                HStack(spacing: 10) {
                    Button("MIT License") {
                        showResource(
                            "LICENSE",
                            extension: nil,
                            title: String(localized: "MIT License")
                        )
                    }
                    Button("Acknowledgements") {
                        showResource(
                            "THIRD_PARTY",
                            extension: "md",
                            title: String(localized: "Acknowledgements")
                        )
                    }
                    Spacer()
                }
            }
            .padding(22)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Color(nsColor: .controlBackgroundColor))
            .overlay { RoundedRectangle(cornerRadius: 8).stroke(.separator) }
            .clipShape(RoundedRectangle(cornerRadius: 8))
        }
        .sheet(item: $presentedDocument) { document in
            VStack(alignment: .leading, spacing: 12) {
                HStack {
                    Text(document.title).font(.title2.weight(.semibold))
                    Spacer()
                    Button("Done") { presentedDocument = nil }
                        .keyboardShortcut(.defaultAction)
                }
                ScrollView {
                    Text(document.body)
                        .font(.system(size: 12, design: .monospaced))
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
            .padding(20)
            .frame(width: 680, height: 480)
        }
        .onDisappear {
            copyResetTask?.cancel()
            copyResetTask = nil
        }
    }

    private func copyVersion() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(versionSummary, forType: .string)
        versionCopied = true
        copyResetTask?.cancel()
        copyResetTask = Task { @MainActor in
            do {
                try await Task.sleep(for: .seconds(2))
            } catch {
                return
            }
            guard !Task.isCancelled else { return }
            versionCopied = false
            copyResetTask = nil
        }
    }

    private func showResource(_ name: String, extension fileExtension: String?, title: String) {
        let body: String
        if let url = Bundle.main.url(forResource: name, withExtension: fileExtension),
            let contents = try? String(contentsOf: url, encoding: .utf8)
        {
            body = contents
        } else {
            body = String(localized: "The bundled document could not be loaded.")
        }
        presentedDocument = AboutDocument(title: title, body: body)
    }

    private var localizedUpdateChannel: String {
        switch model.configuration.updateChannel.lowercased() {
        case "beta": String(localized: "Beta")
        default: String(localized: "Stable")
        }
    }
}
