# Pure PowerShell output-shape controls extracted from the actual runner source.
# No fixture, app, native process, network, delay or altered test deadline.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'Invoke-VoicePair.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'Voice runner PowerShell syntax is invalid.' }
$functions = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] }, $true))
$arm = @($functions | Where-Object Name -CEQ 'Invoke-Arm')
$guard = @($functions | Where-Object Name -CEQ 'Get-OnlyArmOutcome')
if ($arm.Count -ne 1 -or $guard.Count -ne 1) { throw 'Expected source ownership was not unique.' }
$drains = @($arm[0].Body.FindAll({ param($n)
    $n -is [Management.Automation.Language.PipelineAst] -and
    $n.Extent.Text.Contains('[Threading.Tasks.Task]::WhenAll(') -and
    $n.Extent.Text.Contains('.GetAwaiter().GetResult()')
}, $true))
if ($drains.Count -ne 1) { throw 'Expected one source-owned pipe-drain statement.' }
function Assert-DiscardedDrain([string]$Source) {
    if ($Source -cnotmatch '^\s*\[void\]\[Threading\.Tasks\.Task\]::WhenAll\(') {
        throw 'The actual drain result must be explicitly discarded.'
    }
}
Assert-DiscardedDrain $drains[0].Extent.Text
$drainBlock = [scriptblock]::Create($drains[0].Extent.Text)
. ([scriptblock]::Create($guard[0].Extent.Text))
$outDrain = [Threading.Tasks.Task]::CompletedTask
$errDrain = [Threading.Tasks.Task]::CompletedTask
$controls = 0
foreach ($expected in @('Passed', 'Failed')) {
    # Execute the actual statement, then the literal contract outcome. No copy of
    # the statement's implementation and no mock native UI/policy result.
    $actual = @(& { & $drainBlock; $expected })
    if ($actual.Count -ne 1 -or $actual[0] -isnot [string] -or $actual[0] -cne $expected -or
        (Get-OnlyArmOutcome -Output $actual) -cne $expected) { throw 'Drain contaminated the one-outcome success stream.' }
    $controls++
}
$rejected = $false
try { Assert-DiscardedDrain ($drains[0].Extent.Text -replace '^\s*\[void\]', '') } catch { $rejected = $true }
if (-not $rejected) { throw 'Undiscarded-source negative control was accepted.' }
$controls++
function Assert-RejectedArm([object[]]$Bad) {
    $rejected = $false
    try { $null = Get-OnlyArmOutcome -Output $Bad } catch { $rejected = $true }
    if (-not $rejected) { throw 'Non-singleton or invalid arm output was accepted.' }
}
Assert-RejectedArm -Bad @()
$controls++
Assert-RejectedArm -Bad @('Passed', [object]::new())
$controls++
Assert-RejectedArm -Bad @('Unknown')
$controls++
$outDrain = [Threading.Tasks.Task]::FromException([InvalidOperationException]::new('pure-control'))
$rejected = $false; $afterDrainReached = $false
try { & $drainBlock; $afterDrainReached = $true } catch { $rejected = $true }
if (-not $rejected -or $afterDrainReached) { throw 'Explicit discard swallowed a drain failure.' }
$controls++
if ($controls -ne 7) { throw 'Pure output-control inventory changed.' }
Write-Host 'Voice runner output controls: 7 passed (pure PowerShell; no native app execution).'
