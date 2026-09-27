param(
    [string]$Executable,
    [string]$ReportDirectory,
    [int]$StartupTimeoutSeconds = 30
)

# Accessibility check for the native WinUI window. It uses the UI Automation client built into
# Windows (no download), opens every page, and fails when an enabled, visible control that a
# keyboard or screen-reader user must operate has no accessible name or cannot take keyboard
# focus. Fix a finding in XAML (x:Uid .AutomationProperties.Name, Header, or content text); do
# not exempt it here.

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($env:OS -ne "Windows_NT") {
    throw "The Windows accessibility check must run on Windows."
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Executable)) {
    $Executable = Join-Path $repoRoot "build\windows-portable-win-x64\HerdMe.Windows.exe"
}
if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $ReportDirectory = Join-Path $repoRoot "build\windows-accessibility"
}
if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
    throw "Build the portable app first (Windows\package-portable.ps1); $Executable was not found."
}
$Executable = (Resolve-Path -LiteralPath $Executable).Path

$automation = [System.Windows.Automation.AutomationElement]
$controlTypes = [System.Windows.Automation.ControlType]

# Controls a user operates directly. Each needs a name a screen reader can announce.
$namedTypes = @(
    $controlTypes::Button,
    $controlTypes::CheckBox,
    $controlTypes::ComboBox,
    $controlTypes::Edit,
    $controlTypes::Hyperlink,
    $controlTypes::ListItem,
    $controlTypes::MenuItem,
    $controlTypes::RadioButton,
    $controlTypes::Slider,
    $controlTypes::Spinner,
    $controlTypes::SplitButton,
    $controlTypes::TabItem,
    $controlTypes::TreeItem
)
# Controls that must also be reachable with Tab.
$focusableTypes = @(
    $controlTypes::Button,
    $controlTypes::CheckBox,
    $controlTypes::ComboBox,
    $controlTypes::Edit,
    $controlTypes::Hyperlink,
    $controlTypes::RadioButton,
    $controlTypes::Slider,
    $controlTypes::SplitButton
)

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
    @{ Navigation = "NavTinker"; Page = "TinkerPageRoot" },
    @{ Navigation = "NavLogs"; Page = "LogsPageRoot" },
    @{ Navigation = "NavAbout"; Page = "AboutPageRoot" }
)

function Get-HerdMeProcesses {
    @(Get-Process -Name "HerdMe.Windows" -ErrorAction SilentlyContinue)
}

function Wait-Element(
    [System.Windows.Automation.AutomationElement]$Root,
    [string]$AutomationId,
    [int]$TimeoutSeconds = 10
) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        $automation::AutomationIdProperty,
        $AutomationId
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $element -and -not $element.Current.IsOffscreen) { return $element }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "The WinUI element '$AutomationId' did not become available."
}

function Select-Element([System.Windows.Automation.AutomationElement]$Element, [string]$Name) {
    $pattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        ([System.Windows.Automation.SelectionItemPattern]$pattern).Select()
        return
    }
    $pattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
        return
    }
    throw "The navigation item '$Name' cannot be selected."
}

function Get-Findings(
    [System.Windows.Automation.AutomationElement]$Window,
    [string]$Page
) {
    $conditions = [System.Windows.Automation.Condition[]]@($namedTypes | ForEach-Object {
        [System.Windows.Automation.PropertyCondition]::new($automation::ControlTypeProperty, $_)
    })
    $elements = $Window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.OrCondition]::new($conditions)
    )
    $findings = [System.Collections.Generic.List[object]]::new()
    foreach ($element in $elements) {
        try {
            $current = $element.Current
            if ($current.IsOffscreen -or -not $current.IsEnabled) { continue }
            $bounds = $current.BoundingRectangle
            if ($bounds.IsEmpty -or $bounds.Width -le 0 -or $bounds.Height -le 0) { continue }
            $rules = @()
            if ([string]::IsNullOrWhiteSpace($current.Name)) { $rules += "missing-name" }
            if ($focusableTypes -contains $current.ControlType -and -not $current.IsKeyboardFocusable) {
                $rules += "not-keyboard-focusable"
            }
            foreach ($rule in $rules) {
                $findings.Add([ordered]@{
                    page = $Page
                    rule = $rule
                    controlType = $current.ControlType.ProgrammaticName
                    automationId = $current.AutomationId
                    className = $current.ClassName
                    name = $current.Name
                    bounds = "$([int]$bounds.Left),$([int]$bounds.Top),$([int]$bounds.Width)x$([int]$bounds.Height)"
                })
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
            # The element went away while the page settled; the next page scan covers it.
        }
    }
    return , $findings
}

if ((Get-HerdMeProcesses).Count -ne 0) {
    throw "Close HerdMe before the accessibility check so only one HerdMe process runs."
}

New-Item -ItemType Directory -Force -Path $ReportDirectory | Out-Null
$culture = [System.Globalization.CultureInfo]::CurrentUICulture.Name
$reportPath = Join-Path $ReportDirectory "accessibility-$culture.json"
$allFindings = [System.Collections.Generic.List[object]]::new()
$scanned = [System.Collections.Generic.List[string]]::new()

$process = Start-Process -FilePath $Executable -ArgumentList "--acceptance" -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    while ($process.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
    }
    if ($process.MainWindowHandle -eq 0) {
        throw "The HerdMe window did not become available for the accessibility check."
    }
    $window = $automation::FromHandle([IntPtr]$process.MainWindowHandle)
    $navigation = Wait-Element $window "NavigationRoot" $StartupTimeoutSeconds

    foreach ($page in $pages) {
        $item = Wait-Element $navigation $page.Navigation
        Select-Element $item $page.Navigation
        $null = Wait-Element $window $page.Page
        # Let deferred loading (lists, status text) finish before reading the tree.
        Start-Sleep -Milliseconds 800
        $process.Refresh()
        if ($process.HasExited) { throw "HerdMe exited while opening '$($page.Navigation)'." }
        $findings = Get-Findings $window $page.Page
        foreach ($finding in $findings) { $allFindings.Add($finding) }
        $scanned.Add($page.Page)
        Write-Host "Accessibility scan: $($page.Page) ($($findings.Count) finding(s))"
    }
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit(5000) | Out-Null
    }
}

$report = [ordered]@{
    culture = $culture
    executable = $Executable
    pages = @($scanned)
    findings = @($allFindings)
}
[System.IO.File]::WriteAllText(
    $reportPath,
    ($report | ConvertTo-Json -Depth 5) + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false)
)
Write-Host "Accessibility report: $reportPath"

if ($scanned.Count -ne $pages.Count) {
    throw "The accessibility check scanned $($scanned.Count) of $($pages.Count) pages."
}
if ($allFindings.Count -gt 0) {
    $summary = ($allFindings | Select-Object -First 25 | ForEach-Object {
        "$($_.page): $($_.rule) $($_.controlType) id='$($_.automationId)' class='$($_.className)' at $($_.bounds)"
    }) -join [Environment]::NewLine
    throw "The accessibility check found $($allFindings.Count) problem(s):$([Environment]::NewLine)$summary"
}
Write-Host "Windows accessibility checks passed on $($scanned.Count) pages ($culture)."
