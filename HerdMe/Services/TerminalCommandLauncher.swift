import AppKit
import Foundation

enum TerminalCommandError: LocalizedError {
    case couldNotOpen

    var errorDescription: String? {
        "HerdMe could not open the command in Terminal."
    }
}

struct TerminalCommandLauncher {
    let rootURL: URL
    private let fileManager: FileManager

    init(rootURL: URL, fileManager: FileManager = .default) {
        self.rootURL = rootURL
        self.fileManager = fileManager
    }

    @discardableResult
    func open(command: String, title: String) throws -> URL {
        let directory = rootURL.appendingPathComponent("Cache/Terminal", isDirectory: true)
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        let safeTitle = title.unicodeScalars.map {
            CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-_")).contains($0)
                ? Character(String($0))
                : "-"
        }
        let url = directory.appendingPathComponent(String(safeTitle) + "-" + UUID().uuidString + ".command")
        let script = """
        #!/bin/zsh
        /bin/rm -f -- \(Self.shellQuote(url.path))
        \(command)

        """
        try script.write(to: url, atomically: true, encoding: .utf8)
        try fileManager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: url.path)
        guard NSWorkspace.shared.open(url) else {
            try? fileManager.removeItem(at: url)
            throw TerminalCommandError.couldNotOpen
        }
        return url
    }

    static func shellQuote(_ value: String) -> String {
        "'" + value.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }
}
