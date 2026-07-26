function Find-HerdMeSignTool {
    $command = Get-Command "signtool.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path -LiteralPath $kitsRoot -PathType Container) {
        $candidate = Get-ChildItem -LiteralPath $kitsRoot -Recurse -Filter "signtool.exe" -File |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $candidate) { return $candidate.FullName }
    }
    throw "signtool.exe was not found. Install the Windows 10/11 SDK."
}

function Get-HerdMeSigningThumbprint {
    $thumbprint = ($env:HERDME_WINDOWS_SIGNING_THUMBPRINT -replace '\s', '').ToUpperInvariant()
    if ($thumbprint -notmatch '^[0-9A-F]{40}$') {
        throw "HERDME_WINDOWS_SIGNING_THUMBPRINT must be a 40-character SHA-1 certificate thumbprint."
    }
    return $thumbprint
}

function Get-HerdMeTimestampUri {
    $timestampUrl = $env:HERDME_WINDOWS_TIMESTAMP_URL
    $timestampUri = $null
    if (
        [string]::IsNullOrWhiteSpace($timestampUrl) -or
        -not [Uri]::TryCreate($timestampUrl, [UriKind]::Absolute, [ref]$timestampUri) -or
        $timestampUri.Scheme -notin @([Uri]::UriSchemeHttp, [Uri]::UriSchemeHttps)
    ) {
        throw "HERDME_WINDOWS_TIMESTAMP_URL must be an absolute HTTP or HTTPS RFC 3161 timestamp URL."
    }
    return $timestampUri
}

function Assert-HerdMeAuthenticodeSignature(
    [string]$Path,
    [string]$ExpectedThumbprint = (Get-HerdMeSigningThumbprint)
) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if (
        $signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -ne $ExpectedThumbprint
    ) {
        throw "The expected Authenticode certificate was not applied to $Path"
    }
}

function Sign-HerdMePublicArtifact(
    [string]$Path,
    [string]$SignTool = (Find-HerdMeSignTool)
) {
    $thumbprint = Get-HerdMeSigningThumbprint
    $timestampUri = Get-HerdMeTimestampUri

    & $SignTool sign `
        /sha1 $thumbprint `
        /fd SHA256 `
        /tr $timestampUri.AbsoluteUri `
        /td SHA256 `
        /v `
        $Path
    if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed for $Path" }
    & $SignTool verify /pa /all /v $Path
    if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed for $Path" }
    Assert-HerdMeAuthenticodeSignature -Path $Path -ExpectedThumbprint $thumbprint
}
