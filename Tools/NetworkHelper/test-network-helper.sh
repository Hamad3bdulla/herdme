#!/bin/zsh

set -euo pipefail

script_directory="${0:A:h}"
test_directory=$(mktemp -d "${TMPDIR:-/tmp}/herdme-network-helper-test.XXXXXX")
helper_pid=""
legacy_pid_file="$test_directory/legacy.pid"
blocker_pid_file="$test_directory/blocker.pid"
migration_mode="$test_directory/migration-mode"
migration_log="$test_directory/launchctl.log"
legacy_helper="$test_directory/app.herdme.network-helper"
legacy_plist="$test_directory/app.herdme.network-helper.plist"
launchctl_mock="$test_directory/launchctl-mock"

cleanup() {
    if [[ -n "$helper_pid" ]] && kill -0 "$helper_pid" 2>/dev/null; then
        kill "$helper_pid" 2>/dev/null || true
        wait "$helper_pid" 2>/dev/null || true
    fi
    for pid_file in "$legacy_pid_file" "$blocker_pid_file"; do
        if [[ -f "$pid_file" ]]; then
            local process_id
            process_id=$(<"$pid_file")
            kill "$process_id" 2>/dev/null || true
            wait "$process_id" 2>/dev/null || true
        fi
    done
    rm -r "$test_directory" 2>/dev/null || true
}
trap cleanup EXIT INT TERM

front_http_port=18080
front_https_port=18443
front_dns_port=15353
configuration="$test_directory/network-helper.conf"
executable="$test_directory/herdme-network-helper-test"
resolver_directory="$test_directory/resolver"

export HERDME_TEST_LAUNCHCTL_LOG="$migration_log"
export HERDME_TEST_MIGRATION_MODE="$migration_mode"
export HERDME_TEST_LEGACY_PID="$legacy_pid_file"
export HERDME_TEST_BLOCKER_PID="$blocker_pid_file"
export HERDME_TEST_HTTP_PORT="$front_http_port"

printf '%s\n' \
    '#!/bin/zsh' \
    'set -eu' \
    'start_listener() {' \
    '    local pid_file="$1"' \
    '    /usr/bin/nc -l 127.0.0.1 "$HERDME_TEST_HTTP_PORT" </dev/null >/dev/null 2>&1 &' \
    '    local listener_pid="$!"' \
    '    print -r -- "$listener_pid" > "$pid_file"' \
    '    for _ in {1..50}; do' \
    '        if /usr/sbin/lsof -nP -a -p "$listener_pid" -iTCP:"$HERDME_TEST_HTTP_PORT" -sTCP:LISTEN >/dev/null 2>&1; then' \
    '            return 0' \
    '        fi' \
    '        sleep 0.02' \
    '    done' \
    '    return 1' \
    '}' \
    'print -r -- "$*" >> "$HERDME_TEST_LAUNCHCTL_LOG"' \
    'case "$1" in' \
    '    bootout)' \
    '        if [[ -f "$HERDME_TEST_LEGACY_PID" ]]; then' \
    '            kill "$(<"$HERDME_TEST_LEGACY_PID")" 2>/dev/null || true' \
    '            rm -f "$HERDME_TEST_LEGACY_PID"' \
    '        fi' \
    '        if [[ "$(<"$HERDME_TEST_MIGRATION_MODE")" == rollback ]]; then' \
    '            start_listener "$HERDME_TEST_BLOCKER_PID"' \
    '        fi' \
    '        ;;' \
    '    bootstrap)' \
    '        if [[ -f "$HERDME_TEST_BLOCKER_PID" ]]; then' \
    '            kill "$(<"$HERDME_TEST_BLOCKER_PID")" 2>/dev/null || true' \
    '            rm -f "$HERDME_TEST_BLOCKER_PID"' \
    '        fi' \
    '        start_listener "$HERDME_TEST_LEGACY_PID"' \
    '        ;;' \
    'esac' > "$launchctl_mock"
chmod 755 "$launchctl_mock"

xcrun --sdk macosx clang \
    -std=c17 -O1 -Wall -Wextra -Werror \
    -DHERDME_NETWORK_HELPER_TEST=1 \
    -DHERDME_HTTP_LISTEN_PORT="$front_http_port" \
    -DHERDME_HTTPS_LISTEN_PORT="$front_https_port" \
    -DHERDME_DNS_LISTEN_PORT="$front_dns_port" \
    -DHERDME_RESOLVER_DIRECTORY="\"$resolver_directory\"" \
    -DHERDME_LEGACY_HELPER="\"$legacy_helper\"" \
    -DHERDME_LEGACY_PLIST="\"$legacy_plist\"" \
    -DHERDME_LAUNCHCTL_PATH="\"$launchctl_mock\"" \
    -DHERDME_BIND_RETRY_COUNT=10 \
    -DHERDME_BIND_RETRY_NANOSECONDS=50000000L \
    -mmacosx-version-min=13.0 \
    "$script_directory/main.c" \
    -o "$executable"

write_configuration() {
    local https_target="$1"
    local staging="$configuration.next"
    printf 'http=19080\nhttps=%s\ntld=test\n' "$https_target" > "$staging"
    mv "$staging" "$configuration"
}

is_listening() {
    local port="$1"
    lsof -nP -iTCP:"$port" -sTCP:LISTEN 2>/dev/null \
        | awk 'NR > 1 { found = 1 } END { exit !found }'
}

start_legacy_listener() {
    /usr/bin/nc -l 127.0.0.1 "$front_http_port" </dev/null >/dev/null 2>&1 &
    print -r -- "$!" > "$legacy_pid_file"
    wait_for_state "$front_http_port" open
}

stop_legacy_listener() {
    if [[ ! -f "$legacy_pid_file" ]]; then return 0; fi
    local process_id
    process_id=$(<"$legacy_pid_file")
    kill "$process_id" 2>/dev/null || true
    wait "$process_id" 2>/dev/null || true
    rm -f "$legacy_pid_file"
    wait_for_state "$front_http_port" closed
}

wait_for_state() {
    local port="$1"
    local expected="$2"
    for _ in {1..50}; do
        if [[ "$expected" == "open" ]] && is_listening "$port"; then
            return 0
        fi
        if [[ "$expected" == "closed" ]] && ! is_listening "$port"; then
            return 0
        fi
        sleep 0.1
    done
    echo "Port $port did not become $expected." >&2
    return 1
}

write_configuration 0
printf 'http=19080\ntld=test\n' > "$configuration.invalid"
if "$executable" \
    --config "$configuration.invalid" \
    --uid "$(id -u)" \
    --gid "$(id -g)"; then
    echo "The helper accepted an incomplete routing configuration." >&2
    exit 1
fi

mkdir "$resolver_directory"
printf '# Managed by HerdMe\nnameserver 127.0.0.1\nport %s\nsearch_order 1\ntimeout 1\n' \
    "$front_dns_port" > "$resolver_directory/old-test"
printf 'nameserver 192.0.2.1\n' > "$resolver_directory/external"

printf '#!/bin/sh\nexit 0\n' > "$legacy_helper"
chmod 755 "$legacy_helper"
printf '<plist version="1.0"><dict/></plist>\n' > "$legacy_plist"
chmod 644 "$legacy_plist"
printf 'rollback\n' > "$migration_mode"
start_legacy_listener

"$executable" \
    --managed-test "$configuration" \
    --uid "$(id -u)" \
    --gid "$(id -g)"

[[ -f "$legacy_helper" ]]
[[ -f "$legacy_plist" ]]
grep -q '^bootout system/app.herdme.network-helper$' "$migration_log"
grep -q '^enable system/app.herdme.network-helper$' "$migration_log"
grep -q '^bootstrap system ' "$migration_log"
grep -q '^kickstart -k system/app.herdme.network-helper$' "$migration_log"
wait_for_state "$front_http_port" open
stop_legacy_listener

: > "$migration_log"
printf 'success\n' > "$migration_mode"
start_legacy_listener
"$executable" \
    --managed-test "$configuration" \
    --uid "$(id -u)" \
    --gid "$(id -g)" &
helper_pid=$!

wait_for_state "$front_http_port" open
wait_for_state "$front_https_port" open

[[ -f "$resolver_directory/test" ]]
[[ ! -e "$resolver_directory/old-test" ]]
[[ "$(cat "$resolver_directory/external")" == 'nameserver 192.0.2.1' ]]
[[ ! -e "$legacy_helper" ]]
[[ ! -e "$legacy_plist" ]]
grep -q '^bootout system/app.herdme.network-helper$' "$migration_log"
grep -q '^disable system/app.herdme.network-helper$' "$migration_log"
grep -q "port $front_dns_port" "$resolver_directory/test"

write_configuration 19443
wait_for_state "$front_https_port" open

write_configuration 0
wait_for_state "$front_https_port" open

printf 'http=invalid\nhttps=0\ntld=test\n' > "$configuration.next"
mv "$configuration.next" "$configuration"
sleep 1

kill -0 "$helper_pid"
