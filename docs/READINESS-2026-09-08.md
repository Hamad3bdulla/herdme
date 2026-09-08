# Release Readiness, 2026-09-08

The review branch prepares HerdMe 0.1.19 (build 1) for Windows and macOS.
It is not yet ready for signed public distribution. The owner confirmed that
platform code-signing certificates are not currently available. No release tag
or public release has been created.

Branch: `codex/release-readiness-2026-09-08`.

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

## Hosted Verification

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

## Verified Locally

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

## Candidate Files

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

## Remaining Gates

- Visually inspect the modified WinUI build, including Arabic/RTL, narrow
  windows, and display scaling. The existing installed application was inspected;
  its active development session has been left running pending permission to
  restart it with the new build.
- Finish the final hosted macOS package and UI gates and Swift CodeQL analysis.
- Exercise the opt-in managed PHP-FPM, Laravel, Xdebug, and database integration
  cases in their configured runtime environments; the six skips are explicit.
- Obtain the Windows Authenticode and Apple Developer ID/notarization
  credentials documented in `docs/RELEASING.md`. Only the update-feed signing
  secret is currently configured in GitHub Actions.
- Produce and validate a GitHub-verified signed annotated release tag, signed
  Windows artifacts, notarized macOS artifacts, the signed update feed, and the
  exact final release asset set before publication.

Local Inno Setup installation was rejected by automatic approval review. The
existing hosted Windows workflow supplied the clean-profile installer evidence;
the local installed HerdMe session was preserved.

No public-release gate should be marked complete solely because the local
portable build and contract tests passed.
