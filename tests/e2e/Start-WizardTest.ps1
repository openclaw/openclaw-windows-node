#Requires -Version 5.1
<#
.SYNOPSIS
    Reset everything and navigate the OpenClaw onboarding UI to the wizard step.

.DESCRIPTION
    A reliable, repeatable script that kills any running app, ensures the WSL
    gateway is healthy, writes correct settings, launches the app with forced
    onboarding, and uses UIA automation to navigate from Welcome through
    Connection to the Wizard page.

.PARAMETER Build
    Rebuild the app before launching.

.PARAMETER RestartGateway
    Force restart gateway (clears stale wizard sessions).

.PARAMETER SkipToWizard
    Skip gateway setup, just navigate (assumes app is already on Welcome page).

.EXAMPLE
    .\Start-WizardTest.ps1
    .\Start-WizardTest.ps1 -Build
    .\Start-WizardTest.ps1 -RestartGateway
    .\Start-WizardTest.ps1 -SkipToWizard
#>
param(
    [switch]$Build,
    [switch]$RestartGateway,
    [switch]$SkipToWizard
)

$ErrorActionPreference = "Stop"

# Dot-source helpers
. "$PSScriptRoot\helpers\UIA-Helpers.ps1"

# ── Constants ───────────────────────────────────────────
$AppExePath     = "C:\Users\mharsh\gt\openclaw\crew\mharsh\src\OpenClaw.Tray.WinUI\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\OpenClaw.Tray.WinUI.exe"
$AppProjectPath = "C:\Users\mharsh\gt\openclaw\crew\mharsh\src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj"
$SettingsPath   = "$env:APPDATA\OpenClawTray\settings.json"
$GatewayPort    = 19001
$TotalSteps     = 7

# ── Utility helpers ─────────────────────────────────────

function Wait-ForCondition {
    param(
        [string]$Description,
        [scriptblock]$Condition,
        [int]$TimeoutSeconds = 15
    )
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Write-Step {
    param([int]$Step, [string]$Label)
    Write-Host "[$Step/$TotalSteps] $($Label.PadRight(35))" -NoNewline -ForegroundColor White
}

function Write-OK {
    param([string]$Detail, [System.Diagnostics.Stopwatch]$Timer)
    $elapsed = if ($Timer) { " ($([math]::Round($Timer.Elapsed.TotalSeconds, 1))s)" } else { "" }
    Write-Host "✅ $Detail$elapsed" -ForegroundColor Green
}

function Write-Skip {
    param([string]$Detail)
    Write-Host "⏭ $Detail" -ForegroundColor Yellow
}

function Write-Fail {
    param([string]$Detail)
    Write-Host "❌ $Detail" -ForegroundColor Red
}

function Dump-PageState {
    param($Window)
    if (-not $Window) {
        Write-Host "      (no window available)" -ForegroundColor DarkGray
        return
    }
    try {
        $texts = Get-PageTexts -Window $Window
        Write-Host "      Current page texts:" -ForegroundColor DarkGray
        foreach ($t in $texts) {
            Write-Host "        '$t'" -ForegroundColor DarkGray
        }
    } catch {
        Write-Host "      (could not read page state: $($_.Exception.Message))" -ForegroundColor DarkGray
    }
}

# ── Banner ──────────────────────────────────────────────
$totalTimer = [System.Diagnostics.Stopwatch]::StartNew()
Write-Host ""
Write-Host "$([char]::ConvertFromUtf32(0x1F99E)) OpenClaw Wizard Test Setup" -ForegroundColor Cyan
Write-Host "==============================" -ForegroundColor Cyan
Write-Host ""

# ── Step 1: Kill existing app ──────────────────────────
$stepTimer = [System.Diagnostics.Stopwatch]::StartNew()
Write-Step 1 "Killing existing app..."

$procs = Get-Process -Name "OpenClaw.Tray.WinUI" -ErrorAction SilentlyContinue
if ($procs) {
    foreach ($p in $procs) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 500
}

Write-OK -Detail "Done" -Timer $stepTimer

# ── Step 2: Gateway health ─────────────────────────────
if (-not $SkipToWizard) {
    $stepTimer = [System.Diagnostics.Stopwatch]::StartNew()

    if ($RestartGateway) {
        Write-Step 2 "Restarting gateway..."
        wsl bash -c 'pkill -9 -f "openclaw-gateway"; pkill -9 -f "run-node"; rm -rf /tmp/openclaw-*' 2>$null
        Start-Sleep -Seconds 2
    } else {
        Write-Step 2 "Checking gateway health..."
    }

    # Fix __skip__ model if present in config (from earlier skip bug)
    wsl bash -c 'node -e "
const fs = require(\"fs\");
const p = \"/home/mharsh/.openclaw-dev/openclaw.json\";
const c = JSON.parse(fs.readFileSync(p, \"utf8\"));
const m = c.agents?.defaults?.model;
const primary = typeof m === \"string\" ? m : m?.primary;
if (primary && primary.includes(\"__skip__\")) {
  if (typeof m === \"string\") c.agents.defaults.model = \"github-copilot/claude-opus-4.7\";
  else c.agents.defaults.model.primary = \"github-copilot/claude-opus-4.7\";
  fs.writeFileSync(p, JSON.stringify(c, null, 2));
  console.log(\"Fixed __skip__ model -> github-copilot/claude-opus-4.7\");
} else {
  console.log(\"Model OK: \" + primary);
}
"' 2>$null

    # Copy auth profile from main to dev agent if dev is empty and main has credentials
    wsl bash -c '
DEV_DIR="/home/mharsh/.openclaw-dev/agents/dev/agent"
DEV_PROF="$DEV_DIR/auth-profiles.json"
MAIN_PROF="/home/mharsh/.openclaw-dev/agents/main/agent/auth-profiles.json"
mkdir -p "$DEV_DIR"
if [ -f "$MAIN_PROF" ]; then
  MAIN_HAS=$(grep -c "github-copilot" "$MAIN_PROF" 2>/dev/null || echo 0)
  DEV_HAS=$(grep -c "github-copilot" "$DEV_PROF" 2>/dev/null || echo 0)
  if [ "$MAIN_HAS" -gt 0 ] && [ "$DEV_HAS" -eq 0 ]; then
    cp "$MAIN_PROF" "$DEV_PROF"
    echo "Copied auth profile main -> dev"
  fi
fi
' 2>$null

    # Check if already healthy
    $healthy = $false
    try {
        $resp = Invoke-WebRequest -Uri "http://localhost:$GatewayPort/health" -UseBasicParsing -TimeoutSec 3
        if ($resp.StatusCode -eq 200) { $healthy = $true }
    } catch {}

    if (-not $healthy) {
        # Need to start the gateway
        if (-not $RestartGateway) {
            # Rewrite the step header for starting
            Write-Host "" # newline from the -NoNewline
            Write-Step 2 "Starting gateway..."
        }

        # Ensure start script exists
        $startScript = @'
#!/bin/bash
cd /home/mharsh/openclaw
export OPENCLAW_SKIP_CHANNELS=1
exec node scripts/run-node.mjs --dev gateway --port 19001
'@
        $b64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($startScript))
        wsl bash -c "echo '$b64' | base64 -d > /tmp/start-gateway.sh && chmod +x /tmp/start-gateway.sh" 2>$null

        # Launch gateway in background (no -WindowStyle Hidden — WSL needs a live terminal)
        Start-Process -FilePath "wsl" -ArgumentList "bash", "/tmp/start-gateway.sh"

        # Poll for health (up to 180s — first start may need TS rebuild + UI build)
        $ok = Wait-ForCondition -Description "Gateway health" -TimeoutSeconds 180 -Condition {
            try {
                $r = Invoke-WebRequest -Uri "http://localhost:$GatewayPort/health" -UseBasicParsing -TimeoutSec 3
                return ($r.StatusCode -eq 200)
            } catch { return $false }
        }

        if (-not $ok) {
            Write-Fail "Gateway failed to start after 180s"
            exit 1
        }

        Write-OK -Detail "Gateway UP after $([math]::Round($stepTimer.Elapsed.TotalSeconds, 0))s" -Timer $null
    } else {
        Write-OK -Detail "Gateway running" -Timer $stepTimer
    }

    # ── Step 3: Write settings ─────────────────────────
    $stepTimer = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Step 3 "Writing settings..."

    # Read token from WSL gateway config (preferred) or OPENCLAW_GATEWAY_TOKEN env var.
    # NEVER hardcode tokens — they are dev secrets and get flagged by GitHub secret scanning.
    $token = $null
    try {
        $tokenOutput = wsl bash -c 'node -p "JSON.parse(require(\"fs\").readFileSync(\"$HOME/.openclaw-dev/openclaw.json\",\"utf8\")).gateway.auth.token"' 2>$null
        if ($tokenOutput -and $tokenOutput.Trim().Length -gt 10) {
            $token = $tokenOutput.Trim()
        }
    } catch {}

    if (-not $token -and $env:OPENCLAW_GATEWAY_TOKEN) {
        $token = $env:OPENCLAW_GATEWAY_TOKEN
    }

    if (-not $token) {
        Write-Fail "Could not read gateway token from WSL (~/.openclaw-dev/openclaw.json) or OPENCLAW_GATEWAY_TOKEN env var. Start the dev gateway or set the env var, then retry."
        exit 1
    }

    $tokenPreview = $token.Substring(0, [Math]::Min(10, $token.Length)) + "..."

    $settingsDir = [System.IO.Path]::GetDirectoryName($SettingsPath)
    if (-not (Test-Path $settingsDir)) {
        New-Item -Path $settingsDir -ItemType Directory -Force | Out-Null
    }

    $settings = @{
        GatewayUrl     = "ws://localhost:$GatewayPort"
        Token          = $token
        EnableNodeMode = $false
    } | ConvertTo-Json -Depth 5

    [System.IO.File]::WriteAllText($SettingsPath, $settings)
    Write-OK -Detail "Token: $tokenPreview" -Timer $stepTimer
} else {
    # SkipToWizard — skip steps 2 & 3
    Write-Step 2 "Checking gateway health..."
    Write-Skip "Skipped (SkipToWizard)"
    Write-Step 3 "Writing settings..."
    Write-Skip "Skipped (SkipToWizard)"
}

# ── Step 4: Build app ─────────────────────────────────
$stepTimer = [System.Diagnostics.Stopwatch]::StartNew()
Write-Step 4 "Building app..."

if ($Build) {
    $buildResult = & dotnet build $AppProjectPath -p:Platform=x64 --configuration Debug 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "Build failed"
        Write-Host ($buildResult | Out-String) -ForegroundColor Red
        exit 1
    }
    Write-OK -Detail "Build succeeded" -Timer $stepTimer
} elseif (Test-Path $AppExePath) {
    Write-Skip "Skipped (exe exists)"
} else {
    Write-Fail "Exe not found and -Build not specified: $AppExePath"
    exit 1
}

# ── Step 5: Launch app ────────────────────────────────
if (-not $SkipToWizard) {
    $stepTimer = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Step 5 "Launching app..."

    $env:OPENCLAW_FORCE_ONBOARDING = "1"
    $env:OPENCLAW_SKIP_UPDATE_CHECK = "1"

    $appProc = Start-Process -FilePath $AppExePath -PassThru
    Write-OK -Detail "PID: $($appProc.Id)" -Timer $stepTimer
} else {
    Write-Step 5 "Launching app..."
    Write-Skip "Skipped (SkipToWizard)"
}

# ── Step 6: Navigate to wizard ────────────────────────
$stepTimer = [System.Diagnostics.Stopwatch]::StartNew()
Write-Step 6 "Navigating to wizard..."
Write-Host "" # newline

$win = $null

# 6a. Wait for window
$found = Wait-ForCondition -Description "Onboarding window" -TimeoutSeconds 15 -Condition {
    $script:navWin = Find-OnboardingWindow
    return ($null -ne $script:navWin)
}
$win = $script:navWin

if (-not $found -or -not $win) {
    Write-Host "      Welcome -> Window               " -NoNewline
    Write-Fail "Window not found"
    exit 1
}

# 6b. Welcome → Next
Write-Host "      Welcome -> Next                  " -NoNewline
Start-Sleep -Seconds 1  # brief settle

$ok = Wait-ForCondition -Description "Next button enabled" -TimeoutSeconds 15 -Condition {
    try {
        $btn = Find-ElementById -Window $win -AutomationId "OnboardingNext"
        return ($null -ne $btn -and $btn.Current.IsEnabled)
    } catch { return $false }
}
if (-not $ok) {
    Write-Fail "Next button not ready"
    Dump-PageState $win
    exit 1
}

Click-ButtonById -Window $win -AutomationId "OnboardingNext"
Write-OK -Detail ""

# 6c. Connection → Test Connection
Write-Host "      Connection -> Test               " -NoNewline
$ok = Wait-ForCondition -Description "Test Connection button" -TimeoutSeconds 15 -Condition {
    try {
        $btn = Find-ElementById -Window $win -AutomationId "OnboardingTestConnection"
        return ($null -ne $btn -and $btn.Current.IsEnabled)
    } catch { return $false }
}
if (-not $ok) {
    Write-Fail "Test Connection button not found"
    Dump-PageState $win
    exit 1
}

Click-ButtonById -Window $win -AutomationId "OnboardingTestConnection"

# Wait for result: "Connected" or "pairing"
$connResult = ""
$ok = Wait-ForCondition -Description "Connection result" -TimeoutSeconds 15 -Condition {
    try {
        $texts = Get-PageTexts -Window $win
        $match = $texts | Where-Object { $_ -match "Connected|pairing" }
        if ($match) {
            $script:connResultText = ($match | Select-Object -First 1)
            return $true
        }
    } catch {}
    return $false
}
if (-not $ok) {
    Write-Fail "No connection result"
    Dump-PageState $win
    exit 1
}

Write-OK -Detail $script:connResultText

# 6d. Approve if needed
Write-Host "      Connection -> Approve            " -NoNewline
$approveBtn = $null
$hasApprove = Wait-ForCondition -Description "Approve button" -TimeoutSeconds 5 -Condition {
    try {
        $script:approveEl = Find-ElementById -Window $win -AutomationId "OnboardingApprove"
        return ($null -ne $script:approveEl -and $script:approveEl.Current.IsEnabled)
    } catch { return $false }
}

if ($hasApprove -and $script:approveEl) {
    Click-Button -Button $script:approveEl
    Start-Sleep -Seconds 3
    Write-OK -Detail ""
} else {
    Write-Skip "Not needed"
}

# 6e. Connection → Next (with delay for connection to fully establish before wizard mounts)
Write-Host "      Connection -> Next               " -NoNewline
$ok = Wait-ForCondition -Description "Next button enabled" -TimeoutSeconds 15 -Condition {
    try {
        $btn = Find-ElementById -Window $win -AutomationId "OnboardingNext"
        return ($null -ne $btn -and $btn.Current.IsEnabled)
    } catch { return $false }
}
if (-not $ok) {
    Write-Fail "Next button not enabled after connection"
    Dump-PageState $win
    exit 1
}

Start-Sleep -Seconds 3  # Let connection fully establish before wizard mounts
Click-ButtonById -Window $win -AutomationId "OnboardingNext"
Write-OK -Detail ""

# ── Step 7: Wait for wizard step ──────────────────────
$stepTimer = [System.Diagnostics.Stopwatch]::StartNew()
Write-Step 7 "Waiting for wizard step..."

$wizardText = ""
$ok = Wait-ForCondition -Description "Wizard Continue/Yes button" -TimeoutSeconds 60 -Condition {
    try {
        $continueBtn = Find-ElementByName -Window $win "Continue"
        if ($continueBtn) {
            $script:wizardReadyText = "Continue button found"
            return $true
        }
        $yesBtn = Find-ElementByName -Window $win "Yes"
        if ($yesBtn) {
            $script:wizardReadyText = "Yes button found"
            return $true
        }
        # Also check for wizard-related page text
        $texts = Get-PageTexts -Window $win
        $wizMatch = $texts | Where-Object { $_ -match "setup|wizard|configure" }
        if ($wizMatch) {
            $script:wizardReadyText = ($wizMatch | Select-Object -First 1)
            # Still need Continue or Yes to be truly ready
            return ($null -ne (Find-ElementByName -Window $win "Continue") -or
                    $null -ne (Find-ElementByName -Window $win "Yes"))
        }
    } catch {}
    return $false
}

if (-not $ok) {
    Write-Fail "Wizard step not reached"
    Dump-PageState $win
    exit 1
}

Write-OK -Detail "`"$($script:wizardReadyText)`"" -Timer $stepTimer

# ── Done ───────────────────────────────────────────────
$totalTimer.Stop()
Write-Host ""
Write-Host "==============================" -ForegroundColor Cyan
Write-Host "✅ Wizard ready! ($([math]::Round($totalTimer.Elapsed.TotalSeconds, 1))s elapsed)" -ForegroundColor Green
Write-Host "Navigate manually from here." -ForegroundColor White
Write-Host ""
