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

release_mode="${HERDME_RELEASE_MODE:-local}"
case "$release_mode" in
    local|public) ;;
    *)
        echo "HERDME_RELEASE_MODE must be local or public." >&2
        exit 2
        ;;
esac

local_signing_identity="${HERDME_LOCAL_CODESIGN_IDENTITY:-}"
if [[ "$release_mode" == "public" && -n "$local_signing_identity" ]]; then
    echo "HERDME_LOCAL_CODESIGN_IDENTITY may only be used for local packages." >&2
    exit 2
fi

if [[ "$release_mode" == "public" ]]; then
    : "${HERDME_DEVELOPER_ID_APPLICATION:?Set HERDME_DEVELOPER_ID_APPLICATION to a Developer ID Application identity.}"
    : "${HERDME_NOTARY_PROFILE:?Set HERDME_NOTARY_PROFILE to an xcrun notarytool keychain profile.}"
    [[ "$configuration" == "Release" ]] || {
        echo "Public packages must use the Release configuration." >&2
        exit 2
    }
fi

script_directory="${0:A:h}"
repository_root="${script_directory:h}"
derived_data="${HERDME_DERIVED_DATA:-$repository_root/DerivedData}"
output_directory="${HERDME_OUTPUT_DIRECTORY:-$repository_root/dist}"

command -v xcodegen >/dev/null || {
    echo "xcodegen is required to package HerdMe." >&2
    exit 1
}

cd "$repository_root"
"$script_directory/check-version.sh"
xcodegen generate
build_settings=(
    ARCHS="arm64 x86_64"
    ONLY_ACTIVE_ARCH=NO
    HERDME_RELEASE_MODE="$release_mode"
)
if [[ "$release_mode" == "public" ]]; then
    build_settings+=(
        CODE_SIGN_STYLE=Manual
        CODE_SIGN_IDENTITY="$HERDME_DEVELOPER_ID_APPLICATION"
        CODE_SIGN_INJECT_BASE_ENTITLEMENTS=NO
        ENABLE_HARDENED_RUNTIME=YES
    )
elif [[ -n "$local_signing_identity" ]]; then
    build_settings+=(
        CODE_SIGN_STYLE=Manual
        CODE_SIGN_IDENTITY="$local_signing_identity"
        CODE_SIGN_INJECT_BASE_ENTITLEMENTS=NO
        ENABLE_HARDENED_RUNTIME=YES
    )
fi
xcodebuild \
    -project HerdMe.xcodeproj \
    -scheme HerdMe \
    -configuration "$configuration" \
    -derivedDataPath "$derived_data" \
    "${build_settings[@]}" \
    build

application="$derived_data/Build/Products/$configuration/HerdMe.app"
if [[ ! -d "$application" ]]; then
    echo "Built application was not found at $application" >&2
    exit 1
fi

executable="$application/Contents/MacOS/HerdMe"
lipo "$executable" -verify_arch arm64 x86_64
codesign --verify --deep --strict "$application"
if [[ "$configuration" == "Release" ]]; then
    entitlements=$(codesign -d --entitlements - "$application" 2>/dev/null || true)
    if [[ "$entitlements" == *"com.apple.security.get-task-allow"* ]]; then
        echo "Release packages must not contain the get-task-allow entitlement." >&2
        exit 1
    fi
fi
if [[ "$release_mode" == "public" ]]; then
    signing_details=$(codesign -d --verbose=4 "$application" 2>&1)
    [[ "$signing_details" == *"Authority=Developer ID Application:"* ]] || {
        echo "The application is not signed with Developer ID Application." >&2
        exit 1
    }
    [[ "$signing_details" != *"TeamIdentifier=not set"* ]] || {
        echo "The public application signature does not contain a TeamIdentifier." >&2
        exit 1
    }
elif [[ -n "$local_signing_identity" ]]; then
    signing_details=$(codesign -d --verbose=4 "$application" 2>&1)
    [[ "$signing_details" != *"Signature=adhoc"* ]] || {
        echo "The requested local signing identity produced an ad-hoc signature." >&2
        exit 1
    }
    [[ "$signing_details" != *"TeamIdentifier=not set"* ]] || {
        echo "The requested local signing identity did not produce a stable TeamIdentifier." >&2
        exit 1
    }
fi
for resource in LICENSE THIRD_PARTY.md; do
    if [[ ! -f "$application/Contents/Resources/$resource" ]]; then
        echo "Required resource is missing from the application: $resource" >&2
        exit 1
    fi
done
if [[ "$release_mode" == "public" ]]; then
    public_key_path="$application/Contents/Resources/release-public-key.txt"
    feed_url_path="$application/Contents/Resources/release-feed-url.txt"
    [[ -f "$public_key_path" && -f "$feed_url_path" ]] || {
        echo "Public packages require release-public-key.txt and release-feed-url.txt." >&2
        exit 1
    }
    decoded_public_key="$derived_data/release-public-key.bin"
    openssl base64 -d -A -in "$public_key_path" -out "$decoded_public_key" 2>/dev/null || {
        echo "release-public-key.txt is not valid Base64." >&2
        exit 1
    }
    public_key_prefix=$(od -An -t x1 -N 1 "$decoded_public_key" | tr -d '[:space:]')
    [[ $(wc -c < "$decoded_public_key" | tr -d '[:space:]') == 65 \
        && "$public_key_prefix" == 04 ]] || {
        echo "release-public-key.txt must contain a 65-byte P-256 X9.63 public key." >&2
        exit 1
    }
    feed_url=$(tr -d '[:space:]' < "$feed_url_path")
    ruby -ruri -e '
      uri = URI.parse(ARGV.fetch(0))
      abort unless uri.is_a?(URI::HTTPS) && uri.host
    ' "$feed_url" || {
        echo "release-feed-url.txt must contain an absolute HTTPS URL." >&2
        exit 1
    }
fi
helper="$application/Contents/Helpers/herdme-network-helper"
if [[ ! -x "$helper" ]]; then
    echo "Required local network helper is missing from the application." >&2
    exit 1
fi
lipo "$helper" -verify_arch arm64 x86_64
codesign --verify --strict "$helper"
helper_signing_details=$(codesign -d --verbose=4 "$helper" 2>&1)
[[ "$helper_signing_details" == *"Identifier=app.herdme.network-helper"* ]] || {
    echo "The local network helper has an unexpected signing identifier." >&2
    exit 1
}
network_daemon="$application/Contents/Library/LaunchDaemons/app.herdme.network-service.plist"
if [[ ! -f "$network_daemon" ]]; then
    echo "Required SMAppService network daemon plist is missing from the application." >&2
    exit 1
fi
plutil -lint "$network_daemon" >/dev/null
daemon_label=$(/usr/libexec/PlistBuddy -c 'Print :Label' "$network_daemon")
daemon_program=$(/usr/libexec/PlistBuddy -c 'Print :BundleProgram' "$network_daemon")
daemon_mode=$(/usr/libexec/PlistBuddy -c 'Print :ProgramArguments:1' "$network_daemon")
daemon_run_at_load=$(/usr/libexec/PlistBuddy -c 'Print :RunAtLoad' "$network_daemon")
daemon_restart_on_failure=$(/usr/libexec/PlistBuddy -c 'Print :KeepAlive:SuccessfulExit' "$network_daemon")
if [[ "$daemon_label" != "app.herdme.network-service" \
    || "$daemon_program" != "Contents/Helpers/herdme-network-helper" \
    || "$daemon_mode" != "--managed" \
    || "$daemon_run_at_load" != "true" \
    || "$daemon_restart_on_failure" != "false" ]]; then
    echo "The bundled SMAppService network daemon contract is invalid." >&2
    exit 1
fi
if [[ "$release_mode" == "public" ]]; then
    application_signing_details=$(codesign -d --verbose=4 "$application" 2>&1)
    application_team=$(sed -n 's/^TeamIdentifier=//p' <<< "$application_signing_details")
    helper_team=$(sed -n 's/^TeamIdentifier=//p' <<< "$helper_signing_details")
    [[ -n "$application_team" && "$helper_team" == "$application_team" ]] || {
        echo "The application and local network helper must use the same TeamIdentifier." >&2
        exit 1
    }
    [[ "$helper_signing_details" == *"Authority=Developer ID Application:"* \
        && "$helper_signing_details" == *"runtime"* ]] || {
        echo "The public local network helper must use Developer ID and hardened runtime." >&2
        exit 1
    }
fi

version=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" \
    "$application/Contents/Info.plist")
build_number=$(/usr/libexec/PlistBuddy -c "Print :CFBundleVersion" \
    "$application/Contents/Info.plist")
expected_version=$(tr -d '[:space:]' < "$repository_root/VERSION")
expected_build_number=$(tr -d '[:space:]' < "$repository_root/BUILD_NUMBER")
if [[ "$version" != "$expected_version" || "$build_number" != "$expected_build_number" ]]; then
    echo "Built bundle version $version ($build_number) does not match VERSION $expected_version ($expected_build_number)." >&2
    exit 1
fi
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
ditto -c -k --sequesterRsrc --keepParent "$application" "$zip_path"
hdiutil create \
    -volname "HerdMe" \
    -srcfolder "$application" \
    -format UDZO \
    -ov \
    "$dmg_path"

unzip -tq "$zip_path" >/dev/null
hdiutil verify "$dmg_path" >/dev/null

if [[ "$release_mode" == "public" ]]; then
    xcrun notarytool submit "$zip_path" \
        --keychain-profile "$HERDME_NOTARY_PROFILE" \
        --wait
    xcrun stapler staple "$application"
    xcrun stapler validate "$application"

    rm -f "$zip_path" "$dmg_path"
    ditto -c -k --sequesterRsrc --keepParent "$application" "$zip_path"
    hdiutil create \
        -volname "HerdMe" \
        -srcfolder "$application" \
        -format UDZO \
        -ov \
        "$dmg_path"
    codesign --force \
        --sign "$HERDME_DEVELOPER_ID_APPLICATION" \
        --timestamp \
        "$dmg_path"
    xcrun notarytool submit "$dmg_path" \
        --keychain-profile "$HERDME_NOTARY_PROFILE" \
        --wait
    xcrun stapler staple "$dmg_path"
    xcrun stapler validate "$dmg_path"
    codesign --verify --deep --strict "$application"
    spctl --assess --type execute --verbose=4 "$application"
    spctl --assess --type open --context context:primary-signature --verbose=4 "$dmg_path"
fi

# Keep the directly runnable output identical to the final stapled application.
ditto "$application" "$runnable_application"

checksum_path="$output_directory/$artifact_base.sha256"
(
    cd "$output_directory"
    shasum -a 256 "${zip_path:t}" "${dmg_path:t}"
) > "$checksum_path"

echo "Created $release_mode macOS packages:"
echo "$runnable_application"
lipo -archs "$executable"
cat "$checksum_path"
