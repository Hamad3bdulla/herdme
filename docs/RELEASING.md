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

Before accepting a Windows build, run `Windows/check-format.ps1` on native
Windows. The wrapper fails when `dotnet format` cannot load required MSBuild
references or reports that it inspected zero files; a zero exit code from the
underlying tool alone is not sufficient evidence.

The current local macOS baseline is 234 XCTest cases: 232 pass and the two live
opt-in integration cases skip in default runs. Debug, optimized Release,
ASan/UBSan, and TSan agree on that result through the supported `xcodebuild
test` path, with no sanitizer finding. A public release still requires the same
result from hosted CI plus the isolated XCUITest run; local execution does not
replace those gates.

Before selecting a macOS release candidate, run the real Laravel serving gate
against a complete Laravel 13 project (or request temporary project creation):

```sh
HERDME_LARAVEL_PROJECT="/absolute/path/to/project" \
  ./scripts/test-live-laravel.sh
```

The wrapper reads the structured Xcode result and requires the exact Laravel
HTTP/static/HTTPS case to pass. A successful `xcodebuild` invocation that ran
zero tests is rejected.

The current local fuzz baseline is 14,260,961 inputs across the three portable
Core parsers and their clean rerun, plus 6,249,070 inputs across the three Swift
parsers. A release must not hide a sanitizer report by disabling its detector.
The clean Core rerun keeps LeakSanitizer enabled and disables only libFuzzer
22's leaking RSS-monitor thread. The local Apple Swift runtime does not support
LeakSanitizer for the mixed Swift fuzz binaries, so the hosted Linux Swift gate
remains mandatory evidence rather than being replaced by that local run.

CMake and MSBuild read the authoritative files directly. The checkers also
ensure XcodeGen metadata and the update manifest match. Both packaging scripts
run the appropriate checker again and reject a mismatched built bundle.

Exercise the complete update-feed signing contract before a release:

```sh
./scripts/test-update-manifest.sh
```

This test creates an ephemeral P-256 key, signs the current manifest with both
the shell and PowerShell release signers, cross-verifies both envelopes and
derived public keys, and requires modified payloads, signatures, algorithms,
keys, expected content, insecure download URLs, and non-P-256 keys to be
rejected. Both signers also reject duplicate release identities and platform
URLs whose filenames do not match that entry's versioned macOS ZIP and Windows
Setup artifacts. Set `HERDME_PWSH` to the PowerShell executable when it is not
on `PATH`. The regular macOS workflow runs the same contract on every push and
pull request; the release workflow still verifies the real signed feed again
with the public key bundled in both applications.

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
The DMG contains the signed `HerdMe.app` and an `Applications` shortcut; the
packaging gate mounts the final image read-only and verifies both entries before
publishing it.
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
Authenticode certificate and RFC 3161 timestamp on the installer and both
installed executables, runs the installed core health check, verifies that
Setup did not enable launch-at-login without consent, verifies uninstall removes
an application-owned background-start entry, and then performs
the native application single-instance and loopback-listener checks. It also
resolves every supported service, PHP, Node.js, Composer, Laravel Installer, and
Xdebug release and probes the exact HTTPS downloads before packaging can pass.
Public mode rejects `-SkipLiveReleaseChecks`; that switch is reserved for the
fixture-based pull-request gate because the nightly workflow performs the same
live checks independently. Do not
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
`v$(cat VERSION)`, requires GitHub to verify an annotated tag signature, proves
that the signed tag points directly to the exact commit being built, signs and
verifies the update feed, runs the macOS and
Windows acceptance gates, notarizes and signs both platform packages, verifies
their SHA-256 sidecars, and creates a new GitHub Release. Before checksums or
publishing, it requires the merged artifact directory to match the exact public
regular-file allowlist; missing, extra, directory, and symbolic-link entries
all fail the release. It refuses to replace an existing release.

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

Configure a GitHub repository ruleset for `refs/tags/v*` that restricts tag
creation, update, and deletion to the release maintainers. The workflow proves
that GitHub verified the annotated tag signature and that its commit matches the
build, while the repository ruleset controls who is authorized to create that
signed release identity. Do not permit force-updating or deleting published
version tags.

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

A lightweight tag, an unsigned or unverified annotated tag, a tag that targets
another tag instead of a commit, or a signed tag whose target differs from the
workflow commit is rejected before any update-signing or platform-signing secret
is exposed to a build step.

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
- Every Windows Authenticode signature contains an RFC 3161 timestamp from the
  configured HTTPS endpoint and uses the one current code-signing certificate
  selected from the imported PFX.
- The version tag is protected by the repository ruleset, is annotated, has a
  GitHub-verified signature, and points directly to the workflow commit.
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
