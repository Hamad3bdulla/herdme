# HerdMe

HerdMe is an independent open-source local development environment for PHP
and Laravel projects. It provides site discovery, runtime management, local
domains, HTTPS, services, mail inspection, dump inspection, logs, and debugger
controls without subscriptions, activation, license keys, or paid gates.

The portable C++20 core currently provides the JSON contract CLI consumed by
the Windows application. The macOS application uses native Swift domain
implementations, with shared behavioral fixtures covering contracts that exist
in both layers. Platform-specific UI remains native: SwiftUI on macOS and WinUI
on Windows.

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
On both platforms, every managed service exposes an action that writes its
connection settings into a selected project's `.env` file without duplicating
keys. Running MySQL, MariaDB, PostgreSQL, MongoDB, Redis, and Valkey instances
can also be opened directly in TablePlus with their managed loopback connection
settings. MySQL, MariaDB, and PostgreSQL use a unique protected credential per
service instance; new PostgreSQL clusters use SCRAM, and existing passwordless
clusters are migrated without deleting their data. When a default service port is already assigned or owned by another
application, HerdMe keeps the owner running and selects the next free loopback
port instead.
On a new installation, a native first-launch wizard prepares local domains,
trusts HerdMe's HTTPS certificate, installs PHP 8.4, verifies Laravel's required
extensions, and installs Composer, Laravel Installer, and Node.js 22. Every
stage is visible and retryable; completion is persisted only after all stages
succeed. Configurations created by an older HerdMe release are migrated as
already completed, so existing users are not interrupted after an update.

The x64-only Windows WinUI 3 application includes isolated PHP, Xdebug,
Composer, Laravel Installer, and Node runtime management; persistent site
scanning, direct project linking, and project creation; HTTP/HTTPS and FastCGI
serving; tray and sign-in startup; mail and dump capture; logs; Debug Session
URLs; and managed MariaDB, MySQL, PostgreSQL, MongoDB, Redis, Meilisearch,
MinIO, and RustFS services. Valkey and Typesense are also visible in the Windows
catalog with installation disabled and an explicit reason until their upstream
projects publish official native Windows x64 packages. Both native
applications reject a second process before it can start duplicate listeners.
The Windows app uses the same first-launch stages in native WinUI and delays
background listeners until setup succeeds. Its local-domain stage uses the
normal Windows UAC prompt without opening a console.
Stable and beta update checks are implemented on both platforms. Remote feeds
must use HTTPS and a valid ECDSA P-256 signature; Release builds use a bundled
feed URL and public key, while environment overrides are limited to Debug
builds. See `docs/RELEASING.md` for the signing and publishing procedure. Both
applications also expose the MIT license and third-party acknowledgements
in-app. Windows packaging and full runtime verification on Windows hardware
remain in progress.

## PHP and Laravel

HerdMe uses FastCGI for dynamic requests; it does not use FrankenPHP. macOS
uses PHP-FPM, while Windows uses the official `php-cgi.exe -b` runtime because
PHP-FPM is not available in official Windows PHP builds. Static files are
streamed in bounded chunks on both platforms, including single HTTP byte-range
requests for large assets, and Laravel routes fall back to
`public/index.php`. HTTPS requests are forwarded with trusted proxy metadata so
Laravel receives `HTTPS=on`.
Dynamic FastCGI stdout is also streamed progressively on both platforms: HerdMe
buffers only the bounded CGI header, strips hop-by-hop headers, and forwards
body records as PHP produces them.

New projects run directly through the managed Laravel Installer that is already
installed by HerdMe. Project creation performs no version probe or package-manager
update on this fast path; it installs the tool automatically only when its managed
Composer or Laravel Installer files are missing.

Before a PHP runtime can serve a site, HerdMe checks for all Laravel 13
deployment requirements requested by this project: `ctype`, `curl`, `dom`,
`fileinfo`, `filter`, `hash`, `mbstring`, `openssl`, `pcre`, `pdo`, `session`,
`tokenizer`, and `xml`. Startup stops with the exact missing-extension list if
the selected runtime is incomplete.

On macOS the debugger workflow installs and validates a stable Xdebug build
inside `~/Library/Application Support/HerdMe/Extensions`. The source archive
must match the SHA-256 published on the official Xdebug download page before it
is unpacked. HerdMe applies the selected debug mode, trigger, port, and IDE key
to PHP-FPM without modifying Homebrew PHP files.

Certificate trust uses Security.framework in the user's Keychain, while local
DNS and standard ports use an embedded `SMAppService` launch daemon. Neither
path opens Terminal, requests Apple Events permission, or exposes a generic
privileged command channel. The HerdMe network helper binds only loopback DNS,
HTTP, and HTTPS, then moves network handling to the current user. It forwards
the standard ports to the app's managed listeners so browser URLs do not expose
internal ports such as `8080` or `8443`. Its launch service is associated with
the `app.herdme.desktop` application identity. Migration keeps the legacy
service recoverable until the modern unprivileged worker owns every required
port, restores it on failure, and prevents a failed migration from looping.
HerdMe waits for the replacement service to be running before reporting setup
as complete.
Local WebKit previews use the loopback site runtime directly and render a
desktop-width page inside the thumbnail, including when another application
manages the selected local-domain resolver. Windows WebView2 previews apply
the same desktop viewport and fit it to the available preview frame.

The tracked parity target and Windows architecture are in
[`docs/PARITY.md`](docs/PARITY.md).
Release history is maintained in [`CHANGELOG.md`](CHANGELOG.md), while `VERSION`
and `BUILD_NUMBER` are the authoritative release identifiers for both platforms.

## Validation

The current macOS suite executes 151 tests: 149 pass, while the optional live
Laravel-project and database-authentication tests are skipped unless their
integration flags are supplied. Both optional gates were also run explicitly on
July 26, 2026 and passed against an existing Laravel application and a temporary
authenticated instance of the installed MySQL runtime. A live run created Laravel 13.22.0 with the React starter kit
through HerdMe's managed installer, installed its npm packages, built and
verified its Vite manifest, then served its dynamic page, a static asset, and
HTTPS through the managed PHP-FPM environment.
Laravel's standard starter kits use HerdMe's managed Node.js build stages, while
custom starter kits accept a validated `vendor/package` Composer identifier and
use Laravel Installer's official `--using` workflow. Both native applications
keep the creation result visible and report each completed, active, or failed stage.
The portable C++20 tests, expanded Windows C# cross-platform contracts, and all
13 WinUI XAML XML checks pass on macOS. The Windows contract gate compiles every
non-UI model and service, exercises static HTTP routing and traversal rejection,
proves progressive FastCGI output before `END_REQUEST`, and cross-publishes as a
self-contained PE32+ x86-64 executable. Debug builds
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

Swift, portable C++, and both Windows C# projects treat compiler warnings as
errors. The local Release gate builds a universal `arm64`/`x86_64` macOS app,
verifies its signature, ZIP, DMG, and SHA-256 sidecar, installs that exact app,
and confirms the local Laravel site returns `200` over HTTP. A prior explicit
certificate-authorization gate also passed trusted HTTPS; the current freshly
installed build correctly keeps HTTP available while macOS waits for the user
to approve the saved HTTPS identity in Keychain.

Managed services on both platforms include an `Add to .env` action. It lets the
user select a discovered site, creates `.env` from `.env.example` when necessary,
and safely adds or updates that service's Laravel connection variables without
discarding unrelated settings or comments.

The macOS mail inbox persists a bounded metadata index and loads full message
bodies only when selected, so startup memory and disk reads do not scale with
the combined raw size of the retained inbox.
PHP-FPM worker capacity scales with the Mac's logical processor count and stays
inside a bounded 4-to-32 child range.

Managed databases, MinIO, RustFS, and Typesense no longer use shared or empty credentials. HerdMe
generates a unique 256-bit secret per service instance, keeps it in macOS Keychain
or Windows Credential Manager, and uses that same secret for database access,
TablePlus, process launch where applicable, and Laravel `.env` export. A live
macOS gate starts the installed MySQL and MariaDB runtimes and verifies that
authenticated access succeeds while passwordless access fails.

Local HTTPS secrets are protected the same way. macOS prefers Data Protection
Keychain for the CA private key and random PKCS#12 password. Ad-hoc local builds
fall back to the login Keychain only when the required entitlement is missing;
a later properly signed build migrates those secrets before removing the legacy
copies. Automatic startup attempts HTTPS only for a trusted CA and never permits
a Keychain prompt during that background attempt. If protected credentials are
available without UI, this also repairs profiles whose older approval marker is
missing. If legacy credentials still require interaction, sites remain over
HTTP and the General page offers Enable; stable signed builds can then start
HTTPS automatically after that explicit approval. Plaintext keys are removed
only after verified Keychain storage.
Credential-protected services created by an older build likewise wait for one
explicit Start before automatic startup reads their saved Keychain entry.
Windows keeps random PFX passwords in Credential Manager, binds each credential
to the PFX content digest, removes verified legacy `.password` files, and loads
private keys with ephemeral key storage.

The Logs page on both platforms can switch between HerdMe's own diagnostics and
each discovered Laravel project's `storage/logs` directory. Opening Logs from a
site selects that project immediately, while Follow mode refreshes only when the
selected file changes.

Live Windows release probes resolve all installable managed services plus PHP 8.0-8.5,
Node.js 20/22/24/26, Composer, Laravel Installer, and Xdebug. The Xdebug probe
downloads every supported NTS x64 archive, verifies its official GitHub
SHA-256 digest, and extracts only its expected DLL.
New PHP installations on both platforms use the same 8.0-8.5 allowlist. An
older managed cycle remains visible and selectable only when its executables
already exist, and HerdMe does not offer installation or update actions for it.

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

Create local ZIP and DMG artifacts for testing. Local artifacts are ad-hoc
signed with hardened runtime enabled. Public mode requires a Developer ID
Application identity and a notarytool keychain profile, then signs, notarizes,
staples, assesses, and checksums the artifacts as documented in
`docs/RELEASING.md`.

```sh
./scripts/package-macos.sh Release
```

For repeated local installs, use a stable Apple Development identity so macOS
Keychain continues to recognize the application between builds:

```sh
HERDME_LOCAL_CODESIGN_IDENTITY="Apple Development: Developer Name (TEAMID)" \
./scripts/package-macos.sh Release
```

Ad-hoc signing remains the fallback for contributors without an Apple
Development identity. Because its code identity changes on every build, it is
not suitable for repeatedly installing the app against the same Keychain data.

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

Create the per-user Windows installer (it also builds the portable payload)
with Inno Setup 6 installed:

```powershell
.\Windows\package-installer.ps1 -Architecture x64 -Configuration Release
```

The `Windows x64` GitHub Actions workflow runs the same build, contract, XAML,
portable-package, installer install/uninstall, PE-architecture, checksum,
single-instance, listener, SMTP, and VarDumper gates on `windows-2022`, then
uploads the ZIP and Setup executable plus both `.sha256` files.
Live upstream runtime/service metadata and checksum probes run in the separate
scheduled `Upstream release sources` workflow so external outages do not make
pull-request builds flaky.

See [`Windows/README.md`](Windows/README.md) for the Windows prerequisites and
current milestone.

## License

HerdMe source code is available under the MIT License. Downloaded third-party
runtimes and tools retain their own licenses and are not relicensed by HerdMe;
see [`docs/THIRD_PARTY.md`](docs/THIRD_PARTY.md). HerdMe itself has no
activation, subscription, license-key, upgrade, or paid feature mechanism.
