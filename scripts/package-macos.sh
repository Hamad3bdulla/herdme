#!/bin/zsh

set -euo pipefail

configuration="${1:-Release}"
case "$configuration" in
    Debug|Release) ;;
    *)
        echo "Usage: $0 [Debug|Release]" >&2
        exit 2
        ;;
esac

script_directory="${0:A:h}"
repository_root="${script_directory:h}"
derived_data="$repository_root/DerivedData"
output_directory="$repository_root/dist"

command -v xcodegen >/dev/null || {
    echo "xcodegen is required to package HerdMe." >&2
    exit 1
}

cd "$repository_root"
xcodegen generate
xcodebuild \
    -project HerdMe.xcodeproj \
    -scheme HerdMe \
    -configuration "$configuration" \
    -derivedDataPath "$derived_data" \
    ARCHS="arm64 x86_64" \
    ONLY_ACTIVE_ARCH=NO \
    build

application="$derived_data/Build/Products/$configuration/HerdMe.app"
if [[ ! -d "$application" ]]; then
    echo "Built application was not found at $application" >&2
    exit 1
fi

executable="$application/Contents/MacOS/HerdMe"
lipo "$executable" -verify_arch arm64 x86_64
codesign --verify --deep --strict "$application"
for resource in LICENSE THIRD_PARTY.md; do
    if [[ ! -f "$application/Contents/Resources/$resource" ]]; then
        echo "Required resource is missing from the application: $resource" >&2
        exit 1
    fi
done
helper="$application/Contents/Helpers/herdme-network-helper"
if [[ ! -x "$helper" ]]; then
    echo "Required local network helper is missing from the application." >&2
    exit 1
fi

version=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" \
    "$application/Contents/Info.plist")
artifact_base="HerdMe-$version-macOS"
zip_path="$output_directory/$artifact_base.zip"
dmg_path="$output_directory/$artifact_base.dmg"
runnable_application="$output_directory/HerdMe.app"
legacy_runnable_application="$output_directory/HerdMe 2.app"

mkdir -p "$output_directory"
rm -f "$zip_path" "$dmg_path"
for stale_application in "$runnable_application" "$legacy_runnable_application"; do
    if [[ -d "$stale_application" ]]; then
        rm -r "$stale_application"
    fi
done
ditto "$application" "$runnable_application"
ditto -c -k --sequesterRsrc --keepParent "$application" "$zip_path"
hdiutil create \
    -volname "HerdMe" \
    -srcfolder "$application" \
    -format UDZO \
    -ov \
    "$dmg_path"

unzip -tq "$zip_path" >/dev/null
hdiutil verify "$dmg_path" >/dev/null

echo "Created local macOS packages:"
echo "$runnable_application"
lipo -archs "$executable"
shasum -a 256 "$zip_path" "$dmg_path"
