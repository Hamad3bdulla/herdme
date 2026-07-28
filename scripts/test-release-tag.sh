#!/usr/bin/env bash

set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
verifier="$script_directory/verify-release-tag.sh"
temporary_directory=$(mktemp -d "${TMPDIR:-/tmp}/herdme-release-tag.XXXXXX")
trap 'rm -rf "$temporary_directory"' EXIT

tag_name=v1.2.3
tag_sha=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
commit_sha=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
ref_path="$temporary_directory/ref.json"
tag_path="$temporary_directory/tag.json"

write_ref() {
    local object_type=$1
    local object_sha=${2:-$tag_sha}
    printf '{"ref":"refs/tags/%s","object":{"type":"%s","sha":"%s"}}\n' \
        "$tag_name" "$object_type" "$object_sha" > "$ref_path"
}

write_tag() {
    local name=$1
    local target_type=$2
    local target_sha=$3
    local verified=$4
    printf '{"sha":"%s","tag":"%s","object":{"type":"%s","sha":"%s"},"verification":{"verified":%s,"reason":"%s"}}\n' \
        "$tag_sha" "$name" "$target_type" "$target_sha" "$verified" \
        "$([[ "$verified" == true ]] && printf valid || printf unsigned)" > "$tag_path"
}

expect_failure() {
    local label=$1
    shift
    if "$@" > "$temporary_directory/$label.log" 2>&1; then
        echo "Expected failure did not occur: $label" >&2
        exit 1
    fi
}

write_ref tag
write_tag "$tag_name" commit "$commit_sha" true
"$verifier" "$ref_path" "$tag_path" "$tag_name" "$commit_sha"

write_ref commit "$commit_sha"
expect_failure lightweight-tag \
    "$verifier" "$ref_path" "$tag_path" "$tag_name" "$commit_sha"

write_ref tag
write_tag "$tag_name" commit "$commit_sha" false
expect_failure unverified-signature \
    "$verifier" "$ref_path" "$tag_path" "$tag_name" "$commit_sha"

write_tag v1.2.4 commit "$commit_sha" true
expect_failure mismatched-name \
    "$verifier" "$ref_path" "$tag_path" "$tag_name" "$commit_sha"

write_tag "$tag_name" commit cccccccccccccccccccccccccccccccccccccccc true
expect_failure mismatched-commit \
    "$verifier" "$ref_path" "$tag_path" "$tag_name" "$commit_sha"

write_tag "$tag_name" tree "$commit_sha" true
expect_failure non-commit-target \
    "$verifier" "$ref_path" "$tag_path" "$tag_name" "$commit_sha"

printf '{ invalid json\n' > "$tag_path"
expect_failure malformed-response \
    "$verifier" "$ref_path" "$tag_path" "$tag_name" "$commit_sha"

echo "Signed release tag contract passed."
