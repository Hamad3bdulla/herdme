import AppKit
import SwiftUI

struct MailView: View {
    @EnvironmentObject private var model: AppModel
    @State private var selectedMessageID: CapturedMail.ID?
    @State private var selectedMessage: CapturedMail?
    @State private var isLoadingMessage = false
    @State private var detailTab: DetailTab = .preview
    @State private var search = ""

    private enum DetailTab: String, CaseIterable, Identifiable {
        case preview = "Preview"
        case text = "Text"
        case raw = "Raw"

        var id: String { rawValue }
    }

    private var filteredMessages: [CapturedMailSummary] {
        model.mailMessages.filter { $0.matchesSearch(search) }
    }

    var body: some View {
        PageContainer("Mail") {
            SettingsPanel {
                VStack(alignment: .leading, spacing: 9) {
                    HStack {
                        Label("Mail Server", systemImage: "envelope")
                            .font(.headline)
                        Spacer()
                        HStack(spacing: 5) {
                            Circle()
                                .fill(model.isMailServerRunning ? .green : .secondary)
                                .frame(width: 8, height: 8)
                            Text(model.isMailServerRunning ? "Running" : "Stopped")
                                .foregroundStyle(.secondary)
                        }
                        .accessibilityElement(children: .combine)
                        .accessibilityLabel("Mail server")
                        .accessibilityValue(model.isMailServerRunning ? "Running" : "Stopped")
                        Button {
                            model.isMailServerRunning ? model.stopMailServer() : model.startMailServer()
                        } label: {
                            Image(systemName: model.isMailServerRunning ? "stop.fill" : "play.fill")
                        }
                        .buttonStyle(.borderless)
                        .help(model.isMailServerRunning ? "Stop mail server" : "Start mail server")
                        .accessibilityLabel(model.isMailServerRunning ? "Stop mail server" : "Start mail server")
                    }
                    PanelDivider()
                    SettingRow("SMTP Port") {
                        TextField("", value: $model.configuration.smtpPort, format: .number.grouping(.never))
                            .frame(width: 90)
                            .onSubmit { model.restartMailServer() }
                    }
                    PanelDivider()
                    SettingRow("Environment") {
                        Button("Copy mail .env settings") {
                            NSPasteboard.general.clearContents()
                            NSPasteboard.general.setString(
                                "MAIL_MAILER=smtp\nMAIL_HOST=127.0.0.1\nMAIL_PORT=\(model.configuration.smtpPort)",
                                forType: .string
                            )
                        }
                    }
                }
            }

            SettingsPanel {
                HStack(spacing: 0) {
                    VStack(alignment: .leading, spacing: 0) {
                        HStack {
                            Text("Inbox").font(.headline)
                            Spacer()
                            Text("\(model.mailMessages.count)")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                            Button {
                                selectedMessageID = nil
                                model.clearMail()
                            } label: {
                                Image(systemName: "trash")
                            }
                            .buttonStyle(.borderless)
                            .disabled(model.mailMessages.isEmpty)
                            .help("Delete all messages")
                            .accessibilityLabel("Delete all messages")
                        }
                        .padding(.bottom, 8)

                        TextField("Search messages", text: $search)
                            .textFieldStyle(.roundedBorder)
                            .padding(.bottom, 8)

                        if model.mailMessages.isEmpty {
                            EmptyStateView(symbol: "tray", title: "Inbox is empty", message: "")
                        } else if filteredMessages.isEmpty {
                            EmptyStateView(symbol: "magnifyingglass", title: "No matching messages", message: "")
                        } else {
                            ScrollView {
                                LazyVStack(spacing: 2) {
                                    ForEach(filteredMessages) { message in
                                        Button {
                                            selectedMessageID = message.id
                                        } label: {
                                            VStack(alignment: .leading, spacing: 3) {
                                                Text(message.sender).font(.callout.weight(.medium)).lineLimit(1)
                                                Text(message.subject).font(.caption).lineLimit(1)
                                                Text(message.receivedAt, format: .dateTime.month().day().hour().minute())
                                                    .font(.caption2)
                                                    .foregroundStyle(.secondary)
                                            }
                                            .frame(maxWidth: .infinity, alignment: .leading)
                                            .padding(8)
                                            .background(selectedMessageID == message.id ? Color.accentColor.opacity(0.2) : Color.clear)
                                            .clipShape(RoundedRectangle(cornerRadius: 5))
                                        }
                                        .buttonStyle(.plain)
                                    }
                                }
                            }
                        }
                    }
                    .frame(width: 245)
                    Divider().padding(.horizontal, 14)
                    if let message = selectedMessage, message.id == selectedMessageID {
                        VStack(alignment: .leading, spacing: 9) {
                            HStack {
                                Text(message.subject).font(.title3.weight(.semibold)).lineLimit(2)
                                Spacer()
                                Button {
                                    selectedMessageID = nil
                                    model.deleteMail(message)
                                } label: {
                                    Image(systemName: "trash")
                                }
                                .buttonStyle(.borderless)
                                .help("Delete message")
                                .accessibilityLabel("Delete message")
                            }
                            Text("From: " + message.sender).font(.caption).foregroundStyle(.secondary)
                            Text("To: " + message.recipients.joined(separator: ", ")).font(.caption).foregroundStyle(.secondary)
                            Divider()
                            Picker("View", selection: $detailTab) {
                                ForEach(DetailTab.allCases) { tab in Text(tab.rawValue).tag(tab) }
                            }
                            .pickerStyle(.segmented)
                            .labelsHidden()
                            Group {
                                switch detailTab {
                                case .preview:
                                    MailHTMLPreview(
                                        html: message.htmlBody
                                            ?? "<pre>\(MailMIMEParser.escapedHTML(message.body))</pre>"
                                    )
                                case .text:
                                    ScrollView {
                                        Text(message.body)
                                            .textSelection(.enabled)
                                            .frame(maxWidth: .infinity, alignment: .leading)
                                    }
                                case .raw:
                                    ScrollView {
                                        Text(message.raw)
                                            .font(.system(size: 11, design: .monospaced))
                                            .textSelection(.enabled)
                                            .frame(maxWidth: .infinity, alignment: .leading)
                                    }
                                }
                            }
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                        }
                    } else if isLoadingMessage, selectedMessageID != nil {
                        ProgressView("Loading message")
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                    } else {
                        EmptyStateView(symbol: "envelope.open", title: "Select a message", message: "")
                    }
                }
                .frame(height: 260)
            }
        }
        .onAppear {
            selectedMessageID = selectedMessageID ?? model.mailMessages.first?.id
        }
        .onChange(of: model.mailMessages.first?.id) { newestMessageID in
            if let newestMessageID,
               filteredMessages.contains(where: { $0.id == newestMessageID }) {
                selectedMessageID = newestMessageID
            } else if !filteredMessages.contains(where: { $0.id == selectedMessageID }) {
                selectedMessageID = filteredMessages.first?.id
            }
        }
        .onChange(of: selectedMessageID) { _ in detailTab = .preview }
        .onChange(of: search) { _ in
            if !filteredMessages.contains(where: { $0.id == selectedMessageID }) {
                selectedMessageID = filteredMessages.first?.id
            }
        }
        .task(id: selectedMessageID) {
            await loadSelectedMessage()
        }
    }

    @MainActor
    private func loadSelectedMessage() async {
        guard let selectedMessageID else {
            selectedMessage = nil
            isLoadingMessage = false
            return
        }
        if selectedMessage?.id == selectedMessageID { return }
        selectedMessage = nil
        isLoadingMessage = true
        do {
            let message = try await model.mailMessage(id: selectedMessageID)
            guard !Task.isCancelled, self.selectedMessageID == selectedMessageID else { return }
            selectedMessage = message
        } catch is CancellationError {
            return
        } catch {
            guard !Task.isCancelled, self.selectedMessageID == selectedMessageID else { return }
            model.lastError = error.localizedDescription
        }
        if self.selectedMessageID == selectedMessageID {
            isLoadingMessage = false
        }
    }
}
