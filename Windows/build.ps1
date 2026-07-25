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

cmake -S (Join-Path $repoRoot "Core") -B $coreBuild -A $Architecture -DBUILD_TESTING=ON
if ($LASTEXITCODE -ne 0) { throw "CMake configuration failed." }
cmake --build $coreBuild --config $Configuration
if ($LASTEXITCODE -ne 0) { throw "The portable core build failed." }

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
    dotnet run `
        --project $contractProject `
        --configuration $Configuration `
        --no-build `
        --no-restore
    if ($LASTEXITCODE -ne 0) { throw "The Windows contract tests failed." }

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
$coreExecutable = Join-Path $coreBuild "$Configuration\herdme-core.exe"
if (-not (Test-Path $coreExecutable -PathType Leaf)) {
    throw "The portable core executable was not produced at $coreExecutable."
}
Copy-Item $coreExecutable (Join-Path $runtimeDirectory "herdme-core.exe") -Force

dotnet build $project `
    --configuration $Configuration `
    --runtime $runtimeIdentifier `
    -p:Platform=$Architecture `
    -p:TreatWarningsAsErrors=true
if ($LASTEXITCODE -ne 0) { throw "The native WinUI build failed." }

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
