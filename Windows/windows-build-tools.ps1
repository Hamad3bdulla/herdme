$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Find-HerdMeVisualStudioInstallation {
    $installerRoot = ${env:ProgramFiles(x86)}
    if ([string]::IsNullOrWhiteSpace($installerRoot)) {
        throw "Visual Studio was not found and ProgramFiles(x86) is unavailable."
    }
    $vswhere = Join-Path $installerRoot "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw "Visual Studio was not found. Install Visual Studio 2022 with C++ build tools."
    }

    $installationPath = & $vswhere `
        -latest `
        -products * `
        -requires Microsoft.Component.MSBuild Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace([string]$installationPath)) {
        $installationPath = & $vswhere `
            -latest `
            -products * `
            -requires Microsoft.Component.MSBuild `
            -property installationPath |
            Select-Object -First 1
    }
    if ([string]::IsNullOrWhiteSpace($installationPath)) {
        throw "Visual Studio with MSBuild and the x64 C++ tools was not found."
    }
    $installationPath = ([string]$installationPath).Trim()

    $msvcDirectory = Join-Path $installationPath "VC\Tools\MSVC"
    if (-not (Test-Path -LiteralPath $msvcDirectory -PathType Container)) {
        throw "The selected Visual Studio installation is missing MSVC tools at $msvcDirectory."
    }
    return $installationPath
}

function Find-HerdMeMSBuild {
    $installationPath = Find-HerdMeVisualStudioInstallation
    $candidate = Join-Path $installationPath "MSBuild\Current\Bin\MSBuild.exe"
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "MSBuild.exe was not found at $candidate."
    }
    return $candidate
}

function Copy-HerdMeVCRuntime {
    param([Parameter(Mandatory = $true)][string]$DestinationDirectory)

    $installationPath = Find-HerdMeVisualStudioInstallation
    $redistRoot = Join-Path $installationPath "VC\Redist\MSVC"
    $runtimeDirectory = $null
    if (Test-Path -LiteralPath $redistRoot -PathType Container) {
        $runtimeDirectory = Get-ChildItem -LiteralPath $redistRoot -Directory |
            Where-Object { $_.Name -match '^\d+(?:\.\d+){2,3}$' } |
            Sort-Object { [Version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName "x64\Microsoft.VC143.CRT" } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Container } |
            Select-Object -First 1
    }
    if ([string]::IsNullOrWhiteSpace([string]$runtimeDirectory)) {
        $runtimeDirectory = [Environment]::SystemDirectory
    }

    $runtimeFiles = @(
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
    )
    New-Item -ItemType Directory -Force -Path $DestinationDirectory | Out-Null
    foreach ($runtimeFile in $runtimeFiles) {
        $source = Join-Path $runtimeDirectory $runtimeFile
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "The Visual C++ runtime is missing $source."
        }
        $signature = Get-AuthenticodeSignature -LiteralPath $source
        if (
            $signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
            $null -eq $signature.SignerCertificate -or
            $signature.SignerCertificate.Subject -notmatch "(^|,\s*)O=Microsoft Corporation(,|$)"
        ) {
            throw "The Visual C++ runtime file is not validly signed by Microsoft: $source"
        }
        Copy-Item -LiteralPath $source -Destination (Join-Path $DestinationDirectory $runtimeFile) -Force
    }
}

function Install-HerdMeXamlCompilerDependency {
    param(
        [string]$WindowsAppSdkVersion = "1.7.260224002",
        [string]$PermissionsVersion = "6.0.0"
    )

    $packageRoot = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        Join-Path ([Environment]::GetFolderPath("UserProfile")) ".nuget\packages"
    } else {
        $env:NUGET_PACKAGES
    }
    $permissionsAssembly = Join-Path $packageRoot `
        "system.security.permissions\$PermissionsVersion\lib\net6.0\System.Security.Permissions.dll"
    $xamlCompilerDirectory = Join-Path $packageRoot `
        "microsoft.windowsappsdk\$WindowsAppSdkVersion\tools\net6.0"
    if (-not (Test-Path -LiteralPath $permissionsAssembly -PathType Leaf)) {
        throw "The restored XAML compiler dependency was not found: $permissionsAssembly"
    }
    if (-not (Test-Path -LiteralPath $xamlCompilerDirectory -PathType Container)) {
        throw "The Windows App SDK XAML compiler directory was not found: $xamlCompilerDirectory"
    }

    Copy-Item `
        -LiteralPath $permissionsAssembly `
        -Destination (Join-Path $xamlCompilerDirectory "System.Security.Permissions.dll") `
        -Force
}
