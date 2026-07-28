cmake_minimum_required(VERSION 3.20)
file(STRINGS "${CMAKE_CURRENT_LIST_DIR}/../VERSION" HERDME_VERSION LIMIT_COUNT 1)
if(NOT HERDME_VERSION MATCHES "^[0-9]+\\.[0-9]+\\.[0-9]+$")
    message(FATAL_ERROR "VERSION must contain a semantic version such as 1.2.3")
endif()
project(HerdMeCore VERSION "${HERDME_VERSION}" LANGUAGES CXX)

set(HERDME_FUZZING_ENGINE "none" CACHE STRING "Fuzzing engine: none, standalone, or libfuzzer")
set_property(CACHE HERDME_FUZZING_ENGINE PROPERTY STRINGS none standalone libfuzzer)

add_library(herdme-core-library src/core.cpp)
target_include_directories(herdme-core-library PUBLIC include)
target_compile_features(herdme-core-library PUBLIC cxx_std_20)

add_executable(herdme-core src/main.cpp)
target_link_libraries(herdme-core PRIVATE herdme-core-library)
target_compile_definitions(herdme-core PRIVATE HERDME_VERSION="${PROJECT_VERSION}")

include(CTest)

if(NOT HERDME_FUZZING_ENGINE STREQUAL "none")
    if(NOT CMAKE_CXX_COMPILER_ID MATCHES "Clang")
        message(FATAL_ERROR "HerdMe fuzzers require Clang sanitizers")
    endif()
    if(NOT HERDME_FUZZING_ENGINE STREQUAL "standalone"
       AND NOT HERDME_FUZZING_ENGINE STREQUAL "libfuzzer")
        message(FATAL_ERROR "HERDME_FUZZING_ENGINE must be none, standalone, or libfuzzer")
    endif()

    set(HERDME_FUZZ_SANITIZERS "address,undefined")
    if(HERDME_FUZZING_ENGINE STREQUAL "libfuzzer")
        set(HERDME_FUZZ_COMPILE_SANITIZERS "fuzzer-no-link,${HERDME_FUZZ_SANITIZERS}")
        set(HERDME_FUZZ_LINK_SANITIZERS "fuzzer,${HERDME_FUZZ_SANITIZERS}")
    else()
        set(HERDME_FUZZ_COMPILE_SANITIZERS "${HERDME_FUZZ_SANITIZERS}")
        set(HERDME_FUZZ_LINK_SANITIZERS "${HERDME_FUZZ_SANITIZERS}")
    endif()

    target_compile_options(
        herdme-core-library
        PRIVATE
            -fsanitize=${HERDME_FUZZ_COMPILE_SANITIZERS}
            -fno-omit-frame-pointer
    )
    target_link_options(
        herdme-core-library
        INTERFACE
            -fsanitize=${HERDME_FUZZ_SANITIZERS}
            -fno-omit-frame-pointer
    )

    function(add_herdme_fuzzer target source corpus)
        add_executable(${target} ${source})
        target_link_libraries(${target} PRIVATE herdme-core-library)
        target_compile_options(
            ${target}
            PRIVATE
                -fsanitize=${HERDME_FUZZ_COMPILE_SANITIZERS}
                -fno-omit-frame-pointer
        )
        target_link_options(
            ${target}
            PRIVATE
                -fsanitize=${HERDME_FUZZ_LINK_SANITIZERS}
                -fno-omit-frame-pointer
        )
        if(HERDME_FUZZING_ENGINE STREQUAL "standalone")
            target_sources(${target} PRIVATE fuzz/standalone_main.cpp)
            if(BUILD_TESTING)
                add_test(
                    NAME ${target}-corpus
                    COMMAND ${target} "${CMAKE_CURRENT_SOURCE_DIR}/${corpus}"
                )
            endif()
        endif()
    endfunction()

    add_herdme_fuzzer(
        herdme-dns-label-fuzzer
        fuzz/dns_label_fuzzer.cpp
        fuzz/corpus/dns-label
    )
    add_herdme_fuzzer(
        herdme-php-modules-fuzzer
        fuzz/php_modules_fuzzer.cpp
        fuzz/corpus/php-modules
    )
    add_herdme_fuzzer(
        herdme-json-fuzzer
        fuzz/json_fuzzer.cpp
        fuzz/corpus/json
    )
endif()

if(BUILD_TESTING)
    add_executable(herdme-core-tests tests/core_tests.cpp)
    target_link_libraries(herdme-core-tests PRIVATE herdme-core-library)
    add_test(NAME herdme-core-tests COMMAND herdme-core-tests)
    add_test(NAME herdme-core-version COMMAND herdme-core --version)
    set_tests_properties(herdme-core-version PROPERTIES PASS_REGULAR_EXPRESSION "^${PROJECT_VERSION}")
endif()

if(MSVC)
    target_compile_options(herdme-core-library PRIVATE /W4 /WX /permissive-)
    target_compile_options(herdme-core PRIVATE /W4 /WX /permissive-)
    if(BUILD_TESTING)
        target_compile_options(herdme-core-tests PRIVATE /W4 /WX /permissive-)
    endif()
else()
    target_compile_options(herdme-core-library PRIVATE -Wall -Wextra -Wpedantic -Werror)
    target_compile_options(herdme-core PRIVATE -Wall -Wextra -Wpedantic -Werror)
    if(NOT HERDME_FUZZING_ENGINE STREQUAL "none")
        target_compile_options(
            herdme-dns-label-fuzzer
            PRIVATE -Wall -Wextra -Wpedantic -Werror
        )
        target_compile_options(
            herdme-php-modules-fuzzer
            PRIVATE -Wall -Wextra -Wpedantic -Werror
        )
        target_compile_options(
            herdme-json-fuzzer
            PRIVATE -Wall -Wextra -Wpedantic -Werror
        )
    endif()
    if(BUILD_TESTING)
        target_compile_options(herdme-core-tests PRIVATE -Wall -Wextra -Wpedantic -Werror)
    endif()
endif()
