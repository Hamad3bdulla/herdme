# Runs inside Windows Sandbox (see start-clean-acceptance.ps1). The sandbox is a fresh Windows
# profile, so this proves a first install works with no HerdMe data, certificates, runtimes,
# registry values, or developer tools on the PC. Results go to the mapped results folder.

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repository = "C:\HerdMe\repo"
$results = "C:\HerdMe\results"
$checks = [System.Collections.Generic.List[object]]::new()
$failure = $null
Start-Transcript -Path (Join-Path $results "sandbox.log") -Force | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Add-Check([string]$Name, [bool]$Passed, [string]$Detail = "") {
    $checks.Add([ordered]@{ name = $Name; passed = $Passed; detail = $Detail })
    Write-Host ("[{0}] {1} {2}" -f $(if ($Passed) { "pass" } else { "FAIL" }), $Name, $Detail)
    if (-not $Passed) { throw "$Name failed. $Detail" }
}

function Get-HerdMeProcesses {
    @(Get-Process -Name "HerdMe.Windows" -ErrorAction SilentlyContinue)
}

function Wait-MainWindow([System.Diagnostics.Process]$Process, [int]$TimeoutSeconds = 60) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($Process.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $Process.Refresh()
        if ($Process.HasExited) { throw "HerdMe exited with code $($Process.ExitCode) before showing a window." }
    }
    if ($Process.MainWindowHandle -eq 0) { throw "HerdMe did not show a window within $TimeoutSeconds seconds." }
    return [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$Process.MainWindowHandle)
}

function Wait-Element([System.Windows.Automation.AutomationElement]$Root, [string]$AutomationId, [int]$TimeoutSeconds = 30) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "The element '$AutomationId' did not appear."
}

function Stop-HerdMe {
    Get-HerdMeProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ((Get-HerdMeProcesses).Count -ne 0 -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
}

$optInKeys = @(
    "HKCU:\Software\Classes\herdme",
    "HKCU:\Software\Classes\Directory\shell\HerdMe.Link",
    "HKCU:\Software\Classes\Directory\Background\shell\HerdMe.Link"
)
$terminalFragment = Join-Path $env:LOCALAPPDATA "Microsoft\Windows Terminal\Fragments\HerdMe"

try {
    $version = (Get-Content -LiteralPath (Join-Path $repository "VERSION") -Raw).Trim()
    $installer = Join-Path $repository "dist\HerdMe-$version-win-x64-setup.exe"
    $dataRoot = Join-Path $env:LOCALAPPDATA "HerdMe"
    $installRoot = Join-Path $env:LOCALAPPDATA "Programs\HerdMe"
    $app = Join-Path $installRoot "HerdMe.Windows.exe"

    Add-Check "clean profile has no HerdMe data" (-not (Test-Path -LiteralPath $dataRoot))

    $setupLog = Join-Path $results "setup.log"
    $setup = Start-Process -FilePath $installer -ArgumentList @(
        "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=`"$setupLog`""
    ) -PassThru -Wait
    Add-Check "silent per-user install" ($setup.ExitCode -eq 0 -and (Test-Path -LiteralPath $app -PathType Leaf)) "exit code $($setup.ExitCode)"

    $cli = Join-Path $installRoot "herdme.exe"
    $cliVersion = if (Test-Path -LiteralPath $cli -PathType Leaf) { (@(& $cli --version 2>&1) -join " ").Trim() } else { "missing" }
    Add-Check "herdme command reports the version" ($cliVersion -eq "herdme $version") $cliVersion

    # First run on a clean profile shows onboarding and nothing else.
    $first = Start-Process -FilePath $app -PassThru
    $window = Wait-MainWindow $first
    $null = Wait-Element $window "OnboardingStartButton" 60
    Add-Check "first run shows onboarding" $true
    Start-Process -FilePath $app | Out-Null
    Start-Sleep -Seconds 3
    $running = @(Get-HerdMeProcesses)
    Add-Check "a second launch keeps one HerdMe process" ($running.Count -eq 1 -and $running[0].Id -eq $first.Id) "$($running.Count) process(es)"
    Stop-HerdMe

    $main = Start-Process -FilePath $app -ArgumentList "--acceptance" -PassThru
    $window = Wait-MainWindow $main
    $null = Wait-Element $window "NavigationRoot" 60
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 500
        $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.OwningProcess -eq $main.Id })
    } while (@($listeners | Where-Object { $_.LocalPort -in @(2525, 9912) }).Count -lt 2 -and [DateTime]::UtcNow -lt $deadline)
    $exposed = @($listeners | Where-Object { $_.LocalAddress -notin @("127.0.0.1", "::1") } | ForEach-Object { "$($_.LocalAddress):$($_.LocalPort)" })
    Add-Check "mail and dump capture listen" (@($listeners | Where-Object { $_.LocalPort -in @(2525, 9912) }).Count -ge 2)
    Add-Check "every HerdMe listener is loopback-only" ($exposed.Count -eq 0) ($exposed -join ", ")
    Stop-HerdMe

    $written = @($optInKeys | Where-Object { Test-Path -LiteralPath $_ })
    Add-Check "Explorer menu and herdme:// links stay off until enabled" ($written.Count -eq 0) ($written -join ", ")
    Add-Check "the Windows Terminal profile stays off until enabled" (-not (Test-Path -LiteralPath $terminalFragment))

    & (Join-Path $repository "Windows\test-accessibility.ps1") -Executable $app -ReportDirectory $results
    Add-Check "accessibility scan of every page" $true
    Stop-HerdMe

    $uninstaller = Join-Path $installRoot "unins000.exe"
    $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART") -PassThru -Wait
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ((Test-Path -LiteralPath $app) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Seconds 1 }
    Add-Check "silent uninstall removes the app" ($uninstall.ExitCode -eq 0 -and -not (Test-Path -LiteralPath $app)) "exit code $($uninstall.ExitCode)"
}
catch {
    $failure = $_.Exception.Message
    Write-Host "Clean-profile check stopped: $failure"
}
finally {
    Stop-HerdMe
    $summary = [ordered]@{
        passed = ($null -eq $failure)
        error = $failure
        windows = [Environment]::OSVersion.VersionString
        culture = [System.Globalization.CultureInfo]::CurrentUICulture.Name
        checks = @($checks)
    }
    # summary.json is written last; the host waits for it.
    [System.IO.File]::WriteAllText(
        (Join-Path $results "summary.json"),
        ($summary | ConvertTo-Json -Depth 5) + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false)
    )
    Stop-Transcript | Out-Null
}
