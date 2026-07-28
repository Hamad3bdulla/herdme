import Foundation

enum LocalListenerError: LocalizedError {
    case invalidPort(service: String)

    var errorDescription: String? {
        switch self {
        case .invalidPort(let service):
            String.localizedStringWithFormat(
                String(localized: "The configured %@ port is invalid."),
                service
            )
        }
    }
}
