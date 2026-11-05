[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Set-CiDesktopResolution.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw "Desktop setup script has parse errors: $parseErrors" }
$nativeSource = $ast.Find({
    param($node)
    $node -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
        $node.StringConstantType -eq 'SingleQuotedHereString'
}, $true)
if ($null -eq $nativeSource) { throw 'Desktop native binding source was not found.' }
Add-Type -TypeDefinition $nativeSource.Value

# Independent DEVMODEW ABI expectations from wingdi.h, identical on x64 and ARM64.
$modeType = [OpenClaw.Ci.Desktop+DisplayMode]
if ([Runtime.InteropServices.Marshal]::SizeOf([Activator]::CreateInstance($modeType)) -ne 220 -or
    [Runtime.InteropServices.Marshal]::OffsetOf($modeType, 'Size').ToInt32() -ne 68 -or
    [Runtime.InteropServices.Marshal]::OffsetOf($modeType, 'PelsWidth').ToInt32() -ne 172 -or
    [Runtime.InteropServices.Marshal]::OffsetOf($modeType, 'PelsHeight').ToInt32() -ne 176) {
    throw 'The desktop display-mode binding does not match the Windows DEVMODEW ABI.'
}

$originalActions = $env:GITHUB_ACTIONS
$originalEnvironment = $env:RUNNER_ENVIRONMENT
try {
    foreach ($case in @(
        @{ actions = 'false'; environment = 'github-hosted' },
        @{ actions = 'true'; environment = 'self-hosted' }
    )) {
        $env:GITHUB_ACTIONS = $case.actions
        $env:RUNNER_ENVIRONMENT = $case.environment
        $rejected = $false
        try {
            & $scriptPath
        } catch {
            if ($_.Exception.Message -notlike '*restricted to disposable GitHub-hosted CI runners*') { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw 'Desktop changes must refuse local and self-hosted execution.' }
    }
} finally {
    $env:GITHUB_ACTIONS = $originalActions
    $env:RUNNER_ENVIRONMENT = $originalEnvironment
}
Write-Host 'CI desktop preflight tests passed: native ABI and local/self-hosted refusal. No display settings changed.'
