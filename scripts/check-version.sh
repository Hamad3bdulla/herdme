#!/usr/bin/env bash

set -euo pipefail

script_directory=$(cd "$(dirname "$0")" && pwd)
repository_root=$(cd "$script_directory/.." && pwd)
version=$(tr -d '[:space:]' < "$repository_root/VERSION")
build_number=$(tr -d '[:space:]' < "$repository_root/BUILD_NUMBER")

if [[ ! "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
    echo "VERSION must contain a semantic version such as 1.2.3." >&2
    exit 1
fi
if [[ ! "$build_number" =~ ^[1-9][0-9]*$ ]]; then
    echo "BUILD_NUMBER must contain a positive integer." >&2
    exit 1
fi

IFS=. read -r version_major version_minor version_patch <<< "$version"
for version_component in "$version_major" "$version_minor" "$version_patch"; do
    if (( ${#version_component} > 5 )) || (( 10#$version_component > 65535 )); then
        echo "Every VERSION component must be between 0 and 65535 for Windows file versions." >&2
        exit 1
    fi
done
if (( ${#build_number} > 5 )) || (( 10#$build_number > 65535 )); then
    echo "BUILD_NUMBER must be between 1 and 65535 for Windows file versions." >&2
    exit 1
fi

project_version=$(/usr/bin/ruby -ryaml -e '
  document = YAML.load_file(ARGV.fetch(0))
  puts document.fetch("settings").fetch("base").fetch("MARKETING_VERSION")
' "$repository_root/project.yml")
project_build=$(/usr/bin/ruby -ryaml -e '
  document = YAML.load_file(ARGV.fetch(0))
  puts document.fetch("settings").fetch("base").fetch("CURRENT_PROJECT_VERSION")
' "$repository_root/project.yml")
manifest_metadata=$(/usr/bin/ruby -rjson -ruri -e '
  semver = /\A(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-(?:(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+(?:[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z/

  def https_url(value, label)
    uri = URI.parse(value.to_s)
    abort "#{label} must be an absolute HTTPS URL." unless uri.is_a?(URI::HTTPS) && uri.host
    uri.to_s
  rescue URI::InvalidURIError
    abort "#{label} must be an absolute HTTPS URL."
  end

  document = JSON.parse(File.binread(ARGV.fetch(0)))
  releases = document["releases"]
  abort "The release manifest must contain at least one release." unless releases.is_a?(Array) && !releases.empty?
  releases.each_with_index do |release, index|
    label = "releases[#{index}]"
    abort "#{label} is invalid." unless release.is_a?(Hash)
    abort "#{label}.version is invalid." unless release["version"].is_a?(String) && release["version"].match?(semver)
    abort "#{label}.build must be a nonnegative integer." unless release["build"].is_a?(Integer) && release["build"] >= 0
    abort "#{label}.channel must be stable or beta." unless %w[stable beta].include?(release["channel"].to_s.downcase)
    abort "#{label}.notes must be a string." unless release["notes"].is_a?(String)
    abort "#{label}.downloadURL is obsolete; use downloadURLs." unless release["downloadURL"].nil?
    downloads = release["downloadURLs"]
    abort "#{label}.downloadURLs must contain both platform artifacts." unless downloads.is_a?(Hash)
    macos_url = https_url(downloads["macOS"], "#{label}.downloadURLs.macOS")
    windows_url = https_url(downloads["windowsX64"], "#{label}.downloadURLs.windowsX64")
    abort "#{label} must not use the same artifact for macOS and Windows." if macos_url == windows_url
  end
  puts releases.first.fetch("version")
  puts releases.first.fetch("build")
' "$repository_root/HerdMe/Resources/release-manifest.json")
manifest_version=$(printf '%s\n' "$manifest_metadata" | sed -n '1p')
manifest_build=$(printf '%s\n' "$manifest_metadata" | sed -n '2p')

public_key_path="$repository_root/HerdMe/Resources/release-public-key.txt"
feed_url_path="$repository_root/HerdMe/Resources/release-feed-url.txt"
if [[ -f "$public_key_path" || -f "$feed_url_path" ]]; then
    [[ -f "$public_key_path" && -f "$feed_url_path" ]] || {
        echo "release-public-key.txt and release-feed-url.txt must be provided together." >&2
        exit 1
    }
    temporary_key=$(mktemp "${TMPDIR:-/tmp}/herdme-release-public-key.XXXXXX")
    trap 'rm -f "$temporary_key"' EXIT
    openssl base64 -d -A -in "$public_key_path" -out "$temporary_key" 2>/dev/null || {
        echo "release-public-key.txt is not valid Base64." >&2
        exit 1
    }
    public_key_size=$(wc -c < "$temporary_key" | tr -d '[:space:]')
    public_key_prefix=$(od -An -t x1 -N 1 "$temporary_key" | tr -d '[:space:]')
    if [[ "$public_key_size" != 65 || "$public_key_prefix" != 04 ]]; then
        echo "release-public-key.txt must contain a 65-byte P-256 X9.63 public key." >&2
        exit 1
    fi
    feed_url=$(tr -d '[:space:]' < "$feed_url_path")
    /usr/bin/ruby -ruri -e '
      uri = URI.parse(ARGV.fetch(0))
      abort unless uri.is_a?(URI::HTTPS) && uri.host && !uri.host.empty?
    ' "$feed_url" || {
        echo "release-feed-url.txt must contain an absolute HTTPS URL." >&2
        exit 1
    }
fi

if [[ "$project_version" != "$version" || "$manifest_version" != "$version" ]]; then
    echo "Version mismatch: VERSION=$version, project.yml=$project_version, manifest=$manifest_version" >&2
    exit 1
fi
if [[ "$project_build" != "$build_number" || "$manifest_build" != "$build_number" ]]; then
    echo "Build mismatch: BUILD_NUMBER=$build_number, project.yml=$project_build, manifest=$manifest_build" >&2
    exit 1
fi

echo "HerdMe version $version ($build_number) is consistent."
