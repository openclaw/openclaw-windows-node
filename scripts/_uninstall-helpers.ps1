# _uninstall-helpers.ps1
#
# Shared helper functions for OpenClaw uninstall and cleanup scripts.
# Dot-source this file at the top of any script that needs these utilities:
#
#   . "$PSScriptRoot\_uninstall-helpers.ps1"
#
# Note: Add-Step requires a $script:steps array to be initialised in the
# calling script before use (e.g. $script:steps = @()).

# ---------------------------------------------------------------------------
# Distro-name guard
# Exact-match guard. Mirrors C# LocalGatewayUninstall.AllowedDistroName.
# Round 2 (Scott #4): prefix matching was dead allowance that let test
# distros like "OpenClawGateway-test" pass param validation and then strand
# at the final unregister guard (which is exact-match). Exact-everywhere.
# ---------------------------------------------------------------------------

function Test-IsOpenClawOwnedDistroName {
    param([string]$Name)

    return $Name -eq "OpenClawGateway"
}

# ---------------------------------------------------------------------------
# WSL command runner
# ---------------------------------------------------------------------------

function Invoke-WslCommand {
    <#
    .SYNOPSIS
        Runs a bash command inside WSL and returns stdout, stderr, and exit code.
    .PARAMETER Command
        The bash command string to execute via `wsl bash -c`.
    .PARAMETER DistroName
        Optional WSL distribution name. Omit to use the default distribution.
    .OUTPUTS
        A hashtable with keys: Stdout, Stderr, ExitCode.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Command,
        [string]$DistroName
    )

    $wslArgs = if ($DistroName) {
        @("-d", $DistroName, "bash", "-c", $Command)
    } else {
        @("bash", "-c", $Command)
    }

    $stdoutLines = [System.Collections.Generic.List[string]]::new()
    $stderrLines = [System.Collections.Generic.List[string]]::new()

    & wsl @wslArgs 2>&1 | ForEach-Object {
        if ($_ -is [System.Management.Automation.ErrorRecord]) {
            $stderrLines.Add($_.ToString())
        } else {
            $stdoutLines.Add($_)
        }
    }

    return @{
        Stdout   = $stdoutLines -join "`n"
        Stderr   = $stderrLines -join "`n"
        ExitCode = if ($null -eq $global:LASTEXITCODE) { 0 } else { $global:LASTEXITCODE }
    }
}

# ---------------------------------------------------------------------------
# Process termination
# ---------------------------------------------------------------------------

function Stop-OpenClawProcessByPid {
    <#
    .SYNOPSIS
        Terminates a process by PID, suppressing "not found" errors.
    .PARAMETER ProcessId
        PID of the process to terminate.
    .PARAMETER Force
        If specified, uses -Force on Stop-Process.
    #>
    param(
        [Parameter(Mandatory)]
        [int]$ProcessId,
        [switch]$Force
    )

    try {
        if ($Force) {
            Stop-Process -Id $ProcessId -Force -ErrorAction Stop
        } else {
            Stop-Process -Id $ProcessId -ErrorAction Stop
        }
    } catch [Microsoft.PowerShell.Commands.ProcessCommandException] {
        # Process already exited — not an error.
    }
}

# ---------------------------------------------------------------------------
# Dry-run gate
# ---------------------------------------------------------------------------

function Assert-DryRunGate {
    <#
    .SYNOPSIS
        Throws if the caller is in dry-run mode.  Intended to guard any
        statement that mutates persistent state (filesystem, processes, WSL).
    .PARAMETER DryRun
        Boolean dry-run flag from the calling script.
    .PARAMETER OperationDescription
        Human-readable description of the blocked operation (used in error message).
    #>
    param(
        [Parameter(Mandatory)]
        [bool]$DryRun,
        [string]$OperationDescription = "destructive operation"
    )

    if ($DryRun) {
        throw "Dry-run mode is active; $OperationDescription was not executed."
    }
}

# ---------------------------------------------------------------------------
# Step logging
# ---------------------------------------------------------------------------

function Add-Step {
    <#
    .SYNOPSIS
        Appends a structured step entry to `$script:steps` in the calling script.
    .NOTES
        The calling script must declare `$script:steps = @()` before dot-sourcing
        this file or before first calling Add-Step.
    #>
    param(
        [string]$Name,
        [string]$Status,
        [string]$Message,
        [hashtable]$Data = @{}
    )

    $script:steps += [ordered]@{
        name      = $Name
        status    = $Status
        message   = $Message
        data      = $Data
        timestamp = (Get-Date).ToString("o")
    }
}
