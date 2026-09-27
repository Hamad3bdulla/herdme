param(
    [int]$TimeoutMinutes = 20,
    [switch]$NoWait
)

# Installs the locally built HerdMe setup in Windows Sandbox (a fresh Windows profile with no
# HerdMe data, certificates, runtimes, or registry values) and runs the clean-profile checks
# from clean-acceptance-sandbox.ps1. Build first with Windows\package-installer.ps1.

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $repoRoot "VERSION") -Raw).Trim()
$installer = Join-Path $repoRoot "dist\HerdMe-$version-win-x64-setup.exe"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw "Build the installer first (Windows\package-installer.ps1); $installer was not found."
}
$sandbox = Join-Path $env:SystemRoot "System32\WindowsSandbox.exe"
if (-not (Test-Path -LiteralPath $sandbox -PathType Leaf)) {
    throw "Windows Sandbox is not enabled. Turn on the 'Windows Sandbox' optional feature (Windows 10/11 Pro, Enterprise, or Education) and try again."
}
if (@(Get-Process -Name "WindowsSandbox", "WindowsSandboxClient" -ErrorAction SilentlyContinue).Count -ne 0) {
    throw "Windows Sandbox is already running. Close it first; only one sandbox can run at a time."
}

$results = Join-Path $repoRoot "build\clean-acceptance-results"
if (Test-Path -LiteralPath $results) {
    Remove-Item -LiteralPath $results -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $results | Out-Null

$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot "clean-acceptance.wsb") -Raw
$escape = { param([string]$Value) [System.Security.SecurityElement]::Escape($Value) }
$configuration = $template.
    Replace("{{REPOSITORY}}", (& $escape $repoRoot)).
    Replace("{{RESULTS}}", (& $escape $results))
$configurationPath = Join-Path $repoRoot "build\clean-acceptance.wsb"
[System.IO.File]::WriteAllText($configurationPath, $configuration, [System.Text.UTF8Encoding]::new($false))

Write-Host "Starting Windows Sandbox with $installer"
Start-Process -FilePath $sandbox -ArgumentList "`"$configurationPath`"" | Out-Null
if ($NoWait) {
    Write-Host "Results will be written to $results"
    return
}

$summaryPath = Join-Path $results "summary.json"
$deadline = [DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
while (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
    if ([DateTime]::UtcNow -ge $deadline) {
        throw "The sandbox did not finish within $TimeoutMinutes minutes. See $results."
    }
    Start-Sleep -Seconds 5
}
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
Write-Host "Clean-profile results: $results"
foreach ($check in $summary.checks) {
    Write-Host ("[{0}] {1}" -f $(if ($check.passed) { "pass" } else { "FAIL" }), $check.name)
}
if (-not $summary.passed) {
    throw "The clean-profile check failed: $($summary.error)"
}
Write-Host "Clean-profile check passed. Close the sandbox window when you are done; it discards everything."
