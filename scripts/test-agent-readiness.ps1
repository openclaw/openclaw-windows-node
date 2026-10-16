[CmdletBinding()]
param([string]$RepoRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$root = Join-Path $RepoRoot ('TestResults\readiness-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $path = Join-Path $root 'observations.json'
    $facts = [ordered]@{
        windows = $true; git = $true; dotnet = $true; node = $true; windowsSdk = $true; webView2 = $true
        desktopSession = $true; wslCommand = $true; reviewSkill = $true; reviewHelper = $true; python = $true
    }
    $script = Join-Path $RepoRoot 'scripts\setup-dev.ps1'
    foreach ($scenario in @('detected', 'optional-missing', 'native-missing', 'non-windows')) {
        switch ($scenario) {
            'optional-missing' { $facts.reviewHelper = $false; $facts.python = $false }
            'native-missing' { $facts.dotnet = $false; $facts.windowsSdk = $false }
            'non-windows' { $facts.windows = $false; $facts.desktopSession = $false }
        }
        $facts | ConvertTo-Json | Set-Content $path
        $report = (& $script -AgentReadiness -ReadinessFixturePath $path) | ConvertFrom-Json
        if ($report.schemaVersion -ne 1 -or $report.desktopProof.status -ne 'unverified' -or $report.optionalSharedReview.requiredForBuild -ne $false -or
            $report.optionalSharedReview.authentication -ne 'not-checked' -or $report.observationSource -ne 'trusted-test-fixture-not-host-proof') {
            throw "$scenario overclaims readiness or loses optional-tool boundaries."
        }
        if ($scenario -in @('detected', 'optional-missing') -and $report.developer.status -ne 'prerequisites-detected-build-unverified') {
            throw 'Optional review tools became a build dependency.'
        }
        if ($scenario -in @('native-missing', 'non-windows') -and $report.developer.status -ne 'missing-prerequisites') {
            throw 'Missing native prerequisites were not reported.'
        }
    }
    Push-Location $root
    try {
        $before = (Get-Location).Path
        & $script -AgentReadiness -ReadinessFixturePath $path | Out-Null
        if ((Get-Location).Path -cne $before) { throw 'Read-only readiness changed the caller location.' }
    } finally { Pop-Location }
    $facts.dotnet = 'not-a-boolean'
    $facts | ConvertTo-Json | Set-Content $path
    $rejected = $false
    try { & $script -AgentReadiness -ReadinessFixturePath $path | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid readiness observations were accepted.' }
    Write-Host 'Agent readiness regressions passed: four host/tool combinations, caller location preservation, and invalid observations.'
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
$global:LASTEXITCODE = 0
