<#
.SYNOPSIS
Selects the xUnit tests a pull request adds or changes, for the CI new-test soak.
.DESCRIPTION
Diffs the head commit against its base and maps each changed line in the soaked test projects to
test methods (see scripts\CiChangedTests.ps1). Method-level hits come first; class and file
fallbacks follow. UI tests are soaked only for method-level hits, because a helper edit in a large
UI file would otherwise run many native tests the pull request did not write. Caps per project:
OPENCLAW_CI_SOAK_MAX_TESTS (default 30) and OPENCLAW_CI_SOAK_MAX_UI_TESTS (default 8), each at
most 150. Tests left out are reported with the reason. Writes a JSON selection and, under GitHub
Actions, the step outputs has_tests and ui_tests.
.PARAMETER BaseRef
The base commit. Defaults to the first parent of a pull request merge commit.
.EXAMPLE
.\scripts\Get-CiChangedTests.ps1 -BaseRef origin/main -OutputPath $env:TEMP\soak-selection.json
#>
[CmdletBinding()]
param(
    [string]$BaseRef,
    [string]$HeadRef = 'HEAD',
    [Parameter(Mandatory)][string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CiChangedTests.ps1')

# Projects whose tests the soak runs, built the way their CI lanes build them. UI tests launch native
# windows and are costly, so only tests the pull request edits directly are soaked there.
$soakProjects = @(
    [pscustomobject]@{ Name = 'shared'; Project = 'tests/OpenClaw.Shared.Tests'; Runtime = $null; Prebuild = @(); Ui = $false },
    [pscustomobject]@{ Name = 'tray'; Project = 'tests/OpenClaw.Tray.Tests'; Runtime = 'win-x64'; Prebuild = @('src/OpenClaw.Tray.WinUI'); Ui = $false },
    [pscustomobject]@{ Name = 'ui'; Project = 'tests/OpenClaw.Tray.UITests'; Runtime = 'win-x64'; Prebuild = @(); Ui = $true }
)

function Get-SoakCap([string]$Name, [int]$Default) {
    $raw = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($raw)) { return $Default }
    $value = 0
    # The filter lists every selected name, so the cap also keeps the command line well under its limit.
    if (-not [int]::TryParse($raw.Trim(), [ref]$value) -or $value -lt 1 -or $value -gt 150) {
        throw "$Name must be an integer from 1 to 150, not '$raw'."
    }
    return $value
}
$maxTests = Get-SoakCap 'OPENCLAW_CI_SOAK_MAX_TESTS' 30
$maxUiTests = Get-SoakCap 'OPENCLAW_CI_SOAK_MAX_UI_TESTS' 8

if (-not $BaseRef) {
    $parents = @((& git rev-list --parents -n 1 $HeadRef) -split ' ')
    if ($LASTEXITCODE -ne 0) { throw "Could not read the parents of $HeadRef." }
    if ($parents.Count -lt 3) { throw "BaseRef is required when $HeadRef is not a merge commit." }
    $BaseRef = $parents[1]
}
$base = (& git rev-parse --verify "$BaseRef^{commit}").Trim()
if ($LASTEXITCODE -ne 0) { throw "Unknown base commit '$BaseRef'." }
$head = (& git rev-parse --verify "$HeadRef^{commit}").Trim()
if ($LASTEXITCODE -ne 0) { throw "Unknown head commit '$HeadRef'." }

$pathspecs = @($soakProjects | ForEach-Object { ":(glob)$($_.Project)/**/*.cs" })
$diff = (& git -c core.quotePath=false diff --unified=0 --no-color --no-ext-diff --diff-filter=AMR $base $head -- @pathspecs) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'git diff failed while selecting changed tests.' }
$changed = Get-CiChangedLineMap -DiffText $diff

$candidates = [Collections.Generic.List[object]]::new()
foreach ($path in ($changed.Keys | Sort-Object)) {
    if ($path -match '(^|/)(bin|obj)/') { continue }
    $project = $soakProjects | Where-Object { $path.StartsWith($_.Project + '/', [StringComparison]::Ordinal) } | Select-Object -First 1
    if (-not $project) { continue }
    $source = (& git show "${head}:$path") -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "Could not read $path at $head." }
    $methods = Get-CiTestMethods -Source $source
    foreach ($test in (Select-CiChangedTests -Methods $methods -Source $source -ChangedLines $changed[$path].Lines.ToArray() -ForcedLines @($changed[$path].Forced))) {
        $candidates.Add([pscustomobject]@{ Project = $project.Name; FullyQualifiedName = $test.FullyQualifiedName; Kind = $test.Kind; Path = $path })
    }
}

# One entry per test, keeping its most specific reason; method-level hits are soaked first.
# The cap applies per project so cheap unit tests cannot crowd out the slower UI tests.
$rank = @{ method = 0; class = 1; file = 2 }
$unique = @($candidates | Group-Object Project, FullyQualifiedName | ForEach-Object {
        $_.Group | Sort-Object { $rank[$_.Kind] } | Select-Object -First 1
    } | Sort-Object { $rank[$_.Kind] }, Project, FullyQualifiedName)
$soaked = [Collections.Generic.List[object]]::new()
$omitted = [Collections.Generic.List[object]]::new()
foreach ($group in ($unique | Group-Object Project)) {
    $isUi = ($soakProjects | Where-Object Name -EQ $group.Name).Ui
    $cap = if ($isUi) { $maxUiTests } else { $maxTests }
    $eligible = @($group.Group | Where-Object { -not $isUi -or $_.Kind -eq 'method' })
    foreach ($test in $group.Group) {
        $reason = if ($isUi -and $test.Kind -ne 'method') { 'ui-fallback' } elseif (@($eligible).IndexOf($test) -ge $cap) { 'cap' } else { $null }
        if ($reason) {
            $omitted.Add([pscustomobject]@{ Project = $test.Project; FullyQualifiedName = $test.FullyQualifiedName; Kind = $test.Kind; Reason = $reason })
        } else {
            $soaked.Add($test)
        }
    }
}

$selection = [ordered]@{
    base = $base
    head = $head
    maxTests = $maxTests
    maxUiTests = $maxUiTests
    projects = @($soakProjects | ForEach-Object {
            $name = $_.Name
            $tests = @($soaked | Where-Object Project -EQ $name)
            if ($tests.Count) {
                [ordered]@{
                    name = $name; project = $_.Project; runtime = $_.Runtime; prebuild = @($_.Prebuild); ui = $_.Ui
                    tests = @($tests | ForEach-Object { [ordered]@{ fullyQualifiedName = $_.FullyQualifiedName; kind = $_.Kind; path = $_.Path } })
                }
            }
        })
    omitted = @($omitted | ForEach-Object { [ordered]@{ project = $_.Project; fullyQualifiedName = $_.FullyQualifiedName; kind = $_.Kind; reason = $_.Reason } })
}
$directory = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$selection | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding utf8

$hasTests = $soaked.Count -gt 0
$uiTests = @($soaked | Where-Object Project -EQ 'ui').Count -gt 0
Write-Host ("New-test soak selection: {0} test(s) from {1} to {2}; {3} not soaked." -f $soaked.Count, $base.Substring(0, 12), $head.Substring(0, 12), $omitted.Count)
foreach ($test in $soaked) { Write-Host ("  [{0}] {1} ({2})" -f $test.Project, $test.FullyQualifiedName, $test.Kind) }
foreach ($test in $omitted) { Write-Host ("  not soaked [{0}] {1} ({2}, {3})" -f $test.Project, $test.FullyQualifiedName, $test.Kind, $test.Reason) }
$capped = @($omitted | Where-Object Reason -EQ 'cap').Count
if ($capped) {
    Write-Host ("::warning title=New-test soak cap::{0} changed test(s) were not soaked: the selection is capped at {1} per project and {2} for UI tests (OPENCLAW_CI_SOAK_MAX_TESTS, OPENCLAW_CI_SOAK_MAX_UI_TESTS)." -f $capped, $maxTests, $maxUiTests)
}
if ($env:GITHUB_OUTPUT) {
    "has_tests=$($hasTests.ToString().ToLowerInvariant())" | Add-Content -LiteralPath $env:GITHUB_OUTPUT
    "ui_tests=$($uiTests.ToString().ToLowerInvariant())" | Add-Content -LiteralPath $env:GITHUB_OUTPUT
}
