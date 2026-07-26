#!/usr/bin/env bash

set -euo pipefail

if [[ $# -lt 2 || $# -gt 3 ]]; then
  echo "Usage: $0 <signed-manifest.json> <public-key.txt> [expected-manifest.json]" >&2
  exit 64
fi

signed_manifest_path=$1
public_key_path=$2
expected_manifest_path=${3:-}

[[ -f "$signed_manifest_path" ]] || {
  echo "Signed manifest not found: $signed_manifest_path" >&2
  exit 66
}
[[ -f "$public_key_path" ]] || {
  echo "Public key not found: $public_key_path" >&2
  exit 66
}
if [[ -n "$expected_manifest_path" && ! -f "$expected_manifest_path" ]]; then
  echo "Expected manifest not found: $expected_manifest_path" >&2
  exit 66
fi
command -v openssl >/dev/null || { echo "OpenSSL is required." >&2; exit 69; }
command -v ruby >/dev/null || { echo "Ruby is required." >&2; exit 69; }

temporary_directory=$(mktemp -d "${TMPDIR:-/tmp}/herdme-update-verification.XXXXXX")
trap 'rm -rf "$temporary_directory"' EXIT
payload_path="$temporary_directory/payload.json"
signature_path="$temporary_directory/signature.der"
public_der_path="$temporary_directory/public.der"
public_pem_path="$temporary_directory/public.pem"

ruby -rjson -rbase64 - "$signed_manifest_path" "$public_key_path" \
  "$payload_path" "$signature_path" "$public_der_path" <<'RUBY'
signed_path, public_path, payload_path, signature_path, public_der_path = ARGV
begin
  envelope = JSON.parse(File.binread(signed_path))
rescue JSON::ParserError => error
  abort "The signed update manifest is invalid JSON: #{error.message}"
end
abort "The signed update manifest uses an unsupported algorithm." unless \
  envelope["algorithm"] == "ECDSA_P256_SHA256"
begin
  payload = Base64.strict_decode64(envelope.fetch("payload"))
  signature = Base64.strict_decode64(envelope.fetch("signature"))
  public_key = Base64.strict_decode64(File.binread(public_path).strip)
rescue KeyError, ArgumentError => error
  abort "The signed update manifest or public key is invalid: #{error.message}"
end
abort "The signed update payload exceeds the 4 MB application limit." if payload.bytesize > 4 * 1024 * 1024
abort "The public key must be a 65-byte P-256 X9.63 key." unless \
  public_key.bytesize == 65 && public_key.getbyte(0) == 4

spki_prefix = ["3059301306072a8648ce3d020106082a8648ce3d030107034200"].pack("H*")
File.binwrite(payload_path, payload)
File.binwrite(signature_path, signature)
File.binwrite(public_der_path, spki_prefix + public_key)
RUBY

if [[ -n "$expected_manifest_path" ]]; then
  cmp "$expected_manifest_path" "$payload_path" >/dev/null || {
    echo "The signed payload does not exactly match $expected_manifest_path" >&2
    exit 65
  }
fi

openssl pkey -pubin -inform DER -in "$public_der_path" -out "$public_pem_path" >/dev/null 2>&1 || {
  echo "The public key is not a valid P-256 key." >&2
  exit 65
}
openssl dgst -sha256 -verify "$public_pem_path" \
  -signature "$signature_path" "$payload_path" >/dev/null || {
  echo "The update manifest signature is invalid." >&2
  exit 65
}

echo "Verified signed update manifest: $signed_manifest_path"
