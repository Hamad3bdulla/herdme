import Darwin
import Foundation
import Security

enum PrivilegedCommandError: LocalizedError {
    case authorizationFailed(OSStatus)
    case preparationFailed

    var errorDescription: String? {
        switch self {
        case let .authorizationFailed(status):
            if status == errAuthorizationCanceled {
                return "Administrator authorization was cancelled."
            }
            return (SecCopyErrorMessageString(status, nil) as String?)
                ?? "Administrator authorization failed (status \(status))."
        case .preparationFailed:
            return "HerdMe could not prepare the administrator command."
        }
    }
}

enum PrivilegedCommandRunner {
    private typealias AuthorizationExecuteFunction = @convention(c) (
        AuthorizationRef,
        UnsafePointer<CChar>,
        AuthorizationFlags,
        UnsafePointer<UnsafeMutablePointer<CChar>>,
        UnsafeMutablePointer<UnsafeMutablePointer<FILE>?>?
    ) -> OSStatus

    static func execute(_ executable: URL, arguments: [String]) throws {
        var authorization: AuthorizationRef?
        var status = AuthorizationCreate(nil, nil, [], &authorization)
        guard status == errAuthorizationSuccess, let authorization else {
            throw PrivilegedCommandError.authorizationFailed(status)
        }
        defer { AuthorizationFree(authorization, []) }

        status = kAuthorizationRightExecute.withCString { rightName in
            var executeItem = AuthorizationItem(
                name: rightName,
                valueLength: 0,
                value: nil,
                flags: 0
            )
            return withUnsafeMutablePointer(to: &executeItem) { itemPointer in
                var rights = AuthorizationRights(count: 1, items: itemPointer)
                return AuthorizationCopyRights(
                    authorization,
                    &rights,
                    nil,
                    [.interactionAllowed, .extendRights, .preAuthorize],
                    nil
                )
            }
        }
        guard status == errAuthorizationSuccess else {
            throw PrivilegedCommandError.authorizationFailed(status)
        }

        let duplicatedArguments = arguments.map { strdup($0) }
        defer { duplicatedArguments.forEach { free($0) } }
        guard duplicatedArguments.allSatisfy({ $0 != nil }) else {
            throw PrivilegedCommandError.preparationFailed
        }
        var argumentVector = duplicatedArguments + [nil]
        let argumentCount = argumentVector.count
        status = executable.path.withCString { executablePath in
            argumentVector.withUnsafeMutableBufferPointer { argumentsPointer in
                argumentsPointer.baseAddress!.withMemoryRebound(
                    to: UnsafeMutablePointer<CChar>.self,
                    capacity: argumentCount
                ) { cArguments in
                    executeWithPrivileges(authorization, executablePath, cArguments)
                }
            }
        }
        guard status == errAuthorizationSuccess else {
            throw PrivilegedCommandError.authorizationFailed(status)
        }
    }

    private static func executeWithPrivileges(
        _ authorization: AuthorizationRef,
        _ executablePath: UnsafePointer<CChar>,
        _ arguments: UnsafePointer<UnsafeMutablePointer<CChar>>
    ) -> OSStatus {
        guard let securityFramework = dlopen(
            "/System/Library/Frameworks/Security.framework/Security",
            RTLD_LAZY | RTLD_LOCAL
        ) else { return errAuthorizationInternal }
        defer { dlclose(securityFramework) }
        guard let symbol = dlsym(securityFramework, "AuthorizationExecuteWithPrivileges") else {
            return errAuthorizationInternal
        }
        let execute = unsafeBitCast(symbol, to: AuthorizationExecuteFunction.self)
        return execute(authorization, executablePath, [], arguments, nil)
    }
}
