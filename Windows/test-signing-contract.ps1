$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "sign-windows-artifact.ps1")

$thumbprintMessage =
    "HERDME_WINDOWS_SIGNING_THUMBPRINT must be a 40-character SHA-1 certificate thumbprint."
$timestampMessage =
    "HERDME_WINDOWS_TIMESTAMP_URL must be an absolute HTTPS RFC 3161 timestamp URL."

function Assert-FailsWith([scriptblock]$Operation, [string]$ExpectedMessage) {
    $actualMessage = $null
    try {
        & $Operation | Out-Null
    }
    catch {
        $actualMessage = $_.Exception.Message
    }
    if ($actualMessage -ne $ExpectedMessage) {
        throw "Expected '$ExpectedMessage' but received '$actualMessage'."
    }
}

$originalThumbprint = [Environment]::GetEnvironmentVariable(
    "HERDME_WINDOWS_SIGNING_THUMBPRINT",
    "Process"
)
$hadOriginalThumbprint = $null -ne $originalThumbprint
$originalTimestampUrl = [Environment]::GetEnvironmentVariable(
    "HERDME_WINDOWS_TIMESTAMP_URL",
    "Process"
)
$hadOriginalTimestampUrl = $null -ne $originalTimestampUrl
try {
    Remove-Item Env:HERDME_WINDOWS_SIGNING_THUMBPRINT -ErrorAction SilentlyContinue
    Assert-FailsWith { Get-HerdMeSigningThumbprint } $thumbprintMessage

    $env:HERDME_WINDOWS_SIGNING_THUMBPRINT = "   "
    Assert-FailsWith { Get-HerdMeSigningThumbprint } $thumbprintMessage

    $env:HERDME_WINDOWS_SIGNING_THUMBPRINT = "not-a-thumbprint"
    Assert-FailsWith { Get-HerdMeSigningThumbprint } $thumbprintMessage

    $env:HERDME_WINDOWS_SIGNING_THUMBPRINT =
        "01 23 45 67 89 ab cd ef 01 23 45 67 89 ab cd ef 01 23 45 67"
    $normalized = Get-HerdMeSigningThumbprint
    if ($normalized -ne "0123456789ABCDEF0123456789ABCDEF01234567") {
        throw "The Windows signing thumbprint was not normalized safely."
    }

    Remove-Item Env:HERDME_WINDOWS_TIMESTAMP_URL -ErrorAction SilentlyContinue
    Assert-FailsWith { Get-HerdMeTimestampUri } $timestampMessage

    $env:HERDME_WINDOWS_TIMESTAMP_URL = "timestamp.example.test"
    Assert-FailsWith { Get-HerdMeTimestampUri } $timestampMessage

    $env:HERDME_WINDOWS_TIMESTAMP_URL = "http://timestamp.example.test"
    Assert-FailsWith { Get-HerdMeTimestampUri } $timestampMessage

    $env:HERDME_WINDOWS_TIMESTAMP_URL = "https://timestamp.example.test/rfc3161"
    $timestampUri = Get-HerdMeTimestampUri
    if ($timestampUri.AbsoluteUri -ne "https://timestamp.example.test/rfc3161") {
        throw "The Windows timestamp URL was not preserved safely."
    }
}
finally {
    if ($hadOriginalThumbprint) {
        $env:HERDME_WINDOWS_SIGNING_THUMBPRINT = $originalThumbprint
    }
    else {
        Remove-Item Env:HERDME_WINDOWS_SIGNING_THUMBPRINT -ErrorAction SilentlyContinue
    }
    if ($hadOriginalTimestampUrl) {
        $env:HERDME_WINDOWS_TIMESTAMP_URL = $originalTimestampUrl
    }
    else {
        Remove-Item Env:HERDME_WINDOWS_TIMESTAMP_URL -ErrorAction SilentlyContinue
    }
}

Write-Host "Windows signing configuration contract passed."
