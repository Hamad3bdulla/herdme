# Windows x64 Acceptance Checklist

Run this checklist on Windows 10 2004 or newer using an x64 machine. A workflow
counts toward the 99% target only after its checkbox is completed on real
Windows hardware.

## Automated gate

- [ ] Run `Windows\acceptance.ps1 -Configuration Release -LeaveRunning` from
  PowerShell at the repository root.
- [ ] Confirm the script builds Core and WinUI, runs Core and C# contracts,
  validates every XAML document, resolves and probes every live service and
  runtime package source, verifies Xdebug downloads and extraction against
  their published SHA-256 digests, verifies the portable ZIP and its SHA-256
  sidecar, builds the Setup executable, verifies its SHA-256 sidecar, performs
  an isolated silent install/core-health/uninstall cycle, requires the app-local
  Windows App Runtime and WinUI DLLs, starts the app, rejects a second process, and observes loopback
  listeners on ports 2525 and 9912. It must also reject non-loopback capture
  bindings and complete live SMTP and VarDumper exchanges against the running
  app without leaving acceptance records behind.

## Native shell

- [ ] On a clean Windows user profile, launch HerdMe and confirm the welcome
  wizard appears before navigation and does not begin installing until `Set up
  HerdMe` is pressed.
- [ ] Run the wizard and confirm it shows local domains, HTTPS certificate, PHP
  8.4 plus Laravel extension validation, Composer/Laravel Installer, Node.js 22,
  and finishing in order. Approve the native UAC/trust prompts and confirm no
  Command Prompt or PowerShell window appears.
- [ ] Cancel one approval or disconnect the network, confirm the exact failed
  step stays visible, then restore the prerequisite and retry successfully.
- [ ] After success, restart HerdMe and confirm the wizard does not return.
  Also open an installation upgraded from an older settings file and confirm it
  goes directly to the normal application.
- [ ] Verify General, Sites, PHP, Node, Services, Mail, Dumps, Debugger, Logs,
  and About render without clipping at 100%, 125%, and 150% display scaling.
- [ ] Close the window, reopen it from the tray, and quit from the tray.
- [ ] Launch a second copy and confirm the existing window comes to the front.
- [ ] Enable launch at sign-in, sign out and back in, and confirm HerdMe starts
  in the background without a console window.
- [ ] Cause a recoverable page operation to throw in a Debug build and confirm
  HerdMe remains open, shows one error dialog, and appends the exception to
  `%LOCALAPPDATA%\HerdMe\Log\unhandled.log` without opening duplicate dialogs.

## Sites and runtimes

- [ ] Add a sites folder and a direct project link, then verify search and safe
  unlink without deleting the project.
- [ ] Try to add, link, and create a project inside `%USERPROFILE%\Herd` and
  `%LOCALAPPDATA%\Herd`; confirm every operation is rejected while an adjacent
  `HerdMe` folder remains accepted.
- [ ] Install PHP 8.4 and confirm all 13 Laravel-required extensions are shown
  as loaded before the runtime becomes selectable.
- [ ] Install Node.js, Composer, and Laravel Installer and confirm current
  releases do not show an update action.
- [ ] Create a Laravel 13 project and serve static files, GET, POST, a routed
  controller URL, and a request body larger than 65,535 bytes.
- [ ] Download an asset larger than 2MB, seek within it using a browser or
  `Range: bytes=...`, and confirm `206`, `Content-Range`, `HEAD`, and `416`
  behavior without truncation.
- [ ] Create a junction inside a disposable site's `public` directory that
  targets a folder outside the site, request a file through it, and confirm
  HerdMe returns `403` without exposing the file contents.
- [ ] During Laravel creation, confirm validation, Laravel Installer, project,
  optional Boost/Git, verification, registration, and completion are shown;
  force a validation error and confirm its detail remains visible until closed.
- [ ] While `laravel new` or `npm install` is running, press `Cancel` and repeat
  by navigating away from Sites. Confirm the full command tree exits, no final
  project folder appears, and no `.herdme-create-*` staging folder remains in
  the selected parent directory.
- [ ] Assign different PHP and Node versions to two sites and verify both run
  simultaneously with the selected versions.
- [ ] With the site environment running, terminate one managed `php-cgi.exe`
  process in Task Manager and confirm HerdMe reports recovery, restarts the PHP
  pool and HTTP/HTTPS listeners, and serves the sites again without user action.
- [ ] Confirm the site thumbnail uses a desktop-width viewport and HTTPS opens
  the selected site in the default browser.
- [ ] Stop the selected site or make WebView2 unavailable, confirm the preview
  shows a compact retry state instead of a blank panel, and confirm the reason
  is written once to `%LOCALAPPDATA%\HerdMe\Log\diagnostics.jsonl`.

## Trust and local domains

- [ ] Install the HerdMe certificate authority and confirm the Windows trust
  prompt completes without opening a terminal or console window.
- [ ] Upgrade a profile that contains `Certificates\authority.password` and
  `Certificates\server.password`; start HTTPS, confirm the CA thumbprint stays
  unchanged, both plaintext files are removed, and the corresponding
  `HerdMe/Certificates/v1/...` entries exist in Windows Credential Manager.
- [ ] Restart HerdMe after that migration and confirm HTTPS still serves the
  site and no `.password` file is recreated. Repeat with Credential Manager
  write access denied and confirm the plaintext source remains for recovery.
- [ ] Install local-domain hosts entries through the UAC helper, then verify
  `.test` sites resolve while unrelated hosts-file entries remain unchanged.
- [ ] Remove HerdMe domain entries and verify the backup and unrelated entries
  remain intact.

## Developer tools

- [ ] Send plain-text and multipart HTML mail to port 2525 and inspect both
  views; confirm scripts and external network loads are blocked.
- [ ] Send a Symfony VarDumper payload to port 9912 and verify persistence,
  ordering, font size, and clear behavior.
- [ ] Install and enable Xdebug, trigger a selected site, and confirm the IDE
  receives the connection using the configured port and IDE key.
- [ ] Open site, PHP, service, Xdebug, startup, and application logs and confirm
  live refresh and search work.

## Managed services and packaging

The disabled native-service entries must satisfy every gate in
[`docs/WINDOWS_NATIVE_SERVICE_AUDIT.md`](../docs/WINDOWS_NATIVE_SERVICE_AUDIT.md)
before they can be marked installable.

- [ ] Add MariaDB, MySQL, PostgreSQL, MongoDB, Redis, Meilisearch, MinIO, and
  RustFS; confirm each runtime is
  downloaded and installed automatically with checksum verification.
- [ ] Add Valkey and Typesense after HerdMe provides reproducible native x64
  packages; their official upstream releases currently publish no Windows
  executable, so this item must not be marked complete using an unverified
  third-party binary.
- [ ] Confirm Valkey and Typesense remain visible in the service picker, show
  the official-package availability reason, and keep the Add action disabled.
- [ ] Open MinIO and RustFS consoles and confirm both API and console listeners
  bind only to loopback addresses.
- [ ] Start and stop every service, verify loopback-only binding and isolated
  data directories, then enable automatic startup and restart HerdMe.
- [ ] With an automatically started service running, terminate HerdMe from Task
  Manager, confirm the Job Object removes the child service process, then reopen
  HerdMe and confirm the configured service starts again.
- [ ] Start MySQL and confirm only its configured SQL port is listening; the
  additional MySQL X listener must remain disabled.
- [ ] Add MySQL and MariaDB with their default port, confirm the second service
  is offered a different free port, then occupy a chosen port with an external
  process and confirm HerdMe selects an alternative without stopping that process.
- [ ] Start PostgreSQL, read the managed user through HerdMe's `.env` or
  TablePlus action, create a database, restart the service, and confirm the
  database persists. Confirm passwordless TCP login is rejected.
- [ ] Start fresh MySQL and MariaDB instances and confirm the managed credential
  succeeds while both the managed user and root are rejected without a password.
- [ ] Place a disposable pre-authentication MySQL/MariaDB/PostgreSQL data fixture
  in an instance directory, start it, confirm migration writes
  `.herdme-auth-v1` only after verification, and confirm a forced migration
  failure preserves the complete data directory.
- [ ] Start MySQL, MariaDB, PostgreSQL, MongoDB, Redis, and Valkey where available;
  confirm the TablePlus action appears only while each service is running and
  opens TablePlus with the correct loopback host, port, user, and database. Also
  confirm a clear install message appears when TablePlus is not installed.
- [ ] For every managed service, use `Add to .env`, select a site, and confirm
  HerdMe creates `.env` from `.env.example` when needed, preserves unrelated
  lines, updates existing keys without duplicates, and writes the service's
  actual loopback port and managed credentials.
- [ ] Confirm a service on the newest upstream release has no update button and
  an older manifest does show one.
- [ ] Extract the portable ZIP on a clean Windows user account and run HerdMe
  without installing .NET or Windows App Runtime separately.
- [ ] Run the versioned Setup executable on a clean Windows user account,
  confirm Start Menu and optional desktop shortcuts, upgrade it in place, then
  uninstall it and confirm `%LOCALAPPDATA%\HerdMe` user data remains intact.
- [ ] Confirm About displays the MIT license and third-party acknowledgements,
  with no activation, subscription, license key, or paid feature gate.
