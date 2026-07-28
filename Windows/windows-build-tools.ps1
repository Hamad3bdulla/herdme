$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Find-HerdMeMSBuild {
    $command = Get-Command "MSBuild.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }

    $installerRoot = ${env:ProgramFiles(x86)}
    if ([string]::IsNullOrWhiteSpace($installerRoot)) {
        throw "MSBuild.exe was not found and ProgramFiles(x86) is unavailable."
    }
    $vswhere = Join-Path $installerRoot "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw "MSBuild.exe was not found. Install Visual Studio 2022 with C++ build tools."
    }

    $installationPath = [string](& $vswhere `
        -latest `
        -products * `
        -requires Microsoft.Component.MSBuild `
        -property installationPath |
        Select-Object -First 1)
    $installationPath = $installationPath.Trim()
    if ([string]::IsNullOrWhiteSpace($installationPath)) {
        throw "Visual Studio with the MSBuild component was not found."
    }

    $candidate = Join-Path $installationPath "MSBuild\Current\Bin\MSBuild.exe"
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "MSBuild.exe was not found at $candidate."
    }
    return $candidate
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
