# HerdMe Windows x64 AI Handoff

This file is the authoritative handoff for continuing HerdMe on a real Windows
x64 machine. Open the repository in the Windows AI coding agent and tell it:

> Read `WINDOWS_AI_HANDOFF.md` completely, then execute it end to end. Do not
> stop after planning. After every meaningful step, send a short progress
> comment in Arabic. Build, fix, retest, package, and leave the application
> running for me to try.

## Objective

Finish and validate the native HerdMe Windows x64 application on real Windows
hardware, produce a portable ZIP, and leave exactly one application instance
running for interactive testing.

The selected parity target is 99% of the Herd-like local PHP/Laravel workflow.
Expose, Shortcuts, Integrations, and Forge are intentionally excluded. HerdMe
is MIT-licensed and must not contain activation, subscriptions, license keys,
paid gates, or upgrade screens.

## Non-negotiable constraints

- Use native WinUI 3 and the existing C++20 core. Do not replace the app with
  Electron, a web shell, Docker, WSL, or another compatibility layer.
- Work only with HerdMe source and HerdMe-owned data under
  `%LOCALAPPDATA%\HerdMe`. Do not read or reuse another application's private
  runtimes, configuration, binaries, certificates, assets, or signing identity.
- Never leave two HerdMe processes running. A second launch must activate the
  existing window and exit before opening duplicate listeners.
- Privileged operations may show the normal Windows UAC prompt, but must not
  show Command Prompt, PowerShell, or another console window to the user.
- Local sites must open as `https://name.test/` without internal ports such as
  `:8080` or `:8443` in the visible URL.
- PHP must be official Windows NTS x64 and use `php-cgi.exe -b`. Validate these
  Laravel-required extensions before enabling a runtime: `ctype`, `curl`,
  `dom`, `fileinfo`, `filter`, `hash`, `mbstring`, `openssl`, `pcre`, `pdo`,
  `session`, `tokenizer`, and `xml`.
- New PHP installs and release checks accept only cycles 8.0 through 8.5. Keep
  an older cycle such as 7.4 visible and selectable only when it is already
  installed; never offer Install, Update, or Repair for that legacy cycle.
- Adding a supported managed service must download, verify, install, and create
  it automatically. Do not show an Update action when the installed version is
  already the newest release.
- MySQL must bind its SQL listener to loopback and use `--mysqlx=0` so port
  `33060` is not opened. Other auxiliary service listeners must also remain
  loopback-only.
- Valkey and Typesense must remain visible but disabled on Windows until a
  reproducible native x64 build with a verified checksum satisfies
  `docs/WINDOWS_NATIVE_SERVICE_AUDIT.md`. Do not use an unverified third-party
  binary to make the controls appear functional.
- Preserve any user changes already present in the worktree. Do not use
  destructive Git reset or cleanup commands.

## Current verified state

As of 2026-07-26, the current macOS source passes 151 XCTest cases: 149 pass,
zero fail, and two optional integrations skip. The live Laravel and database
gates have also passed separately. A fresh universal Release package, ZIP, and
DMG pass signature, architecture, archive, and SHA-256 checks. The three
installed sites return HTTP 200 without a visible port, with exactly one
HerdMe process running. The latest Apple Development-signed Universal Release
containing semantic update ordering was installed and reverified at 21:43
Asia/Bahrain. Its ZIP and DMG signatures, archives, and SHA-256 sidecar pass.
The current macOS source retries HTTPS non-interactively
whenever the CA is trusted, including profiles with a stale approval marker. Do
not record HTTPS as active until the newly installed build starts a listener
and a live TLS request succeeds; credentials that still require Keychain
interaction must continue to fall back to HTTP.

The Windows non-UI C# contracts build with zero warnings and pass on macOS
against the real C++ executable. They also prove semantic application-update
ordering for stable, prerelease, numeric prerelease, and build-metadata cases.
All 13 WinUI XAML files parse. The latest
PowerShell acceptance changes must still be parsed and executed on Windows.
The native WinUI build cannot complete on macOS because the Windows App SDK
invokes `XamlCompiler.exe`, which requires Windows. Do not treat the contract
test executable as the application.

The hosted Windows x64 run for commit
`66621e86b34674132577ea100b222dd9e6b68d3e` failed before producing an accepted
artifact. The current worktree installs Inno Setup in CI, tests the Setup
installer, and makes the public release gate install, verify, uninstall, and
run the same Authenticode-signed artifacts that it publishes. Make sure these
worktree changes are committed and pushed before using the hosted result as
evidence.

There is still no native Windows application ZIP proven by this macOS session.
The Windows machine must produce and test it.

## Phase 1: inspect and prepare

Start in the repository root. Do not assume the checkout is clean or current.

```powershell
git status --short
git branch --show-current
git remote -v
git pull --ff-only origin master

[Environment]::Is64BitOperatingSystem
[Environment]::Is64BitProcess
dotnet --info
cmake --version
where.exe cl
where.exe msbuild
```

Required environment:

- Windows 10 version 2004 or newer, or Windows 11
- x64 OS and x64 PowerShell process
- Visual Studio 2022 with **Desktop development with C++**
- MSVC v143, Windows 10/11 SDK, and CMake tools for Windows
- .NET 8 SDK x64
- Windows PowerShell 5.1 or newer

If `cl` is missing from a normal PowerShell window, reopen the repository in
**Developer PowerShell for VS 2022**. If a prerequisite is genuinely missing,
install only that prerequisite from Microsoft, report the action in Arabic,
then repeat the checks. Do not install Docker or WSL.

## Phase 2: run the automated hardware gate

From the repository root:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
./Windows/acceptance.ps1 -Configuration Release -LeaveRunning
```

Do not stop at the first failure. Capture the complete error, identify the
owning source file, implement the smallest correct fix consistent with existing
patterns, add or strengthen a focused contract when possible, and rerun the
failed focused command. Then rerun the full acceptance command above.

The full gate must:

- Build the C++ core and run CTest.
- Build and run the Windows C# contracts with warnings treated as errors.
- Parse every PowerShell and XAML file.
- Build and publish the native WinUI application for `win-x64`.
- Require app-local Windows App Runtime and WinUI DLLs.
- Verify the portable ZIP and its SHA-256 sidecar.
- Start the actual `HerdMe.Windows.exe` and show a native window.
- Force a second launch and prove exactly one primary process remains.
- Verify SMTP and VarDumper listen only on loopback ports `2525` and `9912`.
- Complete live SMTP and VarDumper exchanges without leaving test records.
- Probe every supported service/runtime release and verify Xdebug SHA-256 data.

The automated gate uses HerdMe's reserved `--acceptance` launch argument to
start protocol listeners without consuming the real first-launch state. Do not
use that argument for the interactive wizard check.

If the gate succeeds, keep the app running because `-LeaveRunning` is present.

## Phase 3: inspect the generated artifact

The required files are:

```text
dist\HerdMe-0.1.0-win-x64-portable.zip
dist\HerdMe-0.1.0-win-x64-portable.zip.sha256
```

Verify them again:

```powershell
$archive = Get-Item ./dist/HerdMe-0.1.0-win-x64-portable.zip
$sidecar = Get-Item ./dist/HerdMe-0.1.0-win-x64-portable.zip.sha256
Get-FileHash -Algorithm SHA256 $archive
Get-Content $sidecar

$processes = @(Get-Process -Name HerdMe.Windows -ErrorAction SilentlyContinue)
$processes | Select-Object Id, ProcessName, Path, MainWindowTitle
if ($processes.Count -ne 1) { throw "Expected exactly one HerdMe process." }
```

Extract the ZIP into a new empty directory and launch the extracted
`HerdMe.Windows.exe`. It must run without a separately installed .NET runtime
or Windows App Runtime. Launch it a second time and prove the original PID is
still the only HerdMe process.

## Phase 4: interactive acceptance

Read `Windows/ACCEPTANCE.md` completely and work through every applicable item.
Record concrete results in that checklist or a dated Windows acceptance report.
At minimum, verify these workflows on the real display:

1. On a clean Windows user profile, verify the first-launch wizard waits for the
   user, then installs local domains, the trusted CA, PHP 8.4 with all required
   extensions, Composer, Laravel Installer, and Node.js 22 in order. Cancel one
   step, verify its retry state, finish, restart, and confirm it does not return.
2. General, Sites, PHP, Node, Services, Mail, Dumps, Debugger, Logs, and About
   render without clipping at 100%, 125%, and 150% scaling.
3. The tray can reopen a closed window and quit the application.
4. Launch-at-login starts in the background without a black console window.
5. Install PHP 8.4 and verify all 13 Laravel extensions before it becomes
   selectable.
6. Install Node, Composer, and Laravel Installer; current versions must not show
   an Update action.
7. Create a Laravel 13 project through the installed Laravel Installer. Keep
   visible progress for validation, installer preparation, project creation,
   optional Boost/Git, Node/npm/Vite, verification, registration, and result.
8. Open the created site as `https://project.test/` without a port and verify
   static content, Laravel routes, POST, and a large request body.
9. Trust HerdMe's local CA and install local-domain entries through GUI/UAC
   without a visible terminal. Preserve unrelated hosts-file entries.
10. Send plain-text and multipart HTML email to port `2525` and inspect it.
11. Send a Symfony VarDumper payload to port `9912` and inspect it.
12. Install and trigger Xdebug for a selected site and verify the configured IDE
    endpoint.
13. Add MariaDB, MySQL, PostgreSQL, MongoDB, Redis, Meilisearch, MinIO, and
    RustFS. Verify automatic install, checksums, start/stop, persistence,
    loopback bindings, logs, and automatic startup.
14. For each running TablePlus-compatible database service, verify `Open in
    TablePlus` launches TablePlus with the loopback host, configured port, and
    managed default credentials. Hide the action while the service is stopped
    and show a clear install message when TablePlus is unavailable.
15. Confirm MySQL does not listen on `33060`, and MinIO/RustFS API and console
    listeners are loopback-only.
16. Confirm newest PHP, Node, application, and service versions hide their
    Update actions.
17. Confirm About shows MIT and third-party notices with no commercial gates.

Use screenshots for visual failures. Before finishing each page, check long
text, empty states, loading states, errors, disabled controls, and narrow window
sizes. Do not mark a checklist item complete from source inspection alone.

## Phase 5: fix and retest

For every issue found:

1. Explain the observed behavior in a short Arabic progress comment.
2. Locate the responsible WinUI page/service and understand the existing
   ownership boundary before editing.
3. Fix the real behavior; do not hide failures or weaken acceptance checks.
4. Add a regression contract where the behavior is testable without UI.
5. Run the focused test, then the full `acceptance.ps1` command again.
6. Rebuild the portable ZIP and retest the extracted clean copy.

Do not declare the 99% target complete while any required Windows checklist
item is untested, failing, or supported only by indirect evidence.

## Final report and handoff back

The final response must state:

- The exact commit tested.
- Windows edition/build and display scaling tested.
- Automated gate result and any skipped checks.
- Manual checklist results and screenshots for visual fixes.
- The exact ZIP and `.sha256` paths and computed SHA-256 value.
- Proof that a second launch leaves exactly one process.
- Any remaining gap, especially Valkey/Typesense, signing, or installer work.

Leave the verified application open for the user. Do not push code changes
without the user's confirmation, but show `git status --short` and propose a
focused commit containing only HerdMe changes made during Windows acceptance.
