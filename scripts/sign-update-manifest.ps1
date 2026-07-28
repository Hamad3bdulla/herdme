param(
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][string]$PrivateKeyPath,
    [Parameter(Mandatory = $true)][string]$SignedManifestPath,
    [string]$PublicKeyPath
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "Manifest not found: $ManifestPath"
}
if (-not (Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf)) {
    throw "Private key not found: $PrivateKeyPath"
}
if ((Get-Item -LiteralPath $ManifestPath).Length -gt 4MB) {
    throw "The release manifest exceeds the 4 MB application limit."
}

function Assert-HttpsUrl([object]$Value, [string]$Label) {
    $text = [string]$Value
    $uri = $null
    if (
        [string]::IsNullOrWhiteSpace($text) -or
        -not [Uri]::TryCreate($text, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne [Uri]::UriSchemeHttps -or
        [string]::IsNullOrWhiteSpace($uri.Host)
    ) {
        throw "$Label must be an absolute HTTPS URL."
    }
    return $uri.AbsoluteUri
}

$semanticVersionPattern = '^v?(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-(?:(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+(?:[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$'

try {
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
}
catch {
    throw "The release manifest is invalid JSON: $($_.Exception.Message)"
}
$releases = @($manifest.releases)
if ($null -eq $manifest.releases -or $releases.Count -eq 0) {
    throw "The release manifest must contain at least one release."
}
$identities = @{}
for ($index = 0; $index -lt $releases.Count; $index++) {
    $release = $releases[$index]
    $label = "releases[$index]"
    if ([string]$release.version -notmatch $semanticVersionPattern) {
        throw "$label.version is invalid."
    }
    $build = 0L
    if (-not [long]::TryParse([string]$release.build, [ref]$build) -or $build -lt 0) {
        throw "$label.build must be a nonnegative integer."
    }
    if (@("stable", "beta") -notcontains ([string]$release.channel).ToLowerInvariant()) {
        throw "$label.channel must be stable or beta."
    }
    if ($null -eq $release.notes -or $release.notes -isnot [string]) {
        throw "$label.notes must be a string."
    }
    if ($null -ne $release.downloadURL) {
        throw "$label.downloadURL is obsolete; use downloadURLs."
    }
    if ($null -eq $release.downloadURLs) {
        throw "$label.downloadURLs must contain both platform artifacts."
    }
    $artifactVersion = ([string]$release.version) -replace '^v', ''
    $identity = "$artifactVersion|$build|$(([string]$release.channel).ToLowerInvariant())"
    if ($identities.ContainsKey($identity)) {
        throw "$label duplicates another release identity."
    }
    $identities[$identity] = $true
    $macOSUrl = Assert-HttpsUrl $release.downloadURLs.macOS "$label.downloadURLs.macOS"
    $windowsUrl = Assert-HttpsUrl $release.downloadURLs.windowsX64 "$label.downloadURLs.windowsX64"
    if ($macOSUrl -eq $windowsUrl) {
        throw "$label must not use the same artifact for macOS and Windows."
    }
    $macOSUri = [Uri]$macOSUrl
    $windowsUri = [Uri]$windowsUrl
    $expectedMacOS = "HerdMe-$artifactVersion-macOS.zip"
    $expectedWindows = "HerdMe-$artifactVersion-win-x64-setup.exe"
    if ([Uri]::UnescapeDataString($macOSUri.Segments[-1]) -ne $expectedMacOS) {
        throw "$label.downloadURLs.macOS must end with $expectedMacOS."
    }
    if ([Uri]::UnescapeDataString($windowsUri.Segments[-1]) -ne $expectedWindows) {
        throw "$label.downloadURLs.windowsX64 must end with $expectedWindows."
    }
}

$signer = [System.Security.Cryptography.ECDsa]::Create()
try {
    $signer.ImportFromPem([System.IO.File]::ReadAllText($PrivateKeyPath))
    $parameters = $signer.ExportParameters($false)
    if (
        $parameters.Curve.Oid.Value -ne "1.2.840.10045.3.1.7" -or
        $parameters.Q.X.Length -ne 32 -or
        $parameters.Q.Y.Length -ne 32
    ) {
        throw "The private key must be an ECDSA P-256 key."
    }

    $payload = [System.IO.File]::ReadAllBytes($ManifestPath)
    $signature = $signer.SignData(
        $payload,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence
    )
    $publicKey = [byte[]]::new(65)
    $publicKey[0] = 4
    [System.Array]::Copy($parameters.Q.X, 0, $publicKey, 1, 32)
    [System.Array]::Copy($parameters.Q.Y, 0, $publicKey, 33, 32)

    $envelope = [ordered]@{
        algorithm = "ECDSA_P256_SHA256"
        payload = [System.Convert]::ToBase64String($payload)
        signature = [System.Convert]::ToBase64String($signature)
    } | ConvertTo-Json

    $signedDirectory = Split-Path -Parent $SignedManifestPath
    if ($signedDirectory) { [System.IO.Directory]::CreateDirectory($signedDirectory) | Out-Null }
    [System.IO.File]::WriteAllText($SignedManifestPath, $envelope + [Environment]::NewLine)

    if ($PublicKeyPath) {
        $publicDirectory = Split-Path -Parent $PublicKeyPath
        if ($publicDirectory) { [System.IO.Directory]::CreateDirectory($publicDirectory) | Out-Null }
        [System.IO.File]::WriteAllText(
            $PublicKeyPath,
            [System.Convert]::ToBase64String($publicKey) + [Environment]::NewLine
        )
    }
}
finally {
    $signer.Dispose()
}

Write-Host "Signed update manifest: $SignedManifestPath"
if ($PublicKeyPath) { Write-Host "Public verification key: $PublicKeyPath" }
