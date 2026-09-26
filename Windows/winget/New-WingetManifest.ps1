param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,
    [string]$Version = "",
    [string]$Tag = "",
    [string]$OutputDirectory = "",
    [string]$ReleaseNotesUrl = ""
)

# Generates a winget manifest set (manifest schema 1.6.0) for a published HerdMe Windows
# installer. The installer must be the exact file attached to the GitHub release, so its
# SHA-256 matches what winget downloads. This script never submits anything; open a pull
# request against microsoft/winget-pkgs yourself (see README.md next to this script).

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$packageIdentifier = "Hamad3bdulla.HerdMe"
$repositoryUrl = "https://github.com/Hamad3bdulla/herdme"
# Inno Setup registers the uninstall entry as "<AppId>_is1"; keep in sync with Windows/installer.iss.
$productCode = "{6C053A4F-1FF3-4B94-A6DF-17CAF32FAC5F}_is1"
$manifestVersion = "1.6.0"

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (Get-Content -LiteralPath (Join-Path $repoRoot "VERSION") -Raw).Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "The version must look like 0.1.23."
}
if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = "windows-v$Version"
}
if ($Tag -notmatch '^[A-Za-z0-9._-]+$') {
    throw "The release tag contains unsupported characters."
}
if ([string]::IsNullOrWhiteSpace($ReleaseNotesUrl)) {
    $ReleaseNotesUrl = "$repositoryUrl/releases/tag/$Tag"
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "build\winget\$Version"
}

$installer = Resolve-Path -LiteralPath $InstallerPath -ErrorAction Stop
$installerName = Split-Path -Leaf $installer.Path
$expectedName = "HerdMe-$Version-win-x64-setup.exe"
if ($installerName -ne $expectedName) {
    throw "Expected the release installer $expectedName, not $installerName."
}
$sha256 = (Get-FileHash -LiteralPath $installer.Path -Algorithm SHA256).Hash.ToUpperInvariant()
$checksumFile = "$($installer.Path).sha256"
if (Test-Path -LiteralPath $checksumFile -PathType Leaf) {
    $published = ((Get-Content -LiteralPath $checksumFile -Raw).Trim() -split '\s+')[0].ToUpperInvariant()
    if ($published -ne $sha256) {
        throw "The installer does not match its published checksum file."
    }
}
$installerUrl = "$repositoryUrl/releases/download/$Tag/$expectedName"

$header = "# Created with HerdMe Windows/winget/New-WingetManifest.ps1"
$versionManifest = @"
$header
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$manifestVersion.schema.json

PackageIdentifier: $packageIdentifier
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $manifestVersion
"@

$installerManifest = @"
$header
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$manifestVersion.schema.json

PackageIdentifier: $packageIdentifier
PackageVersion: $Version
Platform:
- Windows.Desktop
MinimumOSVersion: 10.0.19041.0
InstallerType: inno
Scope: user
InstallModes:
- interactive
- silent
- silentWithProgress
UpgradeBehavior: install
ProductCode: '$productCode'
AppsAndFeaturesEntries:
- DisplayName: HerdMe
  Publisher: HerdMe contributors
  ProductCode: '$productCode'
Installers:
- Architecture: x64
  InstallerUrl: $installerUrl
  InstallerSha256: $sha256
ManifestType: installer
ManifestVersion: $manifestVersion
"@

$localeManifest = @"
$header
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$manifestVersion.schema.json

PackageIdentifier: $packageIdentifier
PackageVersion: $Version
PackageLocale: en-US
Publisher: HerdMe contributors
PublisherUrl: $repositoryUrl
PublisherSupportUrl: $repositoryUrl/issues
PackageName: HerdMe
PackageUrl: $repositoryUrl
License: MIT
LicenseUrl: $repositoryUrl/blob/main/LICENSE
ShortDescription: Native local PHP and Laravel development environment for Windows.
Description: HerdMe runs PHP sites at https://name.test/ with managed PHP, Composer, Node.js, databases, mail and dump capture, and Xdebug, without Docker or WSL.
Moniker: herdme
Tags:
- php
- laravel
- development
- local-development
- composer
- nginx
ReleaseNotesUrl: $ReleaseNotesUrl
ManifestType: defaultLocale
ManifestVersion: $manifestVersion
"@

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)
$files = @(
    @{ Name = "$packageIdentifier.yaml"; Text = $versionManifest },
    @{ Name = "$packageIdentifier.installer.yaml"; Text = $installerManifest },
    @{ Name = "$packageIdentifier.locale.en-US.yaml"; Text = $localeManifest }
)
foreach ($file in $files) {
    $text = ($file.Text -replace "`r`n", "`n") + "`n"
    [System.IO.File]::WriteAllText((Join-Path $OutputDirectory $file.Name), $text, $utf8)
}

Write-Host "winget manifests for $packageIdentifier $Version written to $OutputDirectory"
Write-Host "Installer: $installerUrl"
Write-Host "SHA-256:   $sha256"
$winget = Get-Command "winget.exe" -ErrorAction SilentlyContinue
if ($null -ne $winget) {
    & $winget.Source validate --manifest $OutputDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "winget validate rejected the generated manifests."
    }
} else {
    Write-Host "winget.exe was not found; run 'winget validate --manifest $OutputDirectory' before submitting."
}
