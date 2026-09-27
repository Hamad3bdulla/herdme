# HerdMe for Windows

This is the native WinUI 3 application for HerdMe. It uses the same portable
C++20 core and JSON contracts as the macOS implementation. It includes native
navigation and tray controls; persistent site roots and direct project links;
managed PHP, Composer, Laravel Installer, Xdebug, and Node.js; Laravel project
creation; mixed PHP versions by site; HTTP/HTTPS and FastCGI serving; MIME
text/HTML mail preview with scripts and external loads disabled; VarDumper capture;
logs; site-specific Debug Session URLs; and managed MariaDB, MySQL, PostgreSQL,
MongoDB, Redis, Meilisearch, MinIO, and RustFS services. A named per-user mutex prevents a second app process from
starting duplicate listeners and signals the existing window to open instead.
Laravel creation keeps a staged progress dialog visible through validation,
managed installer preparation, optional Boost and Git work, managed Node.js
preparation, npm installation, production Vite compilation, site registration,
and the final success or failure result. Starter-kit projects are not registered
until their Vite manifest exists.
When the managed Composer and Laravel Installer files are present, project creation
runs the installed Laravel Installer immediately without a version probe or package
update. The automatic installer path is used only when those managed files are absent.
On a new Windows user profile, HerdMe opens a native setup wizard before the
navigation or background listeners. The user explicitly starts setup; the
wizard then prepares `.test` routing through UAC, trusts the local HTTPS CA,
installs and validates PHP 8.4, installs Composer and Laravel Installer, and
installs Node.js 22. A failed step remains visible and can be retried safely.
Settings written by a release from before this wizard are treated as completed,
so an update does not interrupt an existing installation.
Custom starter kits accept a validated `vendor/package` Composer identifier and
are passed to Laravel Installer through its official `--using` option.
The PHP page refuses to mark a runtime ready until every required module is
loaded. Windows uses the official NTS `php-cgi.exe -b` executable rather than
PHP-FPM. The extension report validates
`ctype`, `curl`, `dom`, `fileinfo`, `filter`, `hash`, `mbstring`, `openssl`,
`pcre`, `pdo`, `session`, `tokenizer`, and `xml`. Stable/beta update checks,
version/build comparison, the MIT license, and third-party acknowledgements are
also available in the native settings pages. Adding a service installs its
checksum-verified package automatically. Installed services hide the
update action when their manifest already matches the current upstream release.
Every added service also exposes an `Add to .env` action that selects a site,
creates `.env` from `.env.example` when needed, and updates only that service's
connection variables. Running MySQL, MariaDB, PostgreSQL, MongoDB, Redis, and
Valkey instances expose a TablePlus action with their loopback connection
settings; the action remains hidden while the service is stopped. MySQL,
MariaDB, and PostgreSQL receive a unique Credential Manager secret per service.
PostgreSQL is initialized with SCRAM, and existing passwordless database data is
migrated before the service is reported as running. `.env` and TablePlus use the
same protected username and password.
From a site's toolbar, **Create database** suggests a name and offers
**Create, connect & open**. The same dialog installs or starts the database
service when needed, creates a dedicated database and user, updates `.env`,
clears cached Laravel configuration, and opens TablePlus. With no configured SQL
service, it prepares MariaDB on an available local port. Progress and errors stay
in the dialog; retrying after creation resumes the remaining steps using the
same credentials. TablePlus opening can be disabled without skipping `.env` setup.
Packages with GitHub or vendor SHA-256 metadata require SHA-256. Oracle's MySQL
Windows page currently publishes MD5 plus a detached signature rather than
SHA-256, so HerdMe accepts its published MD5 only for the exact release parsed
from that page and downloads the matching archive only from Oracle's HTTPS CDN.
PostgreSQL uses the documented EDB Windows archive pinned to a full SHA-256
verified by HerdMe before release, because that download page does not publish
a machine-readable checksum.

Valkey and Typesense remain an explicit Windows parity gap. They are visible in
the service catalog with their native installation controls disabled and a clear
availability reason. Their official release feeds currently publish no native
Windows executable, and HerdMe will not silently substitute an unverified
third-party binary. They remain in the acceptance target until a reproducible
native build with a verified checksum is provided.
The dated upstream evidence and the gates required before enabling either
service are recorded in [`docs/WINDOWS_NATIVE_SERVICE_AUDIT.md`](../docs/WINDOWS_NATIVE_SERVICE_AUDIT.md).

On Windows, the Redis service covers projects that expect Valkey (same protocol
and `.env` variables), and Meilisearch covers Laravel Scout search in place of
Typesense.

## Windows integration

- **`herdme` command.** `herdme.exe` ships next to the app. HerdMe writes
  `%LOCALAPPDATA%\HerdMe\bin\herdme.cmd`, and that folder is already on the user
  PATH, so a new terminal can run `herdme status`, `herdme sites`,
  `herdme link [folder]`, `herdme unlink [site]`, `herdme open <site>`,
  `herdme start`, `herdme stop`, `herdme share <site>` (you still confirm in the
  window), `herdme logs [site]`, `herdme tinker [site]`, and `herdme help`.
  Requests go to the running app over a per-user named pipe
  (`HerdMe.Command.<session>`, created first-instance only, current user only,
  network logons denied, one small JSON line per request).
- **Jump List.** Right-click the taskbar icon for Start all, Stop all, Sites,
  Tinker, and up to six sites (favorites first). Each entry is sent to the running
  instance, so it never starts a second HerdMe.
- **Folder watcher.** Only the top level of each parked folder is watched, so a
  project created, renamed, or removed there appears without Refresh; changes
  inside projects (`vendor`, `node_modules`, `.git`) never cause a rescan.
- **Opt-in shell integration** (General, all off by default, no administrator
  rights, removed when turned off and on uninstall):
  - Explorer **Link with HerdMe** on folders and folder backgrounds
    (`HKCU\Software\Classes\Directory\shell\HerdMe.Link` and
    `...\Directory\Background\shell\HerdMe.Link`). On Windows 11 it is under
    **Show more options**, because the new menu only lists packaged apps.
  - `herdme://` links (`HKCU\Software\Classes\herdme`). A link can only open a
    page, a site, Logs, or Tinker; it cannot change settings, start anything, or
    share a site.
  - A Windows Terminal profile written as a fragment under
    `%LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments\HerdMe`, so Terminal's
    own settings are never edited.
- **Notifications.** A tray notification when a service, queue worker, or public
  link stops without being asked, and when an update is found while HerdMe is in
  the tray. They use the tray icon, are rate-limited, and can be turned off.
- **Crash reports and diagnostics.** An unrecoverable error writes a report and a
  small minidump (thread stacks and modules only, no heap) to
  `%LOCALAPPDATA%\HerdMe\Log\Crashes`, keeping the newest five. **General >
  Export diagnostics** saves a zip of HerdMe logs, settings, and crash reports with
  secrets and the Windows user name masked; minidumps are added only when you tick
  the box. Nothing is uploaded.

## Laravel tools

- **Code quality** (site menu): runs the project's own `vendor/bin/pint --test`
  (check only, never rewrites files), `vendor/bin/phpstan analyse`, and
  `php artisan test` with the site's HerdMe PHP, hidden and bound to a job object.
  Findings are listed; clicking one opens VS Code at the line (or the default app
  when VS Code is not installed). A missing tool shows the `composer require`
  command instead of downloading anything.
- **Live Laravel log** (Logs page, site sources): a level filter (All, Info and
  above, Warning and above, Error and above) that keeps whole entries with their
  stack traces, entry/warning/error counts, and **Open last error**, which opens
  the project file and line the newest error points at (vendor frames are used
  only when nothing else is known).

Local-domain changes use HerdMe's own elevated GUI helper. Windows displays its
normal UAC consent prompt, but HerdMe does not open PowerShell, Command Prompt,
or another console window. The helper accepts only a staged HerdMe hosts file
and can write only to the Windows hosts path.

Requirements:

- Windows 10 version 2004 or newer
- x64 processor for releases, the installer, and acceptance (ARM64 is a build
  preview; see below)
- Windows PowerShell 5.1 or newer
- Visual Studio 2022 with Desktop development with C++
- .NET 8 SDK
- CMake 3.20 or newer
- Inno Setup 6 when building the installer

Build from PowerShell at the repository root:

```powershell
.\Windows\build.ps1 -Architecture x64 -Configuration Debug
```

Run the full automated hardware gate before completing the versioned manual
checklist in `Windows\ACCEPTANCE.md`:

```powershell
.\Windows\acceptance.ps1 -Configuration Release -LeaveRunning
```

The Windows x64 workflow records the complete acceptance transcript. If the
gate fails, it uploads that transcript together with only HerdMe's top-level
`startup`, `environment`, `unhandled`, and structured diagnostic logs in a
separate `win-x64-failure-diagnostics` artifact. Treat that artifact as the
first source of evidence for a failed native run; it does not count as an
accepted Windows build.

The portable core and Windows service contracts can be compiled and tested independently
on other platforms, but the WinUI XAML compiler is a Windows executable. A
complete UI build, certificate-store/UAC verification, and execution of the
official Windows runtime binaries therefore require Windows. The project is not
considered at the 99% acceptance target until those checks pass.

The cross-platform contract executable invokes the real portable core process
for doctor, site scanning, and PHP-extension stdin/JSON contracts, and performs
real loopback SMTP, VarDumper, and FastCGI exchanges. Its FastCGI fixtures verify
parameters, stdout/stderr, request-body chunking above the protocol's 65,535-byte
record limit, and progressive HTTP delivery before `END_REQUEST` while filtering
hop-by-hop response headers. The
contract project compiles every non-UI Windows model and service. It also runs
the local HTTP server against streamed 2MB static GET/HEAD requests, open-ended
and suffix byte ranges with `206/416`, host isolation, method rejection,
relative and absolute-form path traversal, symlink/junction document-root escapes, isolated hosts rendering,
per-site runtime files, update selection, site configuration, MIME parsing, and
percent-encoded `XDEBUG_TRIGGER` session URLs. On non-Windows hosts this project
can additionally cross-publish to a self-contained PE32+ x86-64 executable;
that does not replace native execution on Windows.

Probe the current metadata, checksum, and package URL for every managed service
without downloading or installing the full archives:

```powershell
dotnet run --project .\Windows\HerdMe.Windows.ContractTests `
  --configuration Release -- `
  --live-service-releases --live-runtime-releases
```

The runtime probe covers PHP 8.0-8.5 NTS x64, Node.js 20/22/24/26, Composer,
Laravel Installer, and the matching Xdebug archives. Xdebug uses the official
GitHub release, verifies its published SHA-256 digest, and extracts only the
expected DLL. The native `acceptance.ps1` gate runs both probes automatically
unless `-SkipLiveReleaseChecks` is supplied. Pull-request CI uses that switch so
external outages cannot fail unrelated code; `.github/workflows/upstream-nightly.yml`
runs both live probes every night and can also be dispatched manually.
The same PHP allowlist is enforced inside the installer, not only in the UI.
An older managed cycle is added to the picker only when it is already installed,
and it receives no Install, Update, or Repair action.
The automated listener gate launches with the reserved `--acceptance` argument
so it can test background protocols without modifying first-launch state. This
argument is for the repository's hardware gate; interactive acceptance must
still verify the real wizard on a clean Windows user profile.

The script builds `herdme-core.exe`, copies it into the WinUI runtime folder,
and builds the unpackaged self-contained desktop application. HerdMe stores its
Windows data in `%LOCALAPPDATA%\HerdMe` and does not use another application's
runtime, settings, certificate, or data directories. Saved roots, linked
projects, creation requests, scanner roots, and resolved links into the other
application's default or private data folders are rejected.

Create a portable ZIP after building and publishing the application:

```powershell
.\Windows\package-portable.ps1 -Architecture x64 -Configuration Release
```

The versioned archive is written to `dist` and contains the self-contained
WinUI app, `Runtime\herdme-core.exe`, the MIT license, and third-party
acknowledgements. A matching `.sha256` file is generated beside the ZIP.
Create the per-user installer, which installs under
`%LOCALAPPDATA%\Programs\HerdMe` without requiring elevation, with:

```powershell
.\Windows\package-installer.ps1 -Architecture x64 -Configuration Release
```

The Setup executable includes Start Menu/uninstall registration and an optional
desktop shortcut. It refuses to replace files while HerdMe's single-instance
mutex is active, preserves application data during upgrades and uninstall, and
writes its own matching `.sha256` sidecar.
The repository's `Windows x64` GitHub Actions workflow runs this same package
and automated acceptance gate on `windows-2022`. It launches the native app,
checks single-instance activation and loopback capture services, runs live SMTP
and VarDumper probes, performs an isolated silent install/core-health/uninstall
cycle, and uploads the portable and Setup artifacts for 14 days. It does not replace
the interactive display, certificate, UAC, browser, and service checklist above.
A public Windows release still requires an actual Authenticode certificate.

## Accessibility check

`Windows\test-accessibility.ps1` starts the portable build, opens every page, and
uses the UI Automation client built into Windows (no download) to fail on any
enabled, visible control without an accessible name, and on buttons, fields,
check boxes, combo boxes, links, radio buttons, and sliders that cannot take
keyboard focus. It writes `build\windows-accessibility\accessibility-<culture>.json`.
CI runs it after acceptance in both English and Arabic and uploads the report
with the UI evidence. Fix a finding in XAML (`AutomationProperties.Name`, a
`Header`, or content text); do not exempt it in the script.

## Clean-profile check in Windows Sandbox

Acceptance on a PC that already ran HerdMe cannot prove a first install. With the
Windows Sandbox feature enabled:

```powershell
.\Windows\package-installer.ps1 -Architecture x64 -Configuration Release
.\Windows\start-clean-acceptance.ps1
```

The sandbox maps the repository read-only and only `build\clean-acceptance-results`
writable. Inside it, `clean-acceptance-sandbox.ps1` checks that the profile has no
HerdMe data, installs silently per user, runs `herdme --version`, confirms first run
shows onboarding and a second launch keeps one process, confirms every listener is
loopback-only, confirms the Explorer, `herdme://`, and Terminal entries are absent
until enabled, runs the accessibility scan, and uninstalls. `summary.json` records
each result. Closing the sandbox discards everything.

## ARM64 (build preview)

`build.ps1 -Architecture ARM64 -Configuration Release -SkipTests` cross-compiles the
WinUI app (`win-arm64`) and `herdme-core.exe` for ARM64. PHP, Node.js, and the
services stay the official x64 builds, which Windows 11 on ARM runs under x64
emulation, so the app keeps shipping the x64 Visual C++ runtime for PHP. ARM64
binaries cannot run on an x64 PC, so a cross-compiled build requires `-SkipTests`;
the x64 build runs every test. CI uploads the result as an unsigned
`win-arm64-unsigned-preview` artifact after checking both executables are ARM64.
Packaging and the installer stay x64 until the preview passes acceptance on ARM64
hardware. Until then, the x64 installer (`ArchitecturesAllowed=x64compatible`) is
the supported way to use HerdMe on Windows on ARM.

ReadyToRun: `package-portable.ps1` publishes with `PublishReadyToRun=true`, so the
app starts from precompiled code instead of JIT-compiling at launch.
