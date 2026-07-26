param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $repoRoot "VERSION") -Raw).Trim()
$buildNumber = (Get-Content -LiteralPath (Join-Path $repoRoot "BUILD_NUMBER") -Raw).Trim()
if ($version -notmatch '^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$') {
    throw "VERSION must contain a semantic version such as 1.2.3."
}
if ($buildNumber -notmatch '^[1-9]\d*$') {
    throw "BUILD_NUMBER must contain a positive integer."
}

foreach ($component in $version.Split('.')) {
    [uint32]$numericComponent = 0
    if (
        -not [uint32]::TryParse($component, [ref]$numericComponent) -or
        $numericComponent -gt [uint16]::MaxValue
    ) {
        throw "Every VERSION component must be between 0 and 65535 for Windows file versions."
    }
}
[uint32]$numericBuild = 0
if (
    -not [uint32]::TryParse($buildNumber, [ref]$numericBuild) -or
    $numericBuild -eq 0 -or
    $numericBuild -gt [uint16]::MaxValue
) {
    throw "BUILD_NUMBER must be between 1 and 65535 for Windows file versions."
}

$projectText = Get-Content -LiteralPath (Join-Path $repoRoot "project.yml") -Raw
$projectVersion = [regex]::Match(
    $projectText,
    '(?m)^\s*MARKETING_VERSION:\s*[''\"]?([^\s''\"]+)'
).Groups[1].Value
$projectBuild = [regex]::Match(
    $projectText,
    '(?m)^\s*CURRENT_PROJECT_VERSION:\s*[''\"]?([^\s''\"]+)'
).Groups[1].Value
$manifest = Get-Content -LiteralPath (
    Join-Path $repoRoot "HerdMe\Resources\release-manifest.json"
) -Raw | ConvertFrom-Json
$releasesProperty = $manifest.PSObject.Properties["releases"]
if ($null -eq $releasesProperty) {
    throw "The release manifest must contain at least one release."
}
$releases = @($releasesProperty.Value)
if ($releases.Count -eq 0) {
    throw "The release manifest must contain at least one release."
}
$semanticVersionPattern = '^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-(?:(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+(?:[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$'

for ($index = 0; $index -lt $releases.Count; $index++) {
    $entry = $releases[$index]
    $label = "releases[$index]"
    if ($null -eq $entry -or $entry.version -notmatch $semanticVersionPattern) {
        throw "$label.version is invalid."
    }
    if ("$($entry.build)" -notmatch '^\d+$') {
        throw "$label.build must be a nonnegative integer."
    }
    if (("$($entry.channel)").ToLowerInvariant() -notin @("stable", "beta")) {
        throw "$label.channel must be stable or beta."
    }
    if ($entry.notes -isnot [string]) {
        throw "$label.notes must be a string."
    }
    $legacyDownload = $entry.PSObject.Properties["downloadURL"]
    if ($null -ne $legacyDownload -and $null -ne $legacyDownload.Value) {
        throw "$label.downloadURL is obsolete; use downloadURLs."
    }
    $downloads = $entry.PSObject.Properties["downloadURLs"]
    if ($null -eq $downloads -or $null -eq $downloads.Value) {
        throw "$label.downloadURLs must contain both platform artifacts."
    }
    $macOSValue = $downloads.Value.PSObject.Properties["macOS"]
    $windowsValue = $downloads.Value.PSObject.Properties["windowsX64"]
    $macOSUri = $null
    $windowsUri = $null
    if (
        $null -eq $macOSValue -or
        -not [Uri]::TryCreate("$($macOSValue.Value)", [UriKind]::Absolute, [ref]$macOSUri) -or
        $macOSUri.Scheme -ne [Uri]::UriSchemeHttps
    ) {
        throw "$label.downloadURLs.macOS must be an absolute HTTPS URL."
    }
    if (
        $null -eq $windowsValue -or
        -not [Uri]::TryCreate("$($windowsValue.Value)", [UriKind]::Absolute, [ref]$windowsUri) -or
        $windowsUri.Scheme -ne [Uri]::UriSchemeHttps
    ) {
        throw "$label.downloadURLs.windowsX64 must be an absolute HTTPS URL."
    }
    if ($macOSUri.AbsoluteUri -eq $windowsUri.AbsoluteUri) {
        throw "$label must not use the same artifact for macOS and Windows."
    }
}

$release = $releases[0]

$publicKeyPath = Join-Path $repoRoot "HerdMe\Resources\release-public-key.txt"
$feedUrlPath = Join-Path $repoRoot "HerdMe\Resources\release-feed-url.txt"
$hasPublicKey = Test-Path -LiteralPath $publicKeyPath -PathType Leaf
$hasFeedUrl = Test-Path -LiteralPath $feedUrlPath -PathType Leaf
if ($hasPublicKey -or $hasFeedUrl) {
    if (-not $hasPublicKey -or -not $hasFeedUrl) {
        throw "release-public-key.txt and release-feed-url.txt must be provided together."
    }
    try {
        $publicKey = [Convert]::FromBase64String(
            (Get-Content -LiteralPath $publicKeyPath -Raw).Trim()
        )
    }
    catch {
        throw "release-public-key.txt is not valid Base64."
    }
    if ($publicKey.Length -ne 65 -or $publicKey[0] -ne 4) {
        throw "release-public-key.txt must contain a 65-byte P-256 X9.63 public key."
    }
    $feedUri = $null
    $feedUrl = (Get-Content -LiteralPath $feedUrlPath -Raw).Trim()
    if (
        -not [Uri]::TryCreate($feedUrl, [UriKind]::Absolute, [ref]$feedUri) -or
        $feedUri.Scheme -ne [Uri]::UriSchemeHttps -or
        [string]::IsNullOrWhiteSpace($feedUri.Host)
    ) {
        throw "release-feed-url.txt must contain an absolute HTTPS URL."
    }
}

if ($projectVersion -ne $version -or $release.version -ne $version) {
    throw "Version mismatch: VERSION=$version, project.yml=$projectVersion, manifest=$($release.version)"
}
if ($projectBuild -ne $buildNumber -or "$($release.build)" -ne $buildNumber) {
    throw "Build mismatch: BUILD_NUMBER=$buildNumber, project.yml=$projectBuild, manifest=$($release.build)"
}

Write-Host "HerdMe version $version ($buildNumber) is consistent."
