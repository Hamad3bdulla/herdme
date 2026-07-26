import AppKit
import Combine
import SwiftUI

private struct LogContentVersion: Equatable, Sendable {
    let id: LocalLogFile.ID
    let size: Int64
    let modifiedAt: Date
}

private struct LogSnapshot: Sendable {
    let files: [LocalLogFile]
    let selectedFileID: LocalLogFile.ID?
    let contentVersion: LogContentVersion?
    let content: String
    let didLoadContent: Bool
}

private struct LogSource: Identifiable {
    let id: String
    let title: String
    let rootURL: URL
    let fallbackURL: URL
    let isApplication: Bool
}

struct LogsView: View {
    private static let applicationSourceID = "__herdme_application_logs__"

    @EnvironmentObject private var model: AppModel
    @State private var files: [LocalLogFile] = []
    @State private var selectedFileID: LocalLogFile.ID?
    @State private var content = ""
    @State private var query = ""
    @State private var autoScroll = true
    @State private var loadedContentVersion: LogContentVersion?
    @State private var reloadTask: Task<Void, Never>?
    @State private var reloadToken: UUID?

    private let refreshTimer = Timer.publish(every: 1, on: .main, in: .common).autoconnect()

    private var selectedFile: LocalLogFile? {
        files.first { $0.id == selectedFileID }
    }

    private var applicationSource: LogSource {
        let rootURL = model.configurationStore.rootURL.appendingPathComponent("Log")
        return LogSource(
            id: Self.applicationSourceID,
            title: "HerdMe",
            rootURL: rootURL,
            fallbackURL: rootURL,
            isApplication: true
        )
    }

    private var siteSources: [LogSource] {
        model.sites
            .filter { $0.framework == "Laravel" }
            .map { site in
                LogSource(
                    id: site.id,
                    title: site.name,
                    rootURL: site.path.appendingPathComponent("storage/logs", isDirectory: true),
                    fallbackURL: site.path,
                    isApplication: false
                )
            }
    }

    private var selectedSource: LogSource {
        siteSources.first { $0.id == model.selectedLogSiteID } ?? applicationSource
    }

    private var sourceSelection: Binding<String> {
        Binding(
            get: { selectedSource.id },
            set: { model.selectedLogSiteID = $0 == Self.applicationSourceID ? nil : $0 }
        )
    }

    private var displayedContent: String {
        guard !query.isEmpty else { return content }
        return content.components(separatedBy: .newlines)
            .filter { $0.localizedCaseInsensitiveContains(query) }
            .joined(separator: "\n")
    }

    var body: some View {
        VStack(spacing: 0) {
            toolbar
            Divider()
            HStack(spacing: 0) {
                fileList
                Divider()
                logDetail
            }
        }
        .onAppear { reloadFiles(forceContent: true) }
        .onDisappear {
            reloadTask?.cancel()
            reloadTask = nil
            reloadToken = nil
        }
        .onReceive(refreshTimer) { _ in
            if autoScroll { reloadFiles() }
        }
        .onChange(of: selectedFileID) { _ in reloadFiles(forceContent: true) }
        .onChange(of: model.selectedLogSiteID) { _ in resetForSelectedSource() }
    }

    private var toolbar: some View {
        HStack(spacing: 12) {
            Image(systemName: "doc.text.magnifyingglass")
                .foregroundStyle(.secondary)
            Text(selectedFile?.relativePath ?? "Logs")
                .font(.system(size: 18, weight: .medium))
                .lineLimit(1)
            Picker("Log source", selection: sourceSelection) {
                Label(applicationSource.title, systemImage: "app.badge")
                    .tag(applicationSource.id)
                ForEach(siteSources) { source in
                    Label(source.title, systemImage: "shippingbox.fill")
                        .tag(source.id)
                }
            }
            .labelsHidden()
            .frame(width: 180)
            Spacer()
            Toggle("Follow", isOn: $autoScroll)
                .toggleStyle(.checkbox)
            TextField("Search", text: $query)
                .textFieldStyle(.roundedBorder)
                .frame(width: 220)
            Button {
                openSelectedLogFolder()
            } label: {
                Image(systemName: "folder")
            }
            .buttonStyle(.borderless)
            .help("Open log folder")
            .accessibilityLabel("Open log folder")
            Button { reloadFiles() } label: {
                Image(systemName: "arrow.clockwise")
            }
            .buttonStyle(.borderless)
            .help("Refresh logs")
            .accessibilityLabel("Refresh logs")
        }
        .padding(.horizontal, 16)
        .frame(height: 58)
    }

    @ViewBuilder
    private var fileList: some View {
        if files.isEmpty {
            EmptyStateView(
                symbol: "doc.text",
                title: "No Logs Yet",
                message: ""
            )
            .frame(width: 260)
        } else {
            List(files, selection: $selectedFileID) { file in
                VStack(alignment: .leading, spacing: 3) {
                    Text(file.relativePath)
                        .lineLimit(1)
                    HStack {
                        Text(ByteCountFormatter.string(fromByteCount: file.size, countStyle: .file))
                        Text(file.modifiedAt, style: .time)
                    }
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                }
                .tag(file.id)
            }
            .listStyle(.sidebar)
            .frame(width: 260)
        }
    }

    @ViewBuilder
    private var logDetail: some View {
        if selectedFile == nil {
            EmptyStateView(symbol: "doc.text", title: "Select a log", message: "")
        } else {
            ScrollViewReader { proxy in
                ScrollView {
                    Text(displayedContent.isEmpty ? "This log is empty." : displayedContent)
                        .font(.system(size: 12, design: .monospaced))
                        .foregroundStyle(.secondary)
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(16)
                    Color.clear.frame(height: 1).id("log-end")
                }
                .onChange(of: displayedContent) { _ in
                    if autoScroll {
                        DispatchQueue.main.async { proxy.scrollTo("log-end", anchor: .bottom) }
                    }
                }
                .onAppear {
                    if autoScroll { proxy.scrollTo("log-end", anchor: .bottom) }
                }
            }
        }
    }

    private func reloadFiles(forceContent: Bool = false) {
        if !forceContent, reloadTask != nil { return }
        reloadTask?.cancel()

        let token = UUID()
        reloadToken = token
        let rootURL = selectedSource.rootURL
        let store = selectedSource.isApplication ? model.logStore : LogStore(rootURL: rootURL)
        let requestedFileID = selectedFileID
        let previousVersion = loadedContentVersion

        reloadTask = Task { @MainActor in
            let snapshot = await Task.detached(priority: .utility) {
                let files = store.files()
                let selectedFile = files.first(where: { $0.id == requestedFileID }) ?? files.first
                let version = selectedFile.map {
                    LogContentVersion(id: $0.id, size: $0.size, modifiedAt: $0.modifiedAt)
                }
                let shouldLoad = forceContent || version != previousVersion
                guard shouldLoad else {
                    return LogSnapshot(
                        files: files,
                        selectedFileID: selectedFile?.id,
                        contentVersion: version,
                        content: "",
                        didLoadContent: false
                    )
                }
                guard let selectedFile else {
                    return LogSnapshot(
                        files: files,
                        selectedFileID: nil,
                        contentVersion: nil,
                        content: "",
                        didLoadContent: true
                    )
                }
                let content: String
                do {
                    content = try store.contents(of: selectedFile)
                } catch {
                    content = error.localizedDescription
                }
                return LogSnapshot(
                    files: files,
                    selectedFileID: selectedFile.id,
                    contentVersion: version,
                    content: content,
                    didLoadContent: true
                )
            }.value

            guard !Task.isCancelled, reloadToken == token else { return }
            files = snapshot.files
            selectedFileID = snapshot.selectedFileID
            loadedContentVersion = snapshot.contentVersion
            if snapshot.didLoadContent { content = snapshot.content }
            reloadToken = nil
            reloadTask = nil
        }
    }

    private func resetForSelectedSource() {
        reloadTask?.cancel()
        reloadTask = nil
        reloadToken = nil
        files = []
        selectedFileID = nil
        loadedContentVersion = nil
        content = ""
        reloadFiles(forceContent: true)
    }

    private func openSelectedLogFolder() {
        let source = selectedSource
        if source.isApplication {
            try? FileManager.default.createDirectory(
                at: source.rootURL,
                withIntermediateDirectories: true
            )
        }
        let directory = FileManager.default.fileExists(atPath: source.rootURL.path)
            ? source.rootURL
            : source.fallbackURL
        NSWorkspace.shared.open(directory)
    }
}
