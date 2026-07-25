param(
    [string]$Architecture = "x64",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
if ($Architecture -ne "x64") {
    throw "HerdMe for Windows currently supports x64 only."
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot "HerdMe.Windows\HerdMe.Windows.csproj"
$runtimeIdentifier = "win-x64"
$publishDirectory = Join-Path $repoRoot "build\windows-portable-$runtimeIdentifier"
$outputDirectory = Join-Path $repoRoot "dist"
[xml]$projectXml = Get-Content -Raw $project
$version = ($projectXml.Project.PropertyGroup.Version | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "The Windows project does not define a Version property."
}
$archive = Join-Path $outputDirectory "HerdMe-$version-$runtimeIdentifier-portable.zip"
$checksumFile = "$archive.sha256"

& (Join-Path $PSScriptRoot "build.ps1") `
    -Architecture $Architecture `
    -Configuration $Configuration

if (Test-Path $publishDirectory) {
    Remove-Item -Recurse -Force $publishDirectory
}
New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null

dotnet publish $project `
    --configuration $Configuration `
    --runtime $runtimeIdentifier `
    --self-contained true `
    --output $publishDirectory `
    -p:Platform=$Architecture `
    -p:WindowsPackageType=None `
    -p:TreatWarningsAsErrors=true
if ($LASTEXITCODE -ne 0) { throw "The self-contained Windows publish failed." }

$requiredFiles = @(
    "HerdMe.Windows.exe",
    "Runtime\herdme-core.exe",
    "Microsoft.WindowsAppRuntime.dll",
    "Microsoft.ui.xaml.dll",
    "LICENSE",
    "THIRD_PARTY.md",
    "release-manifest.json"
)
foreach ($relativePath in $requiredFiles) {
    $requiredPath = Join-Path $publishDirectory $relativePath
    if (-not (Test-Path $requiredPath -PathType Leaf)) {
        throw "The portable publish is missing $relativePath."
    }
}

function Assert-X64PortableExecutable([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) {
        throw "$Path is not a valid Windows PE executable."
    }
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ($peOffset -lt 0 -or $peOffset + 6 -gt $bytes.Length) {
        throw "$Path has an invalid PE header offset."
    }
    if ([BitConverter]::ToUInt32($bytes, $peOffset) -ne 0x00004550) {
        throw "$Path is missing the PE signature."
    }
    if ([BitConverter]::ToUInt16($bytes, $peOffset + 4) -ne 0x8664) {
        throw "$Path is not an x64 executable."
    }
}

Assert-X64PortableExecutable (Join-Path $publishDirectory "HerdMe.Windows.exe")
Assert-X64PortableExecutable (Join-Path $publishDirectory "Runtime\herdme-core.exe")

& (Join-Path $publishDirectory "Runtime\herdme-core.exe") doctor
if ($LASTEXITCODE -ne 0) { throw "The packaged portable core health check failed." }

New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
if (Test-Path $archive) {
    Remove-Item -Force $archive
}
Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $archive

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
try {
    $entries = @($zip.Entries | ForEach-Object { $_.FullName.Replace("/", "\") })
    foreach ($relativePath in $requiredFiles) {
        if ($entries -notcontains $relativePath) {
            throw "The portable archive is missing $relativePath."
        }
    }
}
finally {
    $zip.Dispose()
}

$hash = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant()
$checksumLine = "$hash  $(Split-Path -Leaf $archive)"
[System.IO.File]::WriteAllText(
    $checksumFile,
    $checksumLine + [Environment]::NewLine,
    [System.Text.Encoding]::ASCII
)
Write-Host "Created local Windows package:"
Write-Host "$hash  $archive"
Write-Host "Checksum file:"
Write-Host $checksumFile
