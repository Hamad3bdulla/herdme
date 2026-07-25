import AppKit
import Combine
import SwiftUI

struct LogsView: View {
    @EnvironmentObject private var model: AppModel
    @State private var files: [LocalLogFile] = []
    @State private var selectedFileID: LocalLogFile.ID?
    @State private var content = ""
    @State private var query = ""
    @State private var autoScroll = true

    private let refreshTimer = Timer.publish(every: 1, on: .main, in: .common).autoconnect()

    private var store: LogStore {
        LogStore(rootURL: model.configurationStore.rootURL.appendingPathComponent("Log"))
    }

    private var selectedFile: LocalLogFile? {
        files.first { $0.id == selectedFileID }
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
        .onAppear { reloadFiles() }
        .onReceive(refreshTimer) { _ in
            if autoScroll { reloadFiles() }
        }
        .onChange(of: selectedFileID) { _ in reloadContent() }
    }

    private var toolbar: some View {
        HStack(spacing: 12) {
            Image(systemName: "doc.text.magnifyingglass")
                .foregroundStyle(.secondary)
            Text(selectedFile?.relativePath ?? "Logs")
                .font(.system(size: 18, weight: .medium))
                .lineLimit(1)
            Spacer()
            Toggle("Auto-scroll", isOn: $autoScroll)
                .toggleStyle(.checkbox)
            TextField("Search", text: $query)
                .textFieldStyle(.roundedBorder)
                .frame(width: 220)
            Button {
                let directory = model.configurationStore.rootURL.appendingPathComponent("Log")
                try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
                NSWorkspace.shared.open(directory)
            } label: {
                Image(systemName: "folder")
            }
            .buttonStyle(.borderless)
            .help("Open log folder")
            Button { reloadFiles() } label: {
                Image(systemName: "arrow.clockwise")
            }
            .buttonStyle(.borderless)
            .help("Refresh logs")
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

    private func reloadFiles() {
        let selected = selectedFileID
        files = store.files()
        selectedFileID = files.contains(where: { $0.id == selected }) ? selected : files.first?.id
        reloadContent()
    }

    private func reloadContent() {
        guard let selectedFile else {
            content = ""
            return
        }
        do {
            content = try store.contents(of: selectedFile)
        } catch {
            content = error.localizedDescription
        }
    }
}
