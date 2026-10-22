<#
.SYNOPSIS
    Compiles installer.iss against a stub payload to catch Inno Setup script errors.

.DESCRIPTION
    The real installer is only built in the tag-only release job, so a syntax or
    Pascal Script error in installer.iss would otherwise surface at release time.
    This gate compiles the script itself with a throwaway payload so pull requests
    fail fast instead.

    Both the production and DevBuild variants are compiled because the migration
    lock code is inside an #ifndef DevBuild block, so a single variant cannot
    cover it.

    This proves the script compiles. It does not prove installer runtime behavior.
#>
[CmdletBinding()]
param(
    [string]$InnoCompiler,
    # CI must not silently pass when the compiler is absent.
    [switch]$RequireCompiler
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot

function Resolve-InnoCompiler {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    return $null
}

if (-not $InnoCompiler) {
    $InnoCompiler = Resolve-InnoCompiler
}

if (-not $InnoCompiler) {
    if ($RequireCompiler) {
        throw 'Inno Setup compiler (ISCC.exe) was not found, but -RequireCompiler was specified.'
    }
    Write-Warning 'Inno Setup compiler (ISCC.exe) was not found. Skipping installer script compilation.'
    exit 2
}

$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("openclaw-iss-compile-" + [System.Guid]::NewGuid().ToString('n'))
$payloadDir = Join-Path $workRoot 'publish'
$outputDir = Join-Path $workRoot 'out'

try {
    New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

    # ISCC resolves every [Files] Source at compile time, so the stub only needs to
    # exist. Contents are irrelevant to a compilation check.
    Set-Content -LiteralPath (Join-Path $payloadDir 'OpenClaw.Tray.WinUI.exe') -Value 'stub' -Encoding ascii
    $vcRedist = Join-Path $workRoot 'vc_redist.stub.exe'
    Set-Content -LiteralPath $vcRedist -Value 'stub' -Encoding ascii

    $variants = @(
        @{ Name = 'x64'; Arch = 'x64'; Dev = $false },
        @{ Name = 'arm64'; Arch = 'arm64'; Dev = $false },
        @{ Name = 'x64 (DevBuild)'; Arch = 'x64'; Dev = $true }
    )

    foreach ($variant in $variants) {
        $isccArgs = @(
            '/Q',
            "/DMyAppVersion=0.0.0",
            "/DMyAppArch=$($variant.Arch)",
            "/Dpublish=$payloadDir",
            "/DvcRedist=$vcRedist",
            "/O$outputDir"
        )
        if ($variant.Dev) {
            $isccArgs += '/DDevBuild=1'
        }
        $isccArgs += (Join-Path $repoRoot 'installer.iss')

        Write-Host "Compiling installer.iss: $($variant.Name)"
        & $InnoCompiler @isccArgs
        if ($LASTEXITCODE -ne 0) {
            throw "ISCC failed to compile installer.iss for $($variant.Name). Exit code: $LASTEXITCODE."
        }
    }

    Write-Host 'installer.iss compiles for all variants.'
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# GitHub Actions appends `exit $LASTEXITCODE` to pwsh steps.
exit 0
