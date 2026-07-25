#include "herdme/core.hpp"

#include <algorithm>
#include <cctype>
#include <cstdint>
#include <cstdlib>
#include <fstream>
#include <iomanip>
#include <set>
#include <sstream>
#include <system_error>

#ifdef _WIN32
#include <windows.h>
#else
#include <unistd.h>
#endif

namespace herdme {
namespace {

std::string json_escape(const std::string& value) {
    std::ostringstream output;
    for (const unsigned char character : value) {
        switch (character) {
            case '"': output << "\\\""; break;
            case '\\': output << "\\\\"; break;
            case '\b': output << "\\b"; break;
            case '\f': output << "\\f"; break;
            case '\n': output << "\\n"; break;
            case '\r': output << "\\r"; break;
            case '\t': output << "\\t"; break;
            default:
                if (character < 0x20) {
                    const char* hex = "0123456789abcdef";
                    output << "\\u00" << hex[(character >> 4) & 0x0f] << hex[character & 0x0f];
                } else {
                    output << character;
                }
        }
    }
    return output.str();
}

std::optional<std::string> read_trimmed(const std::filesystem::path& path) {
    std::ifstream input(path);
    if (!input) return std::nullopt;
    std::ostringstream contents;
    contents << input.rdbuf();
    std::string value = contents.str();
    const auto first = value.find_first_not_of(" \t\r\n");
    if (first == std::string::npos) return std::nullopt;
    const auto last = value.find_last_not_of(" \t\r\n");
    return value.substr(first, last - first + 1);
}

std::optional<std::string> read_node_version(const std::filesystem::path& root) {
    if (const auto managed = read_trimmed(root / ".herdme-node")) return managed;
    return read_trimmed(root / ".nvmrc");
}

std::string framework_at(const std::filesystem::path& root) {
    if (std::filesystem::exists(root / "artisan")) return "Laravel";
    if (std::filesystem::exists(root / "wp-config.php")) return "WordPress";
    if (std::filesystem::exists(root / "public" / "index.php")) return "PHP";
    if (std::filesystem::exists(root / "package.json")) return "Node.js";
    return "Site";
}

std::vector<std::filesystem::path> split_path() {
    const char* raw_path = std::getenv("PATH");
    if (raw_path == nullptr) return {};
#ifdef _WIN32
    constexpr char separator = ';';
#else
    constexpr char separator = ':';
#endif
    std::vector<std::filesystem::path> values;
    std::stringstream stream(raw_path);
    std::string value;
    while (std::getline(stream, value, separator)) {
        if (!value.empty()) values.emplace_back(value);
    }
    return values;
}

std::optional<std::filesystem::path> find_executable(const std::string& name) {
    for (const auto& directory : split_path()) {
#ifdef _WIN32
        const std::vector<std::string> candidates = {name + ".exe", name + ".cmd", name + ".bat", name};
#else
        const std::vector<std::string> candidates = {name};
#endif
        for (const auto& candidate : candidates) {
            std::error_code error;
            const auto path = directory / candidate;
            if (!std::filesystem::is_regular_file(path, error)) continue;
#ifdef _WIN32
            return path;
#else
            if (::access(path.c_str(), X_OK) == 0) return path;
#endif
        }
    }
    return std::nullopt;
}

std::string path_string(const std::filesystem::path& path) {
    return path.lexically_normal().generic_string();
}

bool is_same_or_child_path(
    const std::filesystem::path& candidate,
    const std::filesystem::path& root
) {
    std::error_code error;
    const auto resolved_candidate = std::filesystem::weakly_canonical(candidate, error);
    if (error) return false;
    const auto resolved_root = std::filesystem::weakly_canonical(root, error);
    if (error) return false;

    auto candidate_text = path_string(resolved_candidate);
    auto root_text = path_string(resolved_root);
#ifdef _WIN32
    std::transform(candidate_text.begin(), candidate_text.end(), candidate_text.begin(), ::tolower);
    std::transform(root_text.begin(), root_text.end(), root_text.begin(), ::tolower);
#endif
    if (candidate_text == root_text) return true;
    if (root_text.empty() || root_text.back() != '/') root_text += '/';
    return candidate_text.rfind(root_text, 0) == 0;
}

bool belongs_to_other_herd(const std::filesystem::path& path) {
    std::vector<std::filesystem::path> roots;
#ifdef _WIN32
    if (const char* user_profile = std::getenv("USERPROFILE")) {
        roots.emplace_back(std::filesystem::path(user_profile) / "Herd");
    }
    if (const char* local_app_data = std::getenv("LOCALAPPDATA")) {
        roots.emplace_back(std::filesystem::path(local_app_data) / "Herd");
    }
    if (const char* app_data = std::getenv("APPDATA")) {
        roots.emplace_back(std::filesystem::path(app_data) / "Herd");
    }
#else
    if (const char* home = std::getenv("HOME")) {
        const auto home_path = std::filesystem::path(home);
        roots.emplace_back(home_path / "Herd");
        roots.emplace_back(home_path / "Library" / "Application Support" / "Herd");
    }
#endif
    return std::any_of(roots.begin(), roots.end(), [&](const auto& root) {
        return is_same_or_child_path(path, root);
    });
}

std::string lowercase_trimmed(std::string value) {
    const auto first = value.find_first_not_of(" \t\r\n");
    if (first == std::string::npos) return {};
    const auto last = value.find_last_not_of(" \t\r\n");
    value = value.substr(first, last - first + 1);
    std::transform(value.begin(), value.end(), value.begin(), [](const unsigned char character) {
        return static_cast<char>(std::tolower(character));
    });
    return value;
}

void append_string_array(std::ostringstream& output, const std::vector<std::string>& values) {
    output << '[';
    for (std::size_t index = 0; index < values.size(); ++index) {
        if (index != 0) output << ',';
        output << '"' << json_escape(values[index]) << '"';
    }
    output << ']';
}

RuntimeCheck inspect_runtime(const std::string& name) {
    const auto executable = find_executable(name);
    if (!executable) return {name, std::nullopt, "missing", false};

    std::string normalized = path_string(*executable);
    std::transform(normalized.begin(), normalized.end(), normalized.begin(), ::tolower);
    std::string managed_root = path_string(support_directory());
    std::transform(managed_root.begin(), managed_root.end(), managed_root.begin(), ::tolower);

    if (normalized.rfind(managed_root + "/", 0) == 0) {
        return {name, executable, "managed", true};
    }
    const bool belongs_to_herd = normalized.find("/application support/herd/") != std::string::npos
        || normalized.find("/appdata/local/herd/") != std::string::npos
        || normalized.find("/appdata/roaming/herd/") != std::string::npos;
    if (belongs_to_herd) {
        return {name, executable, "other-application", false};
    }
    return {name, executable, "system", true};
}

}  // namespace

std::string dns_label(const std::string& name) {
    std::string original;
    original.reserve(name.size());
    for (const unsigned char value : name) {
        original.push_back(value >= 'A' && value <= 'Z' ? static_cast<char>(value + ('a' - 'A')) : value);
    }

    const auto is_alphanumeric = [](const unsigned char value) {
        return (value >= 'a' && value <= 'z') || (value >= '0' && value <= '9');
    };
    const bool is_valid = !original.empty() && original.size() <= 63
        && is_alphanumeric(static_cast<unsigned char>(original.front()))
        && is_alphanumeric(static_cast<unsigned char>(original.back()))
        && std::all_of(original.begin(), original.end(), [&](const unsigned char value) {
            return is_alphanumeric(value) || value == '-';
        });
    if (is_valid) return original;

    std::string normalized;
    for (const unsigned char value : original) {
        const char character = is_alphanumeric(value) || value == '-' ? static_cast<char>(value) : '-';
        if (character != '-' || normalized.empty() || normalized.back() != '-') normalized.push_back(character);
    }
    while (!normalized.empty() && normalized.front() == '-') normalized.erase(normalized.begin());
    while (!normalized.empty() && normalized.back() == '-') normalized.pop_back();

    std::uint32_t hash = 2166136261u;
    for (const unsigned char value : original) {
        hash = (hash ^ value) * 16777619u;
    }
    std::ostringstream suffix;
    suffix << std::hex << std::setfill('0') << std::setw(6) << (hash & 0x00ffffffu);
    if (normalized.size() > 56) normalized.resize(56);
    while (!normalized.empty() && normalized.back() == '-') normalized.pop_back();
    return (normalized.empty() ? "site" : normalized) + "-" + suffix.str();
}

std::filesystem::path support_directory() {
#ifdef _WIN32
    if (const char* app_data = std::getenv("LOCALAPPDATA")) {
        return std::filesystem::path(app_data) / "HerdMe";
    }
    if (const char* app_data = std::getenv("APPDATA")) {
        return std::filesystem::path(app_data) / "HerdMe";
    }
    return std::filesystem::temp_directory_path() / "HerdMe";
#else
    if (const char* user_home = std::getenv("HOME")) {
        return std::filesystem::path(user_home) / "Library" / "Application Support" / "HerdMe";
    }
    return std::filesystem::temp_directory_path() / "HerdMe";
#endif
}

std::vector<Site> scan_sites(
    const std::vector<std::filesystem::path>& roots,
    const std::vector<std::filesystem::path>& linked_sites
) {
    std::vector<Site> sites;
    std::set<std::string> seen;

    const auto append_site = [&](const std::filesystem::path& path, const bool linked) {
        std::error_code error;
        if (!std::filesystem::is_directory(path, error)) return;
        const auto resolved = std::filesystem::weakly_canonical(path, error);
        if (error) return;
        if (belongs_to_other_herd(resolved)) return;
        const auto key = path_string(resolved);
        if (!seen.insert(key).second) return;
        const auto name = resolved.filename().string();
        if (name.empty() || name.front() == '.') return;
        sites.push_back(Site{
            name,
            resolved,
            framework_at(resolved),
            linked,
            read_trimmed(resolved / ".herdme-php"),
            read_node_version(resolved)
        });
    };

    for (const auto& linked_site : linked_sites) append_site(linked_site, true);

    for (const auto& root : roots) {
        std::error_code error;
        if (belongs_to_other_herd(root)) continue;
        if (!std::filesystem::is_directory(root, error)) continue;
        for (const auto& entry : std::filesystem::directory_iterator(root, error)) {
            if (error) break;
            const auto name = entry.path().filename().string();
            if (name.empty() || name.front() == '.') continue;
            const bool linked = entry.is_symlink(error);
            if (!linked && !entry.is_directory(error)) continue;
            append_site(entry.path(), linked);
        }
    }

    std::sort(sites.begin(), sites.end(), [](const Site& left, const Site& right) {
        std::string left_name = left.name;
        std::string right_name = right.name;
        std::transform(left_name.begin(), left_name.end(), left_name.begin(), ::tolower);
        std::transform(right_name.begin(), right_name.end(), right_name.begin(), ::tolower);
        return left_name < right_name;
    });
    return sites;
}

std::vector<RuntimeCheck> inspect_runtimes() {
    return {
        inspect_runtime("php"),
        inspect_runtime("composer"),
        inspect_runtime("node"),
        inspect_runtime("npm"),
        inspect_runtime("nginx"),
        inspect_runtime("dnsmasq")
    };
}

const std::vector<std::string>& laravel_required_php_extensions() {
    static const std::vector<std::string> extensions = {
        "ctype", "curl", "dom", "fileinfo", "filter", "hash", "mbstring",
        "openssl", "pcre", "pdo", "session", "tokenizer", "xml"
    };
    return extensions;
}

PhpExtensionReport inspect_php_module_output(const std::string& module_output) {
    std::set<std::string> loaded;
    std::istringstream lines(module_output);
    std::string line;
    bool reading_php_modules = false;
    while (std::getline(lines, line)) {
        const auto normalized = lowercase_trimmed(line);
        if (normalized == "[php modules]") {
            reading_php_modules = true;
            continue;
        }
        if (!normalized.empty() && normalized.front() == '[' && normalized.back() == ']') {
            reading_php_modules = false;
            continue;
        }
        if (reading_php_modules && !normalized.empty()) loaded.insert(normalized);
    }

    PhpExtensionReport report;
    report.required = laravel_required_php_extensions();
    report.loaded.assign(loaded.begin(), loaded.end());
    for (const auto& required : report.required) {
        if (!loaded.contains(required)) report.missing.push_back(required);
    }
    report.compatible = report.missing.empty();
    return report;
}

std::string sites_json(const std::vector<Site>& sites, const std::string& tld) {
    std::ostringstream output;
    output << "{\"sites\":[";
    for (std::size_t index = 0; index < sites.size(); ++index) {
        if (index != 0) output << ',';
        const auto& site = sites[index];
        output << "{\"name\":\"" << json_escape(site.name)
               << "\",\"path\":\"" << json_escape(path_string(site.path))
               << "\",\"domain\":\"" << json_escape(dns_label(site.name) + "." + tld)
               << "\",\"framework\":\"" << json_escape(site.framework)
               << "\",\"linked\":" << (site.linked ? "true" : "false")
               << ",\"phpVersion\":";
        if (site.php_version) output << '"' << json_escape(*site.php_version) << '"'; else output << "null";
        output << ",\"nodeVersion\":";
        if (site.node_version) output << '"' << json_escape(*site.node_version) << '"'; else output << "null";
        output << '}';
    }
    output << "]}";
    return output.str();
}

std::string doctor_json(const std::vector<RuntimeCheck>& checks) {
    std::ostringstream output;
#ifdef _WIN32
    const char* platform = "windows";
#else
    const char* platform = "macos";
#endif
    output << "{\"platform\":\"" << platform << "\",\"supportPath\":\""
           << json_escape(path_string(support_directory())) << "\",\"runtimes\":[";
    for (std::size_t index = 0; index < checks.size(); ++index) {
        if (index != 0) output << ',';
        output << "{\"name\":\"" << json_escape(checks[index].name) << "\",\"available\":"
               << (checks[index].usable ? "true" : "false") << ",\"detected\":"
               << (checks[index].executable ? "true" : "false") << ",\"source\":\""
               << json_escape(checks[index].source) << "\",\"path\":";
        if (checks[index].executable) {
            output << '"' << json_escape(path_string(*checks[index].executable)) << '"';
        } else {
            output << "null";
        }
        output << '}';
    }
    output << "]}";
    return output.str();
}

std::string php_extensions_json(const PhpExtensionReport& report) {
    std::ostringstream output;
    output << "{\"required\":";
    append_string_array(output, report.required);
    output << ",\"loaded\":";
    append_string_array(output, report.loaded);
    output << ",\"missing\":";
    append_string_array(output, report.missing);
    output << ",\"compatible\":" << (report.compatible ? "true" : "false") << '}';
    return output.str();
}

}  // namespace herdme
