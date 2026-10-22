<#
.SYNOPSIS
    Captures deterministic native Local AI screens from the candidate and a pinned main revision.
#>
[CmdletBinding()]
param([ValidatePattern('^$|^[0-9a-f]{40}$')][string]$BaselineRef = '')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$results = Join-Path $repo 'TestResults/HardwareProfiles'
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('local-ai-proof-' + [guid]::NewGuid().ToString('N'))
$baselineRepo = Join-Path $scratch 'main'
New-Item -ItemType Directory -Path $scratch -Force | Out-Null

function Invoke-Capture([string]$Source, [string]$Stage, [bool]$Baseline) {
    Push-Location $Source
    try {
        $project = 'tests/OpenClaw.Tray.UITests/OpenClaw.Tray.UITests.csproj'
        $expectedHead = (git rev-parse HEAD).Trim()
        $env:GitVersion_NoNormalizeEnabled = 'true'
        $env:GitVersion_NoFetchEnabled = 'true'
        dotnet build $project -c Debug -r win-x64 "-p:LocalAiProofBaseline=$($Baseline.ToString().ToLowerInvariant())"
        if ($LASTEXITCODE -ne 0) { throw "$Stage UI build failed." }

        # Freeze the exact source and loaded binaries before the first native capture.
        $freeze = Join-Path $scratch "$Stage-freeze"
        New-Item -ItemType Directory -Path $freeze -Force | Out-Null
        $head = (git rev-parse HEAD).Trim()
        if ($head -cne $expectedHead) { throw 'Proof build changed the pinned source revision.' }
        $files = @(git -c core.quotepath=false ls-files --cached --others --exclude-standard | Sort-Object -Unique | ForEach-Object {
            @{ path = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $Source $_) -Algorithm SHA256).Hash }
        })
        if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate proof source.' }
        $sourceManifest = Join-Path $freeze 'full-source-manifest.json'
        ConvertTo-Json -InputObject $files -Depth 4 | Set-Content -LiteralPath $sourceManifest -Encoding utf8NoBOM
        $bin = Join-Path $Source 'tests/OpenClaw.Tray.UITests/bin/Debug/net10.0-windows10.0.22621.0/win-x64'
        $binaries = @(Get-ChildItem -LiteralPath $bin -File | ForEach-Object {
            @{ path = "build\ui-tests\$($_.Name)"; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        })
        $buildManifest = Join-Path $freeze 'build-manifest.json'
        ConvertTo-Json -InputObject $binaries -Depth 4 | Set-Content -LiteralPath $buildManifest -Encoding utf8NoBOM
        $manifest = Join-Path $freeze 'manifest.json'
        @{
            head = $head
            fullSourceManifestSha = (Get-FileHash $sourceManifest -Algorithm SHA256).Hash
            buildManifestSha = (Get-FileHash $buildManifest -Algorithm SHA256).Hash
        } | ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding utf8NoBOM

        $env:OPENCLAW_REPO_ROOT = $Source
        $env:OPENCLAW_TRAY_DATA_DIR = Join-Path $scratch "$Stage-data"
        $env:OPENCLAW_TRAY_APPDATA_DIR = Join-Path $scratch "$Stage-roaming"
        $env:OPENCLAW_TRAY_LOCALAPPDATA_DIR = Join-Path $scratch "$Stage-local"
        $env:OPENCLAW_UI_PROOF_DIR = Join-Path $results $Stage
        $env:OPENCLAW_UI_PROOF_FREEZE_DIR = $freeze
        $env:OPENCLAW_UI_PROOF_MANIFEST_SHA256 = (Get-FileHash $manifest -Algorithm SHA256).Hash
        dotnet test $project --no-build -c Debug -r win-x64 `
            --filter 'FullyQualifiedName~LocalAiHardwareProfileTests.LocalAiPage_RendersHardwareFallbackOrUnavailable|FullyQualifiedName~LocalAiHardwareProfileTests.Onboarding_OffersOnlyFittingProfiles' `
            --results-directory (Join-Path $results $Stage) --logger "trx;LogFileName=$Stage.trx" `
            --blame-hang --blame-hang-timeout 120s --blame-hang-dump-type none
        if ($LASTEXITCODE -ne 0) { throw "$Stage native proof failed." }
        $images = @(Get-ChildItem -LiteralPath $env:OPENCLAW_UI_PROOF_DIR -Filter '*.png')
        if ($images.Count -ne 12) { throw "Expected 12 $Stage frames; found $($images.Count)." }
        "${Stage}: $head, $($images.Count) native frames" | Add-Content (Join-Path $results 'revisions.txt')
    }
    finally { Pop-Location }
}

try {
    Invoke-Capture $repo 'After' $false
    if ([string]::IsNullOrWhiteSpace($BaselineRef)) { return }
    git -C $repo worktree add --detach $baselineRepo $BaselineRef
    if ($LASTEXITCODE -ne 0) { throw 'Cannot check out the pinned baseline.' }
    # Only test plumbing crosses into main. All production files remain at BaselineRef.
    foreach ($file in @('LocalAiHardwareProfileTests.cs', 'OpenClaw.Tray.UITests.csproj')) {
        Copy-Item (Join-Path $repo "tests/OpenClaw.Tray.UITests/$file") (Join-Path $baselineRepo "tests/OpenClaw.Tray.UITests/$file")
    }
    Invoke-Capture $baselineRepo 'Before' $true
}
finally {
    $env:OPENCLAW_REPO_ROOT = $repo
    foreach ($name in @('OPENCLAW_UI_PROOF_DIR', 'OPENCLAW_UI_PROOF_FREEZE_DIR', 'OPENCLAW_UI_PROOF_MANIFEST_SHA256')) {
        Remove-Item "Env:$name" -ErrorAction SilentlyContinue
    }
}
