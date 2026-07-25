#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <grp.h>
#include <netinet/in.h>
#include <poll.h>
#include <pwd.h>
#include <signal.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>

#define CONFIG_PATH_LIMIT 1024
#define DNS_PACKET_LIMIT 4096
#define TLD_LIMIT 63

struct routing_config {
    uint16_t http_port;
    uint16_t https_port;
    char tld[TLD_LIMIT + 1];
};

static volatile sig_atomic_t keep_running = 1;

static void stop_handler(int signal_number) {
    (void)signal_number;
    keep_running = 0;
}

static bool valid_tld(const char *value) {
    size_t length = strlen(value);
    if (length == 0 || length > TLD_LIMIT || value[0] == '-' || value[length - 1] == '-') {
        return false;
    }
    for (size_t index = 0; index < length; index++) {
        char character = value[index];
        bool valid = (character >= 'a' && character <= 'z')
            || (character >= '0' && character <= '9') || character == '-';
        if (!valid) return false;
    }
    return true;
}

static bool parse_port(const char *value, uint16_t *port) {
    char *end = NULL;
    errno = 0;
    long parsed = strtol(value, &end, 10);
    if (errno != 0 || end == value || *end != '\0' || parsed < 1024 || parsed > 65535) {
        return false;
    }
    *port = (uint16_t)parsed;
    return true;
}

static struct routing_config load_config(const char *path) {
    struct routing_config config = { .http_port = 8080, .https_port = 8443, .tld = "test" };
    FILE *file = fopen(path, "r");
    if (file == NULL) return config;

    char line[256];
    while (fgets(line, sizeof(line), file) != NULL) {
        line[strcspn(line, "\r\n")] = '\0';
        char *separator = strchr(line, '=');
        if (separator == NULL) continue;
        *separator = '\0';
        const char *value = separator + 1;
        if (strcmp(line, "http") == 0) {
            uint16_t port;
            if (parse_port(value, &port)) config.http_port = port;
        } else if (strcmp(line, "https") == 0) {
            uint16_t port;
            if (parse_port(value, &port)) config.https_port = port;
        } else if (strcmp(line, "tld") == 0 && valid_tld(value)) {
            strncpy(config.tld, value, TLD_LIMIT);
            config.tld[TLD_LIMIT] = '\0';
        }
    }
    fclose(file);
    return config;
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

    struct pollfd descriptors[2] = {
        { .fd = client, .events = POLLIN },
        { .fd = upstream, .events = POLLIN }
    };
    uint8_t buffer[64 * 1024];
    int readable = 2;
    while (readable > 0) {
        int result = poll(descriptors, 2, -1);
        if (result < 0) {
            if (errno == EINTR) continue;
            break;
        }
        for (int index = 0; index < 2; index++) {
            if (!(descriptors[index].revents & (POLLIN | POLLHUP | POLLERR))) continue;
            int source = descriptors[index].fd;
            int destination = descriptors[1 - index].fd;
            ssize_t count = recv(source, buffer, sizeof(buffer), 0);
            if (count > 0) {
                if (!send_all(destination, buffer, (size_t)count)) readable = 0;
            } else {
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

static void accept_connection(
    int listener,
    uint16_t target_port,
    int http_listener,
    int https_listener,
    int dns_socket
) {
    int client = accept(listener, NULL, NULL);
    if (client < 0) return;
    pid_t child = fork();
    if (child == 0) {
        close(http_listener);
        close(https_listener);
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
    return domain[domain_length - tld_length - 1] == '.'
        && strcmp(domain + domain_length - tld_length, tld) == 0;
}

static size_t dns_response(
    const uint8_t *query,
    size_t query_length,
    const char *tld,
    uint8_t *response,
    size_t capacity
) {
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

    const uint8_t answer[] = {
        0xC0, 0x0C, 0x00, 0x01, 0x00, 0x01,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x04,
        127, 0, 0, 1
    };
    memcpy(response + output, answer, sizeof(answer));
    return output + sizeof(answer);
}

static void answer_dns(int descriptor, const char *config_path) {
    uint8_t query[DNS_PACKET_LIMIT];
    uint8_t response[DNS_PACKET_LIMIT];
    struct sockaddr_storage client;
    socklen_t client_length = sizeof(client);
    ssize_t count = recvfrom(
        descriptor,
        query,
        sizeof(query),
        0,
        (struct sockaddr *)&client,
        &client_length
    );
    if (count <= 0) return;
    struct routing_config config = load_config(config_path);
    size_t response_length = dns_response(
        query,
        (size_t)count,
        config.tld,
        response,
        sizeof(response)
    );
    if (response_length == 0) return;
    sendto(
        descriptor,
        response,
        response_length,
        0,
        (struct sockaddr *)&client,
        client_length
    );
}

static bool parse_identity(const char *value, unsigned long *identity) {
    char *end = NULL;
    errno = 0;
    unsigned long parsed = strtoul(value, &end, 10);
    if (errno != 0 || end == value || *end != '\0') return false;
    *identity = parsed;
    return true;
}

int main(int argc, char **argv) {
    const char *config_path = NULL;
    unsigned long target_uid = 0;
    unsigned long target_gid = 0;
    for (int index = 1; index + 1 < argc; index += 2) {
        if (strcmp(argv[index], "--config") == 0) config_path = argv[index + 1];
        else if (strcmp(argv[index], "--uid") == 0) {
            if (!parse_identity(argv[index + 1], &target_uid)) return EXIT_FAILURE;
        } else if (strcmp(argv[index], "--gid") == 0) {
            if (!parse_identity(argv[index + 1], &target_gid)) return EXIT_FAILURE;
        } else return EXIT_FAILURE;
    }
    if (config_path == NULL || strlen(config_path) >= CONFIG_PATH_LIMIT
        || target_uid == 0 || target_gid == 0 || geteuid() != 0) {
        return EXIT_FAILURE;
    }

    int http_listener = bind_loopback_socket(SOCK_STREAM, 80);
    int https_listener = bind_loopback_socket(SOCK_STREAM, 443);
    int dns_socket = bind_loopback_socket(SOCK_DGRAM, 53);
    if (http_listener < 0 || https_listener < 0 || dns_socket < 0) return EXIT_FAILURE;

    if (setgroups(0, NULL) != 0 || setgid((gid_t)target_gid) != 0
        || setuid((uid_t)target_uid) != 0) {
        return EXIT_FAILURE;
    }
    signal(SIGTERM, stop_handler);
    signal(SIGINT, stop_handler);
    signal(SIGPIPE, SIG_IGN);
    signal(SIGCHLD, SIG_IGN);

    struct pollfd descriptors[3] = {
        { .fd = http_listener, .events = POLLIN },
        { .fd = https_listener, .events = POLLIN },
        { .fd = dns_socket, .events = POLLIN }
    };
    while (keep_running) {
        int result = poll(descriptors, 3, 1000);
        if (result < 0) {
            if (errno == EINTR) continue;
            break;
        }
        struct routing_config config = load_config(config_path);
        if (descriptors[0].revents & POLLIN) {
            accept_connection(
                http_listener,
                config.http_port,
                http_listener,
                https_listener,
                dns_socket
            );
        }
        if (descriptors[1].revents & POLLIN) {
            accept_connection(
                https_listener,
                config.https_port,
                http_listener,
                https_listener,
                dns_socket
            );
        }
        if (descriptors[2].revents & POLLIN) answer_dns(dns_socket, config_path);
    }
    close(http_listener);
    close(https_listener);
    close(dns_socket);
    return EXIT_SUCCESS;
}
