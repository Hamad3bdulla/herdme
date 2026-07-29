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
| Domain contracts | Native Swift implementations, shared behavioral fixtures, and the bundled C++20 JSON CLI as an independent PHP-extension cross-check | C++20 core JSON CLI plus native C# adapters |
| Application data | `~/Library/Application Support/HerdMe` | `%LOCALAPPDATA%\HerdMe` |
| Process control | Foundation `Process` adapter | Win32 process and Job Object adapter |
| HTTP/HTTPS | Network.framework adapter | `TcpListener`, `SslStream`, and FastCGI adapters |
| PHP request engine | PHP-FPM and FastCGI gateway | Official `php-cgi.exe -b` and native FastCGI gateway |
| Local CA trust | Security.framework and Keychain | Windows Certificate Store |
| Local domains | HerdMe-owned loopback DNS and standard-port helper | Windows DNS/hosts platform adapter |
| Packaging | Local ZIP/DMG plus gated Developer ID/notarization mode | x64 portable ZIP and per-user Setup executable plus gated Authenticode mode |

Platform UI and privileged operations stay native. The portable core is the
primary Windows contract backend and is also built, signed, and bundled on
macOS, where `PortableCoreClient` cross-checks the native PHP-extension report
through its versioned JSON protocol. macOS implements the remaining selected
domain behavior in Swift. Shared fixtures remain the compatibility gate for
duplicated pure logic; more logic may move into the core only behind a stable C
ABI or versioned JSON contract.

## Parity matrix

| Area | macOS | Windows | Next milestone |
| --- | --- | --- | --- |
| Native shell and navigation | Implemented | Implemented | Verify WinUI rendering on Windows hardware |
| Dashboard | Environment and HTTP/HTTPS state, site and service health, recent mail and dumps, warnings, and direct actions implemented | Equivalent localized WinUI dashboard implemented with the same live sources and actions | Verify English/Arabic rendering and actions on Windows hardware |
| First-launch setup | Native staged wizard installs local domains, CA, PHP 8.4, Composer, Laravel Installer, and Node.js 22 | Equivalent native WinUI wizard additionally installs verified MinGit and exposes all managed tools through the user PATH | Verify cancellation, retry, and completion on clean Windows hardware |
| Single-instance process guard | File lock implemented and process-tested | Named mutex and activation signal implemented | Verify existing-window activation on Windows hardware |
| Menu bar/system tray | Implemented | Implemented | Verify tray interactions on Windows hardware |
| Site scan, details, link, and removal | Search, desktop-width HTTP preview, persistent roots, arbitrary links, runtime editing, safe unlink, Trash-only removal for direct Park children, Git branch/cleanliness in the site list, and complete `.env`, log, route, Git, associated-service, and full-runtime details implemented | Equivalent localized WinUI flow, fitted preview, background cancellable Git status with stale-result rejection, Recycle-Bin removal, and complete site details implemented | Verify picker, details, preview, Git row layout, unlink, and Recycle Bin flow on Windows hardware |
| Laravel project creation | Staged progress, persistent success/failure result, Boost, Git, managed Node.js preparation, npm install, and production Vite build implemented | The same staged flow with managed Composer, checksum-verified Node.js, and managed Git implemented | Verify React/Vite creation on clean Windows hardware |
| PHP runtime management | Functional | Official NTS x64 packages, checksum, and extension validation implemented | Verify installed binaries on Windows hardware |
| Laravel PHP extension validation | Implemented | Enforced by launch policy and PHP page | Verify against managed PHP on Windows |
| Node runtime management | Functional | Official ZIP packages and checksum validation implemented | Verify installed binaries on Windows hardware |
| Per-site PHP and Node selection | Implemented | Override files and per-PHP FastCGI routing implemented | Verify mixed-version sites on Windows hardware |
| In-app Artisan runner | Route list, migration status, migrate, queue worker, and validated custom commands run through managed PHP with streamed bounded output, cancellation, and timeouts | Equivalent WinUI flow uses direct argument passing and hidden child processes with the same presets and limits | Compile and execute the native WinUI flow on Windows x64 |
| In-app npm script runner | Discovers safe scripts from `package.json` and runs them through the site's selected or active managed Node.js runtime with bounded streaming output, cancellation, and timeouts; a live Node.js 22.23.1 invocation passed | Equivalent WinUI flow invokes the managed `npm-cli.js` through `node.exe` with direct arguments and a hidden child process | Compile and execute the native WinUI flow on Windows x64 |
| HTTP host routing | GET, POST, streamed static GET/HEAD with byte ranges, bounded HTTP/1.1 keep-alive, strict anti-desynchronization framing, chunked bodies/trailers, pipelined FastCGI/static requests, front-controller routing, and symlink containment implemented | Equivalent bounded keep-alive, strict framing, sequential and pipelined request preservation, chunked FastCGI requests, static streaming, single-range responses, and symlink/junction containment with final-handle verification implemented | Browser persistent-connection and junction acceptance on Windows hardware |
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

On July 28, 2026, the latest completed macOS Swift 6 Debug XCTest snapshot
covered 234 tests: 232 passed with zero failures, and the optional
live Laravel-project and database-authentication tests were skipped in the
default runs. Both optional tests were also enabled separately and passed:
HerdMe copied and served
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
The latest Laravel rerun uses `scripts/test-live-laravel.sh`, which rejects
zero-test Xcode results and requires the exact Laravel case to report `Passed`
inside `xcresult`. It executed 229 tests: 228 passed, the unrelated opt-in MySQL
gate skipped, and none failed.
The same current 234-test snapshot passed through the supported `xcodebuild
test` path in optimized Release and under AddressSanitizer plus Undefined
Behavior Sanitizer and ThreadSanitizer: 232 tests passed, the two optional live
tests were skipped, and none failed in each run. No sanitizer reported a memory
error, undefined behavior, or data race. The environment
lifecycle fixture now writes real line separators, so both lifecycle tests execute
their intended process protocol instead of a literal `\\n` payload. The legacy
LaunchDaemon update fixture explicitly injects an unregistered service state,
so its result no longer depends on whether the test Mac has HerdMe's modern
`SMAppService` installed. The portable Core
tests passed under AddressSanitizer plus Undefined Behavior Sanitizer, while
Xcode static analysis and Clang analysis of the network helper completed cleanly.
Six libFuzzer targets now cover DNS labels, PHP module parsing, JSON, MIME,
PHP serialization, and FastCGI. The three portable-Core targets processed
10,875,438 inputs in the first campaign. Its only LeakSanitizer report was a
56-byte allocation owned by libFuzzer 22's RSS-monitor thread, so all three were
rerun with that monitor disabled and LeakSanitizer still active; the clean run
processed 3,385,523 more inputs without a leak, crash, ASan finding, or UBSan
finding. The three Swift targets processed 6,249,070 inputs without a crash,
ASan finding, or UBSan finding using the coverage mode supported by the local
Apple toolchain. LeakSanitizer is unsupported for those mixed Swift/Apple-runtime
binaries and was disabled only for that local Swift run. Hosted Linux and macOS
fuzz workflow evidence remains pending.
These checks now run in the macOS `deep-diagnostics` CI job.
The workflow retains complete ASan/UBSan and TSan `xcresult` bundles on failure.
The native Windows build likewise writes both a diagnostic MSBuild log and a
binary log, surfaces the final structured compiler errors in the CI annotation,
and uploads those files with the acceptance transcript or the failing C#
CodeQL job. Runtime startup,
automatic-recovery, background-component, managed-service, preview, log-source,
and recoverable WinUI failures now converge on bounded `diagnostics.jsonl`
entries with atomic per-root/per-component deduplication; raw child-process
stdout/stderr remains in its service log. The cross-platform contracts cover
concurrent writers and every structured field, while native WinUI fault
injection remains an explicit Windows acceptance item. All GitHub-owned build,
analysis, artifact, release, and provenance actions are pinned to their current
immutable SHAs. Swift CodeQL installs XcodeGen through native arm64 Homebrew so
the GitHub runner does not attempt that installation under Rosetta. These CI
changes are locally syntax-checked but still require the first post-push run
before their hosted-runner results can be treated as acceptance evidence.
The Windows environment lifecycle now serializes Stop behind an in-flight
Start, reasserts that recovery is disabled after acquiring the lifecycle lock,
and cancels any health monitor created by the older Start. This closes the
source-level Start/Stop race, but the immediate-Stop scenario remains explicitly
unverified until it passes the native Windows checklist. The cross-platform
contract suite also parses every WinUI XAML surface and enforces matching
`x:Class`, unique per-surface `x:Name` values, and existing code-behind event
handlers, so those contracts no longer depend on a one-off local inspection.
Mail startup loads a bounded metadata index instead of decoding every stored
body, HTML preview, and raw payload; the selected message is loaded on demand.
Legacy captures rebuild the index automatically without changing message files.
PHP-FPM capacity now scales from 4 to 32 children using the machine's logical
processor count instead of applying the same fixed ceiling to every Mac.
The Sites UI now reports HTTPS only when its listener is actually active; a
trusted certificate without Keychain approval is shown as HTTP-only instead of
displaying a misleading lock. General likewise uses green only for an active
HTTPS listener and orange for a trusted certificate whose listener is
unavailable. Opening a stopped or newly discovered site starts
or resynchronizes the local environment before launching the browser, and a
failed transition recovers from `Starting` to an actionable stopped/conflict
state instead of remaining stuck. The current source also checks PHP-FPM,
FastCGI, HTTP, and HTTPS health every five seconds while the environment is
running and recovers an automatically started environment after a component
dies. Mutable engine state is inspected on the main actor, only the bounded
port-owner query runs detached, and a result is discarded when a newer
transition has changed the environment state. After this concurrency change,
the fresh arm64 Debug and Release gates compiled with warnings as errors and
each executed all 225 tests: 223 passed, the two live opt-in tests were skipped,
and none failed. The
installed candidate separately recovered PHP-FPM after its process was killed
and restored all four trusted, portless sites to HTTP `200` within two seconds.
All 77 application Swift files and the complete 79-file application/test tree
also passed Swift 6 type checking, and all 79 files parsed. The newer Artisan
runner and helper bundle-relocation cases are included in both current XCTest
runs. Current-source Xcode static analysis and Clang analysis of the network
helper completed without diagnostics, and the current portable Core tests passed
under AddressSanitizer plus Undefined Behavior Sanitizer. A current-source arm64
Release application build separately completed with warnings as errors, a valid
Apple Development signature, and Hardened Runtime enabled.
The in-app Artisan implementation is included in that Release build. A direct
managed-PHP invocation returned Laravel's four application routes from an
installed project. The Windows contract project includes the same parser and
runner and asserts hidden, no-shell process creation, while native compilation
and execution of its WinUI surface still require Windows x64.
The npm script runner is also implemented on both platforms. Its current macOS
tests cover bounded `package.json` parsing, safe script names, direct argument
passing, cancellation, and numeric managed-Node selection. A live invocation
through HerdMe's managed Node.js 22.23.1 and its bundled `npm-cli.js` completed
successfully; native WinUI execution remains a Windows x64 acceptance item.
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
The first-run setup views on both platforms also preserve the underlying
diagnostic details after a failed trust, runtime, or tool installation and make
those details selectable and copyable before retrying.
About on both platforms exposes the same HTTPS repository, documentation, and
release-note destinations, copies the installed version and build, and checks
the signed update feed using the user's persisted Stable or Beta channel.
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
Both Sites views also expose a full `.env` editor. It bounds UTF-8 input, rejects
symbolic links and Windows reparse points, writes atomically, and compares a
SHA-256 revision before saving so an external edit is never overwritten.
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
the modern launchd job to be running before the UI reports setup success. Its
recorded identity now covers the helper contents, daemon manifest, and canonical
application-bundle path, so moving HerdMe out of an installer directory forces
re-registration. Before writing routing state or touching `SMAppService`, HerdMe
now resolves the application path and requires its `.app` bundle to be under
`/Applications`. A localized message instructs users who launch from a DMG or
build folder to move and reopen the application; regression tests accept real
`/Applications` descendants and reject `/Volumes`, lookalike directories, and
non-app paths without registering the service. The installed candidate exposed the original failure by running
the helper from `/private/tmp`; its one-click update waited for asynchronous
unregistration, retried registration until launchd accepted it, relaunched the
helper from `/Applications/HerdMe.app`, hid the Update button, and kept four
Laravel sites returning trusted HTTP `200` responses on portless HTTPS URLs. A
reboot check with a newly rebuilt current-source candidate remains pending. Live
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

The macOS HTTP/FastCGI socket gate covers streamed 2MB files and byte ranges,
progressive FastCGI output, bounded persistent and pipelined connections,
duplicate or conflicting framing rejection, required and unique HTTP/1.1 Host,
unsupported transfer coding and HTTP version responses, terminal EOF without a
smuggled response, and a decoded chunked POST with trailers followed by a static
request on the same connection. All six related XCTest contracts pass with
Swift warnings treated as errors.

The portable C++20 tests, expanded Windows C# service-contract fixtures, and all
13 WinUI XAML XML checks also pass on macOS. The C# contracts were rerun with a
locally isolated .NET 8.0.423 SDK and `TreatWarningsAsErrors`. The contract project now compiles
every non-UI Windows model and service and runs the local HTTP server against
streamed 2MB static GET/HEAD, single byte ranges with `206/416`, bounded
HTTP/1.1 keep-alive, sequential and pipelined requests, fixed-length FastCGI
followed by a static request on the same connection, rejection of ambiguous
Host/Content-Length/Transfer-Encoding framing, host isolation, method rejection, and encoded traversal in
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
Meilisearch 1.51.0, MinIO `2025-09-07T16-13-09Z`, and RustFS
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
removal, safe connection closure for unframed FastCGI output, isolated hosts-file rendering, per-site PHP/Node overrides, and
stable/beta update selection without writing to `%LOCALAPPDATA%`. Redis for
Windows release selection, published SHA-256 validation, and its persistent
loopback-only launch contract are covered as well. MySQL contracts cover its
official CDN URL, vendor checksum, isolated initialization, loopback-only SQL
listener, and disabled MySQL X listener. PostgreSQL contracts cover the verified
EDB archive, `pgsql` layout normalization, isolated data directory, and
loopback-only launch.

## Publication gate snapshot

This table is the authoritative stop/go snapshot as of July 28, 2026. A local
green result does not substitute for a native or hosted gate in the same row.

| Gate | Current evidence | State |
|---|---|---|
| macOS code quality | The current arm64 Debug, optimized Release, ASan/UBSan, and TSan runs each executed 234 unit tests through `xcodebuild`: 232 passed, two live opt-in tests skipped, and zero failed. Sanitizers reported no memory, undefined-behavior, or data-race defects. Xcode static analysis and the ASan/UBSan portable-Core tests also passed. Six fuzz targets processed 14,260,961 Core inputs across the original and clean rerun plus 6,249,070 Swift inputs without a HerdMe crash or sanitizer finding; hosted fuzzing remains required. Coverage passed at 39.87% for the application and 70.27% for services/models. A separate isolated XCUITest target is implemented for the fresh-installation wizard, all eleven primary pages in English and Arabic, and RTL placement while retaining three screenshots; its current local run built successfully but timed out while macOS enabled automation mode before any UI test executed. Unit and UI tests now use separate generated Xcode schemes, and CI enables `DevToolsSecurity`, runs UI tests separately with bounded time, and retains the complete Xcode result. Hosted `xcodebuild`, sanitizer, and UI evidence remain required. Current CTest, the network-helper dynamic test, workflow and shell lint, localization, update-feed tamper, release-tag, and exact-asset contracts also pass | Partial; passing hosted UI/Xcode and fuzz evidence pending |
| Portable Core and Windows contracts | Both current CTest cases rebuilt and passed normally and under ASan/UBSan, then the current .NET 8.0.423 Release contract executable passed against that real Core binary with zero compiler warnings. The former 4,359-line runner is now a 79-line entry point plus seven focused contract files; official `dotnet run`, direct DLL execution, both live-release flags, the quoted npm fixture, and PHP `-m` simulation all pass after the split. The contract discovers all 13 WinUI XAML files, enforces XAML/code-behind consistency, verifies 555 matching English/Arabic resources plus all 229 XAML localization identifiers, and covers the localized dashboard and complete site details. The self-elevating hosts helper now accepts only a bounded regular UTF-8 staging file, locks the destination, revalidates that no unrelated line changed, and writes the verified text; ZIP extraction also rejects reserved Windows device names in addition to traversal, links, duplicates, and expansion limits. `Windows/check-format.ps1` rejects hidden MSBuild/reference-loading failures and zero-file formatting runs; it inspected all 68 contract-source files locally, while full WinUI formatting still requires native Windows tooling. The current live probes also resolved and validated MariaDB 11.8.8, MySQL 9.7.1, PostgreSQL 18.4, MongoDB 8.0.28, Redis 8.8.1, Meilisearch 1.51.0, MinIO, RustFS, PHP 8.0-8.5, Node.js 20-26, Composer 2.10.2, Laravel Installer 5.31.0, and Xdebug 3.5.3 | Partial; native Windows build and acceptance pending |
| Local macOS candidate | The latest current-source Universal Apple Development DMG passed strict signature, architecture, ZIP, disk-image, checksum, mounted-layout, localization, and matching-TeamIdentifier checks. It contains the signed `HerdMe.app`, `/Applications` shortcut, and Universal signed `herdme-core` plus network helper. The previous installed candidate passed byte-identity and single-instance checks, visual inspection found no black window or incoherent overlap, and all five configured sites returned `200` through the internal HTTP 8080 and HTTPS 8443 listeners. The newest candidate has not replaced the installed app automatically. The standard-port service is currently `requiresApproval`, so 53/80/443 and port-free URLs must be retested after the user enables HerdMe in System Settings > General > Login Items. Gatekeeper/public distribution and clean-machine acceptance still require a notarized Developer ID build | Partial; install newest candidate, Login Item approval, reboot, and notarized clean-machine acceptance pending |
| Hosted CI | The latest `master` runs still target `b22c39ba`. The regular macOS build/test/package job passed, while deep diagnostics failed in TSan; Swift CodeQL failed because Homebrew ran under Rosetta, and C# CodeQL plus native Windows acceptance failed in the WinUI build. The local Swift runner fix matches GitHub's exact annotation, and the local C#/Windows scripts now surface and retain MSBuild diagnostics, but none of these changes has been pushed or rerun | Pending |
| Native Windows x64 | `Windows/acceptance.ps1` now verifies that Setup does not opt the user into launch-at-login, that the application-owned entry uses hidden background mode, and that uninstall removes it, in addition to install/core/single-instance/listener checks. It also selects all eleven navigation items with Windows UI Automation and waits for each page root while asserting the process remains alive. The suite has not yet completed on real Windows hardware and no portable ZIP or Setup artifact has passed native installation and removal | Pending |
| Public signing | Windows signing configuration and PowerShell syntax were exercised with PowerShell 7.6.4. The CI update contract now signs with both shell and PowerShell implementations and cross-verifies their envelopes and derived public key. Public mode selects exactly one current code-signing certificate with a private key, removes the temporary PFX immediately, requires an HTTPS RFC 3161 endpoint and a present timestamp certificate on every signed artifact, then removes the imported leaf certificate after the job. The local Mac build still uses Apple Development, not Developer ID; notarization/stapling and real Authenticode verification still require release credentials | Pending |
| Clean-machine acceptance | A notarized Mac candidate and signed Windows candidate have not yet completed the clean-profile checklists | Pending |
| Hosted release and update feed | The repository has no tags and no GitHub Releases; `release-manifest.signed.json` therefore returns HTTP 404 | Pending |

The public Windows release job now runs the live runtime and managed-service
release probes instead of passing `-SkipLiveReleaseChecks`, and
`Windows/acceptance.ps1` rejects that switch whenever
`HERDME_RELEASE_MODE=public`. Pull-request acceptance remains fixture-only for
repeatability, while the nightly workflow independently runs the same live
upstream probes.

HerdMe must not be described as 100% publication-ready until every Pending row
above has direct evidence and is changed to Pass.

The local release workflow now rejects lightweight tags, unverified annotated
tag signatures, indirect tag targets, and any signed tag whose commit differs
from the source being built before exposing signing secrets. A `refs/tags/v*`
repository ruleset and the first successful signed hosted release remain
external evidence, not local completion.

## Open-source boundary

The MIT license covers HerdMe source code. Product names, logos, screenshots,
assets, binaries, certificates, and private implementation details from other
applications are not part of this repository. Third-party PHP, Node, Composer,
and service packages keep their upstream licenses.
