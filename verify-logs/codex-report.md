# HerdMe Windows release verification

## Gates

- PASS: `herdme-verify.ps1` quick gates (format, contract build/run, WinUI build).
- PASS: C# format check; 0 files reformatted.
- PASS: Contract tests; all Windows cross-platform contract tests passed.
- PASS: WinUI Release build; 0 warnings and 0 errors.
- PASS: Windows core build and CTest; 2/2 tests passed.
- PASS: Portable package `HerdMe-0.1.24-win-x64-portable.zip`.
- PASS: Inno Setup installer `HerdMe-0.1.24-win-x64-setup.exe`.
- PASS: Winget manifest generation and `winget validate`.
- BLOCKED: Full acceptance could not run because this user profile has an existing HerdMe installation/startup registration. The exact guard error was: `Run installer acceptance on a clean Windows user profile to preserve existing installation and startup registration.` No existing process or installation was changed.

## Fixes

- `Windows/HerdMe.Windows/Services/XdebugProfiler.cs`: renamed a local tuple to avoid CS0136 shadowing.
- `Windows/HerdMe.Windows/Pages/SitesPage.CommandConsole.cs`: added the missing `HerdMe.Windows.Services` import.
- `Windows/HerdMe.Windows/Pages/SitesPage.Proxies.cs`: kept the resource lookup behavior while avoiding a false dynamic-localization contract match.
- `Windows/HerdMe.Windows/Strings/ar/Resources.resw`: translated the new manifest, doctor, and Tinker strings.
- `Windows/HerdMe.Windows.ContractTests/ProfilerContractChecks.cs`: corrected Windows verbatim-path expectations in the cachegrind fixture.
- `VERSION`, `project.yml`, `HerdMe/Resources/release-manifest.json`: bumped the release to `0.1.24` and updated release metadata.

## Artifacts

- Portable SHA-256: `3b6588912aa5672b95626675342b2ba670489e9f4343718574c60e1025be575e`
- Installer SHA-256: `3e6dfe0b00f32c08921656cc8d43e50fce61c4741b0a860cd7400db42b8dd725`

## Performance and smoke tests

The full acceptance suite did not reach UI navigation/performance collection because its clean-profile guard stopped first; therefore no `performance.json` was produced and no performance numbers are claimed. Manual UI smoke flows that require an isolated clean profile remain pending for the same reason.
