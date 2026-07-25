param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$LeaveRunning
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($env:OS -ne "Windows_NT") {
    throw "The native Windows acceptance suite must run on Windows."
}
if (-not [Environment]::Is64BitOperatingSystem -or -not [Environment]::Is64BitProcess) {
    throw "The native Windows acceptance suite requires Windows x64 hardware."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDirectory = Join-Path $repoRoot "build\windows-portable-win-x64"
$executable = Join-Path $publishDirectory "HerdMe.Windows.exe"
$project = Join-Path $PSScriptRoot "HerdMe.Windows\HerdMe.Windows.csproj"
$contractProject = Join-Path $PSScriptRoot "HerdMe.Windows.ContractTests\HerdMe.Windows.ContractTests.csproj"
[xml]$projectXml = Get-Content -Raw $project
$version = ($projectXml.Project.PropertyGroup.Version | Select-Object -First 1)
$archive = Join-Path $repoRoot "dist\HerdMe-$version-win-x64-portable.zip"
$checksumFile = "$archive.sha256"

& (Join-Path $PSScriptRoot "package-portable.ps1") `
    -Architecture x64 `
    -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "The Windows package gate failed." }

dotnet run `
    --project $contractProject `
    --configuration $Configuration `
    --no-build `
    --no-restore `
    -- `
    --live-service-releases `
    --live-runtime-releases
if ($LASTEXITCODE -ne 0) { throw "A managed service or runtime release source is unavailable or invalid." }

if (-not (Test-Path $executable -PathType Leaf)) {
    throw "The packaged HerdMe executable was not found."
}
if (-not (Test-Path $archive -PathType Leaf) -or -not (Test-Path $checksumFile -PathType Leaf)) {
    throw "The Windows ZIP or SHA-256 sidecar was not produced."
}
$checksumParts = @((Get-Content -Raw $checksumFile).Trim() -split "\s+")
if ($checksumParts.Count -lt 2 -or $checksumParts[1] -ne (Split-Path -Leaf $archive)) {
    throw "The Windows checksum sidecar has an invalid filename."
}
$actualHash = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant()
if ($checksumParts[0].ToLowerInvariant() -ne $actualHash) {
    throw "The Windows portable ZIP does not match its SHA-256 sidecar."
}

function Get-HerdMeProcesses {
    @(Get-Process -Name "HerdMe.Windows" -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -eq $executable } catch { $false }
    })
}

function Assert-ResponsePrefix(
    [System.IO.StreamReader]$Reader,
    [string]$Prefix,
    [string]$Step
) {
    $line = $Reader.ReadLine()
    if ($null -eq $line -or -not $line.StartsWith($Prefix, [StringComparison]::Ordinal)) {
        throw "The SMTP acceptance probe failed during ${Step}: $line"
    }
}

function Wait-CapturedRecord(
    [string]$Directory,
    [string]$Property,
    [string]$ExpectedValue,
    [string]$Component
) {
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        foreach ($file in @(Get-ChildItem -LiteralPath $Directory -Filter "*.json" -File -ErrorAction SilentlyContinue)) {
            try {
                $record = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
                if ($record.$Property -eq $ExpectedValue) {
                    return [PSCustomObject]@{ File = $file; Record = $record }
                }
            }
            catch {
            }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "The $Component acceptance payload was not persisted."
}

function Assert-SmtpCapture {
    $nonce = [Guid]::NewGuid().ToString("N")
    $subject = "HerdMe acceptance $nonce"
    $mailDirectory = Join-Path ([Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData
    )) "HerdMe\Mail"
    $captured = $null
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $client.ReceiveTimeout = 5000
        $client.SendTimeout = 5000
        $client.Connect([System.Net.IPAddress]::Loopback, 2525)
        $stream = $client.GetStream()
        $reader = [System.IO.StreamReader]::new(
            $stream,
            [System.Text.Encoding]::UTF8,
            $false,
            4096,
            $true
        )
        $writer = [System.IO.StreamWriter]::new(
            $stream,
            [System.Text.UTF8Encoding]::new($false),
            4096,
            $true
        )
        try {
            $writer.NewLine = "`r`n"
            $writer.AutoFlush = $true
            Assert-ResponsePrefix $reader "220 HerdMe SMTP ready" "greeting"
            $writer.WriteLine("EHLO acceptance.herdme.test")
            Assert-ResponsePrefix $reader "250-HerdMe" "EHLO"
            Assert-ResponsePrefix $reader "250-8BITMIME" "EHLO capabilities"
            Assert-ResponsePrefix $reader "250 SIZE" "EHLO size"
            $writer.WriteLine("MAIL FROM:<acceptance@herdme.test>")
            Assert-ResponsePrefix $reader "250" "MAIL FROM"
            $writer.WriteLine("RCPT TO:<inbox@herdme.test>")
            Assert-ResponsePrefix $reader "250" "RCPT TO"
            $writer.WriteLine("DATA")
            Assert-ResponsePrefix $reader "354" "DATA"
            $writer.WriteLine("From: acceptance@herdme.test")
            $writer.WriteLine("To: inbox@herdme.test")
            $writer.WriteLine("Subject: $subject")
            $writer.WriteLine("Content-Type: text/plain; charset=utf-8")
            $writer.WriteLine("")
            $writer.WriteLine("Live Windows SMTP acceptance payload")
            $writer.WriteLine(".")
            Assert-ResponsePrefix $reader "250" "message persistence"
            $writer.WriteLine("QUIT")
            Assert-ResponsePrefix $reader "221" "QUIT"
        }
        finally {
            $writer.Dispose()
            $reader.Dispose()
        }

        $captured = Wait-CapturedRecord $mailDirectory "Subject" $subject "SMTP"
        if (
            $captured.Record.Sender -ne "acceptance@herdme.test" -or
            $captured.Record.Raw -notlike "*Live Windows SMTP acceptance payload*"
        ) {
            throw "The SMTP acceptance payload was persisted incorrectly."
        }
    }
    finally {
        $client.Dispose()
        if ($null -eq $captured) {
            try {
                $captured = Wait-CapturedRecord $mailDirectory "Subject" $subject "SMTP cleanup"
            }
            catch {
            }
        }
        if ($null -ne $captured -and (Test-Path -LiteralPath $captured.File.FullName)) {
            Remove-Item -LiteralPath $captured.File.FullName -Force
        }
    }
}

function Assert-DumpCapture {
    $nonce = [Guid]::NewGuid().ToString("N")
    $source = "herdme-acceptance-$nonce.php"
    $serialized = "a:2:{s:4:`"file`";s:$($source.Length):`"$source`";s:5:`"value`";s:2:`"ok`";}"
    $payload = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($serialized))
    $dumpDirectory = Join-Path ([Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData
    )) "HerdMe\Dumps"
    $captured = $null
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $client.SendTimeout = 5000
        $client.Connect([System.Net.IPAddress]::Loopback, 9912)
        $writer = [System.IO.StreamWriter]::new(
            $client.GetStream(),
            [System.Text.UTF8Encoding]::new($false),
            4096,
            $true
        )
        try {
            $writer.NewLine = "`n"
            $writer.WriteLine($payload)
            $writer.Flush()
        }
        finally {
            $writer.Dispose()
        }

        $captured = Wait-CapturedRecord $dumpDirectory "Source" $source "VarDumper"
        if ($captured.Record.Payload -ne $payload -or $captured.Record.Summary -notlike '*value: "ok"*') {
            throw "The VarDumper acceptance payload was persisted incorrectly."
        }
    }
    finally {
        $client.Dispose()
        if ($null -eq $captured) {
            try {
                $captured = Wait-CapturedRecord $dumpDirectory "Source" $source "VarDumper cleanup"
            }
            catch {
            }
        }
        if ($null -ne $captured -and (Test-Path -LiteralPath $captured.File.FullName)) {
            Remove-Item -LiteralPath $captured.File.FullName -Force
        }
    }
}

$startedBySuite = $false
$processes = @(Get-HerdMeProcesses)
if ($processes.Count -eq 0) {
    Start-Process -FilePath $executable -ArgumentList "--acceptance" | Out-Null
    $startedBySuite = $true
}

try {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 250
        $processes = @(Get-HerdMeProcesses)
    } while ($processes.Count -eq 0 -and [DateTime]::UtcNow -lt $deadline)
    if ($processes.Count -ne 1) {
        throw "HerdMe did not start as exactly one process."
    }

    $primary = $processes[0]
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ($primary.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $primary.Refresh()
    }
    if ($primary.MainWindowHandle -eq 0) {
        throw "The native HerdMe window did not become available."
    }

    Start-Process -FilePath $executable | Out-Null
    Start-Sleep -Seconds 2
    $processes = @(Get-HerdMeProcesses)
    if ($processes.Count -ne 1 -or $processes[0].Id -ne $primary.Id) {
        throw "Launching HerdMe twice did not preserve a single primary process."
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object {
            $_.OwningProcess -eq $primary.Id -and $_.LocalPort -in @(2525, 9912)
        })
        $listenerPorts = @($listeners.LocalPort)
        if ($listenerPorts -contains 2525 -and $listenerPorts -contains 9912) { break }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($listenerPorts -notcontains 2525) {
        throw "The Windows SMTP capture listener did not start on loopback port 2525."
    }
    if ($listenerPorts -notcontains 9912) {
        throw "The Windows dump capture listener did not start on loopback port 9912."
    }
    $nonLoopbackListeners = @($listeners | Where-Object {
        $_.LocalAddress -notin @("127.0.0.1", "::1")
    })
    if ($nonLoopbackListeners.Count -ne 0) {
        $bindings = ($nonLoopbackListeners | ForEach-Object {
            "$($_.LocalAddress):$($_.LocalPort)"
        }) -join ", "
        throw "A capture listener is exposed outside loopback: $bindings"
    }

    Assert-SmtpCapture
    Assert-DumpCapture

    Write-Host "Automated Windows x64 acceptance checks passed."
    Write-Host "Complete the interactive certificate, hosts/UAC, runtime, service, and WinUI checks in Windows\ACCEPTANCE.md."
}
finally {
    if ($startedBySuite -and -not $LeaveRunning) {
        @(Get-HerdMeProcesses) | Stop-Process -Force
    }
}
