#include "herdme/core.hpp"

#include <chrono>
#include <cstdlib>
#include <iostream>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

namespace {

int failures = 0;

void expect(bool condition, const std::string& message) {
    if (condition) return;
    std::cerr << "FAIL: " << message << '\n';
    ++failures;
}

}  // namespace

int main() {
    const std::vector<std::string> expected = {
        "ctype", "curl", "dom", "fileinfo", "filter", "hash", "mbstring",
        "openssl", "pcre", "pdo", "session", "tokenizer", "xml"
    };
    expect(
        herdme::laravel_required_php_extensions() == expected,
        "Laravel 13 requires the exact 13-module runtime contract"
    );

    std::string complete = "[PHP Modules]\n";
    for (const auto& extension : expected) complete += extension + "\n";
    complete += "[Zend Modules]\nXdebug\n";
    const auto compatible = herdme::inspect_php_module_output(complete);
    expect(compatible.compatible, "complete PHP module output must be compatible");
    expect(compatible.missing.empty(), "complete PHP module output must have no missing modules");

    const auto incomplete = herdme::inspect_php_module_output(
        "[PHP Modules]\nctype\nCURL\ndom\nfileinfo\nfilter\nhash\n"
        "openssl\npcre\npdo\nsession\ntokenizer\nxml\n"
    );
    expect(!incomplete.compatible, "missing mbstring must reject the runtime");
    expect(
        incomplete.missing == std::vector<std::string>{"mbstring"},
        "the report must identify mbstring exactly"
    );

    const auto fixture = std::filesystem::temp_directory_path()
        / ("herdme-core-link-" + std::to_string(
            std::chrono::steady_clock::now().time_since_epoch().count()
        ));
    const auto project = fixture / "linked-project";
    std::filesystem::create_directories(project);
    std::ofstream(project / "artisan") << "#!/usr/bin/env php\n";
    const auto linked = herdme::scan_sites({}, {project});
    expect(linked.size() == 1, "a directly linked project must be scanned");
    if (linked.size() == 1) {
        expect(linked[0].linked, "a directly linked project must be marked linked");
        expect(linked[0].framework == "Laravel", "a directly linked Laravel project must be detected");
    }
    const auto deduplicated = herdme::scan_sites({fixture}, {project});
    expect(deduplicated.size() == 1, "a linked project must not be duplicated by a park root");
    std::error_code cleanup_error;
    std::filesystem::remove_all(fixture, cleanup_error);

    const auto independence_fixture = std::filesystem::temp_directory_path()
        / ("herdme-core-independence-" + std::to_string(
            std::chrono::steady_clock::now().time_since_epoch().count()
        ));
    const auto other_herd_project = independence_fixture / "Herd" / "blocked-project";
    const auto herdme_project = independence_fixture / "HerdMe" / "allowed-project";
    std::filesystem::create_directories(other_herd_project);
    std::filesystem::create_directories(herdme_project);
    std::ofstream(other_herd_project / "artisan") << "#!/usr/bin/env php\n";
    std::ofstream(herdme_project / "artisan") << "#!/usr/bin/env php\n";
#ifdef _WIN32
    const char* home_variable = "USERPROFILE";
#else
    const char* home_variable = "HOME";
#endif
    const char* original_home = std::getenv(home_variable);
    const std::string saved_home = original_home == nullptr ? "" : original_home;
#ifdef _WIN32
    _putenv_s(home_variable, independence_fixture.string().c_str());
#else
    setenv(home_variable, independence_fixture.string().c_str(), 1);
#endif
    const auto independent_sites = herdme::scan_sites(
        {independence_fixture / "Herd", independence_fixture / "HerdMe"},
        {other_herd_project}
    );
    expect(independent_sites.size() == 1, "other Herd roots and links must be ignored");
    if (independent_sites.size() == 1) {
        expect(
            independent_sites[0].path.filename() == "allowed-project",
            "HerdMe-owned projects must remain scannable"
        );
    }
#ifdef _WIN32
    _putenv_s(home_variable, saved_home.c_str());
#else
    if (original_home == nullptr) unsetenv(home_variable);
    else setenv(home_variable, saved_home.c_str(), 1);
#endif
    std::filesystem::remove_all(independence_fixture, cleanup_error);

    if (failures == 0) std::cout << "HerdMe core tests passed\n";
    return failures == 0 ? 0 : 1;
}
