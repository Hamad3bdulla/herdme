param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$LeaveRunning,
    [switch]$SkipLiveReleaseChecks
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($env:OS -ne "Windows_NT") {
    throw "The native Windows acceptance suite must run on Windows."
}
if (-not [Environment]::Is64BitOperatingSystem -or -not [Environment]::Is64BitProcess) {
    throw "The native Windows acceptance suite requires Windows x64 hardware."
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$repoRoot = Split-Path -Parent $PSScriptRoot
$releaseMode = if ([string]::IsNullOrWhiteSpace($env:HERDME_RELEASE_MODE)) {
    "local"
} else {
    $env:HERDME_RELEASE_MODE.ToLowerInvariant()
}
if ($releaseMode -notin @("local", "public")) {
    throw "HERDME_RELEASE_MODE must be local or public."
}
if ($releaseMode -eq "public" -and $SkipLiveReleaseChecks) {
    throw "Public release acceptance cannot skip live runtime and service release checks."
}
$publishDirectory = Join-Path $repoRoot "build\windows-portable-win-x64"
$script:captureDatabaseDriverInitialized = $false
$executable = Join-Path $publishDirectory "HerdMe.Windows.exe"
$project = Join-Path $PSScriptRoot "HerdMe.Windows\HerdMe.Windows.csproj"
$contractProject = Join-Path $PSScriptRoot "HerdMe.Windows.ContractTests\HerdMe.Windows.ContractTests.csproj"
$version = (Get-Content -LiteralPath (Join-Path $repoRoot "VERSION") -Raw).Trim()
$archive = Join-Path $repoRoot "dist\HerdMe-$version-win-x64-portable.zip"
$checksumFile = "$archive.sha256"
$installer = Join-Path $repoRoot "dist\HerdMe-$version-win-x64-setup.exe"
$installerChecksumFile = "$installer.sha256"
$installerTestDirectory = Join-Path $repoRoot "build\windows-installer-acceptance"
$startupRegistryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$startupValueName = "HerdMe"
$onboardingAfterReinstallPath = Join-Path ([Environment]::GetFolderPath(
    [Environment+SpecialFolder]::LocalApplicationData
)) "HerdMe\Config\onboarding-after-reinstall.flag"
$onboardingAfterReinstallExisted = Test-Path -LiteralPath $onboardingAfterReinstallPath -PathType Leaf
$onboardingAfterReinstallContents = if ($onboardingAfterReinstallExisted) {
    [System.IO.File]::ReadAllBytes($onboardingAfterReinstallPath)
} else {
    $null
}
if ($releaseMode -eq "public") {
    . (Join-Path $PSScriptRoot "sign-windows-artifact.ps1")
}

function Get-HerdMeStartupValue {
    $properties = Get-ItemProperty `
        -LiteralPath $startupRegistryPath `
        -Name $startupValueName `
        -ErrorAction SilentlyContinue
    if ($null -eq $properties) { return $null }
    return [string]$properties.$startupValueName
}

$preExistingProcesses = @(Get-Process -Name "HerdMe.Windows" -ErrorAction SilentlyContinue)
if ($preExistingProcesses.Count -gt 0) {
    $preExistingProcesses | Stop-Process -Force
    foreach ($process in $preExistingProcesses) {
        if (-not $process.WaitForExit(5000)) {
            throw "HerdMe process $($process.Id) did not exit before the Windows build."
        }
    }
}

& (Join-Path $PSScriptRoot "package-portable.ps1") `
    -Architecture x64 `
    -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "The Windows package gate failed." }
& (Join-Path $PSScriptRoot "package-installer.ps1") `
    -Architecture x64 `
    -Configuration $Configuration `
    -SkipPortableBuild
if ($LASTEXITCODE -ne 0) { throw "The Windows installer gate failed." }

if (-not $SkipLiveReleaseChecks) {
    dotnet run `
        --project $contractProject `
        --configuration $Configuration `
        --no-build `
        --no-restore `
        -- `
        --live-service-releases `
        --live-runtime-releases
    if ($LASTEXITCODE -ne 0) { throw "A managed service or runtime release source is unavailable or invalid." }
}

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
if (
    -not (Test-Path -LiteralPath $installer -PathType Leaf) -or
    -not (Test-Path -LiteralPath $installerChecksumFile -PathType Leaf)
) {
    throw "The Windows installer or SHA-256 sidecar was not produced."
}
$installerChecksumParts = @((Get-Content -Raw $installerChecksumFile).Trim() -split "\s+")
if (
    $installerChecksumParts.Count -lt 2 -or
    $installerChecksumParts[1] -ne (Split-Path -Leaf $installer)
) {
    throw "The Windows installer checksum sidecar has an invalid filename."
}
$actualInstallerHash = (Get-FileHash -Algorithm SHA256 $installer).Hash.ToLowerInvariant()
if ($installerChecksumParts[0].ToLowerInvariant() -ne $actualInstallerHash) {
    throw "The Windows installer does not match its SHA-256 sidecar."
}

if (Test-Path -LiteralPath $installerTestDirectory) {
    Remove-Item -LiteralPath $installerTestDirectory -Recurse -Force
}
if ($null -ne (Get-HerdMeStartupValue)) {
    throw "Windows installer acceptance requires no pre-existing HerdMe startup value."
}
$installProcess = Start-Process `
    -FilePath $installer `
    -ArgumentList @(
        "/VERYSILENT",
        "/SUPPRESSMSGBOXES",
        "/NORESTART",
        "/DIR=`"$installerTestDirectory`""
    ) `
    -Wait `
    -PassThru
if ($installProcess.ExitCode -ne 0) {
    throw "The Windows installer acceptance run failed with exit code $($installProcess.ExitCode)."
}
if ($null -ne (Get-HerdMeStartupValue)) {
    throw "The Windows installer enabled launch at login without user consent."
}
$installedExecutable = Join-Path $installerTestDirectory "HerdMe.Windows.exe"
$installedCore = Join-Path $installerTestDirectory "Runtime\herdme-core.exe"
$installedTrayIcon = Join-Path $installerTestDirectory "Assets\HerdMe.ico"
$installedVcRuntimeFiles = @(
    "concrt140.dll",
    "msvcp140.dll",
    "msvcp140_1.dll",
    "msvcp140_2.dll",
    "msvcp140_atomic_wait.dll",
    "msvcp140_codecvt_ids.dll",
    "vccorlib140.dll",
    "vcruntime140.dll",
    "vcruntime140_1.dll",
    "vcruntime140_threads.dll"
) | ForEach-Object { Join-Path $installerTestDirectory "Prerequisites\VC143\$_" }
foreach ($installedFile in @($installedExecutable, $installedCore, $installedTrayIcon) + $installedVcRuntimeFiles) {
    if (-not (Test-Path -LiteralPath $installedFile -PathType Leaf)) {
        throw "The Windows installer did not install $installedFile."
    }
}
if ($releaseMode -eq "public") {
    $expectedThumbprint = Get-HerdMeSigningThumbprint
    Assert-HerdMeAuthenticodeSignature `
        -Path $installer `
        -ExpectedThumbprint $expectedThumbprint
    Assert-HerdMeAuthenticodeSignature `
        -Path $installedExecutable `
        -ExpectedThumbprint $expectedThumbprint
    Assert-HerdMeAuthenticodeSignature `
        -Path $installedCore `
        -ExpectedThumbprint $expectedThumbprint
}
& $installedCore doctor
if ($LASTEXITCODE -ne 0) { throw "The installed portable core health check failed." }
$uninstallers = @(Get-ChildItem -LiteralPath $installerTestDirectory -Filter "unins*.exe" -File)
if ($uninstallers.Count -ne 1) {
    throw "The Windows installer did not create exactly one uninstaller."
}
New-Item -ItemType Directory -Path $startupRegistryPath -Force | Out-Null
New-ItemProperty `
    -LiteralPath $startupRegistryPath `
    -Name $startupValueName `
    -Value "`"$installedExecutable`" --background" `
    -PropertyType String `
    -Force | Out-Null
$uninstallProcess = Start-Process `
    -FilePath $uninstallers[0].FullName `
    -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART") `
    -Wait `
    -PassThru
if ($uninstallProcess.ExitCode -ne 0) {
    throw "The Windows uninstaller acceptance run failed with exit code $($uninstallProcess.ExitCode)."
}
if (Test-Path -LiteralPath $installedExecutable -PathType Leaf) {
    throw "The Windows uninstaller left the application executable installed."
}
if ($null -ne (Get-HerdMeStartupValue)) {
    throw "The Windows uninstaller left HerdMe enabled at login."
}
if ($onboardingAfterReinstallExisted) {
    [System.IO.File]::WriteAllBytes(
        $onboardingAfterReinstallPath,
        $onboardingAfterReinstallContents
    )
} elseif (Test-Path -LiteralPath $onboardingAfterReinstallPath) {
    Remove-Item -LiteralPath $onboardingAfterReinstallPath -Force
}
if (Test-Path -LiteralPath $installerTestDirectory) {
    Remove-Item -LiteralPath $installerTestDirectory -Recurse -Force
}

function Get-HerdMeProcesses {
    @(Get-Process -Name "HerdMe.Windows" -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -eq $executable } catch { $false }
    })
}

function Wait-AutomationElementById(
    [System.Windows.Automation.AutomationElement]$Root,
    [string]$AutomationId,
    [int]$TimeoutSeconds = 10
) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $element = $Root.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $condition
        )
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "The WinUI element '$AutomationId' did not become available."
}

function Select-AutomationElement(
    [System.Windows.Automation.AutomationElement]$Element,
    [string]$AutomationId
) {
    $patternObject = $null
    if ($Element.TryGetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern,
        [ref]$patternObject
    )) {
        ([System.Windows.Automation.SelectionItemPattern]$patternObject).Select()
        return
    }

    $patternObject = $null
    if ($Element.TryGetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern,
        [ref]$patternObject
    )) {
        ([System.Windows.Automation.InvokePattern]$patternObject).Invoke()
        return
    }

    throw "The WinUI navigation element '$AutomationId' is not selectable."
}

function Assert-ResizableMainWindow(
    [System.Diagnostics.Process]$Process
) {
    $window = [System.Windows.Automation.AutomationElement]::FromHandle(
        [IntPtr]$Process.MainWindowHandle
    )
    if ($null -eq $window) {
        throw "The native HerdMe window is unavailable to UI Automation."
    }

    $windowPatternObject = $null
    if (-not $window.TryGetCurrentPattern(
        [System.Windows.Automation.WindowPattern]::Pattern,
        [ref]$windowPatternObject
    )) {
        throw "The native HerdMe window does not expose its window controls."
    }
    $transformPatternObject = $null
    if (-not $window.TryGetCurrentPattern(
        [System.Windows.Automation.TransformPattern]::Pattern,
        [ref]$transformPatternObject
    )) {
        throw "The native HerdMe window does not expose its resize state."
    }

    $windowPattern = [System.Windows.Automation.WindowPattern]$windowPatternObject
    $transformPattern = [System.Windows.Automation.TransformPattern]$transformPatternObject
    if (
        -not $windowPattern.Current.CanMaximize -or
        -not $windowPattern.Current.CanMinimize -or
        -not $transformPattern.Current.CanResize
    ) {
        throw "The native HerdMe window must support minimizing, maximizing, and resizing."
    }
}

function Assert-OnboardingLayout(
    [System.Diagnostics.Process]$Process
) {
    $window = [System.Windows.Automation.AutomationElement]::FromHandle(
        [IntPtr]$Process.MainWindowHandle
    )
    if ($null -eq $window) {
        throw "The onboarding window is unavailable to UI Automation."
    }
    $startButton = Wait-AutomationElementById $window "OnboardingStartButton"
    $windowBounds = $window.Current.BoundingRectangle
    $buttonBounds = $startButton.Current.BoundingRectangle
    if (
        $startButton.Current.IsOffscreen -or
        $buttonBounds.Width -le 0 -or
        $buttonBounds.Height -le 0 -or
        $buttonBounds.Left -lt $windowBounds.Left -or
        $buttonBounds.Right -gt $windowBounds.Right -or
        $buttonBounds.Top -lt $windowBounds.Top -or
        $buttonBounds.Bottom -gt $windowBounds.Bottom
    ) {
        throw "The onboarding start button is clipped or outside the window."
    }
}

function Assert-WinUiNavigation(
    [System.Diagnostics.Process]$Process
) {
    $window = [System.Windows.Automation.AutomationElement]::FromHandle(
        [IntPtr]$Process.MainWindowHandle
    )
    if ($null -eq $window) {
        throw "The native HerdMe window is unavailable to UI Automation."
    }

    $navigation = Wait-AutomationElementById $window "NavigationRoot"
    $pages = @(
        @{ Navigation = "NavDashboard"; Page = "DashboardPageRoot" },
        @{ Navigation = "NavGeneral"; Page = "GeneralPageRoot" },
        @{ Navigation = "NavSites"; Page = "SitesPageRoot" },
        @{ Navigation = "NavPhp"; Page = "PhpPageRoot" },
        @{ Navigation = "NavNode"; Page = "NodePageRoot" },
        @{ Navigation = "NavServices"; Page = "ServicesPageRoot" },
        @{ Navigation = "NavUpdates"; Page = "UpdatesPageRoot" },
        @{ Navigation = "NavMail"; Page = "MailPageRoot" },
        @{ Navigation = "NavDumps"; Page = "DumpsPageRoot" },
        @{ Navigation = "NavDebugger"; Page = "DebuggerPageRoot" },
        @{ Navigation = "NavLogs"; Page = "LogsPageRoot" },
        @{ Navigation = "NavAbout"; Page = "AboutPageRoot" }
    )

    foreach ($page in $pages) {
        $navigationItem = Wait-AutomationElementById $navigation $page.Navigation
        Select-AutomationElement $navigationItem $page.Navigation
        $null = Wait-AutomationElementById $window $page.Page
        Start-Sleep -Milliseconds 300
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "HerdMe exited while opening '$($page.Navigation)'."
        }
        Write-Host "Verified WinUI page: $($page.Page)"
    }
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

function Initialize-CaptureDatabaseDriver {
    if ($script:captureDatabaseDriverInitialized) { return }

    $nativeLibrary = Join-Path $publishDirectory "e_sqlite3.dll"
    $managedLibraries = @(
        "SQLitePCLRaw.core.dll",
        "SQLitePCLRaw.provider.e_sqlite3.dll",
        "SQLitePCLRaw.batteries_v2.dll",
        "Microsoft.Data.Sqlite.dll"
    )
    foreach ($library in @($nativeLibrary) + @($managedLibraries | ForEach-Object {
        Join-Path $publishDirectory $_
    })) {
        if (-not (Test-Path -LiteralPath $library -PathType Leaf)) {
            throw "The packaged SQLite dependency was not found: $library"
        }
    }

    [System.Runtime.InteropServices.NativeLibrary]::Load($nativeLibrary) | Out-Null
    foreach ($library in $managedLibraries) {
        [System.Reflection.Assembly]::LoadFrom((Join-Path $publishDirectory $library)) | Out-Null
    }
    [SQLitePCL.Batteries_V2]::Init()
    $script:captureDatabaseDriverInitialized = $true
}

function Open-CaptureDatabase {
    Initialize-CaptureDatabaseDriver
    $databasePath = Join-Path ([Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData
    )) "HerdMe\captures.sqlite3"
    $builder = [Microsoft.Data.Sqlite.SqliteConnectionStringBuilder]::new()
    $builder.DataSource = $databasePath
    $builder.Mode = [Microsoft.Data.Sqlite.SqliteOpenMode]::ReadWrite
    $builder.Cache = [Microsoft.Data.Sqlite.SqliteCacheMode]::Shared
    $builder.Pooling = $false
    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new($builder.ToString())
    $connection.Open()
    return $connection
}

function Wait-CapturedRecord(
    [ValidateSet("mail", "dumps")]
    [string]$Table,
    [string]$Property,
    [string]$ExpectedValue,
    [string]$Component
) {
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    $lastDatabaseError = $null
    do {
        $connection = $null
        $command = $null
        $reader = $null
        try {
            $connection = Open-CaptureDatabase
            $command = $connection.CreateCommand()
            $command.CommandText = "SELECT id, payload FROM $Table ORDER BY received_at DESC"
            $reader = $command.ExecuteReader()
            while ($reader.Read()) {
                try {
                    $record = $reader.GetString(1) | ConvertFrom-Json
                }
                catch {
                    continue
                }
                if ($record.$Property -eq $ExpectedValue) {
                    return [PSCustomObject]@{ Id = $reader.GetString(0); Record = $record }
                }
            }
        }
        catch {
            $lastDatabaseError = $_
        }
        finally {
            if ($null -ne $reader) { $reader.Dispose() }
            if ($null -ne $command) { $command.Dispose() }
            if ($null -ne $connection) { $connection.Dispose() }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -ne $lastDatabaseError) {
        throw "The $Component acceptance payload was not persisted. Last SQLite error: $($lastDatabaseError.Exception.Message)"
    }
    throw "The $Component acceptance payload was not persisted."
}

function Remove-CapturedRecord(
    [ValidateSet("mail", "dumps")]
    [string]$Table,
    [string]$Id
) {
    $connection = Open-CaptureDatabase
    try {
        $command = $connection.CreateCommand()
        try {
            $command.CommandText = "DELETE FROM $Table WHERE id = `$id"
            $command.Parameters.AddWithValue("`$id", $Id) | Out-Null
            $command.ExecuteNonQuery() | Out-Null
        }
        finally {
            $command.Dispose()
        }
    }
    finally {
        $connection.Dispose()
    }
}

function Assert-SmtpCapture {
    $nonce = [Guid]::NewGuid().ToString("N")
    $subject = "HerdMe acceptance $nonce"
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

        $captured = Wait-CapturedRecord "mail" "Subject" $subject "SMTP"
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
                $captured = Wait-CapturedRecord "mail" "Subject" $subject "SMTP cleanup"
            }
            catch {
            }
        }
        if ($null -ne $captured) {
            Remove-CapturedRecord "mail" $captured.Id
        }
    }
}

function Assert-DumpCapture {
    $nonce = [Guid]::NewGuid().ToString("N")
    $source = "herdme-acceptance-$nonce.php"
    $serialized = "a:2:{s:4:`"file`";s:$($source.Length):`"$source`";s:5:`"value`";s:2:`"ok`";}"
    $payload = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($serialized))
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

        $captured = Wait-CapturedRecord "dumps" "Source" $source "VarDumper"
        if ($captured.Record.Payload -ne $payload -or $captured.Record.Summary -notlike '*value: "ok"*') {
            throw "The VarDumper acceptance payload was persisted incorrectly."
        }
    }
    finally {
        $client.Dispose()
        if ($null -eq $captured) {
            try {
                $captured = Wait-CapturedRecord "dumps" "Source" $source "VarDumper cleanup"
            }
            catch {
            }
        }
        if ($null -ne $captured) {
            Remove-CapturedRecord "dumps" $captured.Id
        }
    }
}

$onboarding = Start-Process `
    -FilePath $executable `
    -ArgumentList "--acceptance-onboarding" `
    -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ($onboarding.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $onboarding.Refresh()
    }
    if ($onboarding.MainWindowHandle -eq 0) {
        throw "The onboarding acceptance window did not become available."
    }
    Assert-ResizableMainWindow $onboarding
    Assert-OnboardingLayout $onboarding
}
finally {
    if (-not $onboarding.HasExited) {
        Stop-Process -Id $onboarding.Id -Force
        $onboarding.WaitForExit(5000)
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
    Assert-ResizableMainWindow $primary

    Start-Process -FilePath $executable | Out-Null
    Start-Sleep -Seconds 2
    $processes = @(Get-HerdMeProcesses)
    if ($processes.Count -ne 1 -or $processes[0].Id -ne $primary.Id) {
        throw "Launching HerdMe twice did not preserve a single primary process."
    }

    Assert-WinUiNavigation $primary

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
