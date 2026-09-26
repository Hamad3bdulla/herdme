# HerdMe on winget

`New-WingetManifest.ps1` writes the three manifest files winget needs for one HerdMe
Windows release (package `Hamad3bdulla.HerdMe`). It does not publish anything.

## Generate

Download the exact installer and checksum attached to the GitHub release, then run:

```powershell
gh release download windows-v0.1.24 --repo Hamad3bdulla/herdme --pattern 'HerdMe-0.1.24-win-x64-setup.exe*' --dir build\winget-input
./Windows/winget/New-WingetManifest.ps1 -InstallerPath build\winget-input\HerdMe-0.1.24-win-x64-setup.exe -Version 0.1.24
```

The script:

- checks the installer name matches the version;
- computes its SHA-256 and compares it with the published `.sha256` file;
- writes `build\winget\<version>\` (version, installer and en-US locale manifests);
- runs `winget validate` when winget is installed.

## Test locally

```powershell
winget settings --enable LocalManifestFiles
winget install --manifest build\winget\0.1.24
```

Install with `--silent` as well, then check that the app starts and that uninstalling from
Settings > Apps removes it.

## Submit

Fork `microsoft/winget-pkgs`, copy the files to
`manifests/h/Hamad3bdulla/HerdMe/<version>/`, and open a pull request. Alternatively use
`wingetcreate submit build\winget\<version>`.

Notes:

- The installer is per-user (`PrivilegesRequired=lowest`), so the manifest uses `Scope: user`.
- `ProductCode` is the Inno Setup uninstall key `{AppId}_is1`. Keep it in sync with
  `Windows/installer.iss`; the contract tests check this.
- Until the installer is Authenticode-signed, SmartScreen and the winget validation pipeline
  may flag it. Submit after signing if that happens.
