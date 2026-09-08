# Release Readiness, 2026-09-08

The current working tree contains a Windows release candidate based on 0.1.18.
It is not yet approved for public distribution. Version metadata has not been
advanced, and no tag or public release was created by this review. Improvements
are being verified on a review branch using the hosted platform workflows.

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

## Remaining Gates

- Visually inspect the modified WinUI build, including Arabic/RTL, narrow
  windows, and display scaling. The existing installed application was inspected;
  its active development session has been left running pending permission to
  restart it with the new build.
- Run installer acceptance on a clean Windows profile. The current profile has
  an installed and running HerdMe. Automatic approval review rejected installing
  Inno Setup 6.7.1 with `blocked by policy`; a new Setup artifact was not produced.
- Complete the official Windows build/acceptance workflow with MSVC. GitHub
  authentication is available and hosted verification is in progress. The local C++
  compiler is LLVM-MinGW, and Visual Studio is not installed on this machine.
- Execute the Linux release-asset negative tests. Git Bash normally copies files
  for the symlink fixture; with `MSYS=winsymlinks:nativestrict`, this machine
  explicitly refuses symlink creation with `Operation not permitted`. The
  symlink rejection case cannot provide valid evidence here.
- Build and test macOS on macOS/Xcode, including signed helper acceptance.
- Select the next release version, produce signed Windows artifacts and signed,
  notarized macOS artifacts, then verify the signed update feed and exact asset
  set from the final release commit before publication.

No public-release gate should be marked complete solely because the local
portable build and contract tests passed.
