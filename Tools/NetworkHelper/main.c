#include <arpa/inet.h>
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <grp.h>
#include <libproc.h>
#include <limits.h>
#include <netinet/in.h>
#include <poll.h>
#include <pwd.h>
#include <signal.h>
#include <spawn.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

#define CONFIG_PATH_LIMIT 1024
#define DNS_PACKET_LIMIT 4096
#define TLD_LIMIT 63
#define RESOLVER_CONTENT_LIMIT 256

#ifndef HERDME_RESOLVER_DIRECTORY
#define HERDME_RESOLVER_DIRECTORY "/etc/resolver"
#endif

#ifndef HERDME_LEGACY_LABEL
#define HERDME_LEGACY_LABEL "app.herdme.network-helper"
#endif

#ifndef HERDME_LEGACY_HELPER
#define HERDME_LEGACY_HELPER "/Library/PrivilegedHelperTools/app.herdme.network-helper"
#endif

#ifndef HERDME_LEGACY_PLIST
#define HERDME_LEGACY_PLIST "/Library/LaunchDaemons/app.herdme.network-helper.plist"
#endif

#ifndef HERDME_MODERN_LABEL
#define HERDME_MODERN_LABEL "app.herdme.network-service"
#endif

#ifndef HERDME_LAUNCHCTL_PATH
#define HERDME_LAUNCHCTL_PATH "/bin/launchctl"
#endif

#ifndef HERDME_BIND_RETRY_COUNT
#define HERDME_BIND_RETRY_COUNT 50
#endif

#ifndef HERDME_BIND_RETRY_NANOSECONDS
#define HERDME_BIND_RETRY_NANOSECONDS 100000000L
#endif

#ifndef HERDME_HTTP_LISTEN_PORT
#define HERDME_HTTP_LISTEN_PORT 80
#endif

#ifndef HERDME_HTTPS_LISTEN_PORT
#define HERDME_HTTPS_LISTEN_PORT 443
#endif

#ifndef HERDME_DNS_LISTEN_PORT
#define HERDME_DNS_LISTEN_PORT 53
#endif

#ifndef HERDME_RELAY_IDLE_TIMEOUT_MILLISECONDS
#define HERDME_RELAY_IDLE_TIMEOUT_MILLISECONDS 300000
#endif

struct routing_config {
    uint16_t http_port;
    uint16_t https_port;
    char tld[TLD_LIMIT + 1];
};

static volatile sig_atomic_t keep_running = 1;
static volatile sig_atomic_t supervised_worker = -1;
static volatile sig_atomic_t supervisor_stopping = 0;

static void stop_handler(int signal_number) {
    (void)signal_number;
    keep_running = 0;
}

static void supervisor_stop_handler(int signal_number) {
    (void)signal_number;
    supervisor_stopping = 1;
    if (supervised_worker > 0) (void)kill((pid_t)supervised_worker, SIGTERM);
}

static bool valid_tld(const char *value) {
    size_t length = strlen(value);
    if (length == 0 || length > TLD_LIMIT || value[0] == '-' || value[length - 1] == '-') {
        return false;
    }
    for (size_t index = 0; index < length; index++) {
        char character = value[index];
        bool valid =
            (character >= 'a' && character <= 'z') || (character >= '0' && character <= '9') || character == '-';
        if (!valid) return false;
    }
    return true;
}

static bool parse_port(const char *value, uint16_t *port, bool allow_disabled) {
    char *end = NULL;
    errno = 0;
    long parsed = strtol(value, &end, 10);
    if (errno != 0 || end == value || *end != '\0' ||
        ((!allow_disabled || parsed != 0) && (parsed < 1024 || parsed > 65535))) {
        return false;
    }
    *port = (uint16_t)parsed;
    return true;
}

static bool load_config(const char *path, struct routing_config *config) {
    FILE *file = fopen(path, "r");
    if (file == NULL) return false;

    char line[256];
    bool found_http = false;
    bool found_https = false;
    bool found_tld = false;
    bool valid = true;
    while (fgets(line, sizeof(line), file) != NULL) {
        if (strchr(line, '\n') == NULL && !feof(file)) {
            valid = false;
            break;
        }
        line[strcspn(line, "\r\n")] = '\0';
        if (line[0] == '\0') continue;
        char *separator = strchr(line, '=');
        if (separator == NULL) {
            valid = false;
            break;
        }
        *separator = '\0';
        const char *value = separator + 1;
        if (strcmp(line, "http") == 0) {
            uint16_t port;
            if (found_http || !parse_port(value, &port, false)) valid = false;
            else {
                config->http_port = port;
                found_http = true;
            }
        } else if (strcmp(line, "https") == 0) {
            uint16_t port;
            if (found_https || !parse_port(value, &port, true)) valid = false;
            else {
                config->https_port = port;
                found_https = true;
            }
        } else if (strcmp(line, "tld") == 0) {
            if (found_tld || !valid_tld(value)) valid = false;
            else {
                strncpy(config->tld, value, TLD_LIMIT);
                config->tld[TLD_LIMIT] = '\0';
                found_tld = true;
            }
        } else {
            valid = false;
        }
        if (!valid) break;
    }
    if (ferror(file)) valid = false;
    if (fclose(file) != 0) valid = false;
    return valid && found_http && found_https && found_tld;
}

static int resolver_contents(char *buffer, size_t capacity) {
    return snprintf(buffer, capacity, "# Managed by HerdMe\nnameserver 127.0.0.1\nport %d\nsearch_order 1\ntimeout 1\n",
                    HERDME_DNS_LISTEN_PORT);
}

static bool write_all_file(int descriptor, const char *bytes, size_t length) {
    size_t offset = 0;
    while (offset < length) {
        ssize_t count = write(descriptor, bytes + offset, length - offset);
        if (count > 0) {
            offset += (size_t)count;
            continue;
        }
        if (count < 0 && errno == EINTR) continue;
        return false;
    }
    return true;
}

static bool resolver_file_matches(int directory, const char *name, const char *expected) {
    int descriptor = openat(directory, name, O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
    if (descriptor < 0) return false;
    struct stat metadata;
    char contents[RESOLVER_CONTENT_LIMIT];
    ssize_t count = read(descriptor, contents, sizeof(contents) - 1);
    bool matches = fstat(descriptor, &metadata) == 0 && S_ISREG(metadata.st_mode)
#ifndef HERDME_NETWORK_HELPER_TEST
                   && metadata.st_uid == 0
#endif
                   && count >= 0 && (size_t)count < sizeof(contents);
    close(descriptor);
    if (!matches) return false;
    contents[count] = '\0';
    return strcmp(contents, expected) == 0;
}

static bool reconcile_resolver(const char *tld) {
    if (!valid_tld(tld)) return false;
    if (mkdir(HERDME_RESOLVER_DIRECTORY, 0755) != 0 && errno != EEXIST) return false;

    struct stat directory_metadata;
    if (lstat(HERDME_RESOLVER_DIRECTORY, &directory_metadata) != 0 || !S_ISDIR(directory_metadata.st_mode) ||
        (directory_metadata.st_mode & (S_IWGRP | S_IWOTH)) != 0
#ifndef HERDME_NETWORK_HELPER_TEST
        || directory_metadata.st_uid != 0
#endif
    ) {
        return false;
    }

    int directory = open(HERDME_RESOLVER_DIRECTORY, O_RDONLY | O_DIRECTORY | O_CLOEXEC);
    if (directory < 0) return false;
    char expected[RESOLVER_CONTENT_LIMIT];
    int expected_length = resolver_contents(expected, sizeof(expected));
    if (expected_length <= 0 || (size_t)expected_length >= sizeof(expected)) {
        close(directory);
        return false;
    }

    int scan_descriptor = dup(directory);
    DIR *scan = scan_descriptor >= 0 ? fdopendir(scan_descriptor) : NULL;
    if (scan == NULL) {
        if (scan_descriptor >= 0) close(scan_descriptor);
        close(directory);
        return false;
    }
    struct dirent *entry;
    while ((entry = readdir(scan)) != NULL) {
        if (entry->d_name[0] == '.' || strcmp(entry->d_name, tld) == 0) continue;
        if (valid_tld(entry->d_name) && resolver_file_matches(directory, entry->d_name, expected)) {
            (void)unlinkat(directory, entry->d_name, 0);
        }
    }
    closedir(scan);

    char temporary[TLD_LIMIT + 32];
    int temporary_length = snprintf(temporary, sizeof(temporary), ".herdme-%ld", (long)getpid());
    if (temporary_length <= 0 || (size_t)temporary_length >= sizeof(temporary)) {
        close(directory);
        return false;
    }
    (void)unlinkat(directory, temporary, 0);
    int output = openat(directory, temporary, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC | O_NOFOLLOW, 0644);
    if (output < 0) {
        close(directory);
        return false;
    }
    bool succeeded =
        write_all_file(output, expected, (size_t)expected_length) && fsync(output) == 0 && fchmod(output, 0644) == 0;
#ifndef HERDME_NETWORK_HELPER_TEST
    succeeded = succeeded && fchown(output, 0, 0) == 0;
#endif
    if (close(output) != 0) succeeded = false;
    if (succeeded) succeeded = renameat(directory, temporary, directory, tld) == 0;
    if (!succeeded) (void)unlinkat(directory, temporary, 0);
    if (succeeded) (void)fsync(directory);
    close(directory);
    return succeeded;
}

static int bind_loopback_socket(int type, uint16_t port) {
    int descriptor = socket(AF_INET, type, 0);
    if (descriptor < 0) return -1;
    int enabled = 1;
    setsockopt(descriptor, SOL_SOCKET, SO_REUSEADDR, &enabled, sizeof(enabled));
    setsockopt(descriptor, SOL_SOCKET, SO_NOSIGPIPE, &enabled, sizeof(enabled));

    struct sockaddr_in address;
    memset(&address, 0, sizeof(address));
    address.sin_len = sizeof(address);
    address.sin_family = AF_INET;
    address.sin_port = htons(port);
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (bind(descriptor, (struct sockaddr *)&address, sizeof(address)) != 0) {
        close(descriptor);
        return -1;
    }
    if (type == SOCK_STREAM && listen(descriptor, 128) != 0) {
        close(descriptor);
        return -1;
    }
    return descriptor;
}

struct network_listeners {
    int http;
    int https;
    int dns;
};

static void close_listener(int *descriptor) {
    if (*descriptor < 0) return;
    close(*descriptor);
    *descriptor = -1;
}

static void close_listeners(struct network_listeners *listeners) {
    close_listener(&listeners->http);
    close_listener(&listeners->https);
    close_listener(&listeners->dns);
}

static bool bind_listeners(struct network_listeners *listeners) {
    listeners->http = -1;
    listeners->https = -1;
    listeners->dns = -1;
    struct timespec retry_delay = {.tv_sec = 0, .tv_nsec = HERDME_BIND_RETRY_NANOSECONDS};
    for (int attempt = 0; attempt < HERDME_BIND_RETRY_COUNT; attempt++) {
        listeners->http = bind_loopback_socket(SOCK_STREAM, HERDME_HTTP_LISTEN_PORT);
        listeners->https = bind_loopback_socket(SOCK_STREAM, HERDME_HTTPS_LISTEN_PORT);
        listeners->dns = bind_loopback_socket(SOCK_DGRAM, HERDME_DNS_LISTEN_PORT);
        if (listeners->http >= 0 && listeners->https >= 0 && listeners->dns >= 0) {
            return true;
        }
        close_listeners(listeners);
        if (attempt + 1 < HERDME_BIND_RETRY_COUNT) {
            while (nanosleep(&retry_delay, &retry_delay) != 0 && errno == EINTR) {}
            retry_delay.tv_sec = 0;
            retry_delay.tv_nsec = HERDME_BIND_RETRY_NANOSECONDS;
        }
    }
    return false;
}

static int connect_loopback(uint16_t port) {
    int descriptor = socket(AF_INET, SOCK_STREAM, 0);
    if (descriptor < 0) return -1;
    int enabled = 1;
    setsockopt(descriptor, SOL_SOCKET, SO_NOSIGPIPE, &enabled, sizeof(enabled));

    struct sockaddr_in address;
    memset(&address, 0, sizeof(address));
    address.sin_len = sizeof(address);
    address.sin_family = AF_INET;
    address.sin_port = htons(port);
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (connect(descriptor, (struct sockaddr *)&address, sizeof(address)) != 0) {
        close(descriptor);
        return -1;
    }
    return descriptor;
}

static bool send_all(int descriptor, const uint8_t *bytes, size_t length) {
    size_t sent = 0;
    while (sent < length) {
        ssize_t count = send(descriptor, bytes + sent, length - sent, 0);
        if (count > 0) {
            sent += (size_t)count;
            continue;
        }
        if (count < 0 && errno == EINTR) continue;
        return false;
    }
    return true;
}

static void relay_connection(int client, uint16_t target_port) {
    int upstream = connect_loopback(target_port);
    if (upstream < 0) return;

    struct pollfd descriptors[2] = {{.fd = client, .events = POLLIN}, {.fd = upstream, .events = POLLIN}};
    uint8_t buffer[64 * 1024];
    int readable = 2;
    while (readable > 0) {
        int result = poll(descriptors, 2, HERDME_RELAY_IDLE_TIMEOUT_MILLISECONDS);
        if (result < 0) {
            if (errno == EINTR) continue;
            break;
        }
        if (result == 0) break;
        for (int index = 0; index < 2; index++) {
            short events = descriptors[index].revents;
            if (events & POLLNVAL) {
                readable = 0;
                break;
            }
            if (!(events & (POLLIN | POLLHUP | POLLERR))) continue;
            int source = descriptors[index].fd;
            int destination = descriptors[1 - index].fd;
            ssize_t count;
            do {
                count = recv(source, buffer, sizeof(buffer), 0);
            } while (count < 0 && errno == EINTR);
            if (count > 0) {
                if (!send_all(destination, buffer, (size_t)count)) {
                    readable = 0;
                    break;
                }
            } else {
                // Once the upstream server is done, close the client immediately.
                // Waiting for a browser's final FIN leaves one worker in FIN_WAIT_2.
                if (index == 1) {
                    readable = 0;
                    break;
                }
                if (descriptors[index].events != 0) {
                    descriptors[index].events = 0;
                    shutdown(destination, SHUT_WR);
                    readable--;
                }
            }
        }
    }
    close(upstream);
}

static void accept_connection(int listener, uint16_t target_port, int http_listener, int https_listener,
                              int dns_socket) {
    int client = accept(listener, NULL, NULL);
    if (client < 0) return;
    if (target_port == 0) {
        close(client);
        return;
    }
    pid_t child = fork();
    if (child == 0) {
        close(http_listener);
        if (https_listener >= 0) close(https_listener);
        close(dns_socket);
        relay_connection(client, target_port);
        close(client);
        _exit(EXIT_SUCCESS);
    }
    close(client);
}

static bool domain_matches(const char *domain, const char *tld) {
    size_t domain_length = strlen(domain);
    size_t tld_length = strlen(tld);
    if (domain_length == tld_length) return strcmp(domain, tld) == 0;
    if (domain_length <= tld_length + 1) return false;
    return domain[domain_length - tld_length - 1] == '.' && strcmp(domain + domain_length - tld_length, tld) == 0;
}

static size_t dns_response(const uint8_t *query, size_t query_length, const char *tld, uint8_t *response,
                           size_t capacity) {
    if (query_length < 17 || capacity < query_length || (query[2] & 0x80) != 0) return 0;
    size_t index = 12;
    char domain[256] = {0};
    size_t domain_length = 0;
    while (index < query_length) {
        uint8_t label_length = query[index++];
        if (label_length == 0) break;
        if (label_length > 63 || index + label_length > query_length) return 0;
        if (domain_length != 0) domain[domain_length++] = '.';
        if (domain_length + label_length >= sizeof(domain)) return 0;
        for (uint8_t offset = 0; offset < label_length; offset++) {
            char character = (char)query[index + offset];
            if (character >= 'A' && character <= 'Z') character += 'a' - 'A';
            domain[domain_length++] = character;
        }
        index += label_length;
    }
    if (index + 4 > query_length) return 0;
    domain[domain_length] = '\0';
    size_t question_end = index + 4;
    uint16_t query_type = (uint16_t)((query[index] << 8) | query[index + 1]);
    uint16_t query_class = (uint16_t)((query[index + 2] << 8) | query[index + 3]);
    bool matches = domain_matches(domain, tld);
    bool has_answer = matches && query_type == 1 && query_class == 1;
    size_t required = question_end + (has_answer ? 16 : 0);
    if (required > capacity) return 0;

    response[0] = query[0];
    response[1] = query[1];
    response[2] = 0x81;
    response[3] = matches ? 0x80 : 0x83;
    response[4] = 0x00;
    response[5] = 0x01;
    response[6] = 0x00;
    response[7] = has_answer ? 0x01 : 0x00;
    memset(response + 8, 0, 4);
    memcpy(response + 12, query + 12, question_end - 12);
    size_t output = question_end;
    if (!has_answer) return output;

    const uint8_t answer[] = {0xC0, 0x0C, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x04, 127, 0, 0, 1};
    memcpy(response + output, answer, sizeof(answer));
    return output + sizeof(answer);
}

static void answer_dns(int descriptor, const char *tld) {
    uint8_t query[DNS_PACKET_LIMIT];
    uint8_t response[DNS_PACKET_LIMIT];
    struct sockaddr_storage client;
    socklen_t client_length = sizeof(client);
    ssize_t count = recvfrom(descriptor, query, sizeof(query), 0, (struct sockaddr *)&client, &client_length);
    if (count <= 0) return;
    size_t response_length = dns_response(query, (size_t)count, tld, response, sizeof(response));
    if (response_length == 0) return;
    sendto(descriptor, response, response_length, 0, (struct sockaddr *)&client, client_length);
}

static bool parse_identity(const char *value, unsigned long *identity) {
    char *end = NULL;
    errno = 0;
    unsigned long parsed = strtoul(value, &end, 10);
    if (errno != 0 || end == value || *end != '\0') return false;
    *identity = parsed;
    return true;
}

extern char **environ;

#ifndef HERDME_NETWORK_HELPER_TEST
static bool console_user(unsigned long *target_uid, unsigned long *target_gid, char *configuration_path,
                         size_t configuration_capacity) {
    struct stat console;
    if (stat("/dev/console", &console) != 0 || console.st_uid == 0) return false;

    struct passwd password;
    struct passwd *result = NULL;
    char buffer[16 * 1024];
    if (getpwuid_r(console.st_uid, &password, buffer, sizeof(buffer), &result) != 0 || result == NULL ||
        password.pw_dir == NULL || password.pw_dir[0] != '/') {
        return false;
    }
    int length = snprintf(configuration_path, configuration_capacity,
                          "%s/Library/Application Support/HerdMe/Runtime/network-helper.conf", password.pw_dir);
    if (length <= 0 || (size_t)length >= configuration_capacity) return false;
    *target_uid = (unsigned long)console.st_uid;
    *target_gid = (unsigned long)console.st_gid;
    return true;
}
#endif

static bool safe_legacy_file(const char *path, bool must_be_executable) {
    struct stat metadata;
    if (lstat(path, &metadata) != 0 || !S_ISREG(metadata.st_mode)) return false;
#ifndef HERDME_NETWORK_HELPER_TEST
    if (metadata.st_uid != 0) return false;
#endif
    if ((metadata.st_mode & (S_IWGRP | S_IWOTH)) != 0) return false;
    return !must_be_executable || (metadata.st_mode & S_IXUSR) != 0;
}

static bool run_launchctl(char *const arguments[]) {
    pid_t child = 0;
    if (posix_spawn(&child, arguments[0], NULL, NULL, arguments, environ) != 0) {
        return false;
    }
    int status = 0;
    pid_t result;
    do {
        result = waitpid(child, &status, 0);
    } while (result < 0 && errno == EINTR);
    return result == child && WIFEXITED(status) && WEXITSTATUS(status) == 0;
}

static size_t signal_legacy_processes(int signal_number) {
    int required_bytes = proc_listpids(PROC_ALL_PIDS, 0, NULL, 0);
    if (required_bytes <= 0) return SIZE_MAX;
    size_t capacity = (size_t)required_bytes + (64 * sizeof(pid_t));
    if (capacity > INT_MAX) return SIZE_MAX;

    pid_t *processes = calloc(1, capacity);
    if (processes == NULL) return SIZE_MAX;
    int listed_bytes = proc_listpids(PROC_ALL_PIDS, 0, processes, (int)capacity);
    if (listed_bytes < 0) {
        free(processes);
        return SIZE_MAX;
    }

    char canonical_legacy_path[PATH_MAX];
    const char *legacy_path = realpath(HERDME_LEGACY_HELPER, canonical_legacy_path);
    if (legacy_path == NULL) legacy_path = HERDME_LEGACY_HELPER;

    size_t matching_processes = 0;
    size_t process_count = (size_t)listed_bytes / sizeof(pid_t);
    for (size_t index = 0; index < process_count; index++) {
        pid_t process = processes[index];
        if (process <= 1 || process == getpid()) continue;
        char process_path[PROC_PIDPATHINFO_MAXSIZE];
        if (proc_pidpath(process, process_path, sizeof(process_path)) <= 0 || strcmp(process_path, legacy_path) != 0) {
            continue;
        }
        matching_processes++;
        if (signal_number != 0) (void)kill(process, signal_number);
    }
    free(processes);
    return matching_processes;
}

static bool terminate_legacy_processes(void) {
    size_t remaining = signal_legacy_processes(SIGTERM);
    if (remaining == SIZE_MAX) return false;
    struct timespec delay = {.tv_sec = 0, .tv_nsec = 50000000L};
    for (int attempt = 0; attempt < 10 && remaining > 0; attempt++) {
        while (nanosleep(&delay, &delay) != 0 && errno == EINTR) {}
        delay.tv_sec = 0;
        delay.tv_nsec = 50000000L;
        remaining = signal_legacy_processes(0);
        if (remaining == SIZE_MAX) return false;
    }
    if (remaining == 0) return true;

    remaining = signal_legacy_processes(SIGKILL);
    if (remaining == SIZE_MAX) return false;
    for (int attempt = 0; attempt < 10 && remaining > 0; attempt++) {
        while (nanosleep(&delay, &delay) != 0 && errno == EINTR) {}
        delay.tv_sec = 0;
        delay.tv_nsec = 50000000L;
        remaining = signal_legacy_processes(0);
        if (remaining == SIZE_MAX) return false;
    }
    return remaining == 0;
}

static bool legacy_service_can_be_restored(void) {
    return safe_legacy_file(HERDME_LEGACY_HELPER, true) && safe_legacy_file(HERDME_LEGACY_PLIST, false);
}

static void retire_legacy_service(void) {
    char service_target[] = "system/" HERDME_LEGACY_LABEL;
    char *arguments[] = {HERDME_LAUNCHCTL_PATH, "bootout", service_target, NULL};
    (void)run_launchctl(arguments);
}

static bool restore_legacy_service(void) {
    char service_target[] = "system/" HERDME_LEGACY_LABEL;
    char *enable_arguments[] = {HERDME_LAUNCHCTL_PATH, "enable", service_target, NULL};
    char *bootstrap_arguments[] = {HERDME_LAUNCHCTL_PATH, "bootstrap", "system", HERDME_LEGACY_PLIST, NULL};
    char *kickstart_arguments[] = {HERDME_LAUNCHCTL_PATH, "kickstart", "-k", service_target, NULL};
    bool enabled = run_launchctl(enable_arguments);
    bool bootstrapped = run_launchctl(bootstrap_arguments);
    bool started = run_launchctl(kickstart_arguments);
    return enabled && (bootstrapped || started) && started;
}

static bool remove_safe_legacy_file(const char *path, bool must_be_executable) {
    if (lstat(path, &(struct stat){0}) != 0) return errno == ENOENT;
    return safe_legacy_file(path, must_be_executable) && unlink(path) == 0;
}

static bool finalize_legacy_service(void) {
    char service_target[] = "system/" HERDME_LEGACY_LABEL;
    char *disable_arguments[] = {HERDME_LAUNCHCTL_PATH, "disable", service_target, NULL};
    if (!run_launchctl(disable_arguments)) return false;
    bool removed_plist = remove_safe_legacy_file(HERDME_LEGACY_PLIST, false);
    if (removed_plist) {
        (void)remove_safe_legacy_file(HERDME_LEGACY_HELPER, true);
    }
    return removed_plist;
}

static int run_network_worker(const char *config_path, struct routing_config config, unsigned long target_uid,
                              unsigned long target_gid, bool managed, struct network_listeners listeners,
                              int ready_descriptor) {
#ifndef HERDME_NETWORK_HELPER_TEST
    if (setgroups(0, NULL) != 0 || setgid((gid_t)target_gid) != 0 || setuid((uid_t)target_uid) != 0) {
        close(ready_descriptor);
        close_listeners(&listeners);
        return EXIT_FAILURE;
    }
#else
    (void)target_uid;
    (void)target_gid;
#endif

    const char ready = '1';
    if (!write_all_file(ready_descriptor, &ready, 1)) {
        close(ready_descriptor);
        close_listeners(&listeners);
        return EXIT_FAILURE;
    }
    close(ready_descriptor);
    signal(SIGTERM, stop_handler);
    signal(SIGINT, stop_handler);
    signal(SIGPIPE, SIG_IGN);
    signal(SIGCHLD, SIG_IGN);

    int exit_status = EXIT_SUCCESS;
    struct pollfd descriptors[3] = {{.fd = listeners.http, .events = POLLIN},
                                    {.fd = listeners.https, .events = POLLIN},
                                    {.fd = listeners.dns, .events = POLLIN}};
    while (keep_running) {
        int result = poll(descriptors, 3, 1000);
        if (result < 0) {
            if (errno == EINTR) continue;
            exit_status = EXIT_FAILURE;
            break;
        }
        struct routing_config updated;
        if (load_config(config_path, &updated)) config = updated;
#ifndef HERDME_NETWORK_HELPER_TEST
        if (managed) {
            struct stat console;
            if (stat("/dev/console", &console) != 0 || console.st_uid != (uid_t)target_uid) {
                exit_status = EXIT_FAILURE;
                break;
            }
        }
#else
        (void)managed;
#endif
        if (descriptors[0].revents & POLLIN) {
            accept_connection(listeners.http, config.http_port, listeners.http, listeners.https, listeners.dns);
        }
        if (descriptors[1].revents & POLLIN) {
            accept_connection(listeners.https, config.https_port, listeners.http, listeners.https, listeners.dns);
        }
        if (descriptors[2].revents & POLLIN) answer_dns(listeners.dns, config.tld);
    }
    close_listeners(&listeners);
    return exit_status;
}

int main(int argc, char **argv) {
    const char *config_path = NULL;
#ifndef HERDME_NETWORK_HELPER_TEST
    char managed_configuration[CONFIG_PATH_LIMIT];
#endif
    unsigned long target_uid = 0;
    unsigned long target_gid = 0;
    bool managed = false;
    int first_argument = 1;

#ifndef HERDME_NETWORK_HELPER_TEST
    if (argc == 2 && strcmp(argv[1], "--managed") == 0) {
        managed = true;
        if (!console_user(&target_uid, &target_gid, managed_configuration, sizeof(managed_configuration))) {
            return EXIT_FAILURE;
        }
        config_path = managed_configuration;
        first_argument = argc;
    }
#else
    if (argc >= 3 && strcmp(argv[1], "--managed-test") == 0) {
        managed = true;
        config_path = argv[2];
        first_argument = 3;
    }
#endif

    if ((argc - first_argument) % 2 != 0) return EXIT_FAILURE;
    for (int index = first_argument; index + 1 < argc; index += 2) {
        if (strcmp(argv[index], "--config") == 0) config_path = argv[index + 1];
        else if (strcmp(argv[index], "--uid") == 0) {
            if (!parse_identity(argv[index + 1], &target_uid)) return EXIT_FAILURE;
        } else if (strcmp(argv[index], "--gid") == 0) {
            if (!parse_identity(argv[index + 1], &target_gid)) return EXIT_FAILURE;
        } else return EXIT_FAILURE;
    }
    if (config_path == NULL || strlen(config_path) >= CONFIG_PATH_LIMIT || target_uid == 0 || target_gid == 0) {
        return EXIT_FAILURE;
    }
#ifndef HERDME_NETWORK_HELPER_TEST
    if (geteuid() != 0) return EXIT_FAILURE;
#endif

    struct routing_config config;
    if (!load_config(config_path, &config)) return EXIT_FAILURE;
    if (managed && !reconcile_resolver(config.tld)) return EXIT_FAILURE;

    bool legacy_retired = managed && legacy_service_can_be_restored();
    if (legacy_retired) retire_legacy_service();
    if (managed && !terminate_legacy_processes()) {
        if (!legacy_retired) return EXIT_FAILURE;
        return restore_legacy_service() ? EXIT_SUCCESS : EXIT_FAILURE;
    }

    struct network_listeners listeners;
    if (!bind_listeners(&listeners)) {
        if (!legacy_retired) return EXIT_FAILURE;
        return restore_legacy_service() ? EXIT_SUCCESS : EXIT_FAILURE;
    }

    int readiness[2];
    if (pipe(readiness) != 0) {
        close_listeners(&listeners);
        if (!legacy_retired) return EXIT_FAILURE;
        return restore_legacy_service() ? EXIT_SUCCESS : EXIT_FAILURE;
    }

    signal(SIGTERM, supervisor_stop_handler);
    signal(SIGINT, supervisor_stop_handler);
    signal(SIGPIPE, SIG_IGN);

    pid_t worker = fork();
    if (worker == 0) {
        close(readiness[0]);
        int result = run_network_worker(config_path, config, target_uid, target_gid, managed, listeners, readiness[1]);
        _exit(result);
    }
    close(readiness[1]);
    close_listeners(&listeners);
    if (worker < 0) {
        close(readiness[0]);
        if (!legacy_retired) return EXIT_FAILURE;
        return restore_legacy_service() ? EXIT_SUCCESS : EXIT_FAILURE;
    }

    supervised_worker = worker;
    char ready = 0;
    ssize_t ready_count;
    do {
        ready_count = read(readiness[0], &ready, 1);
    } while (ready_count < 0 && errno == EINTR && !supervisor_stopping);
    close(readiness[0]);

    if (ready_count != 1 || ready != '1') {
        (void)kill(worker, SIGTERM);
        int worker_status = 0;
        while (waitpid(worker, &worker_status, 0) < 0 && errno == EINTR) {}
        supervised_worker = -1;
        if (!legacy_retired) return EXIT_FAILURE;
        return restore_legacy_service() ? EXIT_SUCCESS : EXIT_FAILURE;
    }

    if (legacy_retired && !finalize_legacy_service()) {
        (void)kill(worker, SIGTERM);
        int worker_status = 0;
        while (waitpid(worker, &worker_status, 0) < 0 && errno == EINTR) {}
        supervised_worker = -1;
        return restore_legacy_service() ? EXIT_SUCCESS : EXIT_FAILURE;
    }

    int worker_status = 0;
    pid_t waited;
    do {
        waited = waitpid(worker, &worker_status, 0);
    } while (waited < 0 && errno == EINTR);
    supervised_worker = -1;
    if (supervisor_stopping) return EXIT_SUCCESS;
    if (waited == worker && WIFEXITED(worker_status)) return WEXITSTATUS(worker_status);
    return EXIT_FAILURE;
}
