import Foundation

enum LocalListenerError: LocalizedError {
    case invalidPort(service: String)

    var errorDescription: String? {
        switch self {
        case let .invalidPort(service):
            "The configured \(service) port is invalid."
        }
    }
}
