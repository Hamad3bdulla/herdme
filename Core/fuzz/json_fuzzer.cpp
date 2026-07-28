#include "herdme/core.hpp"

#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <filesystem>
#include <optional>
#include <string>
#include <vector>

namespace {

constexpr std::size_t maximum_input_size = 256 * 1024;

bool contains_raw_control_character(const std::string &value) {
    for (const unsigned char character : value) {
        if (character < 0x20) return true;
    }
    return false;
}

} // namespace

extern "C" int LLVMFuzzerTestOneInput(const std::uint8_t *data, const std::size_t size) {
    if (size > maximum_input_size) return 0;
    const char *bytes = size == 0 ? "" : reinterpret_cast<const char *>(data);
    const std::string input(bytes, size);
    const std::size_t split = input.empty() ? 0 : static_cast<std::size_t>(data[0]) % input.size();
    const auto first = input.substr(0, split);
    const auto second = input.substr(split);

    const herdme::Site site{first, std::filesystem::path(second), input, size % 2 == 0, first, second};
    const auto sites = herdme::sites_json({site}, input);
    const std::vector<herdme::RuntimeCheck> checks = {{first, std::filesystem::path(second), input, size % 2 == 0},
                                                      {second, std::nullopt, first, false}};
    const auto doctor = herdme::doctor_json(checks);
    if (sites.empty() || doctor.empty() || contains_raw_control_character(sites) ||
        contains_raw_control_character(doctor)) {
        std::abort();
    }
    return 0;
}
