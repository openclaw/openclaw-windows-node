<#
.SYNOPSIS
    Exercises agent skill validator regressions.
#>

[CmdletBinding()]
param(
    [string]$RepoRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $scriptRoot
}
$repoRootPath = [System.IO.Path]::GetFullPath($RepoRoot)
$validatorPath = Join-Path $repoRootPath "scripts\validate-agent-skills.ps1"
$childPowerShell = [pscustomobject]@{ Source = (Get-Process -Id $PID).Path }

function Invoke-Validator([string]$Root) {
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = (& $childPowerShell.Source `
            -NoProfile `
            -ExecutionPolicy Bypass `
            -File $validatorPath `
            -RepoRoot $Root 2>&1 | Out-String)
        return @{
            ExitCode = $LASTEXITCODE
            Output = $output
        }
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}

$tempRoot = Join-Path (Join-Path $repoRootPath "TestResults") (
    "openclaw-skill-validator-" + [guid]::NewGuid().ToString("N"))
$outside = "$tempRoot-outside"
$junction = Join-Path $tempRoot 'docs\escape'
$assertions = 0
function Assert-Validation([string]$Text, [string]$ExpectedError = '') {
    Set-Content -LiteralPath $skillPath -Value $Text -Encoding UTF8
    $result = Invoke-Validator $tempRoot
    if ($ExpectedError) {
        if ($result.ExitCode -eq 0 -or $result.Output -notmatch $ExpectedError) {
            throw "Expected '$ExpectedError': $($result.Output)"
        }
    } elseif ($result.ExitCode -ne 0) { throw "Valid fixture failed: $($result.Output)" }
    $script:assertions++
}
try {
    $skillRoot = Join-Path $tempRoot ".agents\skills\example"
    New-Item -ItemType Directory -Path $skillRoot -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $skillRoot "reference.md") -Value "# Reference"
    $skillPath = Join-Path $skillRoot "SKILL.md"
    $valid = @"
---
name: example
description: Example validator fixture.
---

# Example

See [reference](reference.md).
"@
    Assert-Validation $valid
    Assert-Validation ($valid.Replace('name: example', 'name: wrong')) 'must match the directory name'
    Assert-Validation ($valid.Replace('reference.md', 'missing.md')) 'local link target does not exist'
    Assert-Validation ($valid.Replace('reference.md', '#example'))
    Assert-Validation ($valid.Replace('reference.md', '#missing')) 'Markdown anchor does not exist'
    New-Item -ItemType Directory -Path (Join-Path $tempRoot 'docs') -Force | Out-Null
    Set-Content (Join-Path $tempRoot 'docs\canonical.md') -Value @'
# Canonical
## Repeat
## Repeat
```powershell
# Not a heading
```
Setext
======
<a id="explicit"></a>
'@
    foreach ($fragment in @('canonical', 'repeat-1', 'setext', 'explicit')) {
        Assert-Validation ($valid.Replace('reference.md', "../../../docs/canonical.md#$fragment"))
    }
    Assert-Validation ($valid.Replace('reference.md', '../../../docs/canonical.md#not-a-heading')) 'Markdown anchor does not exist'
    Assert-Validation ($valid.Replace('reference.md', '../../../../escape.md')) 'leaves the repository'
    Assert-Validation ($valid.Replace('reference.md', '../../../%2e%2e/escape.md')) 'leaves the repository'
    Assert-Validation "---`ndescription: 'Example: it''s valid.'`nname: `"example`"`n---`n# Example"
    foreach ($metadata in @(
        "name: example`nname: example`ndescription: okay",
        "name: example`ndescription: first`ndescription: second"
    )) { Assert-Validation ("---`n$metadata`n---`n# Example") 'duplicate metadata key' }
    foreach ($metadata in @(
        'name: example',
        "name: example`ndescription: ''",
        "name: example`ndescription: >`n  multiline",
        "name: example`ndescription: [list]",
        "name: example`ndescription: okay`nextra: unsupported",
        "name: example`ndescription: `"unterminated",
        "name: example`ndescription: false",
        "name: example`ndescription: # only a comment",
        "name: ''`ndescription: Example",
        "Name: example`ndescription: Example"
    )) { Assert-Validation ("---`n$metadata`n---`n# Example") 'metadata|scalar|string' }
    Assert-Validation ($valid + [char]0x2014) 'em dashes'
    New-Item -ItemType Directory -Path $outside -Force | Out-Null
    Set-Content (Join-Path $outside 'outside.md') '# Outside'
    # Junction creation does not need symlink elevation on Windows. A failure is
    # a failed regression, not a silently skipped escape check.
    New-Item -ItemType Junction -Path $junction -Target $outside -ErrorAction Stop | Out-Null
    Assert-Validation ($valid.Replace('reference.md', '../../../docs/escape/outside.md')) 'reparse point'
    $actual = Invoke-Validator $repoRootPath
    if ($actual.ExitCode -ne 0) { throw "Repository skills failed: $($actual.Output)" }
    Write-Host $actual.Output.Trim()
} finally {
    if (Test-Path -LiteralPath $junction) { [IO.Directory]::Delete($junction) }
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
    if (Test-Path -LiteralPath $outside) { Remove-Item -LiteralPath $outside -Recurse -Force }
}

Write-Host "Agent skill validator regressions passed: $assertions fixtures plus repository skills." -ForegroundColor Green
$global:LASTEXITCODE = 0
exit 0
