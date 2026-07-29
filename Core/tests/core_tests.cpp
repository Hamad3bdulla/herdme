#include "herdme/core.hpp"

#include <chrono>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <optional>
#include <sstream>
#include <string>
#include <vector>

namespace {

int failures = 0;

std::optional<std::string> environment_value(const char *name) {
#ifdef _WIN32
    char *value = nullptr;
    std::size_t length = 0;
    if (_dupenv_s(&value, &length, name) != 0 || value == nullptr) {
        std::free(value);
        return std::nullopt;
    }
    std::string result(value);
    std::free(value);
    return result;
#else
    if (const char *value = std::getenv(name)) return std::string(value);
    return std::nullopt;
#endif
}

void expect(bool condition, const std::string &message) {
    if (condition) return;
    std::cerr << "FAIL: " << message << '\n';
    ++failures;
}

class EnvironmentVariableGuard {
  public:
    explicit EnvironmentVariableGuard(std::string name) : name_(std::move(name)) {
        original_ = environment_value(name_.c_str());
    }

    ~EnvironmentVariableGuard() {
        if (original_) set(*original_);
        else unset();
    }

    void set(const std::string &value) const {
#ifdef _WIN32
        _putenv_s(name_.c_str(), value.c_str());
#else
        setenv(name_.c_str(), value.c_str(), 1);
#endif
    }

  private:
    void unset() const {
#ifdef _WIN32
        _putenv_s(name_.c_str(), "");
#else
        unsetenv(name_.c_str());
#endif
    }

    std::string name_;
    std::optional<std::string> original_;
};

std::filesystem::path unique_fixture(const std::string &prefix) {
    return std::filesystem::temp_directory_path() /
           (prefix + std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()));
}

void write_file(const std::filesystem::path &path, const std::string &contents = {}) {
    std::filesystem::create_directories(path.parent_path());
    std::ofstream(path) << contents;
}

std::string read_file(const std::filesystem::path &path) {
    std::ifstream input(path);
    return {std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>()};
}

std::vector<std::string> nonempty_lines(const std::string &contents) {
    std::vector<std::string> lines;
    std::istringstream input(contents);
    std::string line;
    while (std::getline(input, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (!line.empty()) lines.push_back(line);
    }
    return lines;
}

const herdme::Site *site_named(const std::vector<herdme::Site> &sites, const std::string &name) {
    for (const auto &site : sites) {
        if (site.name == name) return &site;
    }
    return nullptr;
}

const herdme::RuntimeCheck *runtime_named(const std::vector<herdme::RuntimeCheck> &runtimes, const std::string &name) {
    for (const auto &runtime : runtimes) {
        if (runtime.name == name) return &runtime;
    }
    return nullptr;
}

} // namespace

int main() {
    expect(herdme::dns_label("My-App") == "my-app", "valid DNS labels must be lowercased");
    const auto normalized_label = herdme::dns_label("s2-");
    expect(normalized_label != herdme::dns_label("s2"), "normalized DNS labels must avoid collisions");
    expect(!normalized_label.empty() && normalized_label.back() != '-', "DNS labels must not end in a hyphen");
    const auto empty_label = herdme::dns_label("");
    expect(empty_label.rfind("site-", 0) == 0, "empty names must receive a stable site label");
    const std::string long_name(100, 'a');
    const auto long_label = herdme::dns_label(long_name);
    expect(long_label.size() <= 63, "normalized DNS labels must fit the 63-byte limit");
    expect(long_label == herdme::dns_label(long_name), "DNS normalization must be deterministic");

    const std::vector<std::string> expected = {"ctype", "curl",    "dom",     "fileinfo", "filter", "hash", "mbstring",
                                               "openssl", "pcre", "pdo",     "session",  "tokenizer", "xml", "zip"};
    expect(herdme::laravel_required_php_extensions() == expected,
           "Laravel and Composer require the exact 14-module runtime contract");

    std::string complete = "[PHP Modules]\n";
    for (const auto &extension : expected) complete += extension + "\n";
    complete += "[Zend Modules]\nXdebug\n";
    const auto compatible = herdme::inspect_php_module_output(complete);
    expect(compatible.compatible, "complete PHP module output must be compatible");
    expect(compatible.missing.empty(), "complete PHP module output must have no missing modules");

    const auto incomplete =
        herdme::inspect_php_module_output("[PHP Modules]\nctype\nCURL\ndom\nfileinfo\nfilter\nhash\n"
                                          "openssl\npcre\npdo\nsession\ntokenizer\nxml\nzip\n");
    expect(!incomplete.compatible, "missing mbstring must reject the runtime");
    expect(incomplete.missing == std::vector<std::string>{"mbstring"}, "the report must identify mbstring exactly");
    const auto extension_json = herdme::php_extensions_json(incomplete);
    expect(extension_json.find("\"missing\":[\"mbstring\"]") != std::string::npos &&
               extension_json.find("\"compatible\":false") != std::string::npos,
           "the PHP extension report JSON must preserve missing modules and compatibility");

    const auto php_module_fixtures = std::filesystem::path(__FILE__).parent_path() / "fixtures" / "php-modules";
    const std::vector<std::string> php_module_fixture_names = {"complete", "missing-mbstring", "missing-curl-and-xml"};
    for (const auto &name : php_module_fixture_names) {
        const auto module_path = php_module_fixtures / (name + ".modules");
        const auto expected_path = php_module_fixtures / (name + ".missing");
        const auto report = herdme::inspect_php_module_output(read_file(module_path));
        expect(report.missing == nonempty_lines(read_file(expected_path)),
               "shared PHP module fixture must match: " + name);
    }

    const auto framework_fixture = unique_fixture("herdme-core-frameworks-");
    write_file(framework_fixture / "laravel" / "artisan", "#!/usr/bin/env php\n");
    write_file(framework_fixture / "laravel" / ".herdme-php", " 8.3 \n");
    write_file(framework_fixture / "laravel" / ".nvmrc", "20\n");
    write_file(framework_fixture / "laravel" / ".herdme-node", " 22 \n");
    write_file(framework_fixture / "wordpress" / "wp-config.php");
    write_file(framework_fixture / "php-site" / "public" / "index.php");
    write_file(framework_fixture / "node-site" / "package.json", "{}\n");
    std::filesystem::create_directories(framework_fixture / "plain-site");
    std::filesystem::create_directories(framework_fixture / ".hidden-site");
    const auto framework_sites = herdme::scan_sites({framework_fixture});
    expect(framework_sites.size() == 5, "site scanning must include visible project directories only");
    const auto *laravel_site = site_named(framework_sites, "laravel");
    const auto *wordpress_site = site_named(framework_sites, "wordpress");
    const auto *php_site = site_named(framework_sites, "php-site");
    const auto *node_site = site_named(framework_sites, "node-site");
    const auto *plain_site = site_named(framework_sites, "plain-site");
    expect(laravel_site != nullptr && laravel_site->framework == "Laravel", "artisan must identify Laravel");
    expect(wordpress_site != nullptr && wordpress_site->framework == "WordPress",
           "wp-config.php must identify WordPress");
    expect(php_site != nullptr && php_site->framework == "PHP", "public/index.php must identify PHP");
    expect(node_site != nullptr && node_site->framework == "Node.js", "package.json must identify Node.js");
    expect(plain_site != nullptr && plain_site->framework == "Site", "unknown projects must remain generic sites");
    expect(laravel_site != nullptr && laravel_site->php_version == std::optional<std::string>{"8.3"},
           "site scanning must trim managed PHP overrides");
    expect(laravel_site != nullptr && laravel_site->node_version == std::optional<std::string>{"22"},
           "managed Node overrides must take precedence over .nvmrc");
    std::error_code cleanup_error;
    std::filesystem::remove_all(framework_fixture, cleanup_error);

    const herdme::Site escaped_site{
        "demo\"\nsite", std::filesystem::path("folder") / "quoted\"path", "Laravel", true, "8.4", std::nullopt};
    const auto site_json = herdme::sites_json({escaped_site}, "te\"st");
    expect(site_json.find("\"name\":\"demo\\\"\\nsite\"") != std::string::npos, "site JSON must escape names");
    expect(site_json.find("quoted\\\"path") != std::string::npos, "site JSON must escape paths");
    expect(site_json.find(".te\\\"st") != std::string::npos, "site JSON must escape domains");
    expect(site_json.find("\"nodeVersion\":null") != std::string::npos, "site JSON must preserve null overrides");

    const std::vector<herdme::RuntimeCheck> doctor_checks = {
        {"php", std::filesystem::path("bin") / "php\"tool", "system", true}, {"node", std::nullopt, "missing", false}};
    const auto doctor = herdme::doctor_json(doctor_checks);
    expect(doctor.find("\"available\":true") != std::string::npos, "doctor JSON must report usable runtimes");
    expect(doctor.find("php\\\"tool") != std::string::npos, "doctor JSON must escape executable paths");
    expect(doctor.find("\"path\":null") != std::string::npos, "doctor JSON must report missing paths as null");

    const auto runtime_fixture = unique_fixture("herdme-core-runtimes-");
    EnvironmentVariableGuard path_guard("PATH");
#ifdef _WIN32
    EnvironmentVariableGuard support_guard("LOCALAPPDATA");
    support_guard.set(runtime_fixture.string());
    const auto runtime_bin = runtime_fixture / "HerdMe" / "bin";
    write_file(runtime_bin / "php.exe");
    write_file(runtime_bin / "node.exe");
#else
    EnvironmentVariableGuard support_guard("HOME");
    support_guard.set(runtime_fixture.string());
    const auto runtime_bin = runtime_fixture / "Library" / "Application Support" / "HerdMe" / "bin";
    write_file(runtime_bin / "php", "#!/bin/sh\n");
    write_file(runtime_bin / "node", "#!/bin/sh\n");
    std::filesystem::permissions(runtime_bin / "php",
                                 std::filesystem::perms::owner_exec | std::filesystem::perms::owner_read,
                                 std::filesystem::perm_options::add);
    std::filesystem::permissions(runtime_bin / "node",
                                 std::filesystem::perms::owner_exec | std::filesystem::perms::owner_read,
                                 std::filesystem::perm_options::add);
#endif
    path_guard.set(runtime_bin.string());
    const auto runtimes = herdme::inspect_runtimes();
    const auto *php_runtime = runtime_named(runtimes, "php");
    const auto *node_runtime = runtime_named(runtimes, "node");
    const auto *composer_runtime = runtime_named(runtimes, "composer");
    expect(runtimes.size() == 6, "runtime inspection must preserve the six-tool contract");
    expect(php_runtime != nullptr && php_runtime->usable && php_runtime->source == "managed",
           "runtime inspection must identify managed PHP");
    expect(node_runtime != nullptr && node_runtime->usable && node_runtime->source == "managed",
           "runtime inspection must identify managed Node.js");
    expect(composer_runtime != nullptr && !composer_runtime->usable && composer_runtime->source == "missing",
           "runtime inspection must identify missing tools");
#ifdef _WIN32
    EnvironmentVariableGuard runtime_profile_guard("USERPROFILE");
    runtime_profile_guard.set(runtime_fixture.string());
    const auto private_config_bin = runtime_fixture / ".config" / "external-tool" / "bin";
    const auto private_local_bin = runtime_fixture / "DevHerd Local" / "aliases";
    write_file(private_config_bin / "php.bat");
    write_file(private_local_bin / "php.bat");
    path_guard.set(private_config_bin.string() + ";" + private_local_bin.string());
    const auto private_runtimes = herdme::inspect_runtimes();
    const auto *private_php = runtime_named(private_runtimes, "php");
    expect(private_php != nullptr && !private_php->usable && !private_php->executable &&
               private_php->source == "missing",
           "runtime inspection must not probe or expose another application's private PATH runtime");

    const auto system_runtime_bin = runtime_fixture / "System" / "bin";
    write_file(system_runtime_bin / "php.bat");
    path_guard.set(private_config_bin.string() + ";" + private_local_bin.string() + ";" +
                   system_runtime_bin.string());
    const auto fallback_runtimes = herdme::inspect_runtimes();
    const auto *fallback_php = runtime_named(fallback_runtimes, "php");
    expect(fallback_php != nullptr && fallback_php->usable && fallback_php->source == "system" &&
               fallback_php->executable == system_runtime_bin / "php.bat",
           "runtime inspection must continue past a private PATH directory to a system runtime");
#endif
    std::filesystem::remove_all(runtime_fixture, cleanup_error);

    const auto fixture = unique_fixture("herdme-core-link-");
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
    std::filesystem::remove_all(fixture, cleanup_error);

    const auto independence_fixture = unique_fixture("herdme-core-independence-");
    const auto other_herd_project = independence_fixture / "Herd" / "blocked-project";
    const auto herdme_project = independence_fixture / "HerdMe" / "allowed-project";
    std::filesystem::create_directories(other_herd_project);
    std::filesystem::create_directories(herdme_project);
    std::ofstream(other_herd_project / "artisan") << "#!/usr/bin/env php\n";
    std::ofstream(herdme_project / "artisan") << "#!/usr/bin/env php\n";
#ifdef _WIN32
    const char *home_variable = "USERPROFILE";
#else
    const char *home_variable = "HOME";
#endif
    EnvironmentVariableGuard home_guard(home_variable);
    home_guard.set(independence_fixture.string());
    const auto independent_sites =
        herdme::scan_sites({independence_fixture / "Herd", independence_fixture / "HerdMe"}, {other_herd_project});
    expect(independent_sites.size() == 1, "other Herd roots and links must be ignored");
    if (independent_sites.size() == 1) {
        expect(independent_sites[0].path.filename() == "allowed-project",
               "HerdMe-owned projects must remain scannable");
    }
    std::filesystem::remove_all(independence_fixture, cleanup_error);

    if (failures == 0) std::cout << "HerdMe core tests passed\n";
    return failures == 0 ? 0 : 1;
}
