<#
.SYNOPSIS
    Sets up networking for Windows Sandbox to reach the WSL2 OpenClaw gateway.
    Run as Administrator (one-time setup, persists across reboots).

.DESCRIPTION
    Creates a port proxy from the host's 0.0.0.0:18789 to the WSL2 VM's :18789.
    Also adds a firewall rule to allow inbound connections on that port.
    This lets Windows Sandbox connect to the gateway via the host's IP.

.EXAMPLE
    # Set up (run once as admin)
    .\setup-sandbox-network.ps1

    # Check status
    .\setup-sandbox-network.ps1 -Status

    # Remove when done
    .\setup-sandbox-network.ps1 -Remove
#>
param(
    [switch]$Remove,
    [switch]$Status
)

$port = 18789
$ruleName = "OpenClaw Gateway (Dev)"

if ($Status) {
    Write-Host "`n  Port proxy rules:" -ForegroundColor Cyan
    netsh interface portproxy show v4tov4
    Write-Host "`n  Firewall rule:" -ForegroundColor Cyan
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue |
        Select-Object DisplayName, Enabled, Direction, Action | Format-Table
    exit 0
}

if ($Remove) {
    Write-Host "  Removing port proxy..." -ForegroundColor Yellow
    netsh interface portproxy delete v4tov4 listenaddress=0.0.0.0 listenport=$port 2>$null
    Write-Host "  Removing firewall rule..." -ForegroundColor Yellow
    Remove-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
    Write-Host "  ✓ Cleaned up" -ForegroundColor Green
    exit 0
}

# Check admin
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "  ❌ Run as Administrator!" -ForegroundColor Red
    exit 1
}

# Get WSL2 IP
$wslIp = (wsl hostname -I 2>$null)
if (-not $wslIp) {
    Write-Host "  ❌ WSL2 not running or no IP found" -ForegroundColor Red
    exit 1
}
$wslIp = $wslIp.Trim().Split(' ')[0]

Write-Host "`n  🦞 OpenClaw Sandbox Network Setup`n" -ForegroundColor Magenta

# Port proxy
Write-Host "  Setting port proxy: 0.0.0.0:$port → WSL2 ${wslIp}:$port" -ForegroundColor Cyan
netsh interface portproxy delete v4tov4 listenaddress=0.0.0.0 listenport=$port 2>$null
netsh interface portproxy add v4tov4 listenaddress=0.0.0.0 listenport=$port connectaddress=$wslIp connectport=$port
if ($LASTEXITCODE -ne 0) {
    Write-Host "  ❌ Failed to set port proxy" -ForegroundColor Red
    exit 1
}

# Firewall rule
$existing = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "  Firewall rule already exists" -ForegroundColor Gray
} else {
    Write-Host "  Adding firewall rule: Inbound TCP $port" -ForegroundColor Cyan
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -LocalPort $port -Protocol TCP -Action Allow | Out-Null
}

Write-Host @"

  ✓ Network configured!

  From Windows Sandbox, the gateway is reachable at:
    ws://<host-ip>:$port

  The sandbox logon script will show the correct host IP.
  
  To check status:  .\setup-sandbox-network.ps1 -Status
  To remove:        .\setup-sandbox-network.ps1 -Remove

"@ -ForegroundColor Green
