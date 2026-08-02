# HerdMe

An open-source local development environment for PHP and Laravel projects on
macOS and Windows.

[![macOS](https://github.com/Hamad3bdulla/herdme/actions/workflows/macos.yml/badge.svg)](https://github.com/Hamad3bdulla/herdme/actions/workflows/macos.yml)
[![Windows x64](https://github.com/Hamad3bdulla/herdme/actions/workflows/windows-x64.yml/badge.svg)](https://github.com/Hamad3bdulla/herdme/actions/workflows/windows-x64.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-2f855a.svg)](LICENSE)

HerdMe provides the everyday tools needed to run Laravel projects locally from
a native desktop application, without subscriptions, activation, or license
keys. The project uses SwiftUI on macOS, WinUI 3 on Windows, and a portable
C++20 core for shared contracts.

> [!IMPORTANT]
> HerdMe is currently a pre-release project. The macOS application works as a
> local testing build, but public distribution still requires Developer ID
> signing and Apple notarization. The Windows implementation and shared
> contract tests are present, but a complete native build and acceptance run on
> Windows x64 are still required. Development artifacts are not final releases.

## Features

### Sites and Laravel

- Discover Laravel, PHP, and Node projects inside directories selected by the
  user.
- Link an existing project or unlink it without deleting its files.
- Create new Laravel projects quickly through HerdMe's managed Laravel
  Installer.
- Keep project creation progress and the result of every stage visible.
- Serve local domains such as `project.test` without exposing internal ports in
  browser URLs.
- Provide local HTTP and HTTPS with a HerdMe-owned certificate authority.
- Route Laravel through FastCGI and `public/index.php`, not FrankenPHP.
- Select a different PHP and Node version for each site.
- Preview sites, open them in a browser or editor, and run ready-made Artisan
  actions for migrations, seeders, optimization, cache clearing, storage links,
  plus custom Artisan and npm commands.
- Inspect Laravel logs, Git status, route files, and project environment health.
- Edit `.env` safely, create it from `.env.example`, and detect external changes
  before saving.
- Run Composer commands and keep Laravel queue workers and schedulers running
  per site, with live status, captured output, cancellation, and clean shutdown.
- Inspect and repair site health across `.env`, PHP extensions, Composer,
  dependencies, HTTPS, and the local environment.
- Create, inspect, back up, import plain or gzip-compressed SQL, reset, open,
  and delete dedicated MySQL, MariaDB, and PostgreSQL databases for each site.
- Safely remove projects created by HerdMe while protecting linked projects and
  projects outside the managed sites directory from accidental deletion.

### PHP, Composer, Git, and Node

- Manage supported PHP versions from `8.0` through `8.5` inside HerdMe-owned
  storage.
- Validate Laravel's required extensions before serving a site:
  `ctype`, `curl`, `dom`, `fileinfo`, `filter`, `hash`, `mbstring`, `openssl`,
  `pcre`, `pdo`, `session`, `tokenizer`, and `xml`.
- Enable and repair Redis, PDO SQLite, and SQLite3 across every installed
  managed PHP version, and inspect loaded extensions from the site page.
- Install and update Composer and Laravel Installer from the application.
- On Windows, install verified MinGit and expose the managed PHP, Composer,
  Laravel Installer, Node.js, npm, and Git commands to new terminal sessions.
- Manage Node.js versions `20`, `22`, `24`, and `26`.
- Install and manage Xdebug inside HerdMe-owned storage without modifying
  another package manager's configuration.
- Hide update actions when the newest version is already installed.

### Local services

The service catalog supports the following products when an official package
is available for the current platform:

- MariaDB
- MySQL
- PostgreSQL
- MongoDB
- Redis
- Valkey
- Meilisearch
- Typesense
- MinIO
- RustFS

When a service is added, HerdMe downloads, verifies, and installs its package
automatically. Connection settings can be added to a project's `.env` file, and
supported databases can be opened in TablePlus while the service is running.
Passwords are stored in Keychain on macOS or Credential Manager on Windows, and
managed services listen on loopback only.

> Valkey and Typesense are visible on Windows but installation is currently
> disabled because their upstream projects do not publish verifiable native
> Windows x64 packages.

### Development tools

- A local SMTP inbox with safe text and HTML message previews.
- Laravel VarDumper capture and inspection inside the application.
- HerdMe diagnostics and Laravel logs in one log viewer.
- Xdebug settings and a site-specific Debug Session URL.
- A dashboard for environment, site, service, mail, and dump health.
- English and Arabic interfaces with right-to-left layout support.
- Optional launch at login.
- Stable and beta updates through a cryptographically signed HTTPS feed.
- Single-instance enforcement to prevent duplicate background listeners.

## Out of scope

HerdMe does not aim to reproduce every commercial product or replace its cloud
services. The following features are intentionally outside this project's
scope:

- Integrations
- Shortcuts
- Expose
- Forge integration
- Cloud hosting and deployment services

## Platform status

| Platform | Status | Notes |
| --- | --- | --- |
| macOS 13+ | Functional local testing build | Developer ID signing and notarization are required before public release |
| Windows 10 2004+ x64 | Native acceptance pending | Do not publish a Windows build until native build and acceptance pass on Windows |

See [`docs/PARITY.md`](docs/PARITY.md) for the implementation matrix and
remaining release gates.

## Installation

### macOS

After an official Apple-signed and notarized release is published:

1. Download `HerdMe-<version>-macOS.dmg` and its SHA-256 file from
   [GitHub Releases](https://github.com/Hamad3bdulla/herdme/releases).
2. Verify the download:

   ```sh
   shasum -a 256 HerdMe-<version>-macOS.dmg
   ```

3. Open the DMG and drag `HerdMe.app` into `Applications`.
4. Launch HerdMe and complete the first-run wizard. It prepares local domains,
   HTTPS, PHP, Composer, Laravel Installer, and Node.js.

Do not bypass Gatekeeper for an unknown build. A public HerdMe release must be
signed with Developer ID and notarized by Apple.

### Windows

After Windows acceptance passes and an Authenticode-signed release is
published:

1. Download `HerdMe-<version>-win-x64-setup.exe` and its SHA-256 file from
   GitHub Releases.
2. Verify the download in PowerShell:

   ```powershell
   Get-FileHash .\HerdMe-<version>-win-x64-setup.exe -Algorithm SHA256
   ```

3. Run the installer and complete the first-run wizard. Windows may display its
   standard UAC consent prompt while `.test` domains are configured, but HerdMe
   does not require a visible PowerShell or Command Prompt window.

Until the Windows gate is complete, use the source-build instructions below for
development and testing only.

## Building from source

Clone the repository:

```sh
git clone https://github.com/Hamad3bdulla/herdme.git
cd herdme
```

### macOS

Requirements:

- macOS 13 or newer
- Xcode 26 or newer
- XcodeGen

```sh
brew install xcodegen
xcodegen generate
xcodebuild -project HerdMe.xcodeproj -scheme HerdMe \
  -configuration Debug -derivedDataPath DerivedData build
open DerivedData/Build/Products/Debug/HerdMe.app
```

Run the unit tests:

```sh
xcodebuild -project HerdMe.xcodeproj -scheme HerdMe \
  -configuration Debug -derivedDataPath DerivedData \
  -only-testing:HerdMeTests test
```

Create a local testing package:

```sh
./scripts/package-macos.sh Release
```

Signing, notarization, and publishing instructions are documented in
[`docs/RELEASING.md`](docs/RELEASING.md).

### Windows x64

Requirements:

- Windows 10 version 2004 or newer on an x64 processor
- Visual Studio 2022 with `Desktop development with C++`
- .NET 8 SDK
- CMake 3.20 or newer
- Windows PowerShell 5.1 or newer
- Inno Setup 6 when creating the installer

Run from PowerShell at the repository root:

```powershell
.\Windows\build.ps1 -Architecture x64 -Configuration Debug
```

Run the native Windows acceptance gate:

```powershell
.\Windows\acceptance.ps1 -Configuration Release -LeaveRunning
```

Create the portable ZIP and Windows installer:

```powershell
.\Windows\package-portable.ps1 -Architecture x64 -Configuration Release
.\Windows\package-installer.ps1 -Architecture x64 -Configuration Release
```

Read [`Windows/README.md`](Windows/README.md) and
[`Windows/ACCEPTANCE.md`](Windows/ACCEPTANCE.md) before treating a Windows build
as ready for release.

## Contributing

Contributions are welcome across macOS, Windows, the portable core,
documentation, and localization.

1. Read [`CONTRIBUTING.md`](CONTRIBUTING.md) and
   [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md).
2. Search existing issues before opening a bug report or feature request.
3. Fork the repository and create a focused branch for the change.
4. Follow the existing platform conventions and add a test for new or corrected
   behavior.
5. Run the relevant platform tests. Do not ignore compiler or sanitizer
   warnings.
6. Open a pull request that explains the problem, the solution, and how it was
   verified. Include screenshots for user-interface changes.

Example:

```sh
git checkout -b feature/short-description
git add .
git commit -m "Add a short description"
git push origin feature/short-description
```

Never include signing keys, passwords, certificates, or real project data in an
issue or pull request. Report suspected vulnerabilities privately by following
[`SECURITY.md`](SECURITY.md), not through a public issue.

## Independence and supporting Laravel Herd

HerdMe is a clean, independent implementation. It does not use Laravel Herd
source code, assets, branding, certificates, runtime folders, or private
configuration. HerdMe is not affiliated with or endorsed by Laravel LLC or the
makers of Laravel Herd. All product names and trademarks belong to their
respective owners.

If you want the official product, commercial support, or simply want to support
the team building Laravel Herd, use the official version and purchase
**Laravel Herd Pro** directly from:

- [Laravel Herd](https://herd.laravel.com/)
- [Laravel](https://laravel.com/)

Purchasing the official product is the correct way to support its developers.
HerdMe's availability as an open-source project does not reduce the value of
the official product and does not give HerdMe any official status or support.

## Privacy and local data

- HerdMe stores its application data only in HerdMe-owned paths.
- macOS: `~/Library/Application Support/HerdMe`
- Windows: `%LOCALAPPDATA%\HerdMe`
- Project directories are not scanned until the user explicitly adds them.
- Unlinking a site never deletes the project. Deletion is limited to projects
  created by HerdMe inside the managed sites directory and requires user
  confirmation.

## License

HerdMe source code is available under the [MIT License](LICENSE). Runtimes and
tools downloaded by the application retain their respective licenses and are
not relicensed by HerdMe. See [`docs/THIRD_PARTY.md`](docs/THIRD_PARTY.md) for
third-party acknowledgements and license information.

Release history is maintained in [`CHANGELOG.md`](CHANGELOG.md). The parity
roadmap and remaining gates are tracked in [`docs/PARITY.md`](docs/PARITY.md).
