<#
.SYNOPSIS
Regenerates the composer lobster visit goldens from a pinned openclaw/openclaw ref.

.DESCRIPTION
The Hub's lobster visit logic (src/OpenClaw.Tray.WinUI/Chat/LobsterVisits) ports the Control UI's
ui/src/components/lobster-pet-*.ts. Its tests compare against values produced by executing those upstream
modules, never by the port. This script downloads the modules at -UpstreamRef, adds the DOM-free stubs in
scripts/lobster-visit-goldens/stubs, bundles scripts/lobster-visit-goldens/harness.ts with the pinned esbuild
from its package-lock.json, runs it under Node with TZ=UTC, and writes
tests/OpenClaw.Tray.Tests/LobsterVisitGoldens.g.cs.

The only upstream edit is a trailing export of the module-private resolveLobsterComposerScene so lane goldens
can call it directly.

It needs Node.js 22 or later and npm, plus network access to raw.githubusercontent.com and the npm registry.
It writes only the output file and a temporary directory it deletes afterwards.

To move the port to a newer upstream, rerun with that ref, update the pinned ref in the port's file headers and
THIRD_PARTY_NOTICES.md, then fix the port until the Tray tests pass. -Check verifies that the committed file
matches a fresh run without writing it.

.EXAMPLE
.\scripts\Update-LobsterVisitGoldens.ps1

.EXAMPLE
.\scripts\Update-LobsterVisitGoldens.ps1 -UpstreamRef 4cffd43ee043275f6a4c693aaacee8d8e5a61288 -Check
#>
[CmdletBinding()]
param(
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$UpstreamRef = '4cffd43ee043275f6a4c693aaacee8d8e5a61288',

    [string]$OutputPath = (Join-Path $PSScriptRoot '..\tests\OpenClaw.Tray.Tests\LobsterVisitGoldens.g.cs'),

    [switch]$Check
)

$ErrorActionPreference = 'Stop'

$upstreamFiles = @(
    'src/shared/fnv1a.ts'
    'src/shared/session-run-state.ts'
    'ui/src/lib/session-run-state.ts'
    'ui/src/components/lobster-dex.ts'
    'ui/src/components/lobster-favicon.ts'
    'ui/src/components/lobster-image.ts'
    'ui/src/components/lobster-pet-audio.ts'
    'ui/src/components/lobster-pet-contract.ts'
    'ui/src/components/lobster-pet-identity.ts'
    'ui/src/components/lobster-pet-interactions.ts'
    'ui/src/components/lobster-pet-lore.ts'
    'ui/src/components/lobster-pet-moon.ts'
    'ui/src/components/lobster-pet-owner.ts'
    'ui/src/components/lobster-pet-palettes.ts'
    'ui/src/components/lobster-pet-plans.ts'
    'ui/src/components/lobster-pet-scene.ts'
    'ui/src/components/lobster-pet-timers.ts'
    'ui/src/components/lobster-pet-traffic.ts'
)

foreach ($tool in 'node', 'npm') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool was not found on PATH." }
}

$source = Join-Path $PSScriptRoot 'lobster-visit-goldens'
$work = Join-Path ([IO.Path]::GetTempPath()) "openclaw-lobster-goldens-$([guid]::NewGuid().ToString('N'))"
$previousTz = $env:TZ
try {
    $tree = Join-Path $work 'tree'
    foreach ($path in $upstreamFiles) {
        $destination = Join-Path $tree $path
        New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
        $uri = "https://raw.githubusercontent.com/openclaw/openclaw/$UpstreamRef/$path"
        Invoke-WebRequest -Uri $uri -OutFile $destination -UseBasicParsing
    }

    $scene = Join-Path $tree 'ui/src/components/lobster-pet-scene.ts'
    if ((Get-Content -LiteralPath $scene -Raw) -notmatch 'export\s+function\s+resolveLobsterComposerScene') {
        Add-Content -LiteralPath $scene -Value "`nexport { resolveLobsterComposerScene };"
    }

    Copy-Item -Path (Join-Path $source 'stubs\*') -Destination $tree -Recurse -Force -Exclude 'normalization-core.ts'
    foreach ($file in 'harness.ts', 'package.json', 'package-lock.json', 'stubs\normalization-core.ts') {
        Copy-Item -LiteralPath (Join-Path $source $file) -Destination $work
    }

    Push-Location $work
    try {
        npm ci --ignore-scripts --no-audit --no-fund --loglevel=error
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }
        node node_modules/esbuild/bin/esbuild harness.ts --bundle --platform=node --format=esm `
            --outfile=out/harness.mjs --alias:@openclaw/normalization-core=./normalization-core.ts --log-level=warning
        if ($LASTEXITCODE -ne 0) { throw 'esbuild failed.' }
        $generated = Join-Path $work 'LobsterVisitGoldens.g.cs'
        # Goldens use local wall-clock dates; a fixed zone keeps every host's run identical.
        $env:TZ = 'UTC'
        node out/harness.mjs $UpstreamRef $generated
        if ($LASTEXITCODE -ne 0) { throw 'The golden harness failed.' }
    }
    finally {
        Pop-Location
    }

    if ($Check) {
        $expected = (Get-FileHash -LiteralPath $generated -Algorithm SHA256).Hash
        $actual = if (Test-Path -LiteralPath $OutputPath) { (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash } else { '' }
        if ($expected -ne $actual) {
            throw "$OutputPath does not match openclaw/openclaw@$UpstreamRef. Rerun without -Check."
        }
        Write-Host "$OutputPath matches openclaw/openclaw@$UpstreamRef."
    }
    else {
        Copy-Item -LiteralPath $generated -Destination $OutputPath -Force
        Write-Host "Wrote $OutputPath from openclaw/openclaw@$UpstreamRef."
    }
}
finally {
    $env:TZ = $previousTz
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
