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

Local-domain changes use HerdMe's own elevated GUI helper. Windows displays its
normal UAC consent prompt, but HerdMe does not open PowerShell, Command Prompt,
or another console window. The helper accepts only a staged HerdMe hosts file
and can write only to the Windows hosts path.

Requirements:

- Windows 10 version 2004 or newer
- x64 processor; build and packaging scripts reject every other architecture
- Windows PowerShell 5.1 or newer
- Visual Studio 2022 with Desktop development with C++
- .NET 8 SDK
- CMake 3.20 or newer

Build from PowerShell at the repository root:

```powershell
.\Windows\build.ps1 -Architecture x64 -Configuration Debug
```

Run the full automated hardware gate before completing the versioned manual
checklist in `Windows\ACCEPTANCE.md`:

```powershell
.\Windows\acceptance.ps1 -Configuration Release -LeaveRunning
```

The portable core and Windows service contracts can be compiled and tested independently
on other platforms, but the WinUI XAML compiler is a Windows executable. A
complete UI build, certificate-store/UAC verification, and execution of the
official Windows runtime binaries therefore require Windows. The project is not
considered at the 99% acceptance target until those checks pass.

The cross-platform contract executable performs real loopback SMTP, VarDumper,
and FastCGI exchanges. Its FastCGI fixture verifies parameters, stdout/stderr,
and request-body chunking above the protocol's 65,535-byte record limit. The
contract project compiles every non-UI Windows model and service. It also runs
the local HTTP server against static GET/HEAD requests, host isolation, method
rejection, relative and absolute-form path traversal, isolated hosts rendering,
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
expected DLL. The native `acceptance.ps1` gate runs both probes automatically.
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
The repository's `Windows x64` GitHub Actions workflow runs this same package
and automated acceptance gate on `windows-2022`. It launches the native app,
checks single-instance activation and loopback capture services, runs live SMTP
and VarDumper probes, and uploads both files for 14 days. It does not replace
the interactive display, certificate, UAC, browser, and service checklist above.
A public Windows release still requires Authenticode signing and installation
packaging.
