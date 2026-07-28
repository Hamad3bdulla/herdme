import Foundation

struct ErrorPresentation: Equatable {
    let message: String
    let technicalDetails: String?

    init(
        _ rawValue: String,
        fallback: String = String(localized: "The operation could not be completed.")
    ) {
        let extracted = Self.extractDetails(from: rawValue)
        let cleaned = Self.clean(extracted.details)
        let normalized = cleaned.lowercased()

        if normalized.contains("no space left on device")
            || normalized.contains("not enough space on the disk")
        {
            message = String(localized: "There is not enough free disk space to finish the operation. Free some space and try again.")
        } else if normalized.contains("could not be opened in append mode")
            || normalized.contains("unexpectedvalueexception") && normalized.contains("streamhandler")
        {
            message = String(
                localized:
                    "Laravel could not write to the new project's log files. Check disk space and folder permissions, then try again.")
        } else if normalized.contains("permission denied")
            || normalized.contains("operation not permitted")
            || normalized.contains("access is denied")
        {
            message = String(
                localized: "HerdMe does not have permission to write the required files. Choose a writable folder and try again.")
        } else if normalized.contains("could not resolve host")
            || normalized.contains("network is unreachable")
            || normalized.contains("connection timed out")
        {
            message = String(localized: "The download server could not be reached. Check the network connection and try again.")
        } else if normalized.contains("untrusted tap")
            && normalized.contains("brew trust --formula")
        {
            message = String(
                localized:
                    "Homebrew blocked the verified runtime formula. HerdMe could not approve it automatically; review Logs/homebrew.log and try again."
            )
        } else if Self.isPlainMessage(cleaned) {
            message = Bundle.main.localizedString(forKey: cleaned, value: cleaned, table: nil)
        } else {
            message = fallback
        }

        let contextualDetails = [extracted.context, cleaned]
            .compactMap { value in
                guard let value, !value.isEmpty else { return nil }
                return value
            }
            .joined(separator: extracted.context == nil ? "" : "\n\n")
        technicalDetails = Self.technicalDetails(contextualDetails, message: message)
    }

    private static func extractDetails(from rawValue: String) -> (context: String?, details: String) {
        guard let data = rawValue.data(using: .utf8),
            let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else {
            return (nil, rawValue)
        }

        var context: [String] = []
        if let directory = object["directory"] as? String, !directory.isEmpty {
            context.append(
                String.localizedStringWithFormat(
                    String(localized: "Project folder: %@"),
                    directory
                ))
        }
        if let log = object["log"] as? String, !log.isEmpty {
            context.append(
                String.localizedStringWithFormat(
                    String(localized: "Installer log: %@"),
                    log
                ))
        }
        let details = object["tail"] as? String ?? rawValue
        return (context.isEmpty ? nil : context.joined(separator: "\n"), details)
    }

    private static func clean(_ value: String) -> String {
        let ansiPattern = "\u{001B}\\[[0-?]*[ -/]*[@-~]"
        let withoutANSI = value.replacingOccurrences(
            of: ansiPattern,
            with: "",
            options: .regularExpression
        )
        return
            withoutANSI
            .replacingOccurrences(of: "\r\n", with: "\n")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func isPlainMessage(_ value: String) -> Bool {
        guard !value.isEmpty, value.count <= 500, !value.contains("\n") else { return false }
        let normalized = value.lowercased()
        return !value.hasPrefix("{")
            && !normalized.contains("stack trace")
            && !normalized.contains("vendor/")
            && !normalized.contains(" at ")
    }

    private static func technicalDetails(_ value: String, message: String) -> String? {
        guard !value.isEmpty, value != message else { return nil }
        let limit = 6_000
        guard value.count > limit else { return value }
        return "...\n" + String(value.suffix(limit))
    }
}

struct ProjectCreationFailure: Equatable {
    let message: String
    let technicalDetails: String?

    init(_ error: Error) {
        if let projectError = error as? ProjectCreationError {
            message = projectError.localizedDescription
            technicalDetails = projectError.technicalDetails
        } else {
            let presentation = ErrorPresentation(
                error.localizedDescription,
                fallback: String(localized: "The site could not be created.")
            )
            message = presentation.message
            technicalDetails = presentation.technicalDetails
        }
    }
}
