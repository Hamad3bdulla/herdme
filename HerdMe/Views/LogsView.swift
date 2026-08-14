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
    @EnvironmentObject private var navigation: AppNavigation
    @EnvironmentObject private var sitesCoordinator: SitesCoordinator
    @State private var files: [LocalLogFile] = []
    @State private var selectedFileID: LocalLogFile.ID?
    @State private var content = ""
    @State private var renderedContent = ""
    @State private var renderedContentRevision = UUID()
    @State private var query = ""
    @State private var autoScroll = true
    @State private var loadedContentVersion: LogContentVersion?
    @State private var reloadTask: Task<Void, Never>?
    @State private var reloadToken: UUID?
    @State private var renderTask: Task<Void, Never>?
    @State private var renderToken: UUID?

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
        sitesCoordinator.sites
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
        siteSources.first { $0.id == navigation.selectedLogSiteID } ?? applicationSource
    }

    private var sourceSelection: Binding<String> {
        Binding(
            get: { selectedSource.id },
            set: { navigation.selectedLogSiteID = $0 == Self.applicationSourceID ? nil : $0 }
        )
    }

    private var logDisplayText: String {
        if !renderedContent.isEmpty { return renderedContent }
        return query.isEmpty ? String(localized: "This log is empty.") : String(localized: "No matching log entries.")
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
            renderTask?.cancel()
            renderTask = nil
            renderToken = nil
        }
        .onReceive(refreshTimer) { _ in
            if autoScroll { reloadFiles() }
        }
        .onChange(of: selectedFileID) { _ in reloadFiles(forceContent: true) }
        .onChange(of: navigation.selectedLogSiteID) { _ in resetForSelectedSource() }
        .onChange(of: query) { newQuery in render(content, matching: newQuery) }
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
            .accessibilityIdentifier("logs.source")
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
            Button {
                reloadFiles()
            } label: {
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
                message: "Application and site logs will appear here."
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
            EmptyStateView(
                symbol: "doc.text",
                title: "Select a log",
                message: "Choose a log file to inspect it."
            )
        } else {
            LogTextView(
                text: logDisplayText,
                revision: renderedContentRevision,
                followsTail: autoScroll
            )
            .accessibilityIdentifier("logs.content")
        }
    }

    private func reloadFiles(forceContent: Bool = false) {
        if !forceContent, reloadTask != nil { return }
        reloadTask?.cancel()

        let token = UUID()
        reloadToken = token
        let source = selectedSource
        let applicationStore = source.isApplication ? model.logStore : nil
        let requestedFileID = selectedFileID
        let previousVersion = loadedContentVersion

        reloadTask = Task { @MainActor in
            guard
                let snapshot = try? await AsyncProcessLifecycle.runDetached(
                    priority: .utility,
                    operation: {
                        try Task.checkCancellation()
                        let store = applicationStore ?? LogStore(rootURL: source.rootURL)
                        let files = store.files()
                        let selectedFile = files.first(where: { $0.id == requestedFileID }) ?? files.first
                        let version = selectedFile.map {
                            LogContentVersion(id: $0.id, size: $0.size, modifiedAt: $0.modifiedAt)
                        }
                        let shouldLoad = forceContent || version != previousVersion
                        let snapshot: LogSnapshot
                        if !shouldLoad {
                            snapshot = LogSnapshot(
                                files: files,
                                selectedFileID: selectedFile?.id,
                                contentVersion: version,
                                content: "",
                                didLoadContent: false
                            )
                        } else if let selectedFile {
                            let content: String
                            do {
                                content = try store.contents(of: selectedFile)
                            } catch {
                                content = error.localizedDescription
                            }
                            snapshot = LogSnapshot(
                                files: files,
                                selectedFileID: selectedFile.id,
                                contentVersion: version,
                                content: content,
                                didLoadContent: true
                            )
                        } else {
                            snapshot = LogSnapshot(
                                files: files,
                                selectedFileID: nil,
                                contentVersion: nil,
                                content: "",
                                didLoadContent: true
                            )
                        }
                        try Task.checkCancellation()
                        return snapshot
                    })
            else { return }

            guard !Task.isCancelled, reloadToken == token else { return }
            files = snapshot.files
            selectedFileID = snapshot.selectedFileID
            loadedContentVersion = snapshot.contentVersion
            if snapshot.didLoadContent {
                content = snapshot.content
                render(snapshot.content, matching: query)
            }
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
        renderedContent = ""
        renderedContentRevision = UUID()
        renderTask?.cancel()
        renderTask = nil
        renderToken = nil
        reloadFiles(forceContent: true)
    }

    private func render(_ rawContent: String, matching query: String) {
        renderTask?.cancel()
        let token = UUID()
        renderToken = token
        renderTask = Task { @MainActor in
            guard
                let result = try? await AsyncProcessLifecycle.runDetached(
                    priority: .userInitiated,
                    operation: {
                        try Self.filteredContent(rawContent, matching: query)
                    }
                )
            else { return }
            guard !Task.isCancelled, renderToken == token else { return }
            renderedContent = result
            renderedContentRevision = token
            renderToken = nil
            renderTask = nil
        }
    }

    nonisolated private static func filteredContent(
        _ content: String,
        matching query: String
    ) throws -> String {
        guard !query.isEmpty else { return content }
        var matchingLines: [Substring] = []
        for (index, line) in content.split(separator: "\n", omittingEmptySubsequences: false).enumerated() {
            if index.isMultiple(of: 256) { try Task.checkCancellation() }
            if line.localizedCaseInsensitiveContains(query) { matchingLines.append(line) }
        }
        try Task.checkCancellation()
        return matchingLines.joined(separator: "\n")
    }

    private func openSelectedLogFolder() {
        let source = selectedSource
        if source.isApplication {
            try? FileManager.default.createDirectory(
                at: source.rootURL,
                withIntermediateDirectories: true
            )
        }
        let directory =
            FileManager.default.fileExists(atPath: source.rootURL.path)
            ? source.rootURL
            : source.fallbackURL
        NSWorkspace.shared.open(directory)
    }
}

private struct LogTextView: NSViewRepresentable {
    let text: String
    let revision: UUID
    let followsTail: Bool

    func makeCoordinator() -> Coordinator {
        Coordinator()
    }

    func makeNSView(context: Context) -> NSScrollView {
        let scrollView = NSScrollView()
        scrollView.drawsBackground = false
        scrollView.hasVerticalScroller = true
        scrollView.hasHorizontalScroller = true
        scrollView.autohidesScrollers = true

        let textView = NSTextView(frame: .zero)
        textView.isEditable = false
        textView.isSelectable = true
        textView.isRichText = false
        textView.drawsBackground = false
        textView.font = .monospacedSystemFont(ofSize: 12, weight: .regular)
        textView.textColor = .secondaryLabelColor
        textView.textContainerInset = NSSize(width: 16, height: 14)
        textView.textContainer?.lineFragmentPadding = 0
        textView.textContainer?.containerSize = NSSize(
            width: CGFloat.greatestFiniteMagnitude,
            height: CGFloat.greatestFiniteMagnitude
        )
        textView.textContainer?.widthTracksTextView = false
        textView.isHorizontallyResizable = true
        textView.isVerticallyResizable = true
        textView.maxSize = NSSize(
            width: CGFloat.greatestFiniteMagnitude,
            height: CGFloat.greatestFiniteMagnitude
        )
        textView.layoutManager?.allowsNonContiguousLayout = true
        scrollView.documentView = textView
        return scrollView
    }

    func updateNSView(_ scrollView: NSScrollView, context: Context) {
        guard let textView = scrollView.documentView as? NSTextView else { return }
        let didChange = context.coordinator.revision != revision
        if didChange {
            textView.string = text
            context.coordinator.revision = revision
        }
        if followsTail, didChange || !context.coordinator.followsTail {
            textView.scrollToEndOfDocument(nil)
        }
        context.coordinator.followsTail = followsTail
    }

    final class Coordinator {
        var revision: UUID?
        var followsTail = false
    }
}
