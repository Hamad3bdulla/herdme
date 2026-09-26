# Release Readiness, 2026-09-08

The review branch prepares HerdMe 0.1.19 (build 1). The final delivery scope
requested by the owner is Windows; earlier macOS findings remain historical.
It is not yet ready for signed public distribution. The owner confirmed that
platform code-signing certificates are not currently available. No release tag
or public release has been created.

Branch: `codex/release-readiness-2026-09-08`.

## Windows Service Download Follow-up

The current delivery scope is Windows only. The service page now retains
download progress across navigation, reports transfer size/speed and retry
attempts, supports explicit cancellation and retry, and adapts its form to
compact windows. Progress updates preserve keyboard focus on download controls.
Cancellation leaves configured service instances available for a later retry.
Automatic site-scan errors appear inline and cannot cover another page after
navigation. UI acceptance rejects unexpected dialogs and waits for visible
controls before testing cancellation and retry.

Release WinUI compilation, Release contract execution, C# formatting (86 files),
and PowerShell parsing (9 Windows scripts) passed locally for these changes.
New contracts exercise shared requests, page-wait cancellation, installer
cancellation, retry after failure, byte totals, incomplete download cleanup,
and restoration of the previous runtime after promotion failure.
Hosted bilingual acceptance for application commit `e312be7` passed on
2026-09-08 in [Windows run 34252296990](https://github.com/Hamad3bdulla/herdme/actions/runs/34252296990).
Both languages passed Debug/Release builds, contracts, package installation and
removal, both service cancellation controls, retry, native navigation, compact
layout bounds, SMTP capture, and dump capture. English and Arabic screenshots
were inspected and show unobstructed service controls at 800x600, including RTL.
The Arabic screenshot upload initially failed with a GitHub artifact service
403 after acceptance passed. Attempt 2 passed and uploaded the Arabic images;
the complete Windows run is successful. Screenshots are retained locally under
`build/readiness-final-windows-ui/`.

The Windows candidate is ready for unsigned preview distribution. Signed public
release remains dependent on the unavailable Authenticode certificate. Upstream
exception details can still appear in English in Arabic error messages; the
service controls and installation-stage labels are localized.

The final Windows application code also passed C# and C++ CodeQL in
[run 34252297021](https://github.com/Hamad3bdulla/herdme/actions/runs/34252297021),
and portable fuzzing in
[run 34252297188](https://github.com/Hamad3bdulla/herdme/actions/runs/34252297188).

## Current Windows Candidate

The 0.1.19 build 1 packages from the final application commit are available in
`dist/HerdMe-0.1.19-windows-candidate/`. Downloaded files match their SHA-256
sidecars:

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| `HerdMe-0.1.19-win-x64-portable.zip` | 72703835 | `4fb0906963d1d7fa691833be6fb67ecd162653a01128b2b99fa6949eb2bbdc90` |
| `HerdMe-0.1.19-win-x64-setup.exe` | 47157070 | `9c51b41143a03b6492acc261668345b98d00aadc678470881572be4065471d89` |

Authenticode reports `NotSigned` for Setup. These are unsigned review/testing
packages; they do not satisfy the repository's signed public-release policy.
The portable archive includes the native core, application, translation resource
index, license, and third-party notices. No installer was run against the user's
existing local installation.

## Implemented

- Upgrade SQLitePCLRaw from 2.1.6 to 2.1.13 and enable transitive NuGet audits.
- Stage database exports beside their destination and replace the destination
  only after a successful client exit and durable output flush. Preserve older
  backups on failure, cancellation, and promotion errors; reap failed clients.
- Preserve SQL literals and comments during collation repair. Keep tokens intact
  across input buffers and recognize successive merge statements on one line.
- Preserve invalid settings documents, including non-object JSON and null
  required fields, instead of failing during application startup.
- Make site preview height responsive, distinguish database and terminal icons,
  tighten the site list, and wrap long runtime labels.
- Refuse native acceptance on an active development session or an existing
  installation/startup registration. Avoid applying reinstall state during
  onboarding acceptance.
- Correct the socket load probe to use asynchronous socket operations without
  PowerShell delegates on worker threads.
- Require an empty, regular directory for ZIP extraction and check destination
  directories for links before writing files. Exercise actual Windows junctions.
- Stage macOS database exports with owner-only permissions, drain diagnostics
  during execution, propagate cancellation, and use unique backup filenames.
- Prepare Roslyn's generated-output directory before WinUI's `XamlPreCompile`
  pass. CodeQL keeps generated-source analysis enabled.
- Apply the official Swift and C++ formatters, complete missing Arabic runtime
  messages, and capture native Windows acceptance screenshots.

## Earlier Hosted Verification

The following completed runs verify the 0.1.19 source at `19cb67d`:

| Check | Result |
| --- | --- |
| [Windows x64](https://github.com/Hamad3bdulla/herdme/actions/runs/34237253798) | Passed: Debug and Release, MSVC, contracts, portable ZIP, Setup install/uninstall, native navigation, SMTP and dump capture |
| [macOS deep diagnostics](https://github.com/Hamad3bdulla/herdme/actions/runs/34237253715) | Passed: static analysis, native helper checks, ASan/UBSan, TSan, C++ sanitizers; each Swift sanitizer run executed 250 tests with 6 integration tests skipped |
| [Portable core and Swift parser fuzzing](https://github.com/Hamad3bdulla/herdme/actions/runs/34237253813) | Passed with ASan/UBSan and supported leak detection |
| [CodeQL](https://github.com/Hamad3bdulla/herdme/actions/runs/34237253717) | C# and C++ passed; Swift verification pending |

The macOS package job passed source formatting and update-feed, asset-set, tag,
and workflow-security contracts. Its Arabic localization gate exposed six
missing messages, which have been added for the next run. Final macOS unit/UI
tests, packaging, and English/Arabic compact Windows captures are still pending.

## Earlier Local Verification

| Check | Result |
| --- | --- |
| Portable C++ Release build | Passed, Clang 23.1.0, warnings as errors |
| CTest | 2/2 passed, including executable version |
| Windows C# contract suite | Passed in Debug and Release |
| Direct Release contract DLL execution | Passed |
| Native WinUI Release build and self-contained publish | Passed |
| C# formatting | Passed, 86 source files inspected |
| PowerShell syntax | Passed, 11 repository scripts |
| Version metadata | Passed, 0.1.18 build 1 |
| Windows signing configuration contracts | Passed; this does not sign artifacts |
| NuGet vulnerability inventory | No known vulnerable packages reported after upgrade |
| Live service and runtime sources | Passed, including verified Xdebug extraction |
| Live isolated MinGit installation | Passed, 2.55.0.windows.5; download, digest, extraction, execution, and repository creation |
| Socket probe | 32/32 local connections; closed-port failures counted correctly |

Regression coverage includes existing-backup preservation on client failure,
cancellation, and file promotion failure; cleanup of temporary output; prompt
termination after output-open failure; invalid settings recovery; SQL literals
and comments; and every two-chunk split of the SQL streaming fixture.

The native build used the existing workspace .NET 8.0.100 SDK and MSBuild 17.8.3
helper. CMake 4.4.3, Ninja 1.13.2, and LLVM-MinGW 20260826 were downloaded from
their official GitHub releases and checked against their published SHA-256
digests. This local build does not substitute for the supported Visual Studio
MSVC acceptance run in CI.

## Earlier Candidate Files

- `build/readiness-windows/HerdMe.Windows.exe`
- `dist/HerdMe-0.1.18-readiness-win-x64-portable.zip`
- `dist/HerdMe-0.1.18-readiness-win-x64-portable.zip.sha256`

These are unsigned local testing artifacts. The candidate filename deliberately
differs from the public-release filename for version 0.1.18.

The verified hosted Windows 0.1.19 artifacts are downloaded under
`build/readiness-ci-windows-0.1.19/`. Their SHA-256 sidecars match:

- Portable ZIP: `ccbeca506ac7d578fdf7773959401dd8870cca3d1701ec4b8194903f360ad5d7`
- Setup: `976d7a66966ac7e8d53072d6797b73bfacbfbf13327c34c2f5b7016022e17f7e`

These packages are unsigned and are intended for testing, not public release.

## Remaining Public Release Gates

- Obtain the Windows Authenticode credentials documented in
  `docs/RELEASING.md`. The owner confirmed these are unavailable; only the
  update-feed signing secret is currently configured in GitHub Actions.
- Produce and validate a GitHub-verified signed annotated release tag, signed
  Windows artifacts, the signed update feed, and the exact final release asset
  set before signed public publication. macOS signing/notarization is outside
  the current Windows delivery scope.

Local Inno Setup installation was rejected by automatic approval review. The
existing hosted Windows workflow supplied the clean-profile installer evidence;
the local installed HerdMe session was preserved.

No public-release gate should be marked complete solely because the local
portable build and contract tests passed.
