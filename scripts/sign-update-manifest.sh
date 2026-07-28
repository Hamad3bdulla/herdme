#!/bin/bash

set -euo pipefail

if [[ $# -lt 3 || $# -gt 4 ]]; then
  echo "Usage: $0 <manifest.json> <private-key.pem> <signed-manifest.json> [public-key.txt]" >&2
  exit 64
fi

manifest_path=$1
private_key_path=$2
signed_manifest_path=$3
public_key_path=${4:-}

[[ -f "$manifest_path" ]] || { echo "Manifest not found: $manifest_path" >&2; exit 66; }
[[ -f "$private_key_path" ]] || { echo "Private key not found: $private_key_path" >&2; exit 66; }
command -v openssl >/dev/null || { echo "OpenSSL is required." >&2; exit 69; }
command -v ruby >/dev/null || { echo "Ruby is required to validate the release manifest." >&2; exit 69; }

manifest_size=$(wc -c < "$manifest_path" | tr -d '[:space:]')
[[ "$manifest_size" -le 4194304 ]] || {
  echo "The release manifest exceeds the 4 MB application limit." >&2
  exit 65
}

ruby -rjson -rset -ruri - "$manifest_path" <<'RUBY'
SEMVER = /\Av?(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-(?:(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+(?:[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z/

def https_url(value, label)
  uri = URI.parse(value.to_s)
  abort "#{label} must be an absolute HTTPS URL." unless uri.is_a?(URI::HTTPS) && uri.host
  uri.to_s
rescue URI::InvalidURIError
  abort "#{label} must be an absolute HTTPS URL."
end

begin
  manifest = JSON.parse(File.binread(ARGV.fetch(0)))
rescue JSON::ParserError => error
  abort "The release manifest is invalid JSON: #{error.message}"
end

releases = manifest["releases"]
abort "The release manifest must contain at least one release." unless releases.is_a?(Array) && !releases.empty?
identities = Set.new
releases.each_with_index do |release, index|
  label = "releases[#{index}]"
  abort "#{label} is invalid." unless release.is_a?(Hash)
  version = release["version"]
  build = release["build"]
  channel = release["channel"]
  notes = release["notes"]
  abort "#{label}.version is invalid." unless version.is_a?(String) && version.match?(SEMVER)
  abort "#{label}.build must be a nonnegative integer." unless build.is_a?(Integer) && build >= 0
  abort "#{label}.channel must be stable or beta." unless %w[stable beta].include?(channel.to_s.downcase)
  abort "#{label}.notes must be a string." unless notes.is_a?(String)
  identity = [version.delete_prefix("v"), build, channel.to_s.downcase]
  abort "#{label} duplicates another release identity." unless identities.add?(identity)
  abort "#{label}.downloadURL is obsolete; use downloadURLs." unless release["downloadURL"].nil?
  downloads = release["downloadURLs"]
  abort "#{label}.downloadURLs must contain both platform artifacts." unless downloads.is_a?(Hash)
  macos_url = https_url(downloads["macOS"], "#{label}.downloadURLs.macOS")
  windows_url = https_url(downloads["windowsX64"], "#{label}.downloadURLs.windowsX64")
  abort "#{label} must not use the same artifact for macOS and Windows." if macos_url == windows_url
  artifact_version = version.delete_prefix("v")
  expected_macos = "HerdMe-#{artifact_version}-macOS.zip"
  expected_windows = "HerdMe-#{artifact_version}-win-x64-setup.exe"
  abort "#{label}.downloadURLs.macOS must end with #{expected_macos}." unless \
    File.basename(URI.parse(macos_url).path) == expected_macos
  abort "#{label}.downloadURLs.windowsX64 must end with #{expected_windows}." unless \
    File.basename(URI.parse(windows_url).path) == expected_windows
end
RUBY

temporary_directory=$(mktemp -d "${TMPDIR:-/tmp}/herdme-update-signing.XXXXXX")
trap 'rm -rf "$temporary_directory"' EXIT

signature_path="$temporary_directory/signature.der"
public_der_path="$temporary_directory/public.der"
public_raw_path="$temporary_directory/public-x963.bin"
public_pem_path="$temporary_directory/public.pem"
output_path="$temporary_directory/release-manifest.signed.json"
p256_spki_prefix=3059301306072a8648ce3d020106082a8648ce3d030107034200

openssl dgst -sha256 -sign "$private_key_path" -out "$signature_path" "$manifest_path"
openssl pkey -in "$private_key_path" -pubout -out "$public_pem_path"
openssl dgst -sha256 -verify "$public_pem_path" -signature "$signature_path" "$manifest_path" >/dev/null
openssl pkey -in "$private_key_path" -pubout -outform DER -out "$public_der_path"
tail -c 65 "$public_der_path" > "$public_raw_path"

public_der_prefix=$(head -c 26 "$public_der_path" | od -An -t x1 | tr -d '[:space:]')
public_point_prefix=$(od -An -t x1 -N 1 "$public_raw_path" | tr -d '[:space:]')
[[ $(wc -c < "$public_der_path" | tr -d '[:space:]') == 91 \
    && "$public_der_prefix" == "$p256_spki_prefix" \
    && $(wc -c < "$public_raw_path" | tr -d '[:space:]') == 65 \
    && "$public_point_prefix" == 04 ]] || {
  echo "The private key must be an ECDSA P-256 key." >&2
  exit 65
}

payload=$(openssl base64 -A -in "$manifest_path")
signature=$(openssl base64 -A -in "$signature_path")
printf '{\n  "algorithm": "ECDSA_P256_SHA256",\n  "payload": "%s",\n  "signature": "%s"\n}\n' \
  "$payload" "$signature" > "$output_path"

mkdir -p "$(dirname "$signed_manifest_path")"
mv "$output_path" "$signed_manifest_path"

if [[ -n "$public_key_path" ]]; then
  mkdir -p "$(dirname "$public_key_path")"
  openssl base64 -A -in "$public_raw_path" > "$public_key_path"
  printf '\n' >> "$public_key_path"
fi

echo "Signed update manifest: $signed_manifest_path"
[[ -z "$public_key_path" ]] || echo "Public verification key: $public_key_path"
