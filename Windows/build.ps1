param(
    [string]$Architecture = "x64",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
if ($Architecture -ne "x64") {
    throw "HerdMe for Windows currently supports x64 only."
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$coreBuild = Join-Path $repoRoot "build\windows-core-$Architecture"
$project = Join-Path $PSScriptRoot "HerdMe.Windows\HerdMe.Windows.csproj"
$contractProject = Join-Path $PSScriptRoot "HerdMe.Windows.ContractTests\HerdMe.Windows.ContractTests.csproj"
$runtimeDirectory = Join-Path $PSScriptRoot "HerdMe.Windows\Runtime"
$runtimeIdentifier = "win-x64"
$nativeBuildLog = Join-Path $repoRoot "build\windows-native-build.log"
$nativeBuildBinaryLog = Join-Path $repoRoot "build\windows-native-build.binlog"
$nativeBuildLogDirectory = Split-Path -Parent $nativeBuildLog

cmake -S (Join-Path $repoRoot "Core") -B $coreBuild -A $Architecture -DBUILD_TESTING=ON
if ($LASTEXITCODE -ne 0) { throw "CMake configuration failed." }
cmake --build $coreBuild --config $Configuration
if ($LASTEXITCODE -ne 0) { throw "The portable core build failed." }
$coreExecutable = Join-Path $coreBuild "$Configuration\herdme-core.exe"
if (-not (Test-Path $coreExecutable -PathType Leaf)) {
    throw "The portable core executable was not produced at $coreExecutable."
}

if (-not $SkipTests) {
    $scriptFiles = @(Get-ChildItem $PSScriptRoot -Recurse -Filter "*.ps1" -File)
    foreach ($scriptFile in $scriptFiles) {
        $tokens = $null
        $parseErrors = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile(
            $scriptFile.FullName,
            [ref]$tokens,
            [ref]$parseErrors
        )
        if ($parseErrors.Count -ne 0) {
            $details = ($parseErrors | ForEach-Object {
                "line $($_.Extent.StartLineNumber): $($_.Message)"
            }) -join "; "
            throw "PowerShell syntax validation failed for $($scriptFile.FullName): $details"
        }
    }

    ctest --test-dir $coreBuild -C $Configuration --output-on-failure
    if ($LASTEXITCODE -ne 0) { throw "The portable core tests failed." }

    dotnet build $contractProject `
        --configuration $Configuration `
        -p:TreatWarningsAsErrors=true
    if ($LASTEXITCODE -ne 0) { throw "The Windows contract build failed." }
    $previousCoreTestExecutable = $env:HERDME_CORE_TEST_EXECUTABLE
    try {
        $env:HERDME_CORE_TEST_EXECUTABLE = $coreExecutable
        dotnet run `
            --project $contractProject `
            --configuration $Configuration `
            --no-build `
            --no-restore
        if ($LASTEXITCODE -ne 0) { throw "The Windows contract tests failed." }

        $contractAssembly = Join-Path `
            (Split-Path -Parent $contractProject) `
            "bin\$Configuration\net8.0\HerdMe.Windows.ContractTests.dll"
        dotnet $contractAssembly
        if ($LASTEXITCODE -ne 0) {
            throw "The direct Windows contract assembly execution failed."
        }
    }
    finally {
        $env:HERDME_CORE_TEST_EXECUTABLE = $previousCoreTestExecutable
    }

    $xamlFiles = @(Get-ChildItem (Join-Path $PSScriptRoot "HerdMe.Windows") -Recurse -Filter "*.xaml")
    if ($xamlFiles.Count -lt 13) {
        throw "The native Windows project is missing expected XAML files."
    }
    $xmlSettings = [System.Xml.XmlReaderSettings]::new()
    $xmlSettings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    foreach ($xaml in $xamlFiles) {
        $reader = [System.Xml.XmlReader]::Create($xaml.FullName, $xmlSettings)
        try {
            while ($reader.Read()) { }
        }
        finally {
            $reader.Dispose()
        }
    }
}

New-Item -ItemType Directory -Force -Path $runtimeDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $nativeBuildLogDirectory | Out-Null
Copy-Item $coreExecutable (Join-Path $runtimeDirectory "herdme-core.exe") -Force

dotnet build $project `
    --configuration $Configuration `
    --runtime $runtimeIdentifier `
    -p:Platform=$Architecture `
    -p:UseXamlCompilerExecutable=false `
    -p:TreatWarningsAsErrors=true `
    "-bl:$nativeBuildBinaryLog" `
    "-flp:logfile=$nativeBuildLog;verbosity=diagnostic"
if ($LASTEXITCODE -ne 0) {
    $xamlOutputFiles = @(
        Get-ChildItem (Join-Path (Split-Path -Parent $project) "obj") `
            -Recurse `
            -Filter "output.json" `
            -File `
            -ErrorAction SilentlyContinue
    )
    foreach ($xamlOutput in $xamlOutputFiles) {
        Write-Host "XAML compiler diagnostics from $($xamlOutput.FullName):"
        Get-Content -LiteralPath $xamlOutput.FullName | Write-Host
    }
    $diagnostics = @(
        if (Test-Path -LiteralPath $nativeBuildLog -PathType Leaf) {
            Select-String `
                -LiteralPath $nativeBuildLog `
                -Pattern "(^|\s)(error|fatal error)\s+[A-Z]+[0-9]+\s*:|:\s+error\s+" `
                -CaseSensitive:$false |
            Select-Object -Last 20 |
            ForEach-Object { $_.Line.Trim() }
        }
    )
    $detail = if ($diagnostics.Count -eq 0) {
        if ($xamlOutputFiles.Count -gt 0) {
            "No structured compiler error was found; inspect the uploaded XAML output.json and native build logs."
        } elseif (Test-Path -LiteralPath $nativeBuildBinaryLog -PathType Leaf) {
            "No structured compiler error was found; inspect build/windows-native-build.binlog."
        } elseif (Test-Path -LiteralPath $nativeBuildLog -PathType Leaf) {
            "MSBuild failed without a structured compiler error; inspect build/windows-native-build.log."
        } else {
            "MSBuild failed before it could produce the native build diagnostic logs."
        }
    } else {
        $diagnostics -join [Environment]::NewLine
    }
    throw "The native WinUI build failed.$([Environment]::NewLine)$detail"
}

$builtExecutable = Get-ChildItem (Join-Path $PSScriptRoot "HerdMe.Windows\bin") `
    -Recurse `
    -Filter "HerdMe.Windows.exe" |
    Where-Object {
        $_.FullName -like "*\$Architecture\$Configuration\*" -and
        $_.FullName -like "*\$runtimeIdentifier\*"
    } |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $builtExecutable) {
    throw "The native WinUI executable was not produced."
}

$buildOutput = $builtExecutable.DirectoryName
foreach ($relativePath in @(
    "HerdMe.Windows.exe",
    "Runtime\herdme-core.exe",
    "Microsoft.WindowsAppRuntime.dll",
    "Microsoft.ui.xaml.dll",
    "LICENSE",
    "THIRD_PARTY.md",
    "release-manifest.json"
)) {
    $requiredPath = Join-Path $buildOutput $relativePath
    if (-not (Test-Path $requiredPath -PathType Leaf)) {
        throw "The WinUI build output is missing $relativePath."
    }
}

Write-Host "Windows build validation passed:"
Write-Host $builtExecutable.FullName
