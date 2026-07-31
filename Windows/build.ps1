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
$vcRuntimeDirectory = Join-Path $repoRoot "build\windows-vc143-runtime"
. (Join-Path $PSScriptRoot "windows-build-tools.ps1")

Copy-HerdMeVCRuntime -DestinationDirectory $vcRuntimeDirectory

$singleConfigurationGenerators = @("Ninja", "NMake Makefiles")
$singleConfigurationBuild = $env:CMAKE_GENERATOR -in $singleConfigurationGenerators
$cmakeConfigureArguments = @(
    "-S", (Join-Path $repoRoot "Core"),
    "-B", $coreBuild,
    "-DBUILD_TESTING=ON"
)
if ($singleConfigurationBuild) {
    $cmakeConfigureArguments += "-DCMAKE_BUILD_TYPE=$Configuration"
} else {
    $cmakeConfigureArguments += @("-A", $Architecture)
}
cmake @cmakeConfigureArguments
if ($LASTEXITCODE -ne 0) { throw "CMake configuration failed." }
cmake --build $coreBuild --config $Configuration
if ($LASTEXITCODE -ne 0) { throw "The portable core build failed." }
$coreExecutable = if ($singleConfigurationBuild) {
    Join-Path $coreBuild "herdme-core.exe"
} else {
    Join-Path $coreBuild "$Configuration\herdme-core.exe"
}
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

    $xamlProjectRoot = Join-Path $PSScriptRoot "HerdMe.Windows"
    $xamlFiles = @(Get-ChildItem $xamlProjectRoot -Recurse -Filter "*.xaml" -File |
        Where-Object {
            $relativePath = $_.FullName.Substring($xamlProjectRoot.Length)
            $pathSegments = @($relativePath -split "[\/\\]")
            @($pathSegments | Where-Object { $_ -in @("bin", "obj") }).Count -eq 0
        })
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

dotnet restore $project `
    --runtime $runtimeIdentifier `
    -p:Platform=$Architecture
if ($LASTEXITCODE -ne 0) { throw "Restoring the native WinUI project failed." }
Install-HerdMeXamlCompilerDependency
$msbuild = Find-HerdMeMSBuild
& $msbuild $project `
    /t:Build `
    "/p:Configuration=$Configuration" `
    "/p:RuntimeIdentifier=$runtimeIdentifier" `
    "/p:Platform=$Architecture" `
    /p:UseXamlCompilerExecutable=false `
    /p:TreatWarningsAsErrors=true `
    "/bl:$nativeBuildBinaryLog" `
    /fl `
    "/flp:logfile=$nativeBuildLog;verbosity=diagnostic"
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
    "HerdMe.Windows.pri",
    "Assets\HerdMe.ico",
    "Prerequisites\VC143\concrt140.dll",
    "Prerequisites\VC143\msvcp140.dll",
    "Prerequisites\VC143\msvcp140_1.dll",
    "Prerequisites\VC143\msvcp140_2.dll",
    "Prerequisites\VC143\msvcp140_atomic_wait.dll",
    "Prerequisites\VC143\msvcp140_codecvt_ids.dll",
    "Prerequisites\VC143\vccorlib140.dll",
    "Prerequisites\VC143\vcruntime140.dll",
    "Prerequisites\VC143\vcruntime140_1.dll",
    "Prerequisites\VC143\vcruntime140_threads.dll",
    "Runtime\herdme-core.exe",
    "Microsoft.Windows.ApplicationModel.Resources.dll",
    "Microsoft.WindowsAppRuntime.dll",
    "Microsoft.ui.xaml.dll",
    "MRM.dll",
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
