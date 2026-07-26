# HerdMe release guide

This guide covers the application update feed shared by macOS and Windows.
Platform application signing remains a separate release gate: use Developer ID
and notarization for macOS, and Authenticode for Windows.

For repeated local macOS installation tests, sign with a stable Apple
Development identity. This keeps Keychain access stable between builds while
still leaving Developer ID and notarization as mandatory public-release gates:

```sh
HERDME_LOCAL_CODESIGN_IDENTITY="Apple Development: Developer Name (TEAMID)" \
./scripts/package-macos.sh Release
```

## Prepare the version

`VERSION` and `BUILD_NUMBER` are the authoritative release identifiers. Before
creating a release, update those files, the matching `MARKETING_VERSION` and
`CURRENT_PROJECT_VERSION` values in `project.yml`, and the first entry in
`HerdMe/Resources/release-manifest.json`. Move the relevant notes from
`CHANGELOG.md`'s Unreleased section into a dated version section.

Run the platform checker before building:

```sh
./scripts/check-version.sh
```

```powershell
.\scripts\check-version.ps1
```

CMake and MSBuild read the authoritative files directly. The checkers also
ensure XcodeGen metadata and the update manifest match. Both packaging scripts
run the appropriate checker again and reject a mismatched built bundle.

For a public macOS package, first store notarization credentials with
`xcrun notarytool store-credentials`, then run:

```sh
HERDME_RELEASE_MODE=public \
HERDME_DEVELOPER_ID_APPLICATION="Developer ID Application: Example (TEAMID)" \
HERDME_NOTARY_PROFILE="herdme-notary" \
./scripts/package-macos.sh Release
```

The public mode refuses Debug builds and `get-task-allow`, requires a Developer
ID Application signature and hardened runtime, notarizes and staples both the
application and DMG, runs Gatekeeper assessment, and writes a SHA-256 sidecar.
`HERDME_DERIVED_DATA` and `HERDME_OUTPUT_DIRECTORY` may point to isolated build
and artifact directories when validating the pipeline locally.

For public Windows portable and installer packages, install Inno Setup 6 and
import the code-signing certificate into
the current user's certificate store without placing its password in a command
line, then run from Windows:

```powershell
$env:HERDME_RELEASE_MODE = "public"
$env:HERDME_WINDOWS_SIGNING_THUMBPRINT = "0123456789ABCDEF0123456789ABCDEF01234567"
$env:HERDME_WINDOWS_TIMESTAMP_URL = "https://your-rfc3161-timestamp-service.example"
.\Windows\package-installer.ps1 -Architecture x64 -Configuration Release
```

The installer command builds the portable ZIP first. Public mode signs and
verifies `HerdMe.Windows.exe`, the portable core, and the final Setup executable.
It reads only a certificate thumbprint, so the private key and password remain
in the Windows certificate store or CI secret-import step.
The public GitHub workflow imports the certificate before running
`Windows/acceptance.ps1` with `HERDME_RELEASE_MODE=public`. That gate installs
the signed Setup executable into an isolated directory, verifies the expected
Authenticode certificate on the installer and both installed executables,
runs the installed core health check, uninstalls the package, and then performs
the native application single-instance and loopback-listener checks. Do not
replace this with acceptance of an unsigned build followed by an untested
signed rebuild.
Both public packaging modes also refuse to continue unless a valid 65-byte
P-256 `release-public-key.txt` and HTTPS `release-feed-url.txt` are bundled.
The GitHub workflow additionally installs the pinned Microsoft SBOM Tool 4.1.5,
generates and validates an SPDX 2.2 inventory for each platform, and publishes
each SBOM with its own SHA-256 sidecar.
The regular Windows x64 workflow uploads `windows-acceptance.log` and HerdMe's
top-level diagnostic logs as a dedicated failure artifact. Review it before
retrying a failed native gate; never publish or relabel that diagnostic artifact
as an application package.

## GitHub release workflow

`.github/workflows/release.yml` is the authoritative public-release pipeline.
It runs only for tags matching `v*.*.*`, rejects a tag that does not equal
`v$(cat VERSION)`, signs and verifies the update feed, runs the macOS and
Windows acceptance gates, notarizes and signs both platform packages, verifies
their SHA-256 sidecars, and creates a new GitHub Release. It refuses to replace
an existing release.

Configure all of these repository Actions secrets before pushing a release tag:

- `HERDME_UPDATE_SIGNING_PRIVATE_KEY`: the complete PEM-encoded P-256 private
  key matching `HerdMe/Resources/release-public-key.txt`.
- `HERDME_MACOS_CERTIFICATE_P12_BASE64`: the Developer ID Application identity
  and private key exported as a base64-encoded PKCS#12 file.
- `HERDME_MACOS_CERTIFICATE_PASSWORD`: the PKCS#12 export password.
- `HERDME_DEVELOPER_ID_APPLICATION`: the complete `Developer ID Application:`
  identity name reported by `security find-identity`.
- `HERDME_APPLE_ID`, `HERDME_APPLE_TEAM_ID`, and
  `HERDME_APPLE_APP_PASSWORD`: Apple notarization credentials.
- `HERDME_WINDOWS_CERTIFICATE_PFX_BASE64`: the Authenticode certificate and
  private key exported as a base64-encoded PFX file.
- `HERDME_WINDOWS_CERTIFICATE_PASSWORD`: the PFX export password.
- `HERDME_WINDOWS_TIMESTAMP_URL`: an HTTPS RFC 3161 timestamp endpoint accepted
  by `signtool`.

The workflow imports signing identities only into temporary runner stores. The
update private key and platform signing archives must never be committed or
placed in release artifacts.
The publish job also creates GitHub build-provenance attestations for every
release asset after all signatures, checksums, SBOMs, and feed validation pass.

After the secrets are configured, update the version metadata and changelog,
push the release commit, then create and push the exact version tag:

```sh
git tag -s "v$(cat VERSION)" -m "HerdMe $(cat VERSION)"
git push origin "v$(cat VERSION)"
```

Do not treat a successful local package as a public release. The GitHub run must
finish all four jobs and the published release must contain the signed feed,
both platform packages, and all checksum sidecars.

## One-time update signing setup

Generate one ECDSA P-256 key outside the repository. Keep the private key in a
secret manager or an encrypted CI secret and never commit it.

```sh
openssl ecparam -name prime256v1 -genkey -noout -out /secure/release-private-key.pem
```

Create the public key that is safe to commit and bundle. The signing script
also creates a signed feed so its output can be verified immediately.

```sh
./scripts/sign-update-manifest.sh \
  HerdMe/Resources/release-manifest.json \
  /secure/release-private-key.pem \
  /tmp/release-manifest.signed.json \
  HerdMe/Resources/release-public-key.txt
```

On Windows PowerShell, use the equivalent script:

```powershell
./scripts/sign-update-manifest.ps1 `
  -ManifestPath HerdMe/Resources/release-manifest.json `
  -PrivateKeyPath C:\secure\release-private-key.pem `
  -SignedManifestPath $env:TEMP\release-manifest.signed.json `
  -PublicKeyPath HerdMe/Resources/release-public-key.txt
```

Commit `release-public-key.txt`. Both applications reject a remotely hosted
feed when this public key is absent, when the feed is unsigned, or when its
payload or signature has been modified.

## Publish an update

1. Build, test, sign, and package both applications from the release commit.
2. Upload the signed application artifacts and fill every release entry's
   `downloadURLs.macOS` and `downloadURLs.windowsX64` with their final HTTPS
   URLs. A signed feed is rejected unless both platform artifacts are present.
3. Sign the plain `HerdMe/Resources/release-manifest.json` with one of the
   scripts above. The scripts refuse missing, insecure, or identical platform
   URLs and reject manifests over the application's 4 MB limit. Do not edit
   the signed envelope afterward.
4. Upload `release-manifest.signed.json` to a stable HTTPS URL.
5. Put that URL, and only that URL, in
   `HerdMe/Resources/release-feed-url.txt`, then build the applications that
   should consume the feed.
6. Verify stable and beta checks from clean macOS and Windows machines before
   publishing the release.

`HERDME_UPDATE_FEED_URL` and `HERDME_UPDATE_PUBLIC_KEY` are accepted only in
Debug builds. Release builds use the bundled HTTPS URL and public key, which
prevents environment variables from redirecting update checks.

## Required release gates

- Swift, C++, and C# builds pass with project warnings treated as errors.
- macOS tests and portable C++ tests are green in CI.
- Windows x64 acceptance is green on a native Windows runner.
- macOS artifacts pass `codesign`, Gatekeeper assessment, and notarization.
- The embedded macOS `SMAppService` daemon manifest passes its fixed-contract
  check, and its universal network helper has a valid hardened-runtime signature
  with the same TeamIdentifier as the application.
- Windows executables and installer pass Authenticode verification.
- The hosted feed signature verifies and every release contains HTTPS URLs for
  both the macOS and Windows x64 artifacts.
- Every manifest version passes strict semantic-version validation. Stable
  releases outrank prereleases, numeric prerelease identifiers sort
  numerically, and build metadata does not affect update precedence.
- Artifacts were generated from the exact tagged commit and their SHA-256
  values are published with the release.
- Both platform SPDX 2.2 SBOMs pass `sbom-tool validate`, contain package and
  file inventories, and their SHA-256 sidecars verify before publication.
- GitHub build-provenance attestations exist for every published release asset.
