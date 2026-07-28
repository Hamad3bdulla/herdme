#!/usr/bin/env bash

set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(cd -- "$script_directory/.." && pwd)
verifier="$script_directory/verify-release-asset-set.sh"
version=$(tr -d '[:space:]' < "$repository_root/VERSION")
temporary_directory=$(mktemp -d "${TMPDIR:-/tmp}/herdme-release-assets.XXXXXX")
trap 'rm -rf "$temporary_directory"' EXIT

expected=(
  "HerdMe-$version-macOS.zip"
  "HerdMe-$version-macOS.dmg"
  "HerdMe-$version-macOS.sha256"
  "HerdMe-$version-macOS.spdx.json"
  "HerdMe-$version-macOS.spdx.json.sha256"
  "HerdMe-$version-win-x64-portable.zip"
  "HerdMe-$version-win-x64-portable.zip.sha256"
  "HerdMe-$version-win-x64-setup.exe"
  "HerdMe-$version-win-x64-setup.exe.sha256"
  "HerdMe-$version-win-x64.spdx.json"
  "HerdMe-$version-win-x64.spdx.json.sha256"
  "release-manifest.signed.json"
)

expect_failure() {
  local label=$1
  shift
  if "$@" >"$temporary_directory/$label.log" 2>&1; then
    echo "Expected failure did not occur: $label" >&2
    exit 1
  fi
}

asset_directory="$temporary_directory/assets"
mkdir "$asset_directory"
for artifact in "${expected[@]}"; do
  : > "$asset_directory/$artifact"
done

"$verifier" "$asset_directory" "$version"

: > "$asset_directory/unexpected.txt"
expect_failure unexpected-file "$verifier" "$asset_directory" "$version"
rm "$asset_directory/unexpected.txt"

missing_artifact=${expected[0]}
rm "$asset_directory/$missing_artifact"
expect_failure missing-file "$verifier" "$asset_directory" "$version"
: > "$asset_directory/$missing_artifact"

directory_artifact=${expected[1]}
rm "$asset_directory/$directory_artifact"
mkdir "$asset_directory/$directory_artifact"
expect_failure directory-instead-of-file "$verifier" "$asset_directory" "$version"
rmdir "$asset_directory/$directory_artifact"
: > "$asset_directory/$directory_artifact"

symlink_artifact=${expected[2]}
rm "$asset_directory/$symlink_artifact"
ln -s "$asset_directory/${expected[3]}" "$asset_directory/$symlink_artifact"
expect_failure symlink-instead-of-file "$verifier" "$asset_directory" "$version"

echo "Release asset allowlist contract passed."
