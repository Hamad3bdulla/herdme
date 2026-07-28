import XCTest

@MainActor
final class HerdMeUITests: XCTestCase {
    override func setUpWithError() throws {
        continueAfterFailure = false
    }

    func testFreshInstallationShowsSetupBeforeTheApplicationShell() throws {
        let app = try launchApplication(onboardingCompleted: false)

        XCTAssertTrue(app.windows.firstMatch.waitForExistence(timeout: 10))
        XCTAssertTrue(app.staticTexts["onboarding.welcome.title"].waitForExistence(timeout: 5))
        XCTAssertEqual(app.staticTexts["onboarding.welcome.title"].label, "Welcome to HerdMe")
        XCTAssertTrue(app.buttons["onboarding.setup"].exists)
        XCTAssertFalse(app.buttons["sidebar.general"].exists)
        retainScreenshot(named: "fresh-installation", from: app)
    }

    func testCompletedInstallationNavigatesEveryPrimaryPage() throws {
        let app = try launchApplication(onboardingCompleted: true)
        XCTAssertTrue(app.windows.firstMatch.waitForExistence(timeout: 10))

        let pages = [
            ("dashboard", "Dashboard"), ("general", "General"), ("sites", "Sites"), ("php", "PHP"),
            ("node", "Node"), ("services", "Services"), ("mail", "Mail"),
            ("dumps", "Dumps"), ("debugger", "Debugger"), ("logs", "Logs"),
            ("about", "About")
        ]
        for (identifier, title) in pages {
            let button = app.buttons["sidebar.\(identifier)"]
            XCTAssertTrue(button.waitForExistence(timeout: 5), "Missing sidebar page: \(title)")
            XCTAssertEqual(button.label, title)
            button.click()
            XCTAssertTrue(button.isSelected, "Sidebar did not select: \(title)")
        }
        XCTAssertFalse(app.buttons["onboarding.setup"].exists)
        retainScreenshot(named: "application-shell", from: app)
    }

    func testArabicInstallationUsesLocalizedRightToLeftNavigation() throws {
        let app = try launchApplication(
            onboardingCompleted: true,
            language: "ar",
            locale: "ar_BH"
        )
        let window = app.windows.firstMatch
        XCTAssertTrue(window.waitForExistence(timeout: 10))

        let pages = [
            ("dashboard", "لوحة التحكم"), ("general", "عام"), ("sites", "المواقع"), ("php", "PHP"),
            ("node", "Node"), ("services", "الخدمات"), ("mail", "البريد"),
            ("dumps", "التفريغات"), ("debugger", "مصحح الأخطاء"),
            ("logs", "السجلات"), ("about", "حول")
        ]
        for (identifier, title) in pages {
            let button = app.buttons["sidebar.\(identifier)"]
            XCTAssertTrue(button.waitForExistence(timeout: 5), "Missing Arabic sidebar page: \(title)")
            XCTAssertEqual(button.label, title)
            button.click()
            XCTAssertTrue(button.isSelected, "Arabic sidebar did not select: \(title)")
        }

        let generalButton = app.buttons["sidebar.general"]
        XCTAssertGreaterThan(
            generalButton.frame.midX,
            window.frame.midX,
            "Arabic navigation did not move to the right-to-left side of the window"
        )
        retainScreenshot(named: "application-shell-ar", from: app)
    }

    private func launchApplication(
        onboardingCompleted: Bool,
        language: String = "en",
        locale: String = "en_US"
    ) throws -> XCUIApplication {
        let fileManager = FileManager.default
        let rootURL = fileManager.temporaryDirectory.appendingPathComponent(
            "HerdMeUITests-\(UUID().uuidString)",
            isDirectory: true
        )
        try fileManager.createDirectory(at: rootURL, withIntermediateDirectories: true)

        if onboardingCompleted {
            let projectsURL = rootURL.appendingPathComponent("Projects", isDirectory: true)
            let configuration: [String: Any] = [
                "configSchemaVersion": 1,
                "parkPaths": [projectsURL.path],
                "startAutomatically": false,
                "automaticUpdates": false,
                "onboardingCompleted": true
            ]
            let data = try JSONSerialization.data(
                withJSONObject: configuration,
                options: [.prettyPrinted, .sortedKeys]
            )
            try data.write(to: rootURL.appendingPathComponent("config.json"), options: .atomic)
        }

        let app = XCUIApplication()
        app.launchEnvironment["HERDME_UI_TESTING"] = "1"
        app.launchEnvironment["HERDME_UI_TEST_SUPPORT_ROOT"] = rootURL.path
        app.launchArguments += [
            "-AppleLanguages", "(\(language))",
            "-AppleLocale", locale
        ]
        addTeardownBlock {
            app.terminate()
            try? fileManager.removeItem(at: rootURL)
        }
        app.launch()
        return app
    }

    private func retainScreenshot(named name: String, from app: XCUIApplication) {
        let attachment = XCTAttachment(screenshot: app.screenshot())
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }
}
