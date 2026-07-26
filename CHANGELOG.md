# Changelog

All notable changes to HerdMe are documented in this file. The project follows
Semantic Versioning for public releases.

## [Unreleased]

### Added

- Preserve the complete native Windows acceptance transcript and high-level
  HerdMe diagnostic logs as a dedicated CI artifact when the Windows gate
  fails, while keeping successful portable and Setup packages separate.
- Add a macOS deep-diagnostics CI gate covering Xcode and network-helper static
  analysis, Address/Undefined Behavior Sanitizers for the application and Core,
  and ThreadSanitizer for the full Swift test suite.
- Register the fixed local-network daemon through `SMAppService`, with an
  isolated migration test covering both successful handoff and legacy rollback.

### Fixed

- Compare application and runtime releases using semantic-version precedence on
  both platforms, so stable releases outrank prereleases, numeric prerelease
  identifiers sort correctly, build metadata is ignored, and current versions
  do not expose a false update action.
- Run certificate and local-domain authorization work away from the main thread,
  keeping the setup UI responsive while native approval is in progress.
- Use `_dupenv_s` and explicit ASCII case conversion in the portable core on
  Windows, keeping the MSVC `/W4 /WX` build free of CRT and narrowing warnings.
- Serialize HTTP proxy and FastCGI gateway session teardown so an environment
  restart cannot mutate an active-session dictionary concurrently and crash.
- Require recorded HTTPS approval even when the local CA remains trusted. During
  automatic startup, read an already-approved legacy login Keychain entry with
  authentication UI disabled and defer HTTPS only when macOS blocks that read,
  preventing hidden Keychain authorization prompts from delaying site startup.
- Regenerate the local HTTPS identity whenever the exact registered-domain set
  changes, so newly created sites are immediately included in the certificate's
  subject alternative names after the environment restarts.
- Parse the trusted local certificate authority from either PEM or DER data when
  checking macOS trust, keeping HTTPS enabled after a normal application restart.
- Reuse the HTTPS identity already approved during an explicit Enable action,
  so the immediate environment restart cannot fail on a second background
  Keychain read.
- Start configured services and wait for readiness before the site environment,
  preventing database-backed Laravel sites from returning transient HTTP 500
  responses immediately after HerdMe launches.
- Start local sites and managed services before remote runtime and package
  update checks, avoiding multi-minute startup delays when update endpoints are
  slow.
- Support stable Apple Development signing for repeated local macOS package
  installs through `HERDME_LOCAL_CODESIGN_IDENTITY`.
- Never block automatic site startup on a Keychain approval dialog. Sites stay
  available over HTTP and HTTPS approval is deferred to an explicit Enable
  action in General settings; automatic startup does not read Keychain before
  that approval has completed once.
- Defer automatic startup of credential-protected services from older builds
  until the user starts each service once, preventing background Keychain
  dialogs while services without credentials continue to start normally.
- Keep folder pickers non-blocking, preserve pending top-level-domain edits when
  leaving General settings, and provide predictable keyboard focus and default
  actions in the site-creation wizard.
- Present recoverable application errors as non-blocking banners with copyable
  technical details, and do not treat the expected HTTP-only startup state as
  an error before HTTPS receives explicit approval.
- Load the macOS mail inbox from a bounded metadata index and read full message
  bodies only when selected; legacy captures migrate automatically.
- Reuse the injected application log store in the Logs view instead of creating
  a replacement during each refresh.
- Scale the PHP-FPM worker ceiling with available logical processors while
  enforcing a bounded 4-to-32 child range.
- Remove the deprecated generic privileged-command runner. Certificate trust
  now uses Security.framework in the user domain, while network-helper migration
  preserves and restores the legacy launchd service if listener acquisition or
  privilege dropping fails.

## [0.1.0] - 2026-07-26

### Added

- Native macOS and Windows applications for local Laravel development.
- First-run setup for local domains, certificate trust, PHP, Composer, Laravel
  Installer, and Node.js.
- Managed PHP and Node.js versions with per-site overrides and Laravel extension
  validation.
- Laravel project creation with starter kits, optional Boost and Git, and managed
  frontend builds.
- Managed database, cache, search, storage, and realtime services with automatic
  installation, lifecycle controls, update detection, and Laravel `.env` export.
- HTTP/HTTPS site routing, mail capture, VarDumper capture, Xdebug, site previews,
  and per-site Laravel log following.
- Signed update-manifest verification and gated release signing workflows for
  Developer ID/notarization and Authenticode.
- Windows per-user Setup packaging with Start Menu integration, clean uninstall,
  SHA-256 sidecars, and an automated install/core-health/uninstall acceptance gate.
- Managed-service startup waits for a real loopback listener and terminates
  processes that exit or time out before becoming ready.
- Corrupt macOS and Windows settings are preserved as named backups and surfaced
  to the user instead of being silently overwritten by defaults.
- Application, site, PHP, Homebrew, and managed-service logs rotate at 10MB,
  while captured mail and dumps use bounded count and age retention.
- Windows site routing synchronizes live domain changes, recovers crashed PHP
  and HTTP processes, and reuses valid HTTPS certificates across restarts.

### Fixed

- Ad-hoc macOS builds fall back to the login Keychain when Data Protection
  Keychain entitlements are unavailable, so automatic HTTP, HTTPS, and PHP-FPM
  startup remains functional while signed builds retain protected storage.
- Site preview decoding no longer contains a forced-crash initializer.
- Swift, portable C++, and Windows C# builds now reject project warnings.

### Security

- Checksum verification for downloaded runtimes, tools, debugger packages, and
  native Windows service archives.
- Loopback-only listeners, bounded parsers and request bodies, safe archive
  extraction, process timeouts, and single-instance enforcement.
- Bounded tar and ZIP inspection rejects traversal, escaping links, special
  entries, duplicate paths, and oversized runtime archives before installation.
- Unique 256-bit credentials for managed storage and Typesense instances, stored
  in macOS Keychain or Windows Credential Manager and shared with `.env` export.

[Unreleased]: https://github.com/Hamad3bdulla/herdme/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/Hamad3bdulla/herdme/releases/tag/v0.1.0
