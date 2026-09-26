param(
    [Parameter(Mandatory = $true)][string]$InstallDirectory,
    [switch]$InspectOnly
)

$ErrorActionPreference = 'Stop'
$sessionId = (Get-Process -Id $PID).SessionId
$installRoot = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\') + '\'
$runtimeRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'HerdMe\Runtimes')).TrimEnd('\') + '\'

function Get-OwnedProcesses {
    @(Get-CimInstance Win32_Process -Filter "SessionId = $sessionId" | Where-Object {
        $path = $_.ExecutablePath
        $path -and (
            $path.StartsWith($installRoot, [StringComparison]::OrdinalIgnoreCase) -or
            $path.StartsWith($runtimeRoot, [StringComparison]::OrdinalIgnoreCase)
        )
    })
}

if ($InspectOnly) {
    Get-OwnedProcesses | Select-Object ProcessId, ParentProcessId, ExecutablePath
    exit 0
}

# New versions can drain database and site processes before releasing the app.
try {
    $shutdown = [Threading.EventWaitHandle]::OpenExisting('Local\HerdMe.Desktop.Shutdown')
    try { [void]$shutdown.Set() } finally { $shutdown.Dispose() }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ((Get-OwnedProcesses).Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
} catch [Threading.WaitHandleCannotBeOpenedException] {
    # Older releases hide on WM_CLOSE; they require the fallback below.
}

# Stop the app first so its supervisor cannot respawn workers during the update.
# /T also stops project-local npm/Python children outside the managed directories.
$owned = @(Get-OwnedProcesses | Sort-Object @{Expression = {
    if ([IO.Path]::GetFileName($_.ExecutablePath) -eq 'HerdMe.Windows.exe') { 0 } else { 1 }
}})
foreach ($candidate in $owned) {
    $current = Get-CimInstance Win32_Process -Filter "ProcessId = $($candidate.ProcessId)"
    if ($null -eq $current -or $current.CreationDate -ne $candidate.CreationDate -or
        $current.ExecutablePath -ne $candidate.ExecutablePath) { continue }
    & "$env:SystemRoot\System32\taskkill.exe" /PID $candidate.ProcessId /T /F | Out-Null
}

$deadline = [DateTime]::UtcNow.AddSeconds(10)
do {
    $remaining = @(Get-OwnedProcesses)
    if ($remaining.Count -eq 0) { exit 0 }
    Start-Sleep -Milliseconds 250
} while ([DateTime]::UtcNow -lt $deadline)
throw 'HerdMe processes could not be stopped. Close them before retrying the update.'
