#include "herdme/core.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <string>

namespace {

constexpr std::size_t maximum_input_size = 1024 * 1024;

} // namespace

extern "C" int LLVMFuzzerTestOneInput(const std::uint8_t *data, const std::size_t size) {
    if (size > maximum_input_size) return 0;
    const char *bytes = size == 0 ? "" : reinterpret_cast<const char *>(data);
    const std::string input(bytes, size);
    const auto report = herdme::inspect_php_module_output(input);
    const auto &required = herdme::laravel_required_php_extensions();

    if (report.required != required || !std::is_sorted(report.loaded.begin(), report.loaded.end()) ||
        std::adjacent_find(report.loaded.begin(), report.loaded.end()) != report.loaded.end() ||
        report.compatible != report.missing.empty()) {
        std::abort();
    }
    for (const auto &missing : report.missing) {
        if (std::find(required.begin(), required.end(), missing) == required.end() ||
            std::find(report.loaded.begin(), report.loaded.end(), missing) != report.loaded.end()) {
            std::abort();
        }
    }

    const auto json = herdme::php_extensions_json(report);
    if (json.empty() || json.find('\n') != std::string::npos || json.find('\r') != std::string::npos) {
        std::abort();
    }
    return 0;
}
