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
            click(button, in: app.windows.firstMatch)
            XCTAssertTrue(waitForSelection(of: button), "Sidebar did not select: \(title)")
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
            click(button, in: window)
            XCTAssertTrue(waitForSelection(of: button), "Arabic sidebar did not select: \(title)")
        }

        let generalButton = app.buttons["sidebar.general"]
        XCTAssertGreaterThan(
            generalButton.frame.midX,
            window.frame.midX,
            "Arabic navigation did not move to the right-to-left side of the window"
        )
        retainScreenshot(named: "application-shell-ar", from: app)
    }

    func testDashboardCardsAndSiteCommandBarAreAvailable() throws {
        let app = try launchApplication(onboardingCompleted: true, seedSite: true)
        let window = app.windows.firstMatch
        XCTAssertTrue(window.waitForExistence(timeout: 10))

        for identifier in ["sites", "services", "mail", "dumps"] {
            XCTAssertTrue(
                app.descendants(matching: .any)["dashboard.metric.\(identifier)"]
                    .waitForExistence(timeout: 5),
                "Missing Dashboard metric: \(identifier)"
            )
        }

        let sitesButton = app.buttons["sidebar.sites"]
        XCTAssertTrue(sitesButton.waitForExistence(timeout: 5))
        click(sitesButton, in: window)

        for identifier in ["open", "folder", "terminal", "environment", "more"] {
            XCTAssertTrue(
                app.descendants(matching: .any)["sites.command.\(identifier)"]
                    .waitForExistence(timeout: 5),
                "Missing site command: \(identifier)"
            )
        }
        XCTAssertTrue(
            app.descendants(matching: .any)["sites.preview.toggle"]
                .waitForExistence(timeout: 5)
        )
        retainScreenshot(named: "sites-command-bar", from: app)
    }

    private func launchApplication(
        onboardingCompleted: Bool,
        language: String = "en",
        locale: String = "en_US",
        seedSite: Bool = false
    ) throws -> XCUIApplication {
        let fileManager = FileManager.default
        let rootURL = fileManager.temporaryDirectory.appendingPathComponent(
            "HerdMeUITests-\(UUID().uuidString)",
            isDirectory: true
        )
        try fileManager.createDirectory(at: rootURL, withIntermediateDirectories: true)

        if onboardingCompleted {
            let projectsURL = rootURL.appendingPathComponent("Projects", isDirectory: true)
            if seedSite {
                let publicURL =
                    projectsURL
                    .appendingPathComponent("demo", isDirectory: true)
                    .appendingPathComponent("public", isDirectory: true)
                try fileManager.createDirectory(at: publicURL, withIntermediateDirectories: true)
                try Data("<?php echo 'HerdMe';".utf8).write(
                    to: publicURL.appendingPathComponent("index.php"),
                    options: .atomic
                )
            }
            let configuration: [String: Any] = [
                "configSchemaVersion": 1,
                "independenceMigrationVersion": 1,
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

    private func click(
        _ element: XCUIElement,
        in window: XCUIElement,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        let hittable = XCTNSPredicateExpectation(
            predicate: NSPredicate(format: "hittable == true"),
            object: element
        )
        if XCTWaiter.wait(for: [hittable], timeout: 3) == .completed {
            element.click()
            return
        }

        let elementFrame = element.frame
        let windowFrame = window.frame
        XCTAssertFalse(elementFrame.isEmpty, "Element has no clickable frame", file: file, line: line)
        XCTAssertTrue(
            windowFrame.intersects(elementFrame),
            "Element is outside the application window",
            file: file,
            line: line
        )
        guard !elementFrame.isEmpty, windowFrame.intersects(elementFrame) else { return }

        element.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).click()
    }

    private func waitForSelection(of element: XCUIElement) -> Bool {
        let selected = XCTNSPredicateExpectation(
            predicate: NSPredicate(format: "selected == true"),
            object: element
        )
        return XCTWaiter.wait(for: [selected], timeout: 3) == .completed
    }

    private func retainScreenshot(named name: String, from app: XCUIApplication) {
        let attachment = XCTAttachment(screenshot: app.screenshot())
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }
}
