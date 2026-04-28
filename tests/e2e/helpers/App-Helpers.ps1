#Requires -Version 5.1
# App-Helpers.ps1 — Application lifecycle management for E2E tests

$script:AppExePath = "C:\Users\mharsh\gt\openclaw\crew\mharsh\src\OpenClaw.Tray.WinUI\bin\x64\Debug\net10.0-windows10.0.19041.0\OpenClaw.Tray.WinUI.exe"
$script:SettingsPath = "$env:APPDATA\OpenClawTray\settings.json"
$script:AppProcess = $null

function Build-App {
    <#
    .SYNOPSIS
        Builds the OpenClaw WinUI app with dotnet build.
    #>
    $projectDir = "C:\Users\mharsh\gt\openclaw\crew\mharsh\src\OpenClaw.Tray.WinUI"
    Write-Host "  Building app..." -ForegroundColor DarkGray
    $result = & dotnet build $projectDir -p:Platform=x64 --configuration Debug 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed:`n$($result | Out-String)"
    }
    Write-Host "  Build succeeded" -ForegroundColor DarkGray
}

function Start-App {
    param(
        [switch]$ForceOnboarding,
        [switch]$CleanSettings
    )
    <#
    .SYNOPSIS
        Launches the OpenClaw app. Kills any existing instance first.
    #>
    Stop-App

    if ($CleanSettings) {
        $settingsDir = [System.IO.Path]::GetDirectoryName($script:SettingsPath)
        if (Test-Path $script:SettingsPath) {
            Remove-Item $script:SettingsPath -Force
            Write-Host "  Cleared settings.json" -ForegroundColor DarkGray
        }
    }

    if (-not (Test-Path $script:AppExePath)) {
        throw "App executable not found at: $script:AppExePath"
    }

    $env:OPENCLAW_FORCE_ONBOARDING = if ($ForceOnboarding) { "1" } else { $null }
    $env:OPENCLAW_SKIP_UPDATE_CHECK = "1"

    $proc = Start-Process -FilePath $script:AppExePath -PassThru
    $script:AppProcess = $proc
    Write-Host "  App started (PID: $($proc.Id))" -ForegroundColor DarkGray
    return $proc
}

function Stop-App {
    <#
    .SYNOPSIS
        Kills any running OpenClaw.Tray.WinUI process.
    #>
    $procs = Get-Process -Name "OpenClaw.Tray.WinUI" -ErrorAction SilentlyContinue
    foreach ($p in $procs) {
        try {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            Write-Host "  Stopped app (PID: $($p.Id))" -ForegroundColor DarkGray
        } catch {
            # Already exited
        }
    }
    $script:AppProcess = $null
    Start-Sleep -Milliseconds 500
}

function Set-TestSettings {
    param(
        [Parameter(Mandatory)][hashtable]$Settings
    )
    <#
    .SYNOPSIS
        Writes settings.json with specified values.
    #>
    $settingsDir = [System.IO.Path]::GetDirectoryName($script:SettingsPath)
    if (-not (Test-Path $settingsDir)) {
        New-Item -Path $settingsDir -ItemType Directory -Force | Out-Null
    }

    $json = $Settings | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($script:SettingsPath, $json)
    Write-Host "  Settings written: $($Settings.Keys -join ', ')" -ForegroundColor DarkGray
}

function Wait-ForOnboardingWindow {
    param(
        [int]$TimeoutSeconds = 15
    )
    <#
    .SYNOPSIS
        Polls for the "OpenClaw Setup" window via UIA until found or timeout.
    #>
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $win = Find-OnboardingWindow
        if ($win) {
            Write-Host "  Onboarding window found" -ForegroundColor DarkGray
            return $win
        }
        Start-Sleep -Milliseconds 500
    }
    throw "Onboarding window 'OpenClaw Setup' not found after $TimeoutSeconds seconds"
}
