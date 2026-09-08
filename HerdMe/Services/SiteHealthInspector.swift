import Foundation

struct SiteHealthCheck: Identifiable, Sendable {
    let id: String
    let title: String
    let isHealthy: Bool
    let detail: String
    let repairable: Bool
}

struct SiteHealthReport: Sendable {
    let checks: [SiteHealthCheck]

    var healthyCount: Int { checks.filter(\.isHealthy).count }
    var isHealthy: Bool { !checks.isEmpty && healthyCount == checks.count }

    var summary: String {
        "\(healthyCount)/\(checks.count) checks healthy"
    }
}

enum SiteHealthInspector {
    static let laravelRequiredExtensions = [
        "ctype", "curl", "dom", "fileinfo", "filter", "hash", "mbstring",
        "openssl", "pcre", "pdo", "session", "tokenizer", "xml"
    ]

    static func inspect(
        site: SiteProject,
        defaultPHP: String,
        environmentStatus: EnvironmentStatus,
        rootURL: URL,
        fileManager: FileManager = .default
    ) -> SiteHealthReport {
        var checks: [SiteHealthCheck] = []
        let path = site.path
        let artisan = path.appendingPathComponent("artisan")
        let isLaravel = fileManager.isReadableFile(atPath: artisan.path)
        checks.append(
            check(
                "project", "Project folder", fileManager.isDirectory(at: path),
                fileManager.isDirectory(at: path) ? path.path : "The project folder is unavailable.", false
            ))

        let envURL = path.appendingPathComponent(".env")
        let envExists = fileManager.isReadableFile(atPath: envURL.path)
        let env = (try? String(contentsOf: envURL, encoding: .utf8)) ?? ""
        checks.append(check("environment", ".env", envExists, envExists ? "Present" : "Create .env from .env.example.", true))

        if isLaravel {
            let appKey = environmentValue(env, key: "APP_KEY")
            checks.append(
                check(
                    "app-key", "Laravel application key", !(appKey?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ?? true),
                    "APP_KEY is missing.", true))
            let vendor = path.appendingPathComponent("vendor/autoload.php")
            checks.append(
                check("composer", "Composer dependencies", fileManager.isReadableFile(atPath: vendor.path), "Run composer install.", true))
            let storage = [
                path.appendingPathComponent("storage/framework/cache", isDirectory: true),
                path.appendingPathComponent("storage/framework/sessions", isDirectory: true),
                path.appendingPathComponent("storage/framework/views", isDirectory: true),
                path.appendingPathComponent("storage/logs", isDirectory: true)
            ]
            let writable = storage.allSatisfy { directory in
                fileManager.isWritableFile(atPath: directory.path)
                    || fileManager.isWritableFile(atPath: directory.deletingLastPathComponent().path)
            }
            checks.append(check("storage", "Laravel storage", writable, "Laravel storage directories are not writable.", true))
            let storageLink = path.appendingPathComponent("public/storage")
            checks.append(
                check("storage-link", "Storage link", fileManager.fileExists(atPath: storageLink.path), "Run artisan storage:link.", true))

            let cycle = site.phpVersion ?? defaultPHP
            let php = rootURL.appendingPathComponent("Runtimes/php/\(cycle)/bin/php")
            if fileManager.isExecutableFile(atPath: php.path) {
                do {
                    try PHPRuntimeValidator().validate(executable: php)
                    checks.append(check("php", "PHP Laravel extensions", true, "All required extensions are loaded.", false))
                } catch {
                    checks.append(check("php", "PHP Laravel extensions", false, error.localizedDescription, true))
                }
            } else {
                checks.append(check("php", "PHP runtime", false, "PHP \(cycle) is not installed.", true))
            }
        }

        let localEnvironmentHealthy = environmentStatus == .running
        checks.append(
            check(
                "environment-running", "Local environment", localEnvironmentHealthy,
                localEnvironmentHealthy ? "Serving local sites." : "Start the local environment before testing the site.", true
            ))
        return SiteHealthReport(checks: checks)
    }

    static func environmentValue(_ contents: String, key: String) -> String? {
        for rawLine in contents.components(separatedBy: .newlines) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            guard !line.hasPrefix("#"), let separator = line.firstIndex(of: "=") else { continue }
            let candidate = String(line[..<separator]).trimmingCharacters(in: .whitespaces)
            guard candidate == key else { continue }
            var value = String(line[line.index(after: separator)...]).trimmingCharacters(in: .whitespaces)
            if value.count >= 2,
                (value.first == "\"" && value.last == "\"") || (value.first == "'" && value.last == "'")
            {
                value.removeFirst()
                value.removeLast()
            }
            return value
        }
        return nil
    }

    private static func check(_ id: String, _ title: String, _ isHealthy: Bool, _ detail: String, _ repairable: Bool) -> SiteHealthCheck {
        SiteHealthCheck(id: id, title: title, isHealthy: isHealthy, detail: detail, repairable: repairable)
    }
}

extension FileManager {
    fileprivate func isDirectory(at url: URL) -> Bool {
        var isDirectory: ObjCBool = false
        return fileExists(atPath: url.path, isDirectory: &isDirectory) && isDirectory.boolValue
    }
}
