#!/usr/bin/env bash

set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(cd -- "$script_directory/.." && pwd)
scheme_directory="$repository_root/HerdMe.xcodeproj/xcshareddata/xcschemes"
unit_scheme="$scheme_directory/HerdMe.xcscheme"
ui_scheme="$scheme_directory/HerdMeUITests.xcscheme"

command -v xmllint >/dev/null || {
  echo "xmllint is required to validate Xcode schemes." >&2
  exit 69
}

for scheme in "$unit_scheme" "$ui_scheme"; do
  [[ -f "$scheme" ]] || {
    echo "Missing generated Xcode scheme: $scheme" >&2
    exit 66
  }
  xmllint --noout "$scheme"
done

testable_count() {
  local scheme=$1
  local target=$2
  xmllint \
    --xpath "count(//TestAction/Testables/TestableReference/BuildableReference[@BlueprintName='$target'])" \
    "$scheme"
}

[[ $(testable_count "$unit_scheme" HerdMeTests) == "1" ]]
[[ $(testable_count "$unit_scheme" HerdMeUITests) == "0" ]]
[[ $(testable_count "$ui_scheme" HerdMeTests) == "0" ]]
[[ $(testable_count "$ui_scheme" HerdMeUITests) == "1" ]]

echo "Validated isolated macOS unit and UI test schemes."
