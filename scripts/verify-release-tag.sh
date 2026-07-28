#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 4 ]]; then
    echo "Usage: $0 <tag-ref.json> <tag-object.json> <expected-tag> <expected-commit>" >&2
    exit 64
fi

ref_path=$1
tag_path=$2
expected_tag=$3
expected_commit=$4

[[ -f "$ref_path" ]] || { echo "Tag ref response not found: $ref_path" >&2; exit 66; }
[[ -f "$tag_path" ]] || { echo "Tag object response not found: $tag_path" >&2; exit 66; }
[[ "$expected_tag" =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || {
    echo "The release tag must be an exact vMAJOR.MINOR.PATCH value." >&2
    exit 65
}
[[ "$expected_commit" =~ ^[0-9a-fA-F]{40}$ ]] || {
    echo "The expected release commit must be a full 40-character Git SHA." >&2
    exit 65
}
command -v ruby >/dev/null || { echo "Ruby is required to validate the release tag." >&2; exit 69; }
normalized_commit=$(printf '%s' "$expected_commit" | tr '[:upper:]' '[:lower:]')

ruby -rjson - "$ref_path" "$tag_path" "$expected_tag" "$normalized_commit" <<'RUBY'
ref_path, tag_path, expected_tag, expected_commit = ARGV

def read_json(path, label)
  JSON.parse(File.binread(path))
rescue JSON::ParserError => error
  abort "The #{label} response is invalid JSON: #{error.message}"
end

ref = read_json(ref_path, "tag ref")
tag = read_json(tag_path, "tag object")
expected_ref = "refs/tags/#{expected_tag}"

abort "The GitHub tag ref does not match #{expected_ref}." unless ref["ref"] == expected_ref
ref_object = ref["object"]
abort "The release ref must point to an annotated tag object, not a lightweight tag." unless \
  ref_object.is_a?(Hash) && ref_object["type"] == "tag"
tag_sha = ref_object["sha"].to_s.downcase
abort "The annotated tag object identity is inconsistent." unless \
  tag_sha.match?(/\A[0-9a-f]{40}\z/) && tag["sha"].to_s.downcase == tag_sha
abort "The annotated tag name does not match #{expected_tag}." unless tag["tag"] == expected_tag
target = tag["object"]
abort "The release tag must point directly to a commit." unless \
  target.is_a?(Hash) && target["type"] == "commit"
abort "The signed tag does not point to the commit being built." unless \
  target["sha"].to_s.downcase == expected_commit
verification = tag["verification"]
abort "GitHub did not verify the release tag signature." unless \
  verification.is_a?(Hash) && verification["verified"] == true
RUBY

echo "Verified signed release tag $expected_tag at $normalized_commit."
