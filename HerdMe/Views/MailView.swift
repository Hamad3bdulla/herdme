import AppKit
import SwiftUI

struct MailView: View {
    @EnvironmentObject private var model: AppModel
    @State private var selectedMessageID: CapturedMail.ID?
    @State private var detailTab: DetailTab = .preview

    private enum DetailTab: String, CaseIterable, Identifiable {
        case preview = "Preview"
        case text = "Text"
        case raw = "Raw"

        var id: String { rawValue }
    }

    private var selectedMessage: CapturedMail? {
        model.mailMessages.first { $0.id == selectedMessageID }
    }

    var body: some View {
        PageContainer("Mail") {
            SettingsPanel {
                VStack(alignment: .leading, spacing: 9) {
                    HStack {
                        Label("Mail Server", systemImage: "envelope")
                            .font(.headline)
                        Spacer()
                        Circle()
                            .fill(model.isMailServerRunning ? .green : .secondary)
                            .frame(width: 8, height: 8)
                        Text(model.isMailServerRunning ? "Running" : "Stopped")
                            .foregroundStyle(.secondary)
                        Button {
                            model.isMailServerRunning ? model.stopMailServer() : model.startMailServer()
                        } label: {
                            Image(systemName: model.isMailServerRunning ? "stop.fill" : "play.fill")
                        }
                        .buttonStyle(.borderless)
                        .help(model.isMailServerRunning ? "Stop mail server" : "Start mail server")
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
                        }
                        .padding(.bottom, 8)

                        if model.mailMessages.isEmpty {
                            EmptyStateView(symbol: "tray", title: "Inbox is empty", message: "")
                        } else {
                            ScrollView {
                                LazyVStack(spacing: 2) {
                                    ForEach(model.mailMessages) { message in
                                        Button {
                                            selectedMessageID = message.id
                                        } label: {
                                            VStack(alignment: .leading, spacing: 3) {
                                                Text(message.sender).font(.callout.weight(.medium)).lineLimit(1)
                                                Text(message.subject).font(.caption).lineLimit(1)
                                                Text(message.receivedAt, style: .time)
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
                    if let message = selectedMessage {
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
                                            ?? "<pre style=\"white-space:pre-wrap\">\(MailMIMEParser.escapedHTML(message.body))</pre>"
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
            selectedMessageID = newestMessageID
        }
        .onChange(of: selectedMessageID) { _ in detailTab = .preview }
    }
}
