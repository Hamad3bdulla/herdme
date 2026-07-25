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
  sidecar, requires the app-local Windows App Runtime and WinUI DLLs, starts
  the app, rejects a second process, and observes loopback
  listeners on ports 2525 and 9912. It must also reject non-loopback capture
  bindings and complete live SMTP and VarDumper exchanges against the running
  app without leaving acceptance records behind.

## Native shell

- [ ] Verify General, Sites, PHP, Node, Services, Mail, Dumps, Debugger, Logs,
  and About render without clipping at 100%, 125%, and 150% display scaling.
- [ ] Close the window, reopen it from the tray, and quit from the tray.
- [ ] Launch a second copy and confirm the existing window comes to the front.
- [ ] Enable launch at sign-in, sign out and back in, and confirm HerdMe starts
  in the background without a console window.

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
- [ ] During Laravel creation, confirm validation, Laravel Installer, project,
  optional Boost/Git, verification, registration, and completion are shown;
  force a validation error and confirm its detail remains visible until closed.
- [ ] Assign different PHP and Node versions to two sites and verify both run
  simultaneously with the selected versions.
- [ ] Confirm the site thumbnail uses a desktop-width viewport and HTTPS opens
  the selected site in the default browser.

## Trust and local domains

- [ ] Install the HerdMe certificate authority and confirm the Windows trust
  prompt completes without opening a terminal or console window.
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
- [ ] Start MySQL and confirm only its configured SQL port is listening; the
  additional MySQL X listener must remain disabled.
- [ ] Start PostgreSQL, connect as the local `postgres` user, create a database,
  restart the service, and confirm the database persists.
- [ ] Confirm a service on the newest upstream release has no update button and
  an older manifest does show one.
- [ ] Extract the portable ZIP on a clean Windows user account and run HerdMe
  without installing .NET or Windows App Runtime separately.
- [ ] Confirm About displays the MIT license and third-party acknowledgements,
  with no activation, subscription, license key, or paid feature gate.
