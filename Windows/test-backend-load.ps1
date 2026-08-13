param(
    [string]$HostName = "127.0.0.1",
    [int]$Port = 8080,
    [int]$Connections = 256,
    [int]$HoldSeconds = 5
)

$ErrorActionPreference = "Stop"
if ($Port -le 0 -or $Port -gt 65535) { throw "Port must be between 1 and 65535." }
if ($Connections -le 0 -or $Connections -gt 4096) { throw "Connections must be between 1 and 4096." }

$clients = [System.Collections.Generic.List[System.Net.Sockets.TcpClient]]::new()
$failures = 0
try {
    $tasks = 1..$Connections | ForEach-Object {
        [System.Threading.Tasks.Task]::Run([Action]{
            $client = [System.Net.Sockets.TcpClient]::new()
            try {
                $client.Connect($HostName, $Port)
                [System.Threading.Monitor]::Enter($clients)
                try { $clients.Add($client) }
                finally { [System.Threading.Monitor]::Exit($clients) }
            }
            catch {
                $client.Dispose()
                [System.Threading.Interlocked]::Increment([ref]$failures) | Out-Null
            }
        })
    }
    [System.Threading.Tasks.Task]::WaitAll($tasks)
    Start-Sleep -Seconds $HoldSeconds
    Write-Host "Opened $($clients.Count) connections; $failures failed or were rejected."
}
finally {
    foreach ($client in $clients) { $client.Dispose() }
}
