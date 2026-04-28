#Requires -Version 5.1
<#
.SYNOPSIS
    E2E test runner for the OpenClaw onboarding wizard.

.DESCRIPTION
    Runs UI Automation tests against the OpenClaw onboarding flow.
    Uses UIA text assertions as the primary validation method.

.PARAMETER TestFlow
    Which test flow(s) to run. Default: All.

.EXAMPLE
    .\Test-OnboardingWizard.ps1 -TestFlow ConfigureLater
    .\Test-OnboardingWizard.ps1 -TestFlow All
#>
param(
    [ValidateSet("All", "LocalOperator", "LocalNode", "ConfigureLater", "ConnectionFailure")]
    [string]$TestFlow = "All"
)

# Dot-source helpers
. "$PSScriptRoot\helpers\UIA-Helpers.ps1"
. "$PSScriptRoot\helpers\Screenshot-Helpers.ps1"
. "$PSScriptRoot\helpers\Gateway-Helpers.ps1"
. "$PSScriptRoot\helpers\App-Helpers.ps1"

$script:TestResults = @()
$script:ScreenshotDir = "$PSScriptRoot\screenshots"

function Run-Test {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Test
    )
    Write-Host "`n▶ $Name" -ForegroundColor Cyan

    # Save/restore settings to prevent test pollution
    $settingsPath = "$env:APPDATA\OpenClawTray\settings.json"
    $settingsBackup = $null
    if (Test-Path $settingsPath) {
        $settingsBackup = Get-Content $settingsPath -Raw
    }

    try {
        & $Test
        $script:TestResults += @{ Name = $Name; Result = "PASS"; Error = $null }
        Write-Host "  ✅ PASS" -ForegroundColor Green
    } catch {
        $script:TestResults += @{ Name = $Name; Result = "FAIL"; Error = $_.Exception.Message }
        Write-Host "  ❌ FAIL: $($_.Exception.Message)" -ForegroundColor Red

        # Attempt screenshot on failure
        try {
            $win = Find-OnboardingWindow
            if ($win) {
                $hwnd = $win.Current.NativeWindowHandle
                if ($hwnd -ne [IntPtr]::Zero) {
                    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
                    $screenshotPath = Join-Path $script:ScreenshotDir "${Name}_FAIL_${timestamp}.png"
                    Capture-WindowScreenshot -WindowHandle ([IntPtr]$hwnd) -OutputPath $screenshotPath
                }
            }
        } catch {
            Write-Host "  (screenshot capture failed: $($_.Exception.Message))" -ForegroundColor DarkGray
        }
    } finally {
        Stop-App
        # Restore original settings
        if ($settingsBackup) {
            [System.IO.File]::WriteAllText($settingsPath, $settingsBackup)
        } elseif (Test-Path $settingsPath) {
            Remove-Item $settingsPath -Force -ErrorAction SilentlyContinue
        }
    }
}

# ──────────────────────────────────────────────────────
# TEST FLOW 1: Configure Later (simplest — no gateway needed)
# ──────────────────────────────────────────────────────
function Test-ConfigureLater {
    Stop-App
    Start-App -ForceOnboarding -CleanSettings
    $win = Wait-ForOnboardingWindow
    Start-Sleep 3

    # Welcome page
    Assert-TextVisible $win "Welcome"
    Click-ButtonById $win "OnboardingNext"
    Start-Sleep 2

    # Connection page — select "Configure Later"
    Assert-TextVisible $win "Choose your Gateway"
    $laterBtn = Find-ElementByName $win "Configure Later"
    if (-not $laterBtn) { $laterBtn = Find-ElementByName $win "⏭️ Configure Later" }
    if ($laterBtn) { Click-Button $laterBtn } else { throw "Configure Later radio button not found" }
    Start-Sleep 1
    Click-ButtonById $win "OnboardingNext"
    Start-Sleep 2

    # Chat page may appear (Later + ShowChat=true) — skip past it
    $texts = Get-PageTexts $win
    if ($texts | Where-Object { $_ -match "Meet your Agent|chat" }) {
        Click-ButtonById $win "OnboardingNext"
        Start-Sleep 2
    }

    # Should be on Ready page
    Assert-TextVisible $win "All Set!"
    Assert-TextVisible $win "configure"

    # Verify it's the last page (button says "Finish")
    $finishBtn = Find-ElementByName $win "Finish"
    if (-not $finishBtn) { throw "Expected 'Finish' button on last page" }

    Stop-App
}

# ──────────────────────────────────────────────────────
# TEST FLOW 2: Local + Node Mode (no wizard, no chat)
# ──────────────────────────────────────────────────────
function Test-LocalNodeMode {
    Stop-App
    Start-App -ForceOnboarding -CleanSettings
    $win = Wait-ForOnboardingWindow
    Start-Sleep 3

    # Welcome → Connection
    Click-ButtonById $win "OnboardingNext"
    Start-Sleep 2

    # Toggle Node Mode ON
    $toggle = Find-FirstToggle $win
    if ($toggle) { Toggle-Element $toggle }
    Start-Sleep 1

    # Click Next — should go to Permissions (skipping Wizard)
    Click-ButtonById $win "OnboardingNext"
    Start-Sleep 2
    Assert-TextVisible $win "Grant Permissions"

    # Click Next — should go to Ready
    Click-ButtonById $win "OnboardingNext"
    Start-Sleep 2
    Assert-TextVisible $win "All Set!"
    Assert-TextVisible $win "Node Mode Active"
    Assert-TextVisible $win "Screen Capture"
    Assert-TextVisible $win "System Commands"

    Stop-App
}

# ──────────────────────────────────────────────────────
# TEST FLOW 3: Connection Failure (bad URL)
# ──────────────────────────────────────────────────────
function Test-ConnectionFailure {
    Stop-App
    Set-TestSettings @{
        GatewayUrl     = "ws://localhost:99999"
        Token          = "bad-token"
        EnableNodeMode = $false
    }
    Start-App -ForceOnboarding
    $win = Wait-ForOnboardingWindow
    Start-Sleep 3

    # Welcome → Connection
    Click-ButtonById $win "OnboardingNext"
    Start-Sleep 3

    # Click Test Connection
    Click-ButtonById $win "OnboardingTestConnection"
    Start-Sleep 8

    # Should show error
    $texts = Get-PageTexts $win
    $hasError = $texts | Where-Object { $_ -match "❌|error|failed|unreachable|Error|Failed|Lost connection|not connected|invalid|Invalid" }
    if (-not $hasError) { throw "Expected error message after bad connection" }

    Stop-App
}

# ──────────────────────────────────────────────────────
# TEST FLOW 4: Local + Operator (happy path, needs gateway)
# ──────────────────────────────────────────────────────
function Test-LocalOperator {
    if (-not (Wait-ForGatewayHealth -Port 19001 -TimeoutSeconds 5)) {
        Write-Host "  ⏭ SKIPPED (no gateway running on port 19001)" -ForegroundColor Yellow
        $script:TestResults += @{ Name = "LocalOperator"; Result = "SKIP"; Error = "No gateway" }
        return
    }

    Stop-App
    $token = Get-GatewayAuthToken
    if (-not $token) {
        Write-Host "  ⏭ SKIPPED (could not read gateway auth token)" -ForegroundColor Yellow
        $script:TestResults += @{ Name = "LocalOperator"; Result = "SKIP"; Error = "No auth token" }
        return
    }

    Set-TestSettings @{
        GatewayUrl     = "ws://localhost:19001"
        Token          = $token
        EnableNodeMode = $false
    }
    Start-App -ForceOnboarding
    $win = Wait-ForOnboardingWindow

    # Welcome → Connection
    Click-ButtonById $win "OnboardingNext"
    Start-Sleep 1

    # Test Connection
    Click-ButtonById $win "OnboardingTestConnection"
    Wait-ForText $win "pairing" -TimeoutSeconds 15

    # Approve if needed
    $approveBtn = Find-ElementById $win "OnboardingApprove"
    if ($approveBtn) {
        Click-Button $approveBtn
        Start-Sleep 3
    }

    # Navigate through remaining pages
    Click-ButtonById $win "OnboardingNext"
    Start-Sleep 2

    # Continue through wizard
    Click-ButtonById $win "OnboardingNext"
    Start-Sleep 2
    Assert-TextVisible $win "Grant Permissions"

    Click-ButtonById $win "OnboardingNext"
    Start-Sleep 2
    Assert-TextVisible $win "All Set!"

    Stop-App
}

# ──────────────────────────────────────────────────────
# RUNNER
# ──────────────────────────────────────────────────────
if (-not (Test-Path $script:ScreenshotDir)) {
    New-Item -Path $script:ScreenshotDir -ItemType Directory -Force | Out-Null
}

$flows = [ordered]@{
    "ConfigureLater"    = { Test-ConfigureLater }
    "LocalNode"         = { Test-LocalNodeMode }
    "ConnectionFailure" = { Test-ConnectionFailure }
    "LocalOperator"     = { Test-LocalOperator }
}

$toRun = if ($TestFlow -eq "All") { $flows.Keys } else { @($TestFlow) }

foreach ($flow in $toRun) {
    if ($flows.Contains($flow)) {
        Run-Test -Name $flow -Test $flows[$flow]
    }
}

# ──────────────────────────────────────────────────────
# SUMMARY
# ──────────────────────────────────────────────────────
Write-Host "`n" -NoNewline
Write-Host "═══════════════════════════════" -ForegroundColor Cyan
Write-Host " TEST SUMMARY" -ForegroundColor Cyan
Write-Host "═══════════════════════════════" -ForegroundColor Cyan

$pass = @($script:TestResults | Where-Object { $_.Result -eq "PASS" }).Count
$fail = @($script:TestResults | Where-Object { $_.Result -eq "FAIL" }).Count
$skip = @($script:TestResults | Where-Object { $_.Result -eq "SKIP" }).Count

foreach ($r in $script:TestResults) {
    $icon = switch ($r.Result) { "PASS" { "✅" } "FAIL" { "❌" } "SKIP" { "⏭" } }
    $color = switch ($r.Result) { "PASS" { "Green" } "FAIL" { "Red" } "SKIP" { "Yellow" } }
    Write-Host "  $icon $($r.Name)" -ForegroundColor $color
    if ($r.Error) { Write-Host "     $($r.Error)" -ForegroundColor DarkGray }
}

$summaryColor = if ($fail -gt 0) { "Red" } else { "Green" }
Write-Host "`n  $pass passed, $fail failed, $skip skipped" -ForegroundColor $summaryColor

# Exit with non-zero if any failures
if ($fail -gt 0) { exit 1 }
