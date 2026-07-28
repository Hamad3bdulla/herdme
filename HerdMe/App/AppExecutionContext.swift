import Foundation

enum AppExecutionContext {
    static func isTesting(
        environment: [String: String] = ProcessInfo.processInfo.environment
    ) -> Bool {
        if environment["XCTestConfigurationFilePath"] != nil
            || environment["XCTestBundlePath"] != nil
        {
            return true
        }
        #if DEBUG
            return environment["HERDME_UI_TESTING"] == "1"
        #else
            return false
        #endif
    }

    static func configurationStore(
        environment: [String: String] = ProcessInfo.processInfo.environment
    ) -> ConfigurationStore {
        #if DEBUG
            if environment["HERDME_UI_TESTING"] == "1",
                let rawRoot = environment["HERDME_UI_TEST_SUPPORT_ROOT"],
                rawRoot.hasPrefix("/")
            {
                let rootURL = URL(fileURLWithPath: rawRoot, isDirectory: true).standardizedFileURL
                return ConfigurationStore(
                    rootURL: rootURL,
                    projectsURL: rootURL.appendingPathComponent("Projects", isDirectory: true)
                )
            }
        #endif
        return ConfigurationStore()
    }
}
