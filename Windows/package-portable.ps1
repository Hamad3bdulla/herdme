param(
    [string]$Architecture = "x64",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
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
    throw "Public packages must use the Release configuration."
}
if ($Architecture -ne "x64") {
    throw "HerdMe packages are x64 only. ARM64 is a build preview: run Windows\build.ps1 -Architecture ARM64 -SkipTests."
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot "HerdMe.Windows\HerdMe.Windows.csproj"
$runtimeIdentifier = "win-x64"
$publishDirectory = Join-Path $repoRoot "build\windows-portable-$runtimeIdentifier"
$outputDirectory = Join-Path $repoRoot "dist"
& (Join-Path $repoRoot "scripts\check-version.ps1")
$version = (Get-Content -LiteralPath (Join-Path $repoRoot "VERSION") -Raw).Trim()
$archive = Join-Path $outputDirectory "HerdMe-$version-$runtimeIdentifier-portable.zip"
$checksumFile = "$archive.sha256"
. (Join-Path $PSScriptRoot "sign-windows-artifact.ps1")
. (Join-Path $PSScriptRoot "windows-build-tools.ps1")
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
foreach ($oldOutput in @($archive, $checksumFile)) {
    if (Test-Path -LiteralPath $oldOutput) {
        Remove-Item -LiteralPath $oldOutput -Force
    }
}

& (Join-Path $PSScriptRoot "build.ps1") `
    -Architecture $Architecture `
    -Configuration $Configuration

if (Test-Path $publishDirectory) {
    Remove-Item -Recurse -Force $publishDirectory
}
New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null

$msbuild = Find-HerdMeMSBuild
& $msbuild $project `
    /t:Publish `
    "/p:Configuration=$Configuration" `
    "/p:RuntimeIdentifier=$runtimeIdentifier" `
    "/p:Platform=$Architecture" `
    /p:SelfContained=true `
    /p:PublishReadyToRun=true `
    "/p:PublishDir=$publishDirectory\" `
    /p:WindowsPackageType=None `
    /p:UseXamlCompilerExecutable=false `
    /p:TreatWarningsAsErrors=true
if ($LASTEXITCODE -ne 0) { throw "The self-contained Windows publish failed." }

# herdme.exe (the command-line tool) is published self-contained with the same SDK, then only
# its own files are copied next to HerdMe.Windows.exe. Every runtime file it depends on must
# already be in the app publish and match, so both executables share one .NET runtime.
$cliProject = Join-Path $PSScriptRoot "HerdMe.Cli\HerdMe.Cli.csproj"
$cliPublishDirectory = Join-Path $repoRoot "build\windows-cli-$runtimeIdentifier"
if (Test-Path $cliPublishDirectory) {
    Remove-Item -Recurse -Force $cliPublishDirectory
}
dotnet publish $cliProject `
    --configuration $Configuration `
    --runtime $runtimeIdentifier `
    --self-contained true `
    --output $cliPublishDirectory `
    -p:TreatWarningsAsErrors=true
if ($LASTEXITCODE -ne 0) { throw "Publishing the herdme command failed." }
$cliOwnFiles = @("herdme.exe", "herdme.dll", "herdme.deps.json", "herdme.runtimeconfig.json")
foreach ($cliFile in Get-ChildItem -LiteralPath $cliPublishDirectory -Recurse -File) {
    $relativePath = $cliFile.FullName.Substring($cliPublishDirectory.Length).TrimStart("\")
    if ($relativePath -like "*.pdb") { continue }
    $target = Join-Path $publishDirectory $relativePath
    if ($cliOwnFiles -contains $relativePath) {
        Copy-Item -LiteralPath $cliFile.FullName -Destination $target -Force
        continue
    }
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
        throw "herdme.exe needs $relativePath, which the app publish does not contain."
    }
    $cliHash = (Get-FileHash -LiteralPath $cliFile.FullName -Algorithm SHA256).Hash
    $appHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    if ($cliHash -ne $appHash) {
        $cliVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($cliFile.FullName).FileVersion
        $appVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($target).FileVersion
        if ([string]::IsNullOrWhiteSpace($cliVersion) -or $cliVersion -ne $appVersion) {
            throw "herdme.exe and HerdMe.Windows.exe use different builds of $relativePath ($cliVersion vs $appVersion)."
        }
    }
}

$requiredFiles = @(
    "HerdMe.Windows.exe",
    "HerdMe.Windows.pri",
    "herdme.exe",
    "herdme.dll",
    "herdme.deps.json",
    "herdme.runtimeconfig.json",
    "Assets\HerdMe.ico",
    "Assets\HerdMe-Degraded.ico",
    "Assets\HerdMe-Stopped.ico",
    "Assets\Overlay-Activity.ico",
    "Assets\Overlay-Error.ico",
    "Prerequisites\VC143\concrt140.dll",
    "Prerequisites\VC143\msvcp140.dll",
    "Prerequisites\VC143\msvcp140_1.dll",
    "Prerequisites\VC143\msvcp140_2.dll",
    "Prerequisites\VC143\msvcp140_atomic_wait.dll",
    "Prerequisites\VC143\msvcp140_codecvt_ids.dll",
    "Prerequisites\VC143\vccorlib140.dll",
    "Prerequisites\VC143\vcruntime140.dll",
    "Prerequisites\VC143\vcruntime140_1.dll",
    "Prerequisites\VC143\vcruntime140_threads.dll",
    "Runtime\herdme-core.exe",
    "Microsoft.Windows.ApplicationModel.Resources.dll",
    "Microsoft.WindowsAppRuntime.dll",
    "Microsoft.ui.xaml.dll",
    "MRM.dll",
    "LICENSE",
    "THIRD_PARTY.md",
    "release-manifest.json"
)
if ($releaseMode -eq "public") {
    $requiredFiles += @("release-public-key.txt", "release-feed-url.txt")
}
foreach ($relativePath in $requiredFiles) {
    $requiredPath = Join-Path $publishDirectory $relativePath
    if (-not (Test-Path $requiredPath -PathType Leaf)) {
        throw "The portable publish is missing $relativePath."
    }
}

if ($releaseMode -eq "public") {
    $publicKeyPath = Join-Path $publishDirectory "release-public-key.txt"
    try {
        $publicKey = [Convert]::FromBase64String(
            [IO.File]::ReadAllText($publicKeyPath).Trim()
        )
    }
    catch {
        throw "release-public-key.txt is not valid Base64."
    }
    if ($publicKey.Length -ne 65 -or $publicKey[0] -ne 4) {
        throw "release-public-key.txt must contain a 65-byte P-256 X9.63 public key."
    }
    $feedUrlText = [IO.File]::ReadAllText(
        (Join-Path $publishDirectory "release-feed-url.txt")
    ).Trim()
    $feedUri = $null
    if (
        -not [Uri]::TryCreate($feedUrlText, [UriKind]::Absolute, [ref]$feedUri) -or
        $feedUri.Scheme -ne [Uri]::UriSchemeHttps -or
        [string]::IsNullOrWhiteSpace($feedUri.Host)
    ) {
        throw "release-feed-url.txt must contain an absolute HTTPS URL."
    }

    $signTool = Find-HerdMeSignTool
    Sign-HerdMePublicArtifact (Join-Path $publishDirectory "HerdMe.Windows.exe") $signTool
    Sign-HerdMePublicArtifact (Join-Path $publishDirectory "herdme.exe") $signTool
    Sign-HerdMePublicArtifact (Join-Path $publishDirectory "herdme.dll") $signTool
    Sign-HerdMePublicArtifact (Join-Path $publishDirectory "Runtime\herdme-core.exe") $signTool
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
Assert-X64PortableExecutable (Join-Path $publishDirectory "herdme.exe")

$cliVersionOutput = @(& (Join-Path $publishDirectory "herdme.exe") --version 2>&1)
if ($LASTEXITCODE -ne 0 -or ($cliVersionOutput -join "`n").Trim() -ne "herdme $version") {
    throw "The packaged herdme command did not report version ${version}: $($cliVersionOutput -join ' ')"
}

& (Join-Path $publishDirectory "Runtime\herdme-core.exe") doctor
if ($LASTEXITCODE -ne 0) { throw "The packaged portable core health check failed." }

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
