#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <release-assets-directory> <version>" >&2
  exit 64
fi

asset_directory=$1
version=$2

[[ -d "$asset_directory" ]] || {
  echo "Release asset directory not found: $asset_directory" >&2
  exit 66
}
[[ -n "$version" && "$version" != *"/"* && "$version" != *"\\"* ]] || {
  echo "Release version is invalid: $version" >&2
  exit 65
}

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

for artifact in "${expected[@]}"; do
  artifact_path="$asset_directory/$artifact"
  [[ -f "$artifact_path" && ! -L "$artifact_path" ]] || {
    echo "Missing or invalid release artifact: $artifact" >&2
    exit 65
  }
done

actual_count=0
shopt -s nullglob dotglob
for artifact_path in "$asset_directory"/*; do
  ((actual_count += 1))
  artifact=${artifact_path##*/}
  [[ -f "$artifact_path" && ! -L "$artifact_path" ]] || {
    echo "Release assets must be regular files: $artifact" >&2
    exit 65
  }
  allowed=false
  for expected_artifact in "${expected[@]}"; do
    if [[ "$artifact" == "$expected_artifact" ]]; then
      allowed=true
      break
    fi
  done
  [[ "$allowed" == true ]] || {
    echo "Unexpected release artifact: $artifact" >&2
    exit 65
  }
done

[[ "$actual_count" -eq "${#expected[@]}" ]] || {
  echo "The release asset count does not match the public allowlist." >&2
  exit 65
}

echo "Verified exact release asset set for HerdMe $version."
