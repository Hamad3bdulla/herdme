#include "herdme/core.hpp"

#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <string>

namespace {

constexpr std::size_t maximum_input_size = 64 * 1024;

bool is_lowercase_dns_label(const std::string &value) {
    if (value.empty() || value.size() > 63) return false;
    const auto is_alphanumeric = [](const unsigned char character) {
        return (character >= 'a' && character <= 'z') || (character >= '0' && character <= '9');
    };
    if (!is_alphanumeric(static_cast<unsigned char>(value.front())) ||
        !is_alphanumeric(static_cast<unsigned char>(value.back()))) {
        return false;
    }
    for (const unsigned char character : value) {
        if (!is_alphanumeric(character) && character != '-') return false;
    }
    return true;
}

} // namespace

extern "C" int LLVMFuzzerTestOneInput(const std::uint8_t *data, const std::size_t size) {
    if (size > maximum_input_size) return 0;
    const char *bytes = size == 0 ? "" : reinterpret_cast<const char *>(data);
    const std::string input(bytes, size);
    const auto label = herdme::dns_label(input);
    if (!is_lowercase_dns_label(label) || label != herdme::dns_label(input)) std::abort();
    return 0;
}
