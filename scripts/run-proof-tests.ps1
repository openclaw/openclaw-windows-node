<#
.SYNOPSIS
    Runs a filtered proof test command and rejects zero-test success.

.DESCRIPTION
    Restores and builds through dotnet test, writes a deterministic TRX file,
    and requires every selected test to pass so fresh proof hosts cannot turn
    --no-restore behavior or a stale filter into success-shaped evidence.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Project,

    [Parameter(Mandatory = $true)]
    [string]$Filter,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    [string]$ResultName,

    [ValidateSet("win-x64", "win-arm64")]
    [string]$RuntimeIdentifier,

    [string]$ResultsDirectory,
    [string[]]$CaptureArtifact = @(),
    [string[]]$BinaryArtifact = @(),
    [string[]]$NativeReceipt = @(),
    [string[]]$FixtureReceipt = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptRoot "ProofEvidence.ps1")
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $scriptRoot))
$env:OPENCLAW_REPO_ROOT = $repoRoot
$projectPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Project))
$repoPrefix = $repoRoot.TrimEnd("\") + "\"
if (-not $projectPath.StartsWith(
        $repoPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Proof test project escapes the repository: $Project"
}
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Proof test project does not exist: $Project"
}

if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $repoRoot "TestResults\ProofPools\$ResultName"
} else {
    $ResultsDirectory = [System.IO.Path]::GetFullPath($ResultsDirectory)
}

$binding = Get-ProofCommandBinding -RepoRoot $repoRoot -Project $Project -Filter $Filter -ResultName $ResultName -RuntimeIdentifier $RuntimeIdentifier
if ($null -ne $binding) { [void](Assert-ProofResultsDirectory -Path $ResultsDirectory -RepoRoot $repoRoot) }
$invocation = New-TestInvocation -ResultsDirectory $ResultsDirectory -TrxFileName "$ResultName.trx"
$ResultsDirectory = $invocation.directory
$trxPath = $invocation.report
$source = $null
$artifactReferences = @()
if ($null -ne $binding) {
    $source = Get-ProofSourceIdentity $repoRoot
    foreach ($entry in @(
        @{ paths = $CaptureArtifact; kind = 'capture' },
        @{ paths = $BinaryArtifact; kind = 'binary' },
        @{ paths = $NativeReceipt; kind = 'native-receipt' },
        @{ paths = $FixtureReceipt; kind = 'fixture-receipt' }
    )) {
        foreach ($path in $entry.paths) { $artifactReferences += Get-ProofArtifact -Path $path -RepoRoot $repoRoot -Kind $entry.kind }
    }
} elseif ($CaptureArtifact.Count + $BinaryArtifact.Count + $NativeReceipt.Count + $FixtureReceipt.Count -gt 0) {
    throw 'Evidence references require an exact inventory-owned proof command.'
} else {
    Write-Host 'No pool evidence summary: command has no exact inventory binding.'
}

$arguments = @(
    "test",
    $projectPath,
    "--filter", $Filter,
    "--results-directory", $ResultsDirectory,
    "--logger", "trx;LogFileName=$ResultName.trx",
    "--logger", "console;verbosity=normal"
)
if (-not [string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    $arguments += @("-r", $RuntimeIdentifier)
}

$commandExit = -1
$result = $null
$blockedReason = ''
$primaryFailure = $null
try {
    & dotnet @arguments
    $commandExit = $LASTEXITCODE
    $result = Read-TestResultContract -Path $trxPath -StartedUtc $invocation.startedUtc -ExitCode $commandExit
} catch {
    $primaryFailure = $_
    $blockedReason = 'test-command-or-result-contract-failed'
    throw
} finally {
    if ($null -ne $binding) {
        try {
            Write-ProofEvidence -RepoRoot $repoRoot -Binding $binding -Invocation $invocation -Source $source `
                -ExitCode $commandExit -Result $result -BlockedReason $blockedReason -Artifacts $artifactReferences
        } catch {
            if ($null -eq $primaryFailure) { throw }
            Write-Warning "Secondary proof-evidence failure: $($_.Exception.Message)" -WarningAction Continue
        }
    }
}
Write-Host ("Proof tests reported: " + ($result | ConvertTo-Json -Compress)) -ForegroundColor Green
Write-Host "Proof TRX: $trxPath"
