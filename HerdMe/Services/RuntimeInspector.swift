import Foundation

struct RuntimeInspector {
    private let locator: ExecutableLocator
    private let managedRoot: URL

    init(managedRoot: URL) {
        self.managedRoot = managedRoot
        locator = ExecutableLocator(managedRoot: managedRoot)
    }

    func phpVersions(activeCycle: String) -> [RuntimeVersion] {
        let activeURL = locator.find("php")
        let detected = activeURL.flatMap {
            commandOutput(executable: $0.path, arguments: ["-r", "echo PHP_VERSION;"])
        }
        let detectedCycle = detected.flatMap(Self.cycle(from:))
        let phpRoot = managedRoot.appendingPathComponent("Runtimes/php", isDirectory: true)
        let installed = ((try? FileManager.default.contentsOfDirectory(at: phpRoot, includingPropertiesForKeys: nil)) ?? [])
            .reduce(into: [String: String]()) { result, runtime in
                let executable = runtime.appendingPathComponent("bin/php")
                guard FileManager.default.isExecutableFile(atPath: executable.path) else { return }
                let cycle = runtime.lastPathComponent
                let version = commandOutput(
                    executable: executable.path,
                    arguments: ["-r", "echo PHP_VERSION;"]
                ) ?? cycle
                result[cycle] = version
            }
        let known = Set(PHPRuntimeSupport.installableCycles + installed.keys)
        return known.sorted { $0.compare($1, options: .numeric) == .orderedDescending }.map { cycle in
            RuntimeVersion(
                cycle: cycle,
                installedVersion: installed[cycle],
                isActive: installed[cycle] != nil && detectedCycle == cycle && cycle == activeCycle
            )
        }
    }

    func nodeVersions() -> [RuntimeVersion] {
        let activeURL = locator.find("node")
        let activeVersion = activeURL.flatMap {
            commandOutput(executable: $0.path, arguments: ["--version"])
        }?.trimmingCharacters(in: CharacterSet(charactersIn: "v"))
        let activeCycle = activeVersion?.split(separator: ".").first.map(String.init)
        let runtimesRoot = managedRoot.appendingPathComponent("Runtimes/node", isDirectory: true)
        let installed = ((try? FileManager.default.contentsOfDirectory(at: runtimesRoot, includingPropertiesForKeys: nil)) ?? [])
            .filter { !$0.lastPathComponent.hasPrefix(".") && FileManager.default.isExecutableFile(atPath: $0.appendingPathComponent("bin/node").path) }
            .reduce(into: [String: String]()) { result, url in
                let version = url.lastPathComponent
                if let cycle = version.split(separator: ".").first.map(String.init) {
                    result[cycle] = max(result[cycle] ?? "", version)
                }
            }
        let known = Set(["26", "24", "22", "20", "18"] + installed.keys)
        return known.sorted { (Int($0) ?? 0) > (Int($1) ?? 0) }.map { cycle in
            RuntimeVersion(
                cycle: cycle,
                installedVersion: installed[cycle],
                isActive: activeCycle == cycle && installed[cycle] == activeVersion
            )
        }
    }

    private func commandOutput(executable: String, arguments: [String]) -> String? {
        do {
            let result = try ProcessRunner.run(
                URL(fileURLWithPath: executable),
                arguments: arguments,
                timeout: 10
            )
            guard result.status == 0 else { return nil }
            let value = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
            return value.isEmpty ? nil : value
        } catch {
            return nil
        }
    }

    private static func cycle(from version: String) -> String? {
        let parts = version.split(separator: ".")
        guard parts.count >= 2 else { return nil }
        return "\(parts[0]).\(parts[1])"
    }
}
