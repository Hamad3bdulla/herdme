param(
    [string]$HostName = "127.0.0.1",
    [int]$Port = 8080,
    [int]$Connections = 256,
    [int]$HoldSeconds = 5
)

$ErrorActionPreference = "Stop"
if ($Port -le 0 -or $Port -gt 65535) { throw "Port must be between 1 and 65535." }
if ($Connections -le 0 -or $Connections -gt 4096) { throw "Connections must be between 1 and 4096." }
if ($HoldSeconds -lt 0 -or $HoldSeconds -gt 300) { throw "HoldSeconds must be between 0 and 300." }

$clients = [System.Collections.Generic.List[System.Net.Sockets.TcpClient]]::new()
$attempts = [System.Collections.Generic.List[System.Threading.Tasks.Task]]::new()
$failures = 0
try {
    for ($index = 0; $index -lt $Connections; $index++) {
        $client = [System.Net.Sockets.TcpClient]::new()
        $clients.Add($client)
        $attempts.Add($client.ConnectAsync($HostName, $Port))
    }
    # .NET socket tasks do not require a PowerShell runspace on worker threads.
    $allAttempts = [System.Threading.Tasks.Task]::WhenAll($attempts.ToArray())
    try { [void]$allAttempts.Wait(10000) }
    catch [System.AggregateException] { }
    for ($index = 0; $index -lt $attempts.Count; $index++) {
        if ($attempts[$index].Status -ne [System.Threading.Tasks.TaskStatus]::RanToCompletion) {
            $clients[$index].Dispose()
            $failures++
        }
    }
    Start-Sleep -Seconds $HoldSeconds
    Write-Host "Opened $($clients.Count - $failures) connections; $failures failed or were rejected."
}
finally {
    foreach ($client in $clients) { $client.Dispose() }
}
