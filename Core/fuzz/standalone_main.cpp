#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <string>
#include <vector>

extern "C" int LLVMFuzzerTestOneInput(const std::uint8_t *data, std::size_t size);

namespace {

constexpr std::uintmax_t maximum_corpus_file_size = 1024 * 1024;

int replay_file(const std::filesystem::path &path) {
    std::error_code error;
    if (!std::filesystem::is_regular_file(path, error)) return 0;
    const auto size = std::filesystem::file_size(path, error);
    if (error || size > maximum_corpus_file_size) {
        std::cerr << "Refusing oversized or unreadable corpus file: " << path << '\n';
        return 1;
    }
    std::ifstream input(path, std::ios::binary);
    if (!input) {
        std::cerr << "Unable to read corpus file: " << path << '\n';
        return 1;
    }
    const std::vector<std::uint8_t> bytes{std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>()};
    return LLVMFuzzerTestOneInput(bytes.data(), bytes.size());
}

int replay_path(const std::filesystem::path &path) {
    std::error_code error;
    if (std::filesystem::is_regular_file(path, error)) return replay_file(path);
    if (!std::filesystem::is_directory(path, error)) {
        std::cerr << "Corpus path does not exist: " << path << '\n';
        return 1;
    }
    for (std::filesystem::recursive_directory_iterator iterator(path, error), end; iterator != end && !error;
         iterator.increment(error)) {
        if (const int result = replay_file(iterator->path()); result != 0) return result;
    }
    if (error) {
        std::cerr << "Unable to enumerate corpus path: " << path << '\n';
        return 1;
    }
    return 0;
}

} // namespace

int main(const int argc, char *argv[]) {
    if (argc < 2) {
        std::cerr << "Usage: fuzzer <corpus-file-or-directory> [...]\n";
        return 64;
    }
    for (int index = 1; index < argc; ++index) {
        if (const int result = replay_path(argv[index]); result != 0) return result;
    }
    return 0;
}
