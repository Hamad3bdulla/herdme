param(
    [string]$Architecture = "x64",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$InnoCompiler = "",
    [switch]$SkipPortableBuild
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$releaseMode = if ([string]::IsNullOrWhiteSpace($env:HERDME_RELEASE_MODE)) {
    "local"
} else {
    $env:HERDME_RELEASE_MODE.ToLowerInvariant()
}
if ($releaseMode -notin @("local", "public")) {
    throw "HERDME_RELEASE_MODE must be local or public."
}
if ($releaseMode -eq "public" -and $Configuration -ne "Release") {
    throw "Public installers must use the Release configuration."
}
if ($Architecture -ne "x64") {
    throw "HerdMe for Windows currently supports x64 only."
}
if ($env:OS -ne "Windows_NT") {
    throw "The HerdMe installer must be built on Windows."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$runtimeIdentifier = "win-x64"
$publishDirectory = Join-Path $repoRoot "build\windows-portable-$runtimeIdentifier"
$outputDirectory = Join-Path $repoRoot "dist"
$version = (Get-Content -LiteralPath (Join-Path $repoRoot "VERSION") -Raw).Trim()
$buildNumber = (Get-Content -LiteralPath (Join-Path $repoRoot "BUILD_NUMBER") -Raw).Trim()
$outputBaseFilename = "HerdMe-$version-$runtimeIdentifier-setup"
$installerPath = Join-Path $outputDirectory "$outputBaseFilename.exe"
$checksumFile = "$installerPath.sha256"
$installerDefinition = Join-Path $PSScriptRoot "installer.iss"
. (Join-Path $PSScriptRoot "sign-windows-artifact.ps1")

function Find-InnoCompiler([string]$RequestedPath) {
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        if (-not (Test-Path -LiteralPath $RequestedPath -PathType Leaf)) {
            throw "The requested Inno Setup compiler was not found: $RequestedPath"
        }
        return (Resolve-Path -LiteralPath $RequestedPath).Path
    }

    $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $candidates += Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $candidates += Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"
    }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw "ISCC.exe was not found. Install Inno Setup 6 or pass -InnoCompiler."
}

& (Join-Path $repoRoot "scripts\check-version.ps1")
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
foreach ($oldOutput in @($installerPath, $checksumFile)) {
    if (Test-Path -LiteralPath $oldOutput) {
        Remove-Item -LiteralPath $oldOutput -Force
    }
}
if (-not $SkipPortableBuild) {
    & (Join-Path $PSScriptRoot "package-portable.ps1") `
        -Architecture $Architecture `
        -Configuration $Configuration
}

$requiredFiles = @(
    "HerdMe.Windows.exe",
    "Runtime\herdme-core.exe",
    "Microsoft.WindowsAppRuntime.dll",
    "Microsoft.ui.xaml.dll",
    "LICENSE",
    "THIRD_PARTY.md",
    "release-manifest.json"
)
if ($releaseMode -eq "public") {
    $requiredFiles += @("release-public-key.txt", "release-feed-url.txt")
}
foreach ($relativePath in $requiredFiles) {
    $requiredPath = Join-Path $publishDirectory $relativePath
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "The installer payload is missing $relativePath."
    }
}

if ($releaseMode -eq "public") {
    $thumbprint = Get-HerdMeSigningThumbprint
    Assert-HerdMeAuthenticodeSignature `
        -Path (Join-Path $publishDirectory "HerdMe.Windows.exe") `
        -ExpectedThumbprint $thumbprint
    Assert-HerdMeAuthenticodeSignature `
        -Path (Join-Path $publishDirectory "Runtime\herdme-core.exe") `
        -ExpectedThumbprint $thumbprint
}

$compiler = Find-InnoCompiler $InnoCompiler
& $compiler `
    "/DMyAppVersion=$version" `
    "/DMyAppBuild=$buildNumber" `
    "/DSourceDir=$publishDirectory" `
    "/DOutputDir=$outputDirectory" `
    "/DOutputBaseFilename=$outputBaseFilename" `
    $installerDefinition
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed to build the HerdMe installer." }
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "The expected HerdMe installer was not produced: $installerPath"
}

if ($releaseMode -eq "public") {
    Sign-HerdMePublicArtifact -Path $installerPath
}

$installerBytes = [System.IO.File]::ReadAllBytes($installerPath)
if (
    $installerBytes.Length -lt 64 -or
    $installerBytes[0] -ne 0x4d -or
    $installerBytes[1] -ne 0x5a
) {
    throw "The generated installer is not a valid Windows executable."
}

$hash = (Get-FileHash -Algorithm SHA256 $installerPath).Hash.ToLowerInvariant()
$checksumLine = "$hash  $(Split-Path -Leaf $installerPath)"
[System.IO.File]::WriteAllText(
    $checksumFile,
    $checksumLine + [Environment]::NewLine,
    [System.Text.Encoding]::ASCII
)
Write-Host "Created Windows installer:"
Write-Host "$hash  $installerPath"
Write-Host "Checksum file:"
Write-Host $checksumFile
