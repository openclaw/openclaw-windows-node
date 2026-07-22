<#
.SYNOPSIS
    Runs the formal local MXC validation path.

.DESCRIPTION
    Builds the required Windows projects, enables the E2E/MXC gates, and runs
    the real WSL Gateway -> Windows node -> system.run MXC E2E proofs.

    This script is intentionally stricter than the regular GitHub-hosted E2E
    shard: MXC skips fail by default so MXC-related work cannot accidentally
    claim end-to-end validation on a host that did not exercise MXC.

.PARAMETER NoBuild
    Skip build steps and run against existing outputs.

.PARAMETER AllowSkip
    Return success when MXC tests are reported but skipped. Use only for
    discovery/documentation of a non-MXC host; do not use as merge validation
    for MXC, system.run, exec approval, Windows node command execution, or
    gateway setup/connect changes.
#>
[CmdletBinding()]
param(
    [switch]$NoBuild,

    [switch]$AllowSkip,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [ValidateSet("win-x64", "win-arm64")]
    [string]$RuntimeIdentifier,

    [string]$ResultsDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
Set-Location $repoRoot

if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    $RuntimeIdentifier = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        ([System.Runtime.InteropServices.Architecture]::Arm64) { "win-arm64"; break }
        default { "win-x64" }
    }
}

if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $repoRoot "TestResults\MxcE2E"
}

New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Command
    )

    Write-Host ""
    Write-Host "=== $Name ===" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

function Read-Trx {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Expected TRX file was not created: $Path"
    }

    [xml](Get-Content -LiteralPath $Path -Raw)
}

function Get-TrxUnitTestResults {
    param([Parameter(Mandatory = $true)][xml]$Trx)

    @($Trx.SelectNodes("//*[local-name()='UnitTestResult']"))
}

function Get-TrxResultText {
    param([Parameter(Mandatory = $true)][System.Xml.XmlElement]$Result)

    $parts = New-Object System.Collections.Generic.List[string]
    foreach ($node in @($Result.SelectNodes(".//*[local-name()='Message' or local-name()='StdOut' or local-name()='StdErr']"))) {
        if ($node -and -not [string]::IsNullOrWhiteSpace($node.InnerText)) {
            $parts.Add($node.InnerText)
        }
    }

    [string]::Join("`n", $parts)
}

function Assert-GatewayMxcProofsPassed {
    param([Parameter(Mandatory = $true)][xml]$Trx)

    $expectedProofs = @(
        "RealGateway_SystemRun_ExecutesThroughWindowsNodeMxcSandbox",
        "RealGateway_SystemRun_BlocksWritesToTrayDataDirectoryInMxcSandbox"
    )
    $results = Get-TrxUnitTestResults -Trx $Trx
    $errors = New-Object System.Collections.Generic.List[string]

    foreach ($proof in $expectedProofs) {
        $result = @($results | Where-Object { $_.GetAttribute("testName") -like "*$proof*" }) | Select-Object -First 1
        if ($null -eq $result) {
            $errors.Add("Gateway MXC proof was not reported in TRX: $proof")
            continue
        }

        $outcome = $result.GetAttribute("outcome")
        if ($outcome -eq "Passed") {
            Write-Host "Gateway MXC proof passed: $proof" -ForegroundColor Green
            continue
        }

        if ($outcome -eq "NotExecuted" -or $outcome -eq "Skipped") {
            $text = Get-TrxResultText -Result $result
            $skipMessage = if ([string]::IsNullOrWhiteSpace($text)) { "no skip reason in TRX" } else { $text.Trim() }
            $message = "Gateway MXC proof skipped: $proof ($skipMessage)"
            if ($AllowSkip) {
                Write-Warning $message
            } else {
                $errors.Add("$message. Run on an MXC-enabled Windows machine or pass -AllowSkip only when documenting a blocked host.")
            }
            continue
        }

        $errors.Add("Gateway MXC proof '$proof' had unexpected outcome '$outcome'.")
    }

    if ($errors.Count -gt 0) {
        throw [string]::Join("`n", $errors)
    }
}

function Set-ProcessEnv {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$Value
    )

    [Environment]::SetEnvironmentVariable($Name, $Value, "Process")
}

$trackedEnvVars = @(
    "OPENCLAW_REPO_ROOT",
    "OPENCLAW_RUN_E2E",
    "OPENCLAW_RUN_MXC_E2E",
    "OPENCLAW_E2E_GATEWAY_SOURCE"
)
$previousEnv = @{}
foreach ($name in $trackedEnvVars) {
    $previousEnv[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
}

try {
    if ([string]::IsNullOrWhiteSpace($env:OPENCLAW_E2E_GATEWAY_PACKAGE_SPEC)) {
        throw "OPENCLAW_E2E_GATEWAY_PACKAGE_SPEC must name the reviewed HTTP(S) .tgz candidate before formal MXC validation."
    }

    Set-ProcessEnv -Name "OPENCLAW_REPO_ROOT" -Value $repoRoot
    Set-ProcessEnv -Name "OPENCLAW_RUN_E2E" -Value "1"
    Set-ProcessEnv -Name "OPENCLAW_RUN_MXC_E2E" -Value "1"
    Set-ProcessEnv -Name "OPENCLAW_E2E_GATEWAY_SOURCE" -Value "candidate"

    Write-Host "OpenClaw MXC validation"
    Write-Host "  Repo: $repoRoot"
    Write-Host "  Configuration: $Configuration"
    Write-Host "  RuntimeIdentifier: $RuntimeIdentifier"
    Write-Host "  Results: $ResultsDirectory"
    if ($AllowSkip) {
        Write-Warning "-AllowSkip is enabled. This run may document a non-MXC host, but it is not sufficient merge validation for MXC-related work."
    }

    if (-not $NoBuild) {
        Invoke-Checked -Name "Build repository" -Command {
            $powerShellExe = (Get-Process -Id $PID).Path
            & $powerShellExe -NoProfile -File (Join-Path $repoRoot "build.ps1") -Configuration $Configuration
        }

        Invoke-Checked -Name "Build tray app for $RuntimeIdentifier" -Command {
            & dotnet build ".\src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj" -c $Configuration -r $RuntimeIdentifier
        }

        Invoke-Checked -Name "Build E2E tests for $RuntimeIdentifier" -Command {
            & dotnet build ".\tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj" -c $Configuration -r $RuntimeIdentifier
        }
    }

    $e2eTrx = Join-Path $ResultsDirectory "OpenClaw.E2ETests.Mxc.trx"
    $e2eConsoleLog = Join-Path $ResultsDirectory "OpenClaw.E2ETests.Mxc.console.log"
    Invoke-Checked -Name "Run Gateway MXC E2E proofs" -Command {
        & dotnet test ".\tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj" `
            --no-build `
            --no-restore `
            -c $Configuration `
            -r $RuntimeIdentifier `
            --verbosity normal `
            --results-directory $ResultsDirectory `
            --logger "trx;LogFileName=OpenClaw.E2ETests.Mxc.trx" `
            --logger "console;verbosity=detailed" `
            --filter "FullyQualifiedName~OpenClaw.E2ETests.Setup.MxcSetupAndConnectTests" `
            2>&1 | Tee-Object -FilePath $e2eConsoleLog
    }
    Assert-GatewayMxcProofsPassed -Trx (Read-Trx -Path $e2eTrx)

    Write-Host ""
    Write-Host "MXC validation completed successfully." -ForegroundColor Green
} finally {
    foreach ($name in $trackedEnvVars) {
        [Environment]::SetEnvironmentVariable($name, $previousEnv[$name], "Process")
    }
}
