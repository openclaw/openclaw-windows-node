#Requires -Version 5.1
# Gateway-Helpers.ps1 — Gateway lifecycle management for E2E tests

$script:GatewayProcess = $null
$script:GatewayPort = 19001

function Start-TestGateway {
    <#
    .SYNOPSIS
        Starts the gateway in WSL on dev port 19001.
    #>
    $proc = Start-Process -FilePath "wsl" -ArgumentList "bash", "-c", "cd ~/openclaw && PORT=$script:GatewayPort node gateway/server.js" `
        -PassThru -WindowStyle Hidden
    $script:GatewayProcess = $proc
    Write-Host "  Gateway started (PID: $($proc.Id), Port: $script:GatewayPort)" -ForegroundColor DarkGray
    return $proc
}

function Stop-TestGateway {
    <#
    .SYNOPSIS
        Kills the gateway process.
    #>
    if ($script:GatewayProcess -and -not $script:GatewayProcess.HasExited) {
        Stop-Process -Id $script:GatewayProcess.Id -Force -ErrorAction SilentlyContinue
        Write-Host "  Gateway stopped (PID: $($script:GatewayProcess.Id))" -ForegroundColor DarkGray
    }
    # Also kill any lingering gateway in WSL
    wsl bash -c "pkill -f 'node.*gateway/server.js' 2>/dev/null || true" 2>$null
    $script:GatewayProcess = $null
}

function Reset-GatewayState {
    <#
    .SYNOPSIS
        Clears pending.json and paired.json via wsl bash -c.
        Uses base64 for file writes — never \\wsl$\ paths.
    #>
    $emptyJson = "{}"
    $b64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($emptyJson))

    wsl bash -c "echo '$b64' | base64 -d > ~/.openclaw/pending.json" 2>$null
    wsl bash -c "echo '$b64' | base64 -d > ~/.openclaw/paired.json" 2>$null

    Write-Host "  Gateway state reset (pending.json, paired.json cleared)" -ForegroundColor DarkGray
}

function Wait-ForGatewayHealth {
    param(
        [int]$Port = 19001,
        [int]$TimeoutSeconds = 30
    )
    <#
    .SYNOPSIS
        Polls http://localhost:$Port/health until 200 or timeout.
    #>
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri "http://localhost:$Port/health" -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200) {
                Write-Host "  Gateway healthy on port $Port" -ForegroundColor DarkGray
                return $true
            }
        } catch {
            # Not ready yet
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Get-GatewayPairingState {
    <#
    .SYNOPSIS
        Reads paired.json via wsl bash -c to check if device is paired.
    #>
    try {
        $json = wsl bash -c "cat ~/.openclaw/paired.json 2>/dev/null || echo '{}'"
        return $json | ConvertFrom-Json
    } catch {
        return $null
    }
}

function Get-GatewayAuthToken {
    <#
    .SYNOPSIS
        Reads gateway auth token from WSL config.
    #>
    try {
        $json = wsl bash -c "cat /home/mharsh/.openclaw/openclaw.json 2>/dev/null"
        $config = $json | ConvertFrom-Json
        return $config.gateway.auth.token
    } catch {
        Write-Warning "Could not read gateway auth token from WSL"
        return $null
    }
}
