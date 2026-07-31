# Changelog

All notable changes to HerdMe are documented in this file. The project follows
Semantic Versioning for public releases.

## [Unreleased]

## [0.1.4] - 2026-07-31

### Added

- Check installed PHP, Node.js/npm, Composer, Laravel Installer, Git, Xdebug,
  and managed service releases in the background, then show one localized
  notification for all available component updates.
- Allow manual update checks to cover HerdMe and every installed component,
  with direct update actions for managed Git and Xdebug releases.

### Fixed

- Keep component update results independent so one unavailable release source
  does not hide updates returned by the remaining sources.

## [0.1.3] - 2026-07-31

### Added

- Check for HerdMe updates once when the Windows application starts and notify
  the user with release notes and the correct x64 installer link.

### Fixed

- Report that the update service is unavailable when the remote feed cannot be
  reached instead of incorrectly claiming that the bundled version is current.
- Wait until the WinUI root is ready before showing a fast automatic update
  result, and suppress startup checks during onboarding and acceptance runs.

## [0.1.2] - 2026-07-31

### Fixed

- Keep an empty Mail inbox neutral and ignore WebView navigation cancellations
  caused by switching pages or replacing a preview.
- Keep Sites preview navigation cancellations out of diagnostics while still
  reporting real loading failures.
- Pin the Windows SDK reference version required by WinUI, WebView2, and the tray
  icon dependency so .NET 8 release builds remain reproducible.
- Use .NET 8-compatible explicit separator arrays and make the C# formatting
  gate work on its documented PowerShell 5.1 baseline.

## [0.1.1] - 2026-07-31

### Added

- Measure macOS application and production-domain line coverage from the Xcode
  result bundle, fail CI on aggregate or per-file regressions, and retain the
  machine-readable coverage report as a dedicated artifact.
- Add isolated macOS UI tests for the first-install wizard and every primary
  navigation destination in English and Arabic RTL, retaining screenshots
  without reading or changing the developer's real HerdMe settings. The hosted
  gate enables Developer Tools authorization explicitly, bounds execution time,
  and retains the complete Xcode result even when UI automation fails.
- Add a full per-site `.env` editor on macOS and Windows. It can start from
  `.env.example`, preserves external edits through revision checks, rejects
  unsafe links and oversized files, and writes updates atomically.
- Add complete Arabic localization and right-to-left layout coverage to the
  current macOS interface, backed by a CI catalog-extraction gate. Start native
  Windows MRT Core localization with paired English/Arabic resources for the
  tray, navigation, and first-run installer, plus culture-driven root RTL and
  contracts that reject missing or mismatched startup resources.
- Add an in-app Artisan runner on macOS and Windows with route-list, migration,
  queue-worker, and custom-command presets; stream bounded output, support
  cancellation and timeouts, invoke the selected managed PHP runtime without a
  shell, and keep Windows child processes hidden.
- Add an in-app npm script runner on macOS and Windows that discovers safe
  scripts from each site's `package.json`, uses the selected managed Node.js
  runtime, streams bounded output, supports cancellation and timeouts, and
  keeps Windows child processes hidden without invoking a shell.
- Show the local-environment state directly in the macOS menu-bar icon, with
  distinct running, stopped, port-conflict, and transition indicators plus an
  accessible status value.
- Complete About on both platforms with secure repository, documentation, and
  release-note links, copyable version details, and update checks that respect
  the configured release channel.
- Preserve the complete native Windows acceptance transcript and high-level
  HerdMe diagnostic logs, native MSBuild text log, and binary log as a dedicated
  CI artifact when the Windows gate fails, and retain the same MSBuild logs when
  the C# CodeQL build fails, while keeping successful portable and Setup
  packages separate.
- Add a macOS deep-diagnostics CI gate covering Xcode and network-helper static
  analysis, Address/Undefined Behavior Sanitizers for the application and Core,
  and ThreadSanitizer for the full Swift test suite, with complete Xcode result
  bundles retained whenever a sanitizer gate fails.
- Lint every GitHub workflow and shell entry point in the macOS gate, and make
  public Windows acceptance resolve and probe every live runtime and service
  release source instead of allowing a release-tag build to skip those checks.
- Adopt repository-wide formatting rules and strict CI gates for Swift, C/C++,
  and C#, with the current source formatted and verified against those rules.
- Exercise both build modes in pull-request CI: macOS runs its complete XCTest
  suite in Debug and Release, while Windows builds and tests Core, contracts,
  and WinUI in Debug before repeating native acceptance and packaging in Release.
- Register the fixed local-network daemon through `SMAppService`, with an
  isolated migration test covering both successful handoff and legacy rollback.
- Test the update-feed signing tools on every macOS CI run with a temporary
  P-256 key, including mandatory rejection of tampered payloads, signatures,
  algorithms, public keys, insecure downloads, and non-P-256 signing keys.
- Require the final GitHub Release directory to match an exact regular-file
  allowlist, rejecting missing, extra, directory, and symbolic-link entries
  before checksums, attestations, or publishing can run.
- Reject update manifests whose platform URLs do not end in the exact artifact
  names for that release, whose release identity is duplicated, or whose
  current repository release is not on the Stable channel.
- Validate every WinUI XAML surface in the Windows contract suite, including
  matching `x:Class` declarations, unique `x:Name` values, and event handlers
  that exist in the corresponding code-behind file.
- Add contributor guidance, a Code of Conduct, structured bug and feature forms,
  and a pull-request checklist for the open-source repository.
- Add repository-wide editor defaults and explicit ownership for workflows,
  signing, certificates, privileged routing, and update-sensitive source.
- Require every public release to originate from a GitHub-verified annotated
  tag signature that targets the exact commit being built, and keep checkout
  credentials out of every workflow job's Git configuration. A permanent CI
  contract also rejects mutable Action references and privileged PR triggers.

### Fixed

- Keep managed-service installations running across navigation and retain the
  service list when returning to the Services page.
- Write copied mail connection values directly to the selected site's `.env`
  file instead of only placing them on the clipboard.
- Ship the Windows application self-contained so Setup does not ask users to
  download .NET after installation.
- Put managed `npm.cmd` and `npx.cmd` shims ahead of the Node runtime in the
  user PATH so PowerShell never selects the execution-policy-blocked scripts.
- Add searchable Artisan command and npm script suggestions, discover every
  command registered by the selected Laravel project, and keep the Run and
  Cancel controls visible above bounded output.
- Statically link the MinGW C++ runtime into `herdme-core.exe` so a clean
  Windows installation does not fail because `libc++.dll` is missing.
- Move PHP, Node, Composer, Laravel Installer, and Xdebug refresh state into the
  injected macOS runtime coordinator, update installed/latest versions
  atomically after successful operations, and cover the coordinator contract
  with direct tests.
- Correct the architecture and third-party documentation to describe the
  bundled macOS core cross-check and the exact trust boundary delegated to
  Homebrew.
- Build the macOS DMG from an installation layout containing both the signed
  `HerdMe.app` and an `/Applications` shortcut, then mount the final image
  read-only and reject it if either entry is missing or incorrect.
- Centralize Homebrew discovery, environment construction, command execution,
  output normalization, cancellation, and formula-trust validation for macOS
  PHP and service installation so both paths use the same tested behavior.
- Report the macOS HTTPS state consistently after installation: a trusted
  certificate is green only while the HTTPS listener is active, and is orange
  when sites have fallen back to HTTP instead of showing a green "Unavailable"
  status beside the recovery alert.
- Derive the Windows first-run PHP and Node.js version summary from the shared
  runtime catalog, keeping onboarding aligned with the versions the installers
  actually provide.
- Keep first launch after a macOS app replacement quiet and accurate: stale
  local-domain checks can no longer overwrite a completed helper repair, the
  repair is shown as an in-progress operation, and an invalidated Keychain ACL
  falls back to an explicit HTTPS Enable action instead of a repeated red
  startup alert.
- Make the macOS local-environment engine and runtime installer injectable
  behind `LocalEnvironmentRunning` and `RuntimeInstalling`, with isolated tests
  covering environment lifecycle and PHP, Node.js, Composer, and Laravel
  Installer command routing.
- Move macOS DNS, certificate, privileged-operation, and first-run wizard state,
  plus resolver and certificate-manager ownership, into an independently
  observed `SecuritySetupCoordinator`.
- Move macOS local-environment process ownership, lifecycle inspection, status,
  and HTTP/HTTPS endpoint state into an independently observed
  `EnvironmentCoordinator`, while preserving the existing DNS and certificate
  startup order.
- Move the macOS site list, runtime-port map, project linking, and per-site
  PHP/Node selections into an independently observed `SitesCoordinator`, so
  site discovery and routing changes redraw only site-aware views.
- Move macOS PHP, Node.js, Composer, Laravel Installer, and Xdebug state into an
  independently observed `RuntimeCoordinator`; cancelled or shutdown-rejected
  runtime actions now always release their busy state.
- Move macOS mail and dump state, persistence, and listener lifecycles into
  independently observed `MailCoordinator` and `DumpsCoordinator` objects, so
  captured payloads no longer invalidate every view that observes the
  application model.
- Move macOS managed-service process ownership, credentials, update state, and
  operation state into an independently observed `ServicesCoordinator`; also
  guarantee that cancelled or shutdown-rejected actions release their busy
  state instead of leaving a service row disabled.
- Separate macOS navigation and site/log selection from the application-wide
  model so navigation changes no longer invalidate every model-dependent view.
- Preserve the user's macOS main-window size across launches and page changes;
  wider workspaces may grow the window when first opened, but navigation no
  longer shrinks it or resets its height.
- Add site link/path copying and contextual site actions on macOS and Windows,
  plus drag-and-drop registration for macOS site folders.
- Add explicit safe site removal on both platforms. Linked projects are only
  unlinked and keep their source directory; direct children of configured Park
  roots move to Trash on macOS or the Recycle Bin on Windows. Removal rejects
  external, nested, symbolic-link, junction, and reparse-point targets before
  refreshing the site list and local environment.
- Track macOS application work through one shutdown-aware task registry, cancel
  view-owned and detached operations when their owner disappears, propagate
  cancellation into Homebrew and Artisan child processes, and bound application
  termination while active work drains.
- Repair a stopped or outdated macOS local-network helper automatically at
  startup and during health checks, without opening System Settings when the
  background service still requires explicit approval.
- Include the helper executable, signed daemon manifest, and canonical
  application-bundle path in the `SMAppService` identity, forcing a clean
  re-registration after HerdMe moves from an installer directory into
  `/Applications` instead of leaving launchd tied to a temporary bundle.
- Validate every local-site and managed-runtime URL before use, reject insecure,
  credentialed, non-standard-port, or unexpected runtime-download origins, and
  replace force-unwrapped production URL/configuration/parser values with
  recoverable failures instead of a process crash.
- Monitor the macOS PHP-FPM, FastCGI, HTTP, and HTTPS listeners while sites are
  running, serialize health inspections with refresh transitions, keep mutable
  engine state on the main actor, reject stale inspection results, and recover
  a crashed automatic environment without restarting one the user stopped.
- Rework the macOS site detail surface around a persistent project header with
  framework and runtime metadata, direct browser, terminal, and Tinker actions,
  and a desktop-width live preview that remains usable at the minimum window
  width.
- Align macOS managed services with one shared grid definition for service,
  port, status, automatic startup, and actions, preventing row content from
  shifting the column headers as state changes.
- Make the existing-project branch of the macOS site wizard reachable, replace
  numeric step arithmetic with named navigation, and show the active wizard
  stage without exposing Laravel-only configuration for linked projects.
- Rename the Xdebug trigger-only setting to describe `XDEBUG_TRIGGER` behavior
  accurately while preserving compatibility with existing saved preferences.
- Preserve buffered HTTP requests across non-zero `Data` indices so browsers can
  reuse one connection for multiple static assets without hanging after the
  first response; dynamic FastCGI responses without a defined length still
  close safely.
- Harden macOS HTTP request framing with one shared strict parser for request
  completion and dispatch. Duplicate Host or Content-Length fields,
  Content-Length plus Transfer-Encoding, unsupported protocol versions, and
  unsupported transfer codings now fail closed before buffered bytes can be
  interpreted as another request. Valid chunked bodies and trailers are decoded
  before FastCGI dispatch and preserve a following pipelined request.
- Add bounded HTTP/1.1 keep-alive to the Windows site server, preserving
  sequential and pipelined requests and reusing connections for static or
  fixed-length FastCGI responses while closing unframed dynamic responses.
  Ambiguous Host, Content-Length, Transfer-Encoding, and protocol inputs now
  fail closed before a pipelined request can be interpreted.
- Serialize Windows environment shutdown with startup before cancelling the
  health monitor, so an older start cannot re-enable recovery and restart sites
  after the user explicitly stopped them.
- Route Windows environment startup/recovery, background-component,
  managed-service, and recoverable unhandled failures through one bounded JSONL
  diagnostic stream, with atomic deduplication isolated by support root and
  component even when callers report the same failure concurrently.
- Upgrade the immutable GitHub Actions pins used by build, analysis, artifact,
  release, and provenance jobs, and install XcodeGen through native Apple
  Silicon Homebrew during Swift CodeQL analysis instead of invoking it through
  the runner's Rosetta process.
- Preserve first-run setup diagnostics and expose them in a copyable disclosure,
  so failures installing trust, PHP, Composer, Laravel Installer, or Node.js can
  be diagnosed without rerunning HerdMe from a terminal.
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
  authentication UI disabled, including when Data Protection Keychain is not
  available, and defer HTTPS only when macOS blocks that read. This prevents
  hidden authorization prompts without leaving approved sites stuck on HTTP.
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

[Unreleased]: https://github.com/Hamad3bdulla/herdme/compare/v0.1.3...HEAD
[0.1.3]: https://github.com/Hamad3bdulla/herdme/releases/tag/v0.1.3
[0.1.2]: https://github.com/Hamad3bdulla/herdme/releases/tag/preview-0.1.2
[0.1.1]: https://github.com/Hamad3bdulla/herdme/releases/tag/v0.1.1
[0.1.0]: https://github.com/Hamad3bdulla/herdme/releases/tag/v0.1.0
