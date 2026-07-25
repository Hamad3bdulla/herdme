#include "herdme/core.hpp"

#include <iostream>
#include <sstream>
#include <string>
#include <vector>

namespace {

void print_usage() {
    std::cout << "HerdMe Core 0.1.0\n"
              << "Usage:\n"
              << "  herdme-core doctor\n"
              << "  php -m | herdme-core php-extensions\n"
              << "  herdme-core scan [--tld test] [--path <directory>] [--site <project>]\n"
              << "  herdme-core support-path\n";
}

}  // namespace

int main(int argc, char* argv[]) {
    if (argc < 2) {
        print_usage();
        return 0;
    }

    const std::string command = argv[1];
    if (command == "doctor") {
        std::cout << herdme::doctor_json(herdme::inspect_runtimes()) << '\n';
        return 0;
    }
    if (command == "php-extensions") {
        std::ostringstream module_output;
        module_output << std::cin.rdbuf();
        std::cout << herdme::php_extensions_json(
            herdme::inspect_php_module_output(module_output.str())
        ) << '\n';
        return 0;
    }
    if (command == "support-path") {
        std::cout << herdme::support_directory().string() << '\n';
        return 0;
    }
    if (command == "scan") {
        std::string tld = "test";
        std::vector<std::filesystem::path> paths;
        std::vector<std::filesystem::path> linked_sites;
        for (int index = 2; index < argc; ++index) {
            const std::string option = argv[index];
            if (option == "--tld" && index + 1 < argc) {
                tld = argv[++index];
            } else if (option == "--path" && index + 1 < argc) {
                paths.emplace_back(argv[++index]);
            } else if (option == "--site" && index + 1 < argc) {
                linked_sites.emplace_back(argv[++index]);
            } else {
                std::cerr << "Unknown or incomplete option: " << option << '\n';
                return 2;
            }
        }
        if (paths.empty() && linked_sites.empty()) {
            std::cerr << "At least one --path or --site is required.\n";
            return 2;
        }
        std::cout << herdme::sites_json(herdme::scan_sites(paths, linked_sites), tld) << '\n';
        return 0;
    }
    if (command == "--version" || command == "version") {
        std::cout << "0.1.0\n";
        return 0;
    }

    print_usage();
    return 2;
}
