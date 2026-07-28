#!/usr/bin/env bash

set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(cd -- "$script_directory/.." && pwd)
manifest_path="$repository_root/HerdMe/Resources/release-manifest.json"
signer="$script_directory/sign-update-manifest.sh"
powershell_signer="$script_directory/sign-update-manifest.ps1"
verifier="$script_directory/verify-signed-update-manifest.sh"
powershell_executable=${HERDME_PWSH:-pwsh}

command -v openssl >/dev/null || { echo "OpenSSL is required." >&2; exit 69; }
command -v ruby >/dev/null || { echo "Ruby is required." >&2; exit 69; }
if [[ "$powershell_executable" == */* ]]; then
  [[ -x "$powershell_executable" ]] || {
    echo "PowerShell is required: $powershell_executable" >&2
    exit 69
  }
else
  command -v "$powershell_executable" >/dev/null || {
    echo "PowerShell is required. Set HERDME_PWSH to its executable path." >&2
    exit 69
  }
fi

temporary_directory=$(mktemp -d "${TMPDIR:-/tmp}/herdme-update-contract.XXXXXX")
trap 'rm -rf "$temporary_directory"' EXIT

private_key="$temporary_directory/private-key.pem"
public_key="$temporary_directory/public-key.txt"
signed_manifest="$temporary_directory/release-manifest.signed.json"
powershell_public_key="$temporary_directory/powershell-public-key.txt"
powershell_signed_manifest="$temporary_directory/powershell-release-manifest.signed.json"

expect_failure() {
  local label=$1
  shift
  if "$@" >"$temporary_directory/$label.log" 2>&1; then
    echo "Expected failure did not occur: $label" >&2
    exit 1
  fi
}

openssl ecparam -name prime256v1 -genkey -noout -out "$private_key"
"$signer" "$manifest_path" "$private_key" "$signed_manifest" "$public_key"
"$verifier" "$signed_manifest" "$public_key" "$manifest_path"
"$powershell_executable" -NoLogo -NoProfile -NonInteractive \
  -File "$powershell_signer" \
  -ManifestPath "$manifest_path" \
  -PrivateKeyPath "$private_key" \
  -SignedManifestPath "$powershell_signed_manifest" \
  -PublicKeyPath "$powershell_public_key"
"$verifier" "$powershell_signed_manifest" "$public_key" "$manifest_path"
"$verifier" "$signed_manifest" "$powershell_public_key" "$manifest_path"
cmp "$public_key" "$powershell_public_key" >/dev/null || {
  echo "The shell and PowerShell signers derived different public keys." >&2
  exit 65
}

ruby -rbase64 - "$public_key" <<'RUBY'
key = Base64.strict_decode64(File.binread(ARGV.fetch(0)).strip)
abort "Generated update key is not a P-256 X9.63 public key." unless \
  key.bytesize == 65 && key.getbyte(0) == 4
RUBY

tampered_payload="$temporary_directory/tampered-payload.json"
tampered_signature="$temporary_directory/tampered-signature.json"
tampered_algorithm="$temporary_directory/tampered-algorithm.json"
ruby -rjson -rbase64 - "$signed_manifest" \
  "$tampered_payload" "$tampered_signature" "$tampered_algorithm" <<'RUBY'
source, payload_path, signature_path, algorithm_path = ARGV
envelope = JSON.parse(File.binread(source))

payload = Base64.strict_decode64(envelope.fetch("payload"))
payload.setbyte(0, payload.getbyte(0) ^ 1)
payload_envelope = envelope.merge("payload" => Base64.strict_encode64(payload))
File.write(payload_path, JSON.pretty_generate(payload_envelope) + "\n")

signature = Base64.strict_decode64(envelope.fetch("signature"))
signature.setbyte(signature.bytesize - 1, signature.getbyte(-1) ^ 1)
signature_envelope = envelope.merge("signature" => Base64.strict_encode64(signature))
File.write(signature_path, JSON.pretty_generate(signature_envelope) + "\n")

algorithm_envelope = envelope.merge("algorithm" => "ECDSA_P384_SHA384")
File.write(algorithm_path, JSON.pretty_generate(algorithm_envelope) + "\n")
RUBY

expect_failure tampered-payload \
  "$verifier" "$tampered_payload" "$public_key"
expect_failure tampered-signature \
  "$verifier" "$tampered_signature" "$public_key"
expect_failure tampered-algorithm \
  "$verifier" "$tampered_algorithm" "$public_key"

wrong_private_key="$temporary_directory/wrong-private-key.pem"
wrong_public_key="$temporary_directory/wrong-public-key.txt"
wrong_signed_manifest="$temporary_directory/wrong-signed-manifest.json"
openssl ecparam -name prime256v1 -genkey -noout -out "$wrong_private_key"
"$signer" \
  "$manifest_path" "$wrong_private_key" "$wrong_signed_manifest" "$wrong_public_key" \
  >/dev/null
expect_failure wrong-public-key \
  "$verifier" "$signed_manifest" "$wrong_public_key"

different_manifest="$temporary_directory/different-manifest.json"
cp "$manifest_path" "$different_manifest"
printf '\n' >> "$different_manifest"
expect_failure different-payload \
  "$verifier" "$signed_manifest" "$public_key" "$different_manifest"

insecure_manifest="$temporary_directory/insecure-manifest.json"
ruby -rjson - "$manifest_path" "$insecure_manifest" <<'RUBY'
source, destination = ARGV
manifest = JSON.parse(File.binread(source))
manifest.fetch("releases").fetch(0).fetch("downloadURLs")["macOS"] = \
  "http://example.invalid/HerdMe.zip"
File.write(destination, JSON.pretty_generate(manifest) + "\n")
RUBY
expect_failure insecure-download \
  "$signer" "$insecure_manifest" "$private_key" \
  "$temporary_directory/insecure-signed.json"
expect_failure powershell-insecure-download \
  "$powershell_executable" -NoLogo -NoProfile -NonInteractive \
  -File "$powershell_signer" \
  -ManifestPath "$insecure_manifest" \
  -PrivateKeyPath "$private_key" \
  -SignedManifestPath "$temporary_directory/powershell-insecure-signed.json"

wrong_artifact_manifest="$temporary_directory/wrong-artifact-manifest.json"
ruby -rjson - "$manifest_path" "$wrong_artifact_manifest" <<'RUBY'
source, destination = ARGV
manifest = JSON.parse(File.binread(source))
manifest.fetch("releases").fetch(0).fetch("downloadURLs")["macOS"] = \
  "https://example.invalid/not-the-release.zip"
File.write(destination, JSON.pretty_generate(manifest) + "\n")
RUBY
expect_failure wrong-artifact-name \
  "$signer" "$wrong_artifact_manifest" "$private_key" \
  "$temporary_directory/wrong-artifact-signed.json"
expect_failure powershell-wrong-artifact-name \
  "$powershell_executable" -NoLogo -NoProfile -NonInteractive \
  -File "$powershell_signer" \
  -ManifestPath "$wrong_artifact_manifest" \
  -PrivateKeyPath "$private_key" \
  -SignedManifestPath "$temporary_directory/powershell-wrong-artifact-signed.json"

duplicate_manifest="$temporary_directory/duplicate-manifest.json"
ruby -rjson - "$manifest_path" "$duplicate_manifest" <<'RUBY'
source, destination = ARGV
manifest = JSON.parse(File.binread(source))
manifest.fetch("releases") << JSON.parse(JSON.generate(manifest.fetch("releases").fetch(0)))
File.write(destination, JSON.pretty_generate(manifest) + "\n")
RUBY
expect_failure duplicate-release \
  "$signer" "$duplicate_manifest" "$private_key" \
  "$temporary_directory/duplicate-signed.json"
expect_failure powershell-duplicate-release \
  "$powershell_executable" -NoLogo -NoProfile -NonInteractive \
  -File "$powershell_signer" \
  -ManifestPath "$duplicate_manifest" \
  -PrivateKeyPath "$private_key" \
  -SignedManifestPath "$temporary_directory/powershell-duplicate-signed.json"

p384_private_key="$temporary_directory/p384-private-key.pem"
openssl ecparam -name secp384r1 -genkey -noout -out "$p384_private_key"
expect_failure wrong-curve \
  "$signer" "$manifest_path" "$p384_private_key" \
  "$temporary_directory/p384-signed.json"
expect_failure powershell-wrong-curve \
  "$powershell_executable" -NoLogo -NoProfile -NonInteractive \
  -File "$powershell_signer" \
  -ManifestPath "$manifest_path" \
  -PrivateKeyPath "$p384_private_key" \
  -SignedManifestPath "$temporary_directory/powershell-p384-signed.json"

echo "Shell and PowerShell update manifest signing contract passed."
