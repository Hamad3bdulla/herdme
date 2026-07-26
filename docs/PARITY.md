# HerdMe Parity Plan

HerdMe targets 99% behavioral and visual parity for the selected local
development workflow while remaining an independent implementation. Expose,
Shortcuts, Integrations, and Forge integration are intentionally outside the
scope. Activation, subscriptions, license keys, upgrade screens, and Pro gates
will not be implemented.

## Platform architecture

| Layer | macOS | Windows |
| --- | --- | --- |
| Native shell | SwiftUI | WinUI 3 |
| Domain contracts | Native Swift implementations checked against shared behavioral fixtures | C++20 core JSON CLI plus native C# adapters |
| Application data | `~/Library/Application Support/HerdMe` | `%LOCALAPPDATA%\HerdMe` |
| Process control | Foundation `Process` adapter | Win32 process and Job Object adapter |
| HTTP/HTTPS | Network.framework adapter | `TcpListener`, `SslStream`, and FastCGI adapters |
| PHP request engine | PHP-FPM and FastCGI gateway | Official `php-cgi.exe -b` and native FastCGI gateway |
| Local CA trust | Security.framework and Keychain | Windows Certificate Store |
| Local domains | HerdMe-owned loopback DNS and standard-port helper | Windows DNS/hosts platform adapter |
| Packaging | Local ZIP/DMG plus gated Developer ID/notarization mode | x64 portable ZIP and per-user Setup executable plus gated Authenticode mode |

Platform UI and privileged operations stay native. The portable core is the
Windows contract backend today; macOS implements the same selected behavior in
Swift. Shared fixtures are the compatibility gate for duplicated pure logic,
starting with Laravel PHP-extension inspection. More logic may move into the
core only behind a stable C ABI or versioned JSON contract.

## Parity matrix

| Area | macOS | Windows | Next milestone |
| --- | --- | --- | --- |
| Native shell and navigation | Implemented | Implemented | Verify WinUI rendering on Windows hardware |
| First-launch setup | Native staged wizard installs local domains, CA, PHP 8.4, Composer, Laravel Installer, and Node.js 22 | Equivalent native WinUI wizard with UAC and delayed background startup | Verify cancellation, retry, and completion on Windows hardware |
| Single-instance process guard | File lock implemented and process-tested | Named mutex and activation signal implemented | Verify existing-window activation on Windows hardware |
| Menu bar/system tray | Implemented | Implemented | Verify tray interactions on Windows hardware |
| Site scan and link | Search, desktop-width HTTP preview, persistent roots, arbitrary links, runtime editing, and safe unlink implemented | Search, fitted desktop-width live preview, persistent roots, arbitrary links, runtime editing, and unlink implemented | Verify picker, preview, and unlink flow on Windows hardware |
| Laravel project creation | Staged progress, persistent success/failure result, Boost, Git, managed Node.js preparation, npm install, and production Vite build implemented | The same staged flow with managed Composer and checksum-verified Node.js implemented | Verify React/Vite creation on Windows hardware |
| PHP runtime management | Functional | Official NTS x64 packages, checksum, and extension validation implemented | Verify installed binaries on Windows hardware |
| Laravel PHP extension validation | Implemented | Enforced by launch policy and PHP page | Verify against managed PHP on Windows |
| Node runtime management | Functional | Official ZIP packages and checksum validation implemented | Verify installed binaries on Windows hardware |
| Per-site PHP and Node selection | Implemented | Override files and per-PHP FastCGI routing implemented | Verify mixed-version sites on Windows hardware |
| HTTP host routing | GET, POST, streamed static GET/HEAD with byte ranges, front-controller, chunked requests, and symlink containment implemented | Equivalent bounded static streaming, single-range responses, and symlink/junction containment with final-handle verification implemented | Browser and junction test on Windows hardware |
| PHP FastCGI serving | PHP-FPM, progressively streamed and Laravel-tested | `php-cgi.exe -b`, progressively streamed and protocol-tested | Run Laravel 13 against official Windows PHP |
| HTTPS and local CA | Implemented | `SslStream` and independent Windows certificate-store CA implemented | Verify trust prompts on Windows hardware |
| Local DNS | Implemented | Isolated hosts block with backup and console-free self-elevating helper implemented | Verify elevation flow on Windows hardware |
| SMTP capture and inbox | MIME text/HTML preview implemented and tested | MIME text/HTML preview implemented and protocol-tested | Visual verification on Windows hardware |
| VarDumper capture | Implemented | Implemented and parser-tested | Visual verification on Windows hardware |
| Managed service lifecycle | MariaDB, MySQL, PostgreSQL, MongoDB, Redis, Valkey, Meilisearch, Typesense, MinIO, plus RustFS on arm64; automatic installation, update detection, current-version update hiding, automatic startup, storage consoles, process recovery, MySQL/MariaDB package-conflict recovery, and verified per-instance database authentication | MariaDB, MySQL, PostgreSQL, MongoDB, Redis, Meilisearch, MinIO, and RustFS implemented with verified packages, automatic installation/update, automatic startup, storage consoles, and equivalent database authentication/migration contracts. Valkey and Typesense remain disabled because upstream publishes no native Windows assets. | Run authentication migration and every service on Windows hardware; resolve vetted Valkey/Typesense builds |
| Logs | HerdMe and per-site Laravel logs with search and Follow mode implemented | Equivalent source picker, direct site navigation, search, and live refresh implemented | Visual verification on Windows hardware |
| Xdebug | Installed, configured, and FPM-tested | Isolated installer, settings, site selection, and trigger URL implemented | Connect and test with `php-cgi.exe` on Windows |
| Launch at login | Implemented | HKCU background startup and saved-site restore implemented | Verify sign-in behavior on Windows hardware |
| Updates and packaging | Stable/beta checks, separate platform artifacts, local packaging, and a Developer ID/notarization gate implemented | Stable/beta checks, separate platform artifacts, portable self-contained ZIP, per-user installer with automated install/uninstall acceptance, Authenticode gate, SHA-256 sidecars, and Windows x64 CI workflow implemented | Supply release certificates and feed URLs, run both public pipelines, then complete native acceptance |

## Delivery order

1. Validate the complete WinUI application, official PHP/Node packages, local
   certificate trust, UAC helper, and managed services on Windows hardware.
2. Provide reproducible, checksum-verified native Windows builds for Valkey and
   Typesense without substituting an untrusted third-party binary.
3. Run the cross-platform behavioral and visual parity checklist and close any
   differences within the selected 99% scope.
4. Run the implemented Developer ID/notarization and Authenticode modes with
   release credentials after the behavior target passes.

## 99% acceptance target

The percentage applies only to the selected scope above. It is measured from a
versioned checklist of user workflows, visible states, configuration results,
and cross-platform fixtures. A workflow counts only when it is implemented,
tested on its target platform, and does not read another application's private
runtime or configuration. Expose, Shortcuts, Integrations, Forge integration,
and all commercial licensing screens are excluded from both the numerator and
denominator.

## Laravel runtime contract

Dynamic PHP requests use FastCGI rather than FrankenPHP. macOS starts PHP-FPM;
Windows starts the official NTS `php-cgi.exe` because PHP-FPM is not distributed
for Windows. Each selected PHP executable is validated before its process starts. The required
module set is `ctype`, `curl`, `dom`, `fileinfo`, `filter`, `hash`, `mbstring`,
`openssl`, `pcre`, `pdo`, `session`, `tokenizer`, and `xml`. macOS integration
tests cover GET and POST requests, static files, front-controller fallback,
trusted HTTPS forwarding, the active PHP request limits, Xdebug loading, and
an actual Laravel 13 application. The Windows protocol fixtures cover the same
request contract, including chunked bodies, but the official Windows binaries,
certificate store, UAC flow, and WinUI rendering still require a Windows machine
before the 99% acceptance target can be declared complete.

## Current validation snapshot

On July 26, 2026, the macOS Swift 6 suite executed 151 tests: 149 passed with zero
failures, and the optional live Laravel-project and database-authentication tests were skipped in the default
run. Both optional tests were then enabled and passed: HerdMe copied and served
an existing Laravel application through PHP-FPM and trusted HTTPS, and an
isolated temporary instance of the installed MySQL runtime accepted the managed
credential while rejecting passwordless access. A separate live gate created Laravel 13.22.0 with the React starter kit
through HerdMe's managed installer, restored its npm dependencies, built and
verified its production Vite manifest, and served dynamic HTTP, a static asset,
and trusted-metadata HTTPS through PHP-FPM. The suite includes an installed PHP 8.4
module check for every Laravel 13 extension above, PHP-FPM HTTP/HTTPS requests,
Xdebug, DNS, certificates, mail MIME parsing, per-site runtimes, full installed
PHP version detection, application and PHP update selection, Homebrew service
update detection, service install/upgrade conflict recovery, service descriptors,
persisted service-process recovery with stale-PID rejection, and the
single-instance file lock.
The same suite also passed under AddressSanitizer plus Undefined
Behavior Sanitizer and, separately, under ThreadSanitizer. The portable Core
tests passed under AddressSanitizer plus Undefined Behavior Sanitizer, while
Xcode static analysis and Clang analysis of the network helper completed cleanly.
These checks now run in the macOS `deep-diagnostics` CI job.
Mail startup loads a bounded metadata index instead of decoding every stored
body, HTML preview, and raw payload; the selected message is loaded on demand.
Legacy captures rebuild the index automatically without changing message files.
PHP-FPM capacity now scales from 4 to 32 children using the machine's logical
processor count instead of applying the same fixed ceiling to every Mac.
The Sites UI now reports HTTPS only when its listener is actually active; a
trusted certificate without Keychain approval is shown as HTTP-only instead of
displaying a misleading lock. Opening a stopped or newly discovered site starts
or resynchronizes the local environment before launching the browser, and a
failed transition recovers from `Starting` to an actionable stopped/conflict
state instead of remaining stuck.
Swift, portable C++, and both Windows C# projects now treat compiler warnings as
errors. The final forced-crash decoder path in the macOS site preview was
replaced with a real initializer. A universal local Release package passed
signature, archive, disk-image, checksum, version, and architecture validation;
the exact app installed under `/Applications` then returned `200` for
`lllkkk.test` over HTTP and trusted HTTPS without an exposed port.
Project creation has real process-backed fixtures that verify its visible
Laravel, Boost, Node.js, npm, Vite, Git, and project-verification stages, the
resulting Git repository, and the generated Vite manifest. Incomplete Laravel
or frontend output is rejected before site registration. The
macOS and Windows creation views now keep the complete stage list and failure
detail visible until the user closes the result.
Custom starter kits accept a validated `vendor/package` Composer identifier and
use Laravel Installer's official `--using` option on both platforms.
The Logs page on both platforms discovers every Laravel project's `storage/logs`
directory, preserves direct navigation from the selected site, supports source
switching and search, and follows files without creating or modifying project
directories.
Every managed service exposes an `Add to .env` action on macOS and Windows. The
user selects a discovered site, and HerdMe creates `.env` from `.env.example`
when needed or updates the matching connection variables in place while
preserving comments and line endings. The contract suite verifies every catalog
service has a mapping and that repeated updates do not append duplicate keys.
Database, storage, and Typesense credentials are unique per service instance and
persist in Keychain on macOS or Windows Credential Manager. MySQL, MariaDB, and
PostgreSQL use that same credential in the engine, `.env`, and TablePlus.
PostgreSQL starts with SCRAM; older trust/passwordless clusters migrate with an
atomic marker and restore their access file if verification fails. A live macOS
gate starts installed MySQL and MariaDB builds and proves that managed login
succeeds and passwordless login fails.
Certificate secrets follow the same platform boundary: macOS prefers Data
Protection Keychain for its CA key and random identity password, falls back to
the login Keychain for ad-hoc builds missing the entitlement, and migrates the
fallback after a protected write succeeds. Windows migrates PFX passwords
to Credential Manager and imports private keys with ephemeral key storage.
The application also migrates legacy park-path references away from another
application's project and private data folders, rejects new roots, links, or
project creation under those paths, and ignores symbolic links that resolve
into them.
The Windows settings, creation flow, managed-core scanner, and returned-site
filter enforce the same boundary for the default project folder plus local and
roaming private application-data paths. The portable core fixture verifies that
other Herd roots and links are ignored while HerdMe-owned projects remain
scannable.
Apple Silicon tests and Intel builds pass. Native visual checks confirm real
HTTP content in the site preview, start/stop, Logs, the Dock/About icon,
Mail/Dumps listeners, and rejection of a forced second process. Certificate
trust now uses Security.framework in the user trust domain, and the
resolver/standard-port helper is registered through `SMAppService` without a
Terminal, Apple Events, or a generic privileged command runner. The local
network helper carries a stable HerdMe identifier, associates its launch service
with `app.herdme.desktop`, rolls back to a preserved legacy service if migration
cannot acquire every listener, detects installed-helper updates, and waits for
the modern launchd job to be running before the UI reports setup success. Live
acceptance still requires a notarized Developer ID build on a clean Mac.
Automatic listener and site
environment failures are written to `app.log`, while managed-service failures
are written to the service log, without blocking the main window.

A real conflicting-formula acceptance installed MySQL 9.7.1 while MariaDB
12.3.2 was already installed and linked. HerdMe temporarily unlinked MariaDB,
installed MySQL into its isolated service runtime, restored MariaDB's Homebrew
links, hid the update action for the current MySQL release, and started MySQL
successfully on `127.0.0.1:3306`.

Live visual acceptance also captured an HTML message through SMTP and a real
base64 PHP-serialized VarDumper payload. Service ports and the Xdebug IDE
endpoint now use locale-independent, ungrouped digits such as `9003` instead of
`9,003`.

The portable C++20 tests, expanded Windows C# service-contract fixtures, and all
13 WinUI XAML XML checks also pass on macOS. The C# contracts were rerun with a
locally isolated .NET 8.0.423 SDK and `TreatWarningsAsErrors`. The contract project now compiles
every non-UI Windows model and service and runs the local HTTP server against
streamed 2MB static GET/HEAD, single byte ranges with `206/416`, host isolation,
method rejection, and encoded traversal in
origin-form and absolute-form targets. It also cross-publishes as a
self-contained PE32+ x86-64 executable. Windows is explicitly x64-only, and its
contracts cover the selected-site Debug Session URL. Local macOS ZIP/DMG and
Windows portable ZIP and Setup scripts are present. A Windows x64 CI workflow
runs the native package gate, validates installation/removal, and uploads both
artifacts plus SHA-256 sidecars after the repository is pushed. A complete WinUI build cannot be produced on macOS: the Windows App
SDK executable compiler cannot run there, while its managed compiler ultimately
requires `kernel32.dll`. Final Windows execution, the first CI artifact, signing,
and the first signed release remain outstanding acceptance gates.

The optional live Windows release-source probe currently resolves and reaches
MariaDB 11.8.8, MySQL 9.7.1, PostgreSQL 18.4, MongoDB 8.0.28, Redis 8.8.1,
Meilisearch 1.50.0, MinIO `2025-09-07T16-13-09Z`, and RustFS
`1.0.0-beta.11-preview.1`, with the required published checksum for each. The
native acceptance script runs this probe before launching the packaged app.

The same gate resolves PHP 8.0-8.5 NTS x64, Node.js 20/22/24/26, Composer
2.10.2, Laravel Installer 5.31.0, and Xdebug 3.5.3. Xdebug no longer depends on
the legacy PECL TLS endpoint: HerdMe selects the matching archive from the
official Xdebug GitHub release, verifies the published SHA-256 digest, extracts
only the expected DLL, and rejects archive traversal entries.

The Windows service-contract fixtures use real loopback protocol sessions for
SMTP, VarDumper, and FastCGI. They verify persisted mail and dumps, PHP
serialization, FastCGI parameters and stdout/stderr, request-body chunking above
65,535 bytes, progressive stdout delivery before `END_REQUEST`, hop-by-hop header
removal, isolated hosts-file rendering, per-site PHP/Node overrides, and
stable/beta update selection without writing to `%LOCALAPPDATA%`. Redis for
Windows release selection, published SHA-256 validation, and its persistent
loopback-only launch contract are covered as well. MySQL contracts cover its
official CDN URL, vendor checksum, isolated initialization, loopback-only SQL
listener, and disabled MySQL X listener. PostgreSQL contracts cover the verified
EDB archive, `pgsql` layout normalization, isolated data directory, and
loopback-only launch.

## Open-source boundary

The MIT license covers HerdMe source code. Product names, logos, screenshots,
assets, binaries, certificates, and private implementation details from other
applications are not part of this repository. Third-party PHP, Node, Composer,
and service packages keep their upstream licenses.
