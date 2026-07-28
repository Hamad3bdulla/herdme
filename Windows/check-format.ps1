param(
    [string]$ProjectPath = (Join-Path $PSScriptRoot "HerdMe.Windows.ContractTests/HerdMe.Windows.ContractTests.csproj"),
    [string]$DotNetPath = "dotnet",
    [ValidateSet("x64")]
    [string]$Architecture = "x64",
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-DotNetChecked {
    param(
        [string[]]$Arguments,
        [string]$FailureMessage
    )

    $lines = @(& $DotNetPath @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    foreach ($line in $lines) {
        Write-Host $line
    }
    if ($exitCode -ne 0) {
        throw "$FailureMessage (exit code $exitCode)."
    }

    return ,$lines
}

$resolvedProject = (Resolve-Path -LiteralPath $ProjectPath).Path
$previousPlatform = $env:Platform
try {
    $env:Platform = $Architecture
    if (-not $NoRestore) {
        Invoke-DotNetChecked `
            -Arguments @(
                "restore",
                $resolvedProject,
                "--verbosity",
                "minimal",
                "-p:Platform=$Architecture"
            ) `
            -FailureMessage "Restoring the C# formatting workspace failed" | Out-Null
    }

    $formatOutput = Invoke-DotNetChecked `
        -Arguments @(
            "format",
            $resolvedProject,
            "--verify-no-changes",
            "--no-restore",
            "--verbosity",
            "diagnostic"
        ) `
        -FailureMessage "C# formatting verification failed"
}
finally {
    $env:Platform = $previousPlatform
}

$outputText = $formatOutput -join "`n"
$loadFailurePatterns = @(
    "Msbuild failed when processing the file",
    "Required references did not load"
)
foreach ($pattern in $loadFailurePatterns) {
    if ($outputText.Contains($pattern, [StringComparison]::OrdinalIgnoreCase)) {
        throw "C# formatting verification did not load the complete project: $pattern"
    }
}

$formattedFiles = [regex]::Match(
    $outputText,
    "Formatted\s+\d+\s+of\s+(\d+)\s+files\.",
    [Text.RegularExpressions.RegexOptions]::IgnoreCase
)
if (-not $formattedFiles.Success -or [int]$formattedFiles.Groups[1].Value -le 0) {
    throw "C# formatting verification did not inspect any source files."
}

Write-Host "C# formatting verification inspected $($formattedFiles.Groups[1].Value) files."
