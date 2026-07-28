#!/usr/bin/env bash

set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(cd -- "$script_directory/.." && pwd)

command -v xcodebuild >/dev/null || {
  echo "Xcode is required to run the live Laravel gate." >&2
  exit 69
}
xcrun --find xcresulttool >/dev/null
xcrun --find swift >/dev/null

if [[ -n ${HERDME_LARAVEL_PROJECT:-} ]]; then
  [[ -d $HERDME_LARAVEL_PROJECT ]] || {
    echo "HERDME_LARAVEL_PROJECT is not a directory: $HERDME_LARAVEL_PROJECT" >&2
    exit 66
  }
  laravel_project=$(cd -- "$HERDME_LARAVEL_PROJECT" && pwd -P)
  for required_path in artisan vendor/autoload.php public/index.php; do
    [[ -f "$laravel_project/$required_path" ]] || {
      echo "The Laravel fixture is missing $required_path: $laravel_project" >&2
      exit 66
    }
  done
  export HERDME_LARAVEL_PROJECT=$laravel_project
elif [[ ${HERDME_CREATE_LARAVEL_INTEGRATION:-} != "1" ]]; then
  echo "Set HERDME_LARAVEL_PROJECT to a complete Laravel 13 project, or set HERDME_CREATE_LARAVEL_INTEGRATION=1." >&2
  exit 64
fi

machine_architecture=$(uname -m)
case "$machine_architecture" in
  arm64 | x86_64) ;;
  *)
    echo "Unsupported macOS architecture: $machine_architecture" >&2
    exit 69
    ;;
esac

run_identifier="$(date -u +%Y%m%d-%H%M%S)-$$"
build_root="$repository_root/build"
derived_data="${HERDME_DERIVED_DATA:-$build_root/macos-live-laravel-$run_identifier}"
result_bundle="${HERDME_RESULT_BUNDLE:-$build_root/macos-live-laravel-$run_identifier.xcresult}"
work_directory=$(mktemp -d "${TMPDIR:-/tmp}/herdme-live-laravel.XXXXXX")

cleanup() {
  rm -rf -- "$work_directory"
}
trap cleanup EXIT

[[ ! -e $result_bundle ]] || {
  echo "The result bundle already exists: $result_bundle" >&2
  exit 73
}
mkdir -p -- "$build_root"

# Xcode 26 can report success with zero tests when a single extension method is
# selected. Run the containing class, then prove the exact live case passed.
xcodebuild \
  -project "$repository_root/HerdMe.xcodeproj" \
  -scheme HerdMe \
  -configuration Debug \
  -destination "platform=macOS,arch=$machine_architecture" \
  -derivedDataPath "$derived_data" \
  -resultBundlePath "$result_bundle" \
  -test-timeouts-enabled YES \
  -default-test-execution-time-allowance 60 \
  -maximum-test-execution-time-allowance 180 \
  -only-testing:HerdMeTests/ConfigurationAndSiteScannerTests \
  test

test_tree="$work_directory/tests.json"
xcrun xcresulttool get test-results tests --path "$result_bundle" > "$test_tree"

xcrun swift - "$test_tree" \
  "ConfigurationAndSiteScannerTests/testExistingLaravelProjectThroughFPMWhenRequested()" <<'SWIFT'
import Darwin
import Foundation

guard CommandLine.arguments.count == 3 else {
    FileHandle.standardError.write(Data("Invalid result verifier arguments.\n".utf8))
    exit(64)
}

let resultURL = URL(fileURLWithPath: CommandLine.arguments[1])
let expectedIdentifier = CommandLine.arguments[2]
let object = try JSONSerialization.jsonObject(with: Data(contentsOf: resultURL))

func containsPassingTest(_ value: Any) -> Bool {
    if let dictionary = value as? [String: Any] {
        if dictionary["nodeIdentifier"] as? String == expectedIdentifier,
            dictionary["result"] as? String == "Passed"
        {
            return true
        }
        return dictionary.values.contains(where: containsPassingTest)
    }
    if let array = value as? [Any] {
        return array.contains(where: containsPassingTest)
    }
    return false
}

guard containsPassingTest(object) else {
    FileHandle.standardError.write(
        Data("The live Laravel test did not produce an explicit Passed result.\n".utf8)
    )
    exit(1)
}
SWIFT

echo "Live Laravel HTTP, static-file, and HTTPS gate passed."
echo "Result bundle: $result_bundle"
