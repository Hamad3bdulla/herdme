#!/usr/bin/env bash

set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(cd -- "$script_directory/.." && pwd)
catalog="$repository_root/HerdMe/Resources/Localizable.xcstrings"
work_directory=$(mktemp -d "${TMPDIR:-/tmp}/herdme-localization.XXXXXX")

cleanup() {
  rm -rf -- "$work_directory"
}
trap cleanup EXIT

command -v ruby >/dev/null || {
  echo "Ruby is required to validate localization coverage." >&2
  exit 69
}
xcrun --find xcstringstool >/dev/null

swift_sources=()
while IFS= read -r source; do
  swift_sources+=("$source")
done < <(find "$repository_root/HerdMe" -type f -name '*.swift' -print | sort)

if [[ ${#swift_sources[@]} -eq 0 ]]; then
  echo "No Swift sources were found for localization extraction." >&2
  exit 66
fi

extracted_directory="$work_directory/extracted"
compiled_directory="$work_directory/compiled"
mkdir -p "$extracted_directory" "$compiled_directory"

xcrun xcstringstool extract \
  --SwiftUI \
  --modern-localizable-strings \
  --output-format xcstrings \
  --output-directory "$extracted_directory" \
  "${swift_sources[@]}"

xcrun xcstringstool compile \
  "$catalog" \
  --output-directory "$compiled_directory" \
  --language en \
  --language ar \
  --serialization-format text

ruby -rjson -rset - "$catalog" "$extracted_directory/Localizable.xcstrings" <<'RUBY'
catalog_path, extracted_path = ARGV
catalog = JSON.parse(File.binread(catalog_path))
extracted = JSON.parse(File.binread(extracted_path))

abort "The localization source language must be English." unless catalog["sourceLanguage"] == "en"
abort "The localization catalog has no strings." unless catalog["strings"].is_a?(Hash) && !catalog["strings"].empty?

invalid_arabic = catalog.fetch("strings").filter_map do |key, entry|
  unit = entry.dig("localizations", "ar", "stringUnit")
  key unless unit.is_a?(Hash) && unit["state"] == "translated" && !unit["value"].to_s.strip.empty?
end
unless invalid_arabic.empty?
  abort "Missing or incomplete Arabic translations:\n- #{invalid_arabic.sort.join("\n- ")}"
end

normalize = lambda do |key|
  key.gsub(/%(?:[0-9]+\$)?(?:arg|lld|ld|d|@|lf|f)/, "%FORMAT%")
end
translated_keys = catalog.fetch("strings").keys.map(&normalize).reject(&:empty?).to_set
extracted_keys = extracted.fetch("strings").keys.map(&normalize).reject(&:empty?).to_set
missing = (extracted_keys - translated_keys).sort
unless missing.empty?
  abort "SwiftUI strings without Arabic translations:\n- #{missing.join("\n- ")}"
end

puts "Validated #{extracted_keys.length} extracted SwiftUI keys and #{translated_keys.length} Arabic catalog keys."
RUBY
