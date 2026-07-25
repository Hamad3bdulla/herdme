# HerdMe

HerdMe is an independent open-source local development environment for PHP
and Laravel projects. It provides site discovery, runtime management, local
domains, HTTPS, services, mail inspection, dump inspection, logs, and debugger
controls without subscriptions, activation, license keys, or paid gates.

The shared runtime and CLI are portable C++20 so the same backend can power
the native macOS and Windows applications. Platform-specific UI remains native:
SwiftUI on macOS and WinUI on Windows.

## Independence

HerdMe is a clean, independent implementation. It does not copy Herd source
code, assets, signing identities, binaries, runtime folders, or configuration.
Its application data lives under `HerdMe`-owned paths. Existing project folders
may be scanned only when the user adds them.

HerdMe is not affiliated with or endorsed by Laravel LLC or the makers of
Laravel Herd.

## Current status

The macOS SwiftUI application currently includes the native shell and menu bar,
site scanning and creation, isolated PHP and Node runtimes, HTTP and HTTPS
reverse proxies, PHP-FPM/FastCGI request handling, local CA generation, local
DNS, SMTP capture, VarDumper capture, logs, debugger controls, per-site PHP and
Node selection, direct project linking, launch at login, and managed local service
processes. The inbox decodes MIME text and HTML messages and renders HTML with
scripts and external network loads disabled. PHP
runtimes are validated before startup for Laravel's required extensions.
Xdebug is installed into HerdMe-owned storage and loaded by PHP-FPM without
changing package-manager configuration; memory, upload, and post limits are
also applied to the active FPM pool. Its service catalog includes MariaDB,
MySQL, PostgreSQL, MongoDB, Redis, Valkey, Meilisearch, Typesense, MinIO, and
RustFS on Apple Silicon.

The x64-only Windows WinUI 3 application includes isolated PHP, Xdebug,
Composer, Laravel Installer, and Node runtime management; persistent site
scanning, direct project linking, and project creation; HTTP/HTTPS and FastCGI
serving; tray and sign-in startup; mail and dump capture; logs; Debug Session
URLs; and managed MariaDB, MySQL, PostgreSQL, MongoDB, Redis, Meilisearch,
MinIO, and RustFS services. Valkey and Typesense are also visible in the Windows
catalog with installation disabled and an explicit reason until their upstream
projects publish official native Windows x64 packages. Both native
applications reject a second process before it can start duplicate listeners.
Stable and beta update checks are implemented on both platforms, with a local
feed for development and an overridable HTTPS feed for releases. Both
applications also expose the MIT license and third-party acknowledgements
in-app. Windows packaging and full runtime verification on Windows hardware
remain in progress.

## PHP and Laravel

HerdMe uses FastCGI for dynamic requests; it does not use FrankenPHP. macOS
uses PHP-FPM, while Windows uses the official `php-cgi.exe -b` runtime because
PHP-FPM is not available in official Windows PHP builds. Static files are
served directly and Laravel routes fall back to
`public/index.php`. HTTPS requests are forwarded with trusted proxy metadata so
Laravel receives `HTTPS=on`.

New projects run directly through the managed Laravel Installer that is already
installed by HerdMe. Project creation performs no version probe or package-manager
update on this fast path; it installs the tool automatically only when its managed
Composer or Laravel Installer files are missing.

Before a PHP runtime can serve a site, HerdMe checks for all Laravel 13
deployment requirements requested by this project: `ctype`, `curl`, `dom`,
`fileinfo`, `filter`, `hash`, `mbstring`, `openssl`, `pcre`, `pdo`, `session`,
`tokenizer`, and `xml`. Startup stops with the exact missing-extension list if
the selected runtime is incomplete.

On macOS the debugger workflow installs and validates a stable PECL Xdebug
build inside `~/Library/Application Support/HerdMe/Extensions`, applies the
selected debug mode, trigger, port, and IDE key to PHP-FPM, and opens triggered
site sessions without modifying Homebrew PHP files.

Certificate trust and resolver setup use native macOS Authorization Services
without opening Terminal or requesting Apple Events permission.
The installed HerdMe network helper binds only loopback DNS, HTTP, and HTTPS,
then drops administrator privileges to the current user. It forwards the
standard ports to the app's managed listeners so browser URLs do not expose
internal ports such as `8080` or `8443`. Its launch service is associated with
the `app.herdme.desktop` application identity. HerdMe detects an outdated helper,
offers an explicit update action, and waits for the replacement service to be
running before reporting setup as complete.
Local WebKit previews use the loopback site runtime directly and render a
desktop-width page inside the thumbnail, including when another application
manages the selected local-domain resolver. Windows WebView2 previews apply
the same desktop viewport and fit it to the available preview frame.

The tracked parity target and Windows architecture are in
[`docs/PARITY.md`](docs/PARITY.md).

## Validation

The current macOS suite executes 73 tests: 72 pass and the optional live
Laravel-project test is skipped unless a project path or temporary-creation flag
is supplied. A live run created Laravel 13.22.0 with the React starter kit
through HerdMe's managed installer, installed its npm packages, built and
verified its Vite manifest, then served its dynamic page, a static asset, and
HTTPS through the managed PHP-FPM environment.
Laravel's standard starter kits use HerdMe's managed Node.js build stages, while
custom starter kits accept a validated `vendor/package` Composer identifier and
use Laravel Installer's official `--using` workflow. Both native applications
keep the creation result visible and report each completed, active, or failed stage.
The portable C++20 tests, expanded Windows C# cross-platform contracts, and all
12 WinUI XAML XML checks pass on macOS. The Windows contract gate compiles every
non-UI model and service, exercises static HTTP routing and traversal rejection,
and cross-publishes as a self-contained PE32+ x86-64 executable. Debug builds
pass for both Apple Silicon and
Intel. Native visual checks cover site start/stop, a real local HTTP preview,
Logs, About and application icons, Mail/Dumps listeners, and single-instance
behavior. A real Homebrew conflict check also confirms that MySQL can be
installed and run from HerdMe's isolated runtime while an existing MariaDB
installation remains linked. Running MinIO and RustFS instances expose their
loopback-only web consoles from the service menu. Service and debugger ports are rendered without
locale thousands separators, and live SMTP HTML plus VarDumper captures have
been visually verified. A Windows machine is still required for the
complete WinUI build and runtime acceptance checklist.

Live Windows release probes resolve all installable managed services plus PHP 8.0-8.5,
Node.js 20/22/24/26, Composer, Laravel Installer, and Xdebug. The Xdebug probe
downloads every supported NTS x64 archive, verifies its official GitHub
SHA-256 digest, and extracts only its expected DLL.

HerdMe removes legacy references to another application's default project or
private data folders from its own configuration. New park paths, project links,
project creation requests, and symbolic links into those folders are rejected
on both native platforms; this policy is covered by the macOS, portable-core,
and Windows contract suites.

## Build

Requirements:

- macOS 13 or newer
- Xcode 26 or newer
- XcodeGen

```sh
xcodegen generate
xcodebuild -project HerdMe.xcodeproj -scheme HerdMe \
  -configuration Debug -derivedDataPath DerivedData build
open DerivedData/Build/Products/Debug/HerdMe.app
```

Run the macOS test suite with:

```sh
xcodebuild -project HerdMe.xcodeproj -scheme HerdMe \
  -configuration Debug -derivedDataPath DerivedData test
```

Run the full live Laravel creation and serving gate with:

```sh
xcodebuild -project HerdMe.xcodeproj -scheme HerdMe \
  -configuration Debug -derivedDataPath DerivedData \
  HERDME_CREATE_LARAVEL_INTEGRATION=1 \
  -only-testing:HerdMeTests/ConfigurationAndSiteScannerTests/testExistingLaravelProjectThroughFPMWhenRequested \
  test
```

Create local ZIP and DMG artifacts for testing. These artifacts are ad-hoc
signed; public releases still need a Developer ID signature and notarization.

```sh
./scripts/package-macos.sh Release
```

Build and inspect the shared core without any package manager dependencies:

```sh
clang++ -std=c++20 -Wall -Wextra -Wpedantic \
  -ICore/include Core/src/core.cpp Core/src/main.cpp \
  -o build/herdme-core
./build/herdme-core doctor
```

The portable core also builds with CMake and MSVC on Windows:

```powershell
cmake -S Core -B build/core -G "Visual Studio 17 2022"
cmake --build build/core --config Release
build\core\Release\herdme-core.exe doctor
```

Build the native Windows application and portable core together from Windows:

```powershell
.\Windows\build.ps1 -Architecture x64 -Configuration Debug
```

Create a self-contained, unpackaged Windows portable ZIP with:

```powershell
.\Windows\package-portable.ps1 -Architecture x64 -Configuration Release
```

The `Windows x64` GitHub Actions workflow runs the same build, contract, XAML,
portable-package, PE-architecture, checksum, single-instance, listener, SMTP,
and VarDumper gates on `windows-2022`, then uploads the ZIP plus its `.sha256`
file.

See [`Windows/README.md`](Windows/README.md) for the Windows prerequisites and
current milestone.

## License

HerdMe source code is available under the MIT License. Downloaded third-party
runtimes and tools retain their own licenses and are not relicensed by HerdMe;
see [`docs/THIRD_PARTY.md`](docs/THIRD_PARTY.md). HerdMe itself has no
activation, subscription, license-key, upgrade, or paid feature mechanism.
