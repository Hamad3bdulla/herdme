import AppKit
import SwiftUI

private struct AboutDocument: Identifiable {
    let id = UUID()
    let title: String
    let body: String
}

struct AboutView: View {
    @State private var presentedDocument: AboutDocument?

    private var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "Development"
    }

    private var build: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "0"
    }

    var body: some View {
        PageContainer("About") {
            VStack(spacing: 18) {
                HStack(spacing: 28) {
                    Image(nsImage: NSApplication.shared.applicationIconImage)
                        .resizable()
                        .scaledToFit()
                        .shadow(color: .black.opacity(0.18), radius: 8, y: 3)
                    .frame(width: 160, height: 160)
                    VStack(alignment: .leading, spacing: 8) {
                        Text("HerdMe").font(.system(size: 28, weight: .semibold))
                        Text("Version \(version) (Build: \(build))")
                        Text("Independent open-source local development environment")
                            .foregroundStyle(.secondary)
                        Text("MIT License")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Spacer()
                }
                Divider()
                HStack {
                    Button("MIT License") { showResource("LICENSE", extension: nil, title: "MIT License") }
                    Button("Acknowledgements") {
                        showResource("THIRD_PARTY", extension: "md", title: "Acknowledgements")
                    }
                    Spacer()
                }
            }
            .padding(22)
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
    }

    private func showResource(_ name: String, extension fileExtension: String?, title: String) {
        let body: String
        if let url = Bundle.main.url(forResource: name, withExtension: fileExtension),
           let contents = try? String(contentsOf: url, encoding: .utf8) {
            body = contents
        } else {
            body = "The bundled document could not be loaded."
        }
        presentedDocument = AboutDocument(title: title, body: body)
    }
}
