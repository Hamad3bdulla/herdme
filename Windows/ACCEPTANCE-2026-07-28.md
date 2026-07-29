# Windows x64 Acceptance Report - 2026-07-28

## Build under test

- Base commit: `03b6461cee06525b00c229bab018eb96ea7d14f3`
- Branch: `master`
- The artifacts include the uncommitted Windows acceptance fixes listed by
  `git status`; no commit or push was made.
- Host: Microsoft Windows 11 Pro 25H2, build `26200.8894`, x64 OS and process.
- Final display scale: 200% (`AppliedDPI=192`).

## Automated gate

`Windows\acceptance.ps1 -Configuration Release -LeaveRunning` passed on the
real Windows host after the focused fixes. The latest run completed at about
22:41 Asia/Bahrain and covered:

- CMake Release build and both CTest cases.
- C# contract build and execution with zero warnings and zero errors.
- PowerShell and XAML parsing, native WinUI build, and self-contained publish.
- Portable ZIP and sidecar verification.
- Local unsigned Setup build, sidecar verification, and the isolated silent
  install/core-health/uninstall cycle.
- Live service, PHP, Node.js, Composer, Laravel Installer, and Xdebug release
  checks. Xdebug SHA-256 data was verified for PHP 8.0 through 8.5.
- Single-instance enforcement, all eleven WinUI page roots, loopback SMTP and
  VarDumper listeners, and live protocol exchanges without retained records.
- The published main window reported a 1080 by 720 bounding rectangle with
  `CanResize`, `CanMaximize`, and `CanMinimize` all false.

The final runtime doctor output rejected private runtimes under `.config`,
Laraboxs application data, and DevHerd application data. It reported only
Node.js and npm from `C:\Program Files\nodejs`; PHP, Composer, nginx, and
dnsmasq were missing rather than borrowed from another application.

## Focused fixes verified

- Private PATH directories are rejected before executable probing, and a later
  legitimate system runtime remains discoverable. A Windows C++ regression
  test covers both private configuration and local application-data roots.
- Runtime results no longer expose a resolved private executable path.
- MSBuild discovery requires both MSBuild and the x64 MSVC tools, preventing an
  incomplete Visual Studio 18 installation from taking precedence over the
  complete Visual Studio 2022 installation.
- Programmatic tray commands use direct ResourceLoader keys instead of XAML
  property keys. The final PRI dump contains the expected Open, Start all, Stop
  all, and Quit values; the repository contract rejects the former lookup form.
- The tray uses the shipped `Assets\HerdMe.ico` instead of a generated glyph,
  and the icon is copied to build, publish, ZIP, and Setup payloads. The source,
  published, and archived icon hashes match byte-for-byte.
- The tray menu uses the native work-area-aware `PopupMenu` mode instead of the
  preview `SecondWindow` mode that clipped the lower options at 200% scaling.
  Repository contracts reject both the old menu mode and generated icon source.
- The onboarding summary now uses the direct `OnboardingSetupSummaryText`
  ResourceLoader key. The final PRI contains that key and no longer contains
  the invalid programmatic `OnboardingSetupSummary.Text` lookup.
- Managed PHP receives the signed x64 Microsoft VC143 app-local runtime, so it
  does not depend on a system-wide Visual C++ installation. All ten runtime
  files in publish and ZIP are Microsoft-signed version `14.44.35211.0`.
- Official PHP 8.4.23 was downloaded with its published SHA-256, loaded
  `VCRUNTIME140.dll` from its own HerdMe-managed directory, and started the
  curl, fileinfo, mbstring, and openssl extensions successfully.
- `herdme-core.exe` now statically links the Microsoft C/C++ runtime. Its final
  PE import table contains only `KERNEL32.dll`, so the PHP extension validation
  step no longer depends on a system-wide VC++ installation on a clean host.
- PHP 8.4 module output was piped through the final published Core executable;
  it returned `compatible: true` with an empty missing-extension list.
- Core process failures now report the executable name plus decimal and
  hexadecimal exit codes instead of the ambiguous `HerdMe Core failed` text.
- Inno Setup 6.6 compiled the fixed-size 100% wizard without the obsolete
  `WizardResizable` warning; older compiler versions retain the directive
  conditionally.
- Windows PowerShell 5.1 path handling, junction cleanup, pre-build process
  shutdown, and per-user Inno Setup discovery passed in the full gate.

## Artifacts

| Artifact | SHA-256 |
| --- | --- |
| `dist\HerdMe-0.1.0-win-x64-portable.zip` | `95a6ed6716622c4eb1cae6f7fa5f377ac9482fb3748030238e99d7fe17f712a6` |
| `dist\HerdMe-0.1.0-win-x64-setup.exe` | `0e781d304d65b5a55db575caebe1a38b75004532e84587e5debb01fccdb87259` |

The matching `.sha256` sidecars contain the same values and filenames. The ZIP
is 71,205,969 bytes; the Setup executable is 46,118,251 bytes.

## Manual UI results

- All eleven pages were visually inspected at 150% with no visible clipping:
  Dashboard, General, Sites, PHP, Node.js, Services, Mail, Dumps, Debugger,
  Logs, and About.
- Sites was explicitly rechecked at 125% with no visible clipping.
- The rebuilt General page and Dashboard were checked maximized at 200%.
  General showed no private Herd, Laraboxs, or DevHerd runtime paths.
- Valkey and Typesense remain visible in the service picker. Each shows the
  official Windows-package availability warning and keeps Add disabled.
- About shows MIT licensing and explicitly shows no activation, subscription,
  license key, paid tier, or feature gate.
- A second launch preserved the original PID and left exactly one
  `HerdMe.Windows` process.

## Incomplete manual acceptance

The following items are not accepted by this run and must not be counted toward
the 99% workflow target:

- Clean-profile onboarding, cancellation/retry, restart, and upgrade-profile
  behavior.
- Full page coverage at 100%, and the remaining pages at 125%.
- UAC, certificate/credential migration, hosts-file conflict, and security
  prompt workflows.
- Tray close/reopen/quit and launch-at-sign-in after a real sign-out/sign-in.
  The rebuilt PRI, packaged-icon checks, and popup-menu contract verify the
  implementation fixes, but the notification-area menu itself was not
  targetable by the UI automation client for a post-build screenshot.
- Full clean-profile managed PHP/Node/Composer/Laravel installation, site HTTP
  behavior, Xdebug-to-IDE, and browser connection/range checks. The isolated
  Composer and Laravel project-creation smoke test described below passed.
- Manual Mail, Dumps, Logs, diagnostics, and recovery workflows.
- Managed-service install, checksum, lifecycle, persistence, database,
  TablePlus, port-conflict, loopback, MySQL X-port, and automatic-start tests.
- Clean-user portable execution and manual installer shortcut, upgrade, and
  data-preserving uninstall workflows. Only the automated isolated installer
  cycle passed.
- The Stable application-update UI ended in `Update check failed`; application
  update behavior is not manually accepted even though live runtime and service
  release-source checks passed.
- No public Authenticode signing evidence was produced. The acceptance run used
  local unsigned release mode.

Valkey and Typesense therefore remain intentionally unavailable until verified,
reproducible native Windows x64 packages satisfy the native service audit.

## DPI follow-up - 2026-07-29

A clean-machine screenshot exposed that the fixed 1080 by 720 physical-pixel
window was too small at 200% display scaling. The follow-up build now converts
the intended logical size through `GetDpiForWindow`, caps it to the active
display work area, centers it, and still disables resize, maximize, and
minimize. Dashboard summary cards, environment details, and recent activity
also switch to compact layouts when the content width is constrained.

The Release XAML build, both CTest cases, the C# contract suite, portable
publish, and Setup packaging passed. The rebuilt Dashboard was captured at
200% scaling: all four summary cards, their status text, the environment grid,
and recent activity were visible without clipped or character-by-character
wrapping. Focused formatting verification passed for the changed C# files;
the repository-wide formatter remains blocked by pre-existing mixed line
endings in unrelated files.

## Composer clean-PATH follow-up - 2026-07-29

A clean-machine report showed that Laravel Installer could not find the bare
`composer` command even though HerdMe had installed `composer.phar`. HerdMe now
creates and repairs `%LOCALAPPDATA%\HerdMe\bin\composer.cmd`, which invokes the
selected managed PHP runtime and forwards all arguments to the managed PHAR.
The command uses paths relative to its own directory and is regenerated when
the selected PHP cycle changes.

With system Composer excluded from `PATH`, the generated command reported
Composer 2.10.2 on managed PHP 8.4.23. A real Laravel project was then created
through the final project-creation path; `artisan` and `vendor\autoload.php`
were both present. The temporary smoke project was removed after verification.

## Managed Git and user PATH follow-up - 2026-07-29

A subsequent clean-machine report showed project creation reaching the Git
stage and failing because `git.exe` was not installed. First-run setup and
upgrade repair now install the official 64-bit MinGit ZIP from Git for Windows.
HerdMe requires the GitHub release asset's published size and SHA-256 digest,
extracts it into HerdMe-owned application data, and validates `git --version`
before accepting the runtime. Project creation invokes that managed executable
directly instead of depending on system Git.

HerdMe also persists the active managed PHP, Composer/Laravel bin, Node/npm,
and Git command directories at the front of the current user's `PATH`, removes
only stale HerdMe runtime entries, preserves unrelated entries, and broadcasts
the Windows environment change. PHP or Node version changes from the UI repeat
the synchronization.

The live MinGit install test downloaded Git 2.55.0.windows.3, then a CMD with no
system Git on `PATH` ran both `git --version` and `git init` successfully. The
complete persisted-PATH test opened a new CMD environment and resolved all six
commands from HerdMe before external tools: PHP 8.4.24, Composer 2.10.2,
Laravel Installer 5.31.0, Node.js 24.18.0, npm 11.16.0, and Git
2.55.0.windows.3. The final managed workflow then created a real Laravel
project with Git enabled; `artisan`, `vendor\autoload.php`, and `.git` were all
present. All temporary Git and Laravel test directories were removed.

## Final machine state

No HerdMe process was left running after the follow-up package build.
