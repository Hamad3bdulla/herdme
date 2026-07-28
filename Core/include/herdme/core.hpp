#pragma once

#include <filesystem>
#include <optional>
#include <string>
#include <vector>

namespace herdme {

struct Site {
    std::string name;
    std::filesystem::path path;
    std::string framework;
    bool linked = false;
    std::optional<std::string> php_version;
    std::optional<std::string> node_version;
};

struct RuntimeCheck {
    std::string name;
    std::optional<std::filesystem::path> executable;
    std::string source;
    bool usable = false;
};

struct PhpExtensionReport {
    std::vector<std::string> required;
    std::vector<std::string> loaded;
    std::vector<std::string> missing;
    bool compatible = false;
};

std::filesystem::path support_directory();
std::string dns_label(const std::string &name);
std::vector<Site> scan_sites(const std::vector<std::filesystem::path> &roots,
                             const std::vector<std::filesystem::path> &linked_sites = {});
std::vector<RuntimeCheck> inspect_runtimes();
const std::vector<std::string> &laravel_required_php_extensions();
PhpExtensionReport inspect_php_module_output(const std::string &module_output);
std::string sites_json(const std::vector<Site> &sites, const std::string &tld);
std::string doctor_json(const std::vector<RuntimeCheck> &checks);
std::string php_extensions_json(const PhpExtensionReport &report);

} // namespace herdme
